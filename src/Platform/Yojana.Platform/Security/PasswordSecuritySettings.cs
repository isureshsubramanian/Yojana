using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Security;

public sealed record PasswordSecuritySettingsView(int ResetLinkMinutes, bool AdminTemporaryPasswordAllowed,
    string Version);

public sealed class PasswordSecuritySettings(
    ITenantSettings current,
    IServiceScopeFactory scopes,
    PlatformDbContext db)
{
    public const string ResetLinkMinutesKey = "security.password_reset.link_minutes";
    public const string AdminTemporaryPasswordAllowedKey = "security.admin_temporary_password.allowed";
    public const int DefaultResetLinkMinutes = 30;
    public const int MinimumResetLinkMinutes = 10;
    public const int MaximumResetLinkMinutes = 1_440;

    public async Task<PasswordSecuritySettingsView> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var reset = await current.GetVersionedAsync(ResetLinkMinutesKey, cancellationToken);
        var temporary = await current.GetVersionedAsync(AdminTemporaryPasswordAllowedKey, cancellationToken);
        return new PasswordSecuritySettingsView(ParseMinutes(reset?.Value), ParseBoolean(temporary?.Value),
            JoinVersions(reset?.Version, temporary?.Version));
    }

    public async Task<PasswordSecuritySettingsView> UpdateCurrentAsync(int resetLinkMinutes,
        bool adminTemporaryPasswordAllowed, string version, CancellationToken cancellationToken)
    {
        if (resetLinkMinutes is < MinimumResetLinkMinutes or > MaximumResetLinkMinutes)
            throw new ArgumentOutOfRangeException(nameof(resetLinkMinutes),
                $"Reset links must work for {MinimumResetLinkMinutes} to {MaximumResetLinkMinutes} minutes.");
        var versions = SplitVersions(version);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var reset = await current.SetAsync(ResetLinkMinutesKey,
                resetLinkMinutes.ToString(CultureInfo.InvariantCulture), versions.Reset, cancellationToken);
            var temporary = await current.SetAsync(AdminTemporaryPasswordAllowedKey,
                adminTemporaryPasswordAllowed ? "true" : "false", versions.Temporary, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new PasswordSecuritySettingsView(resetLinkMinutes, adminTemporaryPasswordAllowed,
                JoinVersions(reset.Version, temporary.Version));
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<int> ForTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenantId);
        var value = await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
            .GetAsync(ResetLinkMinutesKey, cancellationToken);
        return ParseMinutes(value);
    }

    public async Task<bool> TemporaryAllowedForCurrentAsync(CancellationToken cancellationToken) =>
        ParseBoolean(await current.GetAsync(AdminTemporaryPasswordAllowedKey, cancellationToken));

    private static int ParseMinutes(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed is >= MinimumResetLinkMinutes and <= MaximumResetLinkMinutes
            ? parsed : DefaultResetLinkMinutes;

    private static bool ParseBoolean(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    private static string JoinVersions(string? reset, string? temporary) =>
        $"{reset ?? ITenantSettings.MissingVersion};{temporary ?? ITenantSettings.MissingVersion}";

    private static (string Reset, string Temporary) SplitVersions(string value)
    {
        var parts = value.Split(';');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace))
            throw new TenantSettingVersionConflictException(ResetLinkMinutesKey, value, "unknown");
        return (parts[0], parts[1]);
    }
}
