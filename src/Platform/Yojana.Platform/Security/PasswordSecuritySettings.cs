using System.Text.Json.Serialization;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Security;

public sealed record PasswordSecuritySettingsView(int ResetLinkMinutes, bool AdminTemporaryPasswordAllowed,
    int InvitationValidDays, int MinimumLength, bool RequireUppercase, bool RequireLowercase, bool RequireDigit,
    bool RequireSymbol, string Version)
{
    [JsonIgnore]
    public PasswordRules Rules => new(MinimumLength, RequireUppercase, RequireLowercase, RequireDigit, RequireSymbol);
}

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
    public const string MinimumLengthKey = "security.password.minimum_length";
    public const string RequireUppercaseKey = "security.password.require_uppercase";
    public const string RequireLowercaseKey = "security.password.require_lowercase";
    public const string RequireDigitKey = "security.password.require_digit";
    public const string RequireSymbolKey = "security.password.require_symbol";

    // Saved together and versioned together, in this order; the view's Version joins the versions in the same order.
    private static readonly string[] Keys =
    [
        ResetLinkMinutesKey, AdminTemporaryPasswordAllowedKey, InvitationService.ValidDaysSetting, MinimumLengthKey,
        RequireUppercaseKey, RequireLowercaseKey, RequireDigitKey, RequireSymbolKey,
    ];

    public async Task<PasswordSecuritySettingsView> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var rows = new VersionedTenantSetting?[Keys.Length];
        for (var index = 0; index < Keys.Length; index++)
            rows[index] = await current.GetVersionedAsync(Keys[index], cancellationToken);
        return View(rows.Select(row => row?.Value).ToArray(), JoinVersions(rows.Select(row => row?.Version).ToArray()));
    }

    /// <summary>Saves all or none; returns the changed keys for the audit record.</summary>
    public async Task<(PasswordSecuritySettingsView View, IReadOnlyList<string> Changed)> UpdateCurrentAsync(
        PasswordSecuritySettingsView requested, string version, CancellationToken cancellationToken)
    {
        if (requested.InvitationValidDays is < InvitationService.MinimumValidDays or > InvitationService.MaximumValidDays)
            throw new ArgumentOutOfRangeException(nameof(requested),
                $"Invitations must work for {InvitationService.MinimumValidDays} to {InvitationService.MaximumValidDays} days.");
        if (requested.ResetLinkMinutes is < MinimumResetLinkMinutes or > MaximumResetLinkMinutes)
            throw new ArgumentOutOfRangeException(nameof(requested),
                $"Reset links must work for {MinimumResetLinkMinutes} to {MaximumResetLinkMinutes} minutes.");
        if (requested.MinimumLength is < PasswordRules.ShortestMinimum or > PasswordRules.LongestMinimum)
            throw new ArgumentOutOfRangeException(nameof(requested),
                $"The shortest password must be {PasswordRules.ShortestMinimum} to {PasswordRules.LongestMinimum} characters.");
        var versions = SplitVersions(version);
        var before = await GetCurrentAsync(cancellationToken);
        var values = Values(requested);
        var previous = Values(before);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var saved = new string[Keys.Length];
            for (var index = 0; index < Keys.Length; index++)
                saved[index] = (await current.SetAsync(Keys[index], values[index], versions[index], cancellationToken))
                    .Version;
            await transaction.CommitAsync(cancellationToken);
            var changed = Keys.Where((_, index) => previous[index] != values[index]).ToList();
            return (View(values, JoinVersions(saved)), changed);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>One company's password rules, read in that company's scope (no request tenant is needed).</summary>
    public async Task<PasswordRules> RulesForTenantAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenantId);
        var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
        return new PasswordRules(
            ParseLength(await settings.GetAsync(MinimumLengthKey, cancellationToken)),
            ParseSwitch(await settings.GetAsync(RequireUppercaseKey, cancellationToken)),
            ParseSwitch(await settings.GetAsync(RequireLowercaseKey, cancellationToken)),
            ParseSwitch(await settings.GetAsync(RequireDigitKey, cancellationToken)),
            ParseSwitch(await settings.GetAsync(RequireSymbolKey, cancellationToken)));
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

    private static int ParseLength(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
        && parsed is >= PasswordRules.ShortestMinimum and <= PasswordRules.LongestMinimum
            ? parsed : PasswordRules.Default.MinimumLength;

    /// <summary>The class rules default to on (ADR-0008 §1), so only a stored "false" turns one off.</summary>
    private static bool ParseSwitch(string? value) =>
        !bool.TryParse(value, out var parsed) || parsed;

    private static PasswordSecuritySettingsView View(string?[] values, string version) => new(
        ParseMinutes(values[0]), ParseBoolean(values[1]), ParseDays(values[2]), ParseLength(values[3]),
        ParseSwitch(values[4]), ParseSwitch(values[5]), ParseSwitch(values[6]), ParseSwitch(values[7]), version);

    private static string[] Values(PasswordSecuritySettingsView view) =>
    [
        view.ResetLinkMinutes.ToString(CultureInfo.InvariantCulture), Flag(view.AdminTemporaryPasswordAllowed),
        view.InvitationValidDays.ToString(CultureInfo.InvariantCulture),
        view.MinimumLength.ToString(CultureInfo.InvariantCulture), Flag(view.RequireUppercase),
        Flag(view.RequireLowercase), Flag(view.RequireDigit), Flag(view.RequireSymbol),
    ];

    private static string Flag(bool value) => value ? "true" : "false";

    private static string JoinVersions(params string?[] versions) =>
        string.Join(';', versions.Select(value => value ?? ITenantSettings.MissingVersion));

    private static string[] SplitVersions(string value)
    {
        var parts = value.Split(';');
        if (parts.Length != Keys.Length || parts.Any(string.IsNullOrWhiteSpace))
            throw new TenantSettingVersionConflictException(ResetLinkMinutesKey, value, "unknown");
        return parts;
    }
}
