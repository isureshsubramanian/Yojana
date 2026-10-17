using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Historical entitlement and as-built query used by supplier recovery; claims see only its narrow result.</summary>
public sealed class RecoveryEntitlementQueries(SerialisationDbContext db, ITenantContext tenant) : IEntitlementRecoveryQuery
{
    public async Task<IReadOnlyList<InboundRecoveryCoverage>> FindAsync(Guid rootUnitId, Guid lineUnitId, DateOnly claimDate, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var units = new HashSet<Guid> { lineUnitId };
        if (lineUnitId == rootUnitId)
        {
            var frontier = new List<Guid> { rootUnitId };
            var from = claimDate.ToDateTime(TimeOnly.MinValue);
            var until = from.AddDays(1);
            for (var depth = 0; depth < InboundRecoveryRules.MaximumAssemblyDepth && frontier.Count > 0; depth++)
            {
                var children = await db.AssemblyLinks.AsNoTracking()
                    .Where(l => l.TenantId == tenantId && frontier.Contains(l.ParentUnitId) && l.ChildUnitId != null
                        && l.InstalledAt < until && (l.RemovedAt == null || l.RemovedAt >= from))
                    .Select(l => l.ChildUnitId!.Value).Distinct().ToListAsync(cancellationToken);
                frontier = children.Where(units.Add).ToList();
            }
        }

        var candidates = await db.Entitlements.AsNoTracking()
            .Where(e => e.TenantId == tenantId && units.Contains(e.UnitId) && e.Direction == EntitlementDirection.Inbound && e.PayerPartyRef != null)
            .OrderBy(e => e.UnitId).ThenBy(e => e.EntitlementId).ToListAsync(cancellationToken);
        return candidates.Where(e => RecoveryEligibility.IsLive(e, claimDate))
            .Select(e => new InboundRecoveryCoverage(e.EntitlementId, e.UnitId, e.PayerPartyRef!.Value)).ToList();
    }
}
