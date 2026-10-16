using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Maps <see cref="PolicyTerms"/> to and from the version row's columns and reads what the validator needs.</summary>
public sealed class PolicyStore(SerialisationDbContext db, ITenantContext tenant)
{
    /// <summary>
    /// The version of the duration arithmetic (P2-T04). An entitlement records the policy version and this number as
    /// <c>computed_by_rule_version</c>, so a later fix to the arithmetic never silently changes how an old end date was read.
    /// </summary>
    public const int DurationRuleImplementation = 1;

    public static string RuleVersionFor(int version) => $"v{version}.d{DurationRuleImplementation}";

    public async Task WriteTermsAsync(WarrantyPolicyVersion row, PolicyTerms terms, CancellationToken cancellationToken)
    {
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
        row.Exclusions = Json(terms.Exclusions);
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
    }

    public async Task<PolicyTerms> ReadTermsAsync(WarrantyPolicyVersion row, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var replacement = row.ReplacementPolicyId is { } id
            ? await db.ReplacementPolicies.AsNoTracking().Where(r => r.TenantId == tenantId && r.ReplacementPolicyId == id).Select(r => r.Code).FirstOrDefaultAsync(cancellationToken)
            : null;
        return new PolicyTerms(
            new DurationRule(row.DurationValue, row.DurationUnit, row.EndOfMonthRule, row.EndInclusive), row.DurationBasis, Read<List<UsageLimitTerm>>(row.UsageLimits),
            new StartRule(row.StartTrigger, Read<List<StartFallback>>(row.FallbackChain), row.StartCapDays), Read<CoverageTerms>(row.Coverage), Read<List<string>>(row.Exclusions), Read<List<string>>(row.RegionScope),
            row.Transferable, row.SettlementFormula, replacement, Read<List<RefurbishedTerm>>(row.RefurbishedPolicies), row.DoaWindowDays, row.AssuranceOrService, row.ReferenceTimeZoneRule, row.ServiceContractRegulated,
            row.Eligibility is null ? null : Read<EligibilityRule>(row.Eligibility));
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
        return PolicyValidator.Validate(terms, new PolicyValidationContext(row.CoverageFamily, families, replacements, row.AppliesToContractsFrom, previous));
    }

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, LedgerJson.Options);

    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, LedgerJson.Options)!;
}
