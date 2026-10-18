using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Entitlements;

public sealed class CertificateEntitlementReadPort(SerialisationDbContext db, ITenantContext tenant) : ICertificateEntitlementReadPort
{
    public async Task<IReadOnlyList<CertificateEntitlementFact>> LiveAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var all = await (from entitlement in db.Entitlements.AsNoTracking()
                      join policy in db.PolicyVersions.AsNoTracking()
                          on new { entitlement.TenantId, entitlement.PolicyVersionId } equals new { policy.TenantId, policy.PolicyVersionId }
                      where entitlement.TenantId == tenantId && entitlement.UnitId == unitId
                          && entitlement.Direction == EntitlementDirection.Outbound
                      orderby entitlement.CoverageFamily, entitlement.StartDate, entitlement.EntitlementId
                      select new CertificateEntitlementFact(entitlement.EntitlementId, entitlement.CoverageFamily,
                          entitlement.Type, entitlement.Status, entitlement.StartBasis, entitlement.StartDate,
                          entitlement.EndDate, policy.PolicyCode, policy.Version, entitlement.DurationValue,
                          entitlement.DurationUnit, entitlement.Coverage, entitlement.SupersedesEntitlementId,
                          entitlement.RootEntitlementId, new List<Guid>())).ToListAsync(cancellationToken);
        var byId = all.ToDictionary(row => row.EntitlementId);
        return all.Where(row => row.Status != EntitlementStatus.Closed).Select(row => row with
        {
            SupersededLineage = Lineage(row, byId),
        }).ToList();
    }

    private static List<Guid> Lineage(CertificateEntitlementFact row,
        Dictionary<Guid, CertificateEntitlementFact> all)
    {
        var result = new List<Guid>();
        var previous = row.SupersedesEntitlementId;
        while (previous is { } id && all.TryGetValue(id, out var found) && result.Count < 100)
        {
            result.Add(id);
            previous = found.SupersedesEntitlementId;
        }
        return result;
    }
}
