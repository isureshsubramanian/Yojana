using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Security;

public sealed record PasswordSecuritySettingsView(int ResetLinkMinutes, bool AdminTemporaryPasswordAllowed,
    int InvitationValidDays, string Version);

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
        var invitation = await current.GetVersionedAsync(InvitationService.ValidDaysSetting, cancellationToken);
        return new PasswordSecuritySettingsView(ParseMinutes(reset?.Value), ParseBoolean(temporary?.Value),
            ParseDays(invitation?.Value), JoinVersions(reset?.Version, temporary?.Version, invitation?.Version));
    }

    /// <summary>Saves all three or none; returns the changed keys for the audit record.</summary>
    public async Task<(PasswordSecuritySettingsView View, IReadOnlyList<string> Changed)> UpdateCurrentAsync(
        int resetLinkMinutes, bool adminTemporaryPasswordAllowed, int invitationValidDays, string version,
        CancellationToken cancellationToken)
    {
        if (invitationValidDays is < InvitationService.MinimumValidDays or > InvitationService.MaximumValidDays)
            throw new ArgumentOutOfRangeException(nameof(invitationValidDays),
                $"Invitations must work for {InvitationService.MinimumValidDays} to {InvitationService.MaximumValidDays} days.");
        if (resetLinkMinutes is < MinimumResetLinkMinutes or > MaximumResetLinkMinutes)
            throw new ArgumentOutOfRangeException(nameof(resetLinkMinutes),
                $"Reset links must work for {MinimumResetLinkMinutes} to {MaximumResetLinkMinutes} minutes.");
        var versions = SplitVersions(version);
        var before = await GetCurrentAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var reset = await current.SetAsync(ResetLinkMinutesKey,
                resetLinkMinutes.ToString(CultureInfo.InvariantCulture), versions.Reset, cancellationToken);
            var temporary = await current.SetAsync(AdminTemporaryPasswordAllowedKey,
                adminTemporaryPasswordAllowed ? "true" : "false", versions.Temporary, cancellationToken);
            var invitation = await current.SetAsync(InvitationService.ValidDaysSetting,
                invitationValidDays.ToString(CultureInfo.InvariantCulture), versions.Invitation, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            var changed = new List<string>();
            if (before.ResetLinkMinutes != resetLinkMinutes) changed.Add(ResetLinkMinutesKey);
            if (before.AdminTemporaryPasswordAllowed != adminTemporaryPasswordAllowed)
                changed.Add(AdminTemporaryPasswordAllowedKey);
            if (before.InvitationValidDays != invitationValidDays) changed.Add(InvitationService.ValidDaysSetting);
            return (new PasswordSecuritySettingsView(resetLinkMinutes, adminTemporaryPasswordAllowed,
                invitationValidDays, JoinVersions(reset.Version, temporary.Version, invitation.Version)), changed);
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

    private static int ParseDays(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed is >= InvitationService.MinimumValidDays and <= InvitationService.MaximumValidDays
            ? parsed : InvitationService.DefaultValidDays;

    private static bool ParseBoolean(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    private static string JoinVersions(params string?[] versions) =>
        string.Join(';', versions.Select(value => value ?? ITenantSettings.MissingVersion));

    private static (string Reset, string Temporary, string Invitation) SplitVersions(string value)
    {
        var parts = value.Split(';');
        if (parts.Length != 3 || parts.Any(string.IsNullOrWhiteSpace))
            throw new TenantSettingVersionConflictException(ResetLinkMinutesKey, value, "unknown");
        return (parts[0], parts[1], parts[2]);
    }
}
