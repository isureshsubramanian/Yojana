using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Published policy catalogue exposed through a read-only module contract.</summary>
public sealed class PublishedWarrantyPolicyPort(SerialisationDbContext db, ITenantContext tenant)
    : IPublishedWarrantyPolicyPort
{
    public async Task<IReadOnlyList<PublishedWarrantyPolicy>> ListAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = await (
            from policy in db.PolicyVersions.AsNoTracking()
            join family in db.CoverageFamilies.AsNoTracking()
                on new { policy.TenantId, FamilyCode = policy.CoverageFamily }
                equals new { family.TenantId, family.FamilyCode }
            where policy.TenantId == tenantId && policy.Status == PolicyVersionStatus.Published
            select new
            {
                policy.PolicyCode,
                policy.Name,
                policy.CoverageFamily,
                FamilyName = family.Name,
                policy.EntitlementType,
                policy.Version,
            }).ToListAsync(cancellationToken);

        return rows.GroupBy(row => row.PolicyCode, StringComparer.Ordinal)
            .Select(group => group.MaxBy(row => row.Version)!)
            .OrderBy(row => row.PolicyCode, StringComparer.Ordinal)
            .Select(row => new PublishedWarrantyPolicy(row.PolicyCode, row.Name, row.CoverageFamily,
                row.FamilyName, row.EntitlementType))
            .ToList();
    }
}
