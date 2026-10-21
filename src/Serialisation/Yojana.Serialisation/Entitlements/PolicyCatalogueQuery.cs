using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Read-only migration view of tenant families and every published immutable policy version.</summary>
public sealed class PolicyCatalogueQuery(SerialisationDbContext db, ITenantContext tenant) : IPolicyCatalogueQuery
{
    public async Task<PolicyCatalogue> ReadAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var families = await db.CoverageFamilies.AsNoTracking()
            .Where(family => family.TenantId == tenantId)
            .OrderBy(family => family.FamilyCode)
            .Select(family => new PolicyCatalogueFamily(family.FamilyCode, family.Name, family.Stacking,
                family.Active))
            .ToListAsync(cancellationToken);
        var rows = await db.PolicyVersions.AsNoTracking()
            .Where(policy => policy.TenantId == tenantId && policy.Status == PolicyVersionStatus.Published)
            .OrderBy(policy => policy.CoverageFamily).ThenBy(policy => policy.PolicyCode)
            .ThenBy(policy => policy.Version)
            .Select(policy => new
            {
                policy.CoverageFamily,
                policy.PolicyVersionId,
                policy.PolicyCode,
                policy.Version,
                policy.Name,
                policy.DurationValue,
                policy.DurationUnit,
                policy.Coverage,
            })
            .ToListAsync(cancellationToken);
        var policies = rows.Select(row =>
        {
            var coverage = JsonSerializer.Deserialize<CoverageTerms>(row.Coverage, LedgerJson.Options)
                ?? throw new InvalidOperationException($"Published policy '{row.PolicyCode}' has no coverage terms.");
            return new PolicyCataloguePolicy(row.CoverageFamily, row.PolicyVersionId, row.PolicyCode, row.Version,
                row.Name, new(row.DurationValue, row.DurationUnit, coverage.PartsPct, coverage.LabourPct,
                    coverage.TravelPct, coverage.ServiceMode, coverage.ResponseHours, coverage.CoverageHours));
        }).ToList();
        return new(families, policies);
    }
}
