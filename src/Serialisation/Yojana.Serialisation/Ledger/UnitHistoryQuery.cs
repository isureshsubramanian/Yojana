using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Ledger;

/// <summary>
/// "Full history of serial X" over the live ledger and the archive, in the ADR's order: business time, then
/// recorded time, then seq (case 116). Pages server-side; newest first for the screen, oldest first for a replay.
/// </summary>
public sealed class UnitHistoryQuery(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<IReadOnlyList<UnitEventRow>> OldestFirstAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId).ToListAsync(cancellationToken);
        var archived = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId).ToListAsync(cancellationToken);
        return live.Cast<UnitEventRow>().Concat(archived).OrderBy(e => e.EventTime).ThenBy(e => e.RecordedTime).ThenBy(e => e.Seq).ToList();
    }

    public async Task<IReadOnlyList<UnitEventRow>> NewestFirstPageAsync(Guid unitId, int skip, int take, CancellationToken cancellationToken)
    {
        var all = await OldestFirstAsync(unitId, cancellationToken);
        return all.AsEnumerable().Reverse().Skip(skip).Take(take).ToList();
    }
}
