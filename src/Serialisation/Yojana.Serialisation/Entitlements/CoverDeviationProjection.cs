using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

public interface IUnitCoverDeviationProjection
{
    Task RebuildAsync(Guid unitId, CancellationToken cancellationToken);
    Task RebuildManyAsync(IReadOnlyList<Guid> unitIds, CancellationToken cancellationToken);
}

public sealed class CoverDeviationProjection(SerialisationDbContext db, ITenantContext tenant, IClock clock,
    CoverHistory history, IItemQueryPort items) : IUnitCoverDeviationProjection
{
    public Task RebuildAsync(Guid unitId, CancellationToken cancellationToken) => RebuildManyAsync([unitId], cancellationToken);

    public async Task RebuildManyAsync(IReadOnlyList<Guid> unitIds, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Cover rebuilding requires the entitlement transaction.");
        foreach (var batch in unitIds.Distinct().Chunk(500))
        {
            var data = await history.LoadManyAsync(batch, DateOnly.FromDateTime(clock.UtcNow), cancellationToken);
            var parents = await db.CoverDeviations.Where(r => r.TenantId == tenant.TenantId && batch.Contains(r.UnitId)).ToDictionaryAsync(r => r.UnitId, cancellationToken);
            var current = await db.Current.AsNoTracking().Where(r => r.TenantId == tenant.TenantId && batch.Contains(r.UnitId)).ToDictionaryAsync(r => r.UnitId, cancellationToken);
            var profiles = await items.GetManyAsync(current.Values.Select(r => r.ItemId).Distinct().ToList(), cancellationToken);
            await db.CoverDeviationChanges.Where(r => r.TenantId == tenant.TenantId && batch.Contains(r.UnitId)).ExecuteDeleteAsync(cancellationToken);
            await db.CoverDeviationValues.Where(r => r.TenantId == tenant.TenantId && batch.Contains(r.UnitId)).ExecuteDeleteAsync(cancellationToken);
            foreach (var unitId in batch)
            {
                var snapshot = data[unitId];
                parents.TryGetValue(unitId, out var row);
                if (snapshot.Base is null || snapshot.SaleDate is null)
                {
                    if (row is not null) db.CoverDeviations.Remove(row);
                    continue;
                }
                var changes = snapshot.Changes.Where(c => c.Counted).ToList();
                if (row is null) { row = new() { TenantId = tenant.TenantId, UnitId = unitId }; db.CoverDeviations.Add(row); }
                row.SaleDate = snapshot.SaleDate.Value;
                row.SaleDocumentType = snapshot.Base.SourceDocumentType;
                row.SaleDocumentId = snapshot.Base.SourceDocumentId;
                row.CustomerPartyRef = snapshot.SaleCustomerPartyRef;
                row.ItemId = current[unitId].ItemId;
                row.ItemFamily = profiles.GetValueOrDefault(row.ItemId)?.ItemClass;
                row.Changed = changes.Count > 0;
                row.ChangeCount = changes.Count;
                row.Added = changes.Any(c => c.Kind == "added");
                row.Upgraded = changes.Any(c => c.Kind == "upgraded");
                row.CoverChanged = changes.Any(c => c.Kind == "changed");
                row.Amended = changes.Any(c => c.Kind == "amended");
                row.ClosedEarly = changes.Any(c => c.Kind == "closed_early");
                row.Suspended = changes.Any(c => c.Kind == "suspended");
                row.LastChangeAt = changes.Select(c => c.Fact.At).DefaultIfEmpty(snapshot.Facts.First(f => f.EventId == snapshot.Base.CreatedEventId).At).Max();
                foreach (var c in changes) db.CoverDeviationChanges.Add(new()
                {
                    TenantId = tenant.TenantId, UnitId = unitId, EventId = c.Fact.EventId, EntitlementId = c.Entitlement.EntitlementId,
                    Kind = c.Kind, EventTime = c.Fact.At, BusinessDate = c.Date, MonthsAfterSale = c.Months, Amount = c.Amount, Currency = c.Currency,
                });
                foreach (var group in changes.Where(c => c.Currency is not null && c.Amount is not null).GroupBy(c => c.Currency!))
                    db.CoverDeviationValues.Add(new() { TenantId = tenant.TenantId, UnitId = unitId, Currency = group.Key, Amount = group.Sum(c => c.Amount!.Value) });
            }
            await db.SaveChangesAsync(cancellationToken);
            // ExecuteDelete bypasses tracking. Detach replaced children so another append in this transaction can
            // rebuild the same keys (for example a multi-line invoice); the report parents remain tracked.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is UnitCoverDeviationChange c && batch.Contains(c.UnitId)
                         || e.Entity is UnitCoverDeviationValue v && batch.Contains(v.UnitId)).ToList()) entry.State = EntityState.Detached;
        }
    }

}

