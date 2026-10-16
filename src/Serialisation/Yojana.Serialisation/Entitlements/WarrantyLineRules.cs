using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

public static class WarrantyLineRules
{
    public static IReadOnlyList<string> ValidateTarget(WarrantyLineTarget target, bool targetsKnown, bool serialTracked)
    {
        var reasons = new List<string>();
        if (!WarrantyTargetRule.All.Contains(target.Rule)) reasons.Add("target_rule_unknown");
        if (target.Rule == WarrantyTargetRule.ReferencedDocument && target.Document is null) reasons.Add("referenced_document_required");
        if (target.Rule == WarrantyTargetRule.AttachedList && (target.Serials?.Count ?? 0) == 0 && (target.Scheme is null || target.RangeStart is null || target.RangeEnd is null)) reasons.Add("attached_list_required");
        if (!targetsKnown) reasons.Add("target_units_unknown");
        if (!serialTracked) reasons.Add("target_not_serialised");
        return reasons;
    }

    public static IReadOnlyList<string> ValidateResolvedTarget(WarrantyLineTarget target, int resolved, int unknown, int nonSerialTracked)
    {
        var reasons = ValidateTarget(target, resolved > 0 && unknown == 0, nonSerialTracked == 0).ToList();
        return reasons.Distinct(StringComparer.Ordinal).ToList();
    }

    public static string? Regulation(PolicyTerms terms, WarrantyReferencePolicy policy, string? country, string? regionGroup, bool hasLicensedObligor)
    {
        if (!terms.ServiceContractRegulated) return null;
        var rule = policy.Jurisdictions.FirstOrDefault(r => string.Equals(r.CountryOrRegionGroup, country, StringComparison.OrdinalIgnoreCase))
            ?? policy.Jurisdictions.FirstOrDefault(r => string.Equals(r.CountryOrRegionGroup, regionGroup, StringComparison.OrdinalIgnoreCase));
        if (rule is null) return null; // OD-05: no tenant rule means allowed everywhere.
        if (!rule.Allowed) return WarrantyLineMissingInput.RegulatedJurisdiction;
        if (rule.LicensedObligorRequired && !hasLicensedObligor) return WarrantyLineMissingInput.RegulatedJurisdiction;
        return null;
    }
}

public sealed class WarrantyReferenceSettings(ITenantSettings settings)
{
    // Coupled to the factory registration in SerialisationModule: a direct registration exposes PlatformDbContext to
    // Wolverine beside SerialisationDbContext and prevents the invoice handler's transaction from being selected.
    public const string Key = "entitlements.warranty_reference";
    public static readonly WarrantyReferencePolicy Default = new([]);

    public async Task<WarrantyReferencePolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } json ? JsonSerializer.Deserialize<WarrantyReferencePolicy>(json, LedgerJson.Options) ?? Default : Default;

    public Task SetAsync(WarrantyReferencePolicy policy, CancellationToken cancellationToken)
    {
        var duplicate = policy.Jurisdictions.GroupBy(r => r.CountryOrRegionGroup, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new CommandRefusedException("jurisdiction_duplicate", $"Jurisdiction '{duplicate.Key}' appears more than once.");
        if (policy.Jurisdictions.Any(r => string.IsNullOrWhiteSpace(r.CountryOrRegionGroup))) throw new CommandRefusedException("jurisdiction_required", "Every service-contract rule names a country or region group.");
        return settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }
}
