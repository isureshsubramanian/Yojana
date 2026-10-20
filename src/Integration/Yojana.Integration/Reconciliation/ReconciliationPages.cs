using Microsoft.EntityFrameworkCore;

namespace Yojana.Integration.Reconciliation;

public sealed partial class ReconciliationService
{
    public async Task<ReconciliationResultPage<ReconciliationEntityDayView>> EntityDaysAsync(Guid runId, string? cursor, int take, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 100);
        var query = db.ReconciliationEntityDayResults.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.ReconciliationRunId == runId);
        if (cursor is not null) { var id = ResultCursor(cursor); query = query.Where(x => x.ReconciliationEntityDayResultId.CompareTo(id) > 0); }
        var rows = await query.OrderBy(x => x.ReconciliationEntityDayResultId).Take(take + 1).ToListAsync(ct);
        return new(rows.Take(take).Select(x => new ReconciliationEntityDayView(x.ReconciliationEntityDayResultId, x.Entity, x.BusinessDate,
            x.SourceCount, x.ModuleCount, x.SourceQuantity, x.ModuleQuantity, x.SourceDistinctSerials, x.ModuleDistinctSerials,
            x.SourceKeyFieldHash, x.ModuleKeyFieldHash, x.Status, x.RangedReadSupported, x.BackfillJobId, x.DeadLetterItemId)).ToList(),
            rows.Count > take ? rows[take - 1].ReconciliationEntityDayResultId.ToString("D") : null);
    }

    public async Task<ReconciliationResultPage<ReconciliationLocationView>> LocationsAsync(Guid runId, string? cursor, int take, bool differences, CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 100);
        var query = db.ReconciliationLocationResults.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.ReconciliationRunId == runId);
        if (differences) query = query.Where(x => x.SourceOnHand != x.ModuleUnitCount);
        if (cursor is not null) { var id = ResultCursor(cursor); query = query.Where(x => x.ReconciliationLocationResultId.CompareTo(id) > 0); }
        var rows = await query.OrderBy(x => x.ReconciliationLocationResultId).Take(take + 1).ToListAsync(ct);
        return new(rows.Take(take).Select(x => new ReconciliationLocationView(x.ReconciliationLocationResultId,
            x.ItemCode, x.ItemName, x.LocationCode, x.LocationName, x.SourceOnHand, x.ModuleUnitCount, x.Status)).ToList(),
            rows.Count > take ? rows[take - 1].ReconciliationLocationResultId.ToString("D") : null);
    }

    private static Guid ResultCursor(string cursor) => Guid.TryParse(cursor, out var id) ? id
        : throw new ReconciliationRuleException("cursor_invalid", "The result cursor is invalid.");
}
