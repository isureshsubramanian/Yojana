using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Documents;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Read models of the Warranty policy editor: the policy list, a policy's versions with their live entitlement counts, one version in full.</summary>
public sealed class PolicyQueries(SerialisationDbContext db, PolicyStore store, ClaimWaitingPeriodSettings waitingPeriods, ITenantContext tenant)
{
    public async Task<IReadOnlyList<PolicySummary>> ListAsync(string? family, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var versions = await db.PolicyVersions.AsNoTracking().Where(v => v.TenantId == tenantId && (family == null || v.CoverageFamily == family))
            .Select(v => new { v.PolicyVersionId, v.PolicyCode, v.Name, v.CoverageFamily, v.Version, v.Status, v.AppliesToContractsFrom }).ToListAsync(cancellationToken);
        var live = await LiveCountsAsync(cancellationToken);
        return versions.GroupBy(v => v.PolicyCode).Select(g =>
        {
            var newest = g.MaxBy(v => v.Version)!;
            var inForce = g.Where(v => v.Status == PolicyVersionStatus.Published).MaxBy(v => v.Version);
            var draft = g.FirstOrDefault(v => v.Status == PolicyVersionStatus.Draft);
            return new PolicySummary(g.Key, newest.Name, newest.CoverageFamily, g.Count(), inForce?.Version, inForce?.AppliesToContractsFrom, draft?.Version, g.Sum(v => live.GetValueOrDefault(v.PolicyVersionId)));
        }).OrderBy(p => p.PolicyCode, StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<PolicyVersionRow>> VersionsAsync(string policyCode, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var live = await LiveCountsAsync(cancellationToken);
        return (await db.PolicyVersions.AsNoTracking().Where(v => v.TenantId == tenantId && v.PolicyCode == policyCode).OrderByDescending(v => v.Version).ToListAsync(cancellationToken))
            .Select(v => new PolicyVersionRow(v.PolicyVersionId, v.Version, v.AppliesToContractsFrom, v.Status, v.RuleVersion, live.GetValueOrDefault(v.PolicyVersionId), v.CreatedAt, v.CreatedBy, v.PublishedAt, v.PublishedBy)).ToList();
    }

    public async Task<PolicyVersionDetail?> DetailAsync(Guid policyVersionId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var row = await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenantId && v.PolicyVersionId == policyVersionId, cancellationToken);
        if (row is null) return null;
        var terms = await store.ReadTermsAsync(row, cancellationToken, includeItems: true);
        var families = await db.CoverageFamilies.AsNoTracking().Where(f => f.TenantId == tenantId).Select(f => f.FamilyCode).ToListAsync(cancellationToken);
        var defaults = await waitingPeriods.GetAsync(families, cancellationToken);
        return new PolicyVersionDetail(row.PolicyVersionId, row.PolicyCode, row.Version, row.Name, row.CoverageFamily, row.EntitlementType, row.AppliesToContractsFrom, row.Status, row.RuleVersion, terms, await store.ValidateAsync(row, terms, cancellationToken), DocumentVersion.Encode(row.RowVersion), defaults.DaysByCoverageFamily.GetValueOrDefault(row.CoverageFamily));
    }

    public async Task<IReadOnlyList<CoverageFamilyRow>> FamiliesAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return (await db.CoverageFamilies.AsNoTracking().Where(f => f.TenantId == tenantId).OrderBy(f => f.FamilyCode).ToListAsync(cancellationToken))
            .Select(f => new CoverageFamilyRow(f.FamilyCode, f.Name, f.Stacking, f.AccountingTag, f.Active, DocumentVersion.Encode(f.RowVersion))).ToList();
    }

    public async Task<IReadOnlyList<ReplacementPolicyRow>> ReplacementPoliciesAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return (await db.ReplacementPolicies.AsNoTracking().Where(r => r.TenantId == tenantId).OrderBy(r => r.Code).ToListAsync(cancellationToken))
            .Select(r => new ReplacementPolicyRow(r.ReplacementPolicyId, r.Code, r.Rule, r.MinDays, r.ExtensionMonths, r.Description, DocumentVersion.Encode(r.RowVersion))).ToList();
    }

    // Live entitlements per policy version, one grouped read over the (tenant, policy_version_id) index.
    private async Task<Dictionary<Guid, long>> LiveCountsAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.Status != EntitlementStatus.Closed)
            .GroupBy(e => e.PolicyVersionId).Select(g => new { g.Key, Count = g.LongCount() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
    }
}
