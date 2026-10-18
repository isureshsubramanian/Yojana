using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Refurbishments;

public sealed class CertificateRefurbishmentReadPort(SerialisationDbContext db, ITenantContext tenant) : ICertificateRefurbishmentReadPort
{
    public async Task<CertificateRefurbishmentFact> ReadAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var work = await db.RefurbishmentWorkOrders.AsNoTracking().Where(row => row.TenantId == tenantId
                && (row.TargetUnitId == unitId || row.ReleasedUnitId == unitId) && row.ReleasedAt != null)
            .OrderByDescending(row => row.ReleasedAt).Select(row => new { row.CycleNumber, row.WorkOrderNumber, row.WorkOrderId })
            .FirstOrDefaultAsync(cancellationToken);
        var life = await db.LifeEpisodes.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId)
            .OrderByDescending(row => row.StartedAt).Select(row => new ReleaseLifeEpisodeSnapshot(row.Kind, row.StartedAt, row.EndedAt, row.CycleNumber))
            .FirstOrDefaultAsync(cancellationToken);
        var refurbisher = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenantId && row.UnitId == unitId && row.RefurbisherPartyRef != null)
            .OrderByDescending(row => row.StartDate).Select(row => row.RefurbisherPartyRef).FirstOrDefaultAsync(cancellationToken);
        return new CertificateRefurbishmentFact(work?.CycleNumber, work?.WorkOrderNumber, refurbisher, life);
    }
}