// TIGHT COUPLING (entitlement report consistency)
// WHAT: UnitEventWriter calls this observer in the same SerialisationDbContext transaction as all entitlement facts,
// including document corrections that bypass EntitlementLedger. This observer saves the facts before rebuilding.
// WHY: an asynchronous subscriber cannot provide the required read-your-writes report. WHAT BREAKS IF SEPARATED:
// committed changes can be absent from the report, or rolled-back changes can remain. Keep the writer-side comment
// in step. Only the IUnitCoverDeviationProjection contract is shared with the projection implementation.
public sealed class CoverDeviationAppendObserver(SerialisationDbContext db, IUnitCoverDeviationProjection projection) : IUnitEventAppendObserver
{
    public async Task AppendedAsync(IReadOnlyList<UnitEvent> written, CancellationToken cancellationToken)
    {
        var ids = written.Where(e => e.EventType.StartsWith("entitlement.", StringComparison.Ordinal)
            || e.EventType is "unit.item_corrected" or "unit.merged").Select(e => e.UnitId).Distinct().Order().ToList();
        if (ids.Count == 0) return;
        await db.SaveChangesAsync(cancellationToken);
        await projection.RebuildManyAsync(ids, cancellationToken);
    }
}

/// <summary>One bounded batch per scheduled run; cursor and rebuilt rows commit together.</summary>
public sealed class CoverDeviationBackfill(SerialisationDbContext db, ITenantContext tenant, IUnitCoverDeviationProjection projection)
{
    public async Task<int> BatchAsync(int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 100);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialise resumptions for a tenant; ordinary entitlement commands retain their existing rowversion guard.
        var resource = $"cover-deviation-backfill:{tenant.TenantId}";
        await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_getapplock @Resource={resource}, @LockMode=N'Exclusive', @LockOwner=N'Transaction', @LockTimeout=30000", ct);
        var checkpoint = await db.CoverDeviationBackfillCheckpoints.SingleOrDefaultAsync(r => r.TenantId == tenant.TenantId, ct);
        if (checkpoint is null) { checkpoint = new() { TenantId = tenant.TenantId }; db.CoverDeviationBackfillCheckpoints.Add(checkpoint); }
        if (checkpoint.Complete) return 0;
        var cursor = checkpoint.CursorUnitId;
        var ids = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && (cursor == null || e.UnitId.CompareTo(cursor.Value) > 0))
            .Select(e => e.UnitId).Distinct().OrderBy(id => id).Take(take).ToListAsync(ct);
        foreach (var id in ids)
        {
            // Hold the unit row against entitlement writers until this snapshot commits. A writer already in flight
            // either precedes this read or rebuilds again after it; stale backfill must never overwrite newer cover.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT unit_id FROM serialisation.unit_current WITH (UPDLOCK, HOLDLOCK) WHERE tenant_id={tenant.TenantId.Value} AND unit_id={id}", ct);
            await projection.RebuildAsync(id, ct);
            checkpoint.CursorUnitId = id;
            checkpoint.UnitsProcessed++;
        }
        checkpoint.Complete = ids.Count < take;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ids.Count;
    }
}

[DisallowConcurrentExecution]
public sealed class CoverDeviationBackfillJob(ITenantContextSetter setter, CoverDeviationBackfill backfill) : TenantJob(setter)
{
    public const string JobName = "serialisation.cover_deviation_backfill";
    public const string Cron = "0/30 * * * * ?";
    protected override async Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await backfill.BatchAsync(25, cancellationToken);
}
