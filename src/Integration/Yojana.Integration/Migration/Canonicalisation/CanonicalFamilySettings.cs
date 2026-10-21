using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Canonicalisation;

public sealed class CanonicalFamilySettings(ITenantSettings settings)
{
    public const string SettingKey = "migration.canonicalisation.families";
    private static readonly IReadOnlyList<CanonicalFamilyRule> Defaults =
        [new("", StripHyphens: true, StripSpaces: true, StripLeadingZeros: true)];

    public async Task<CanonicalFamilySettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        return new(Resolve(stored?.Value), stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<CanonicalFamilySettingsView> SetAsync(UpdateCanonicalFamilySettingsRequest request,
        CancellationToken cancellationToken)
    {
        var families = Validate(request.Families);
        var stored = await settings.SetAsync(SettingKey, JsonSerializer.Serialize(families),
            request.ExpectedVersion, cancellationToken);
        return new(families, stored.Version);
    }

    public async Task<IReadOnlyList<CanonicalFamilyRule>> ResolveAsync(CancellationToken cancellationToken) =>
        Resolve(await settings.GetAsync(SettingKey, cancellationToken));

    private static IReadOnlyList<CanonicalFamilyRule> Resolve(string? json) => json is null
        ? Defaults
        : Validate(JsonSerializer.Deserialize<List<CanonicalFamilyRule>>(json)
            ?? throw new InvalidOperationException($"Tenant setting '{SettingKey}' is invalid."));

    private static List<CanonicalFamilyRule> Validate(IReadOnlyList<CanonicalFamilyRule> families)
    {
        if (families.Count == 0 || families.Count > 100)
            throw new MigrationRuleException("migration_canonical_families_invalid",
                "Provide between one and 100 item-family rules.");
        var cleaned = families.Select(rule => rule with { ItemCodePrefix = rule.ItemCodePrefix.Trim().ToUpperInvariant() })
            .ToList();
        if (cleaned.Any(rule => rule.ItemCodePrefix.Length > 100) ||
            cleaned.Select(rule => rule.ItemCodePrefix).Distinct(StringComparer.Ordinal).Count() != cleaned.Count ||
            cleaned.Count(rule => rule.ItemCodePrefix.Length == 0) != 1)
            throw new MigrationRuleException("migration_canonical_families_invalid",
                "Family prefixes must be unique, no longer than 100 characters, and include one empty catch-all prefix.");
        return cleaned.OrderByDescending(rule => rule.ItemCodePrefix.Length)
            .ThenBy(rule => rule.ItemCodePrefix, StringComparer.Ordinal).ToList();
    }
}
