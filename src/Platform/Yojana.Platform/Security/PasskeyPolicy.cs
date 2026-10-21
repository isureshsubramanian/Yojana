using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Security;

public sealed record PasskeySettingsView(bool Allowed, bool Passwordless, string Version);
public sealed record PasskeyPermission(bool Allowed, bool Passwordless);

/// <summary>
/// The tenant's passkey rules (sign-in policy screen): whether people may use passkeys at all, and whether a passkey
/// alone may sign them in. Passkeys are allowed and passwordless sign-in is off until an administrator turns it on.
/// </summary>
public sealed class PasskeyPolicy(
    ITenantSettings current,
    IServiceScopeFactory scopes,
    PlatformDbContext db)
{
    public const string AllowedKey = "security.passkeys.allowed";
    public const string PasswordlessKey = "security.passkeys.passwordless";

    public async Task<PasskeySettingsView> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var allowed = await current.GetVersionedAsync(AllowedKey, cancellationToken);
        var passwordless = await current.GetVersionedAsync(PasswordlessKey, cancellationToken);
        var permission = Parse(allowed?.Value, passwordless?.Value);
        return new PasskeySettingsView(permission.Allowed, permission.Passwordless,
            $"{allowed?.Version ?? ITenantSettings.MissingVersion};{passwordless?.Version ?? ITenantSettings.MissingVersion}");
    }

    /// <summary>Saves both or neither; returns the changed keys for the audit record.</summary>
    public async Task<(PasskeySettingsView View, IReadOnlyList<string> Changed)> UpdateCurrentAsync(bool allowed,
        bool passwordless, string version, CancellationToken cancellationToken)
    {
        if (passwordless && !allowed)
            throw new ArgumentException("Passwordless sign-in needs passkeys to be allowed.", nameof(passwordless));
        var parts = version.Split(';');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new TenantSettingVersionConflictException(AllowedKey, version, "unknown");
        var before = await GetCurrentAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var savedAllowed = await current.SetAsync(AllowedKey, allowed ? "true" : "false", parts[0], cancellationToken);
            var savedPasswordless = await current.SetAsync(PasswordlessKey, passwordless ? "true" : "false", parts[1],
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var changed = new List<string>();
            if (before.Allowed != allowed) changed.Add(AllowedKey);
            if (before.Passwordless != passwordless) changed.Add(PasswordlessKey);
            return (new PasskeySettingsView(allowed, passwordless, $"{savedAllowed.Version};{savedPasswordless.Version}"),
                changed);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// A user identity is global, so the strictest answer across every tenant of an active membership wins, as for the
    /// second step: one company turning passkeys off must not be bypassed by signing in through another.
    /// </summary>
    public async Task<PasskeyPermission> EffectiveForUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        var tenants = await db.Memberships.AsNoTracking()
            .Where(row => row.UserId == userId && row.DeactivatedAt == null && row.Tenant!.IsActive)
            .Select(row => row.TenantId).Distinct().ToListAsync(cancellationToken);
        if (tenants.Count == 0) return new PasskeyPermission(false, false);
        var allowed = true;
        var passwordless = true;
        // Tenant settings sit behind SQL Server RLS, so each tenant is read in its own scope (see SecondStepPolicy).
        foreach (var tenantId in tenants)
        {
            using var scope = scopes.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenantId);
            var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
            var permission = Parse(await settings.GetAsync(AllowedKey, cancellationToken),
                await settings.GetAsync(PasswordlessKey, cancellationToken));
            allowed &= permission.Allowed;
            passwordless &= permission.Passwordless;
        }
        return new PasskeyPermission(allowed, allowed && passwordless);
    }

    public static PasskeyPermission Parse(string? allowed, string? passwordless)
    {
        var isAllowed = !bool.TryParse(allowed, out var parsedAllowed) || parsedAllowed;
        return new PasskeyPermission(isAllowed, isAllowed && bool.TryParse(passwordless, out var parsed) && parsed);
    }
}

/// <summary>
/// Where passkeys are valid. Every passkey is bound by the browser to the relying party id (a host name) when it is
/// created, and an answer is accepted only from a listed origin.
/// </summary>
public sealed class PasskeyOptions
{
    public const string SectionName = "Platform:Passkeys";

    /// <summary>
    /// The host name passkeys belong to, for example yojana.example.com, set in appsettings.json and never worked
    /// out from another setting. Empty means passkeys are off. Changing it later makes every registered passkey
    /// stop working, because the authenticator signs for the id it was created with: people would have to add
    /// their passkeys again.
    /// </summary>
    public string RelyingPartyId { get; set; } = "";
    public string RelyingPartyName { get; set; } = "Yojana";
    /// <summary>Origins that may answer (scheme, host and port), each on the relying party id. Empty means passkeys are off.</summary>
    public string[]? Origins { get; set; }

    public bool Configured => RelyingPartyId.Length > 0 && Origins is { Length: > 0 };

    /// <summary>Normalises the settings and checks every origin belongs to the relying party id.</summary>
    public PasskeyOptions Resolve()
    {
        var resolved = new PasskeyOptions
        {
            RelyingPartyId = RelyingPartyId.Trim().ToLowerInvariant(),
            RelyingPartyName = string.IsNullOrWhiteSpace(RelyingPartyName) ? "Yojana" : RelyingPartyName.Trim(),
            Origins = Origins?.Where(origin => !string.IsNullOrWhiteSpace(origin))
                .Select(origin => origin.Trim().TrimEnd('/')).ToArray(),
        };
        if (!resolved.Configured) return resolved;
        foreach (var origin in resolved.Origins!)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.AbsolutePath != "/" ||
                uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
                throw new InvalidOperationException(
                    $"{SectionName}:Origins: '{origin}' must be an https origin (http only on this computer).");
            var host = uri.Host.ToLowerInvariant();
            if (host != resolved.RelyingPartyId && !host.EndsWith("." + resolved.RelyingPartyId, StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"{SectionName}:Origins: '{origin}' is not on the relying party id '{resolved.RelyingPartyId}'.");
        }
        return resolved;
    }
}
