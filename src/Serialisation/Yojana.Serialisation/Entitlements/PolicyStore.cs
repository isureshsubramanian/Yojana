using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Maps <see cref="PolicyTerms"/> to and from the version row's columns and reads what the validator needs.</summary>
public sealed class PolicyStore(SerialisationDbContext db, ITenantContext tenant, ITenantCurrency currency,
    IJurisdictionRegionPort regions, PolicyCoverItems items)
{
    /// <summary>
    /// The version of the duration arithmetic (P2-T04). An entitlement records the policy version and this number as
    /// <c>computed_by_rule_version</c>, so a later fix to the arithmetic never silently changes how an old end date was read.
    /// </summary>
    public const int DurationRuleImplementation = 1;

    public static string RuleVersionFor(int version) => $"v{version}.d{DurationRuleImplementation}";

    public async Task WriteTermsAsync(WarrantyPolicyVersion row, PolicyTerms terms, CancellationToken cancellationToken)
    {
        // Policy region tokens are coupled to tenant region-group codes through this explicit port. Saving an unknown
        // token would otherwise look valid here and silently match no country in PolicyResolver.
        foreach (var scopeCode in terms.RegionScope.Concat(terms.Refurbished.Select(item => item.Region))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!await regions.IsKnownScopeAsync(scopeCode, cancellationToken))
                throw new CommandRefusedException("policy_region_unknown",
                    $"Region code '{scopeCode}' is neither WORLD, an ISO 3166-1 country nor a tenant region group.",
                    $"define '{scopeCode}' in Settings → Jurisdictions, or use an ISO 3166-1 alpha-2 country code");
        }
        var tenantId = tenant.TenantId;
        row.DurationValue = terms.Duration.Value;
        row.DurationUnit = terms.Duration.Unit;
        row.EndOfMonthRule = terms.Duration.EndOfMonth;
        row.EndInclusive = terms.Duration.EndInclusive;
        row.DurationBasis = terms.DurationBasis;
        row.UsageLimits = Json(terms.UsageLimits);
        row.StartTrigger = terms.Start.Trigger;
        row.FallbackChain = Json(terms.Start.Fallbacks);
        row.StartCapDays = terms.Start.CapDays;
        row.Coverage = Json(terms.Coverage);
        // Legacy exclusions are read-only; structured child rows own all new wording.
        if (terms.Items is not null) await items.WriteAsync(row, terms.Items, cancellationToken);
        row.RegionScope = Json(terms.RegionScope);
        row.Transferable = terms.Transferable;
        row.SettlementFormula = terms.SettlementFormula;
        row.ReplacementPolicyId = terms.ReplacementPolicyCode is { } code
            ? await db.ReplacementPolicies.Where(r => r.TenantId == tenantId && r.Code == code).Select(r => (Guid?)r.ReplacementPolicyId).FirstOrDefaultAsync(cancellationToken)
            : null;
        row.RefurbishedPolicies = Json(terms.Refurbished);
        row.DoaWindowDays = terms.DoaWindowDays;
        row.AssuranceOrService = terms.AssuranceOrService;
        row.ReferenceTimeZoneRule = terms.ReferenceTimeZoneRule;
        row.ServiceContractRegulated = terms.ServiceContractRegulated;
        row.Eligibility = terms.Eligibility is null ? null : Json(terms.Eligibility);
        row.AmendmentValue = terms.AmendmentValue is null ? null : Json(terms.AmendmentValue);
    }

    public async Task<PolicyTerms> ReadTermsAsync(WarrantyPolicyVersion row, CancellationToken cancellationToken, bool includeItems = false)
    {
        var tenantId = tenant.TenantId;
        var replacement = row.ReplacementPolicyId is { } id
            ? await db.ReplacementPolicies.AsNoTracking().Where(r => r.TenantId == tenantId && r.ReplacementPolicyId == id).Select(r => r.Code).FirstOrDefaultAsync(cancellationToken)
            : null;
        return new PolicyTerms(
            new DurationRule(row.DurationValue, row.DurationUnit, row.EndOfMonthRule, row.EndInclusive), row.DurationBasis, Read<List<UsageLimitTerm>>(row.UsageLimits),
            new StartRule(row.StartTrigger, Read<List<StartFallback>>(row.FallbackChain), row.StartCapDays), Read<CoverageTerms>(row.Coverage), Read<List<string>>(row.Exclusions), Read<List<string>>(row.RegionScope),
            row.Transferable, row.SettlementFormula, replacement, Read<List<RefurbishedTerm>>(row.RefurbishedPolicies), row.DoaWindowDays, row.AssuranceOrService, row.ReferenceTimeZoneRule, row.ServiceContractRegulated,
            row.Eligibility is null ? null : Read<EligibilityRule>(row.Eligibility),
            row.AmendmentValue is null ? null : Read<AmendmentValueTerm>(row.AmendmentValue),
            includeItems ? await items.ReadAsync(row.PolicyVersionId, cancellationToken) : null);
    }

    public async Task<PolicyValidation> ValidateAsync(WarrantyPolicyVersion row, PolicyTerms terms, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var families = await db.CoverageFamilies.AsNoTracking().Where(f => f.TenantId == tenantId).ToDictionaryAsync(f => f.FamilyCode, f => (f.AccountingTag, f.Active), cancellationToken);
        var replacements = (await db.ReplacementPolicies.AsNoTracking().Where(r => r.TenantId == tenantId).Select(r => r.Code).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        // the newest published version below this one: a new version must apply from a later contract date
        var previous = await db.PolicyVersions.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.PolicyCode == row.PolicyCode && v.Version < row.Version && v.Status != PolicyVersionStatus.Draft)
            .OrderByDescending(v => v.Version).Select(v => (DateOnly?)v.AppliesToContractsFrom).FirstOrDefaultAsync(cancellationToken);
        // ITenantCurrency is registered through a factory in PlatformSetup so this store keeps PlatformDbContext out of the
        // generated graph of every policy and entitlement handler that takes it (see the comment there).
        var tenantCurrency = terms.AmendmentValue is null ? null : await currency.GetAsync(cancellationToken);
        return PolicyValidator.Validate(terms, new PolicyValidationContext(row.CoverageFamily, families, replacements, row.AppliesToContractsFrom, previous, tenantCurrency));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, LedgerJson.Options);

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, LedgerJson.Options)!;
}
