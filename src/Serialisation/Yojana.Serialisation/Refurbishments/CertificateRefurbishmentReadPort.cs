using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Refurbishments;

public sealed class CertificateRefurbishmentReadPort(SerialisationDbContext db, ITenantContext tenant) : ICertificateRefurbishmentReadPort
{
    public async Task<CertificateRefurbishmentFact> ReadAsync(Guid unitId, CancellationToken cancellationToken) =>
        (await ReadManyAsync([unitId], cancellationToken))[unitId];

    /// <summary>One query per source table for the whole list; the single-unit read goes through here too.</summary>
    public async Task<IReadOnlyDictionary<Guid, CertificateRefurbishmentFact>> ReadManyAsync(IReadOnlyCollection<Guid> unitIds,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var ids = unitIds.Distinct().ToList();
        var work = await db.RefurbishmentWorkOrders.AsNoTracking().Where(row => row.TenantId == tenantId
                && (ids.Contains(row.TargetUnitId) || (row.ReleasedUnitId != null && ids.Contains(row.ReleasedUnitId.Value)))
                && row.ReleasedAt != null)
            .OrderByDescending(row => row.ReleasedAt)
            .Select(row => new { row.TargetUnitId, row.ReleasedUnitId, row.CycleNumber, row.WorkOrderNumber })
            .ToListAsync(cancellationToken);
        var life = (await db.LifeEpisodes.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId))
                .OrderByDescending(row => row.StartedAt)
                .Select(row => new { row.UnitId, Snapshot = new ReleaseLifeEpisodeSnapshot(row.Kind, row.StartedAt, row.EndedAt, row.CycleNumber) })
                .ToListAsync(cancellationToken))
            .GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.First().Snapshot);
        var refurbishers = (await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenantId && ids.Contains(row.UnitId)
                    && row.RefurbisherPartyRef != null)
                .OrderByDescending(row => row.StartDate).Select(row => new { row.UnitId, row.RefurbisherPartyRef })
                .ToListAsync(cancellationToken))
            .GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.First().RefurbisherPartyRef);

        var facts = new Dictionary<Guid, CertificateRefurbishmentFact>(ids.Count);
        foreach (var unitId in ids)
        {
            var latest = work.FirstOrDefault(row => row.TargetUnitId == unitId || row.ReleasedUnitId == unitId);
            facts[unitId] = new CertificateRefurbishmentFact(latest?.CycleNumber, latest?.WorkOrderNumber,
                refurbishers.GetValueOrDefault(unitId), life.GetValueOrDefault(unitId));
        }
        return facts;
    }
}
