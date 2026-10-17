using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Refurbishments;

public sealed class RefurbishmentQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<RefurbishmentWorkOrderRow?> DetailAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await db.RefurbishmentWorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.TenantId == tenant.TenantId && w.WorkOrderId == workOrderId, cancellationToken);
        return workOrder is null ? null : await RowAsync(workOrder, cancellationToken);
    }

    public async Task<RefurbishmentWorkOrderPage> PageAsync(string? status, string? kind, Guid? unitId, int skip, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var query = db.RefurbishmentWorkOrders.AsNoTracking().Where(w => w.TenantId == tenant.TenantId);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(w => w.Status == status);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(w => w.Kind == kind);
        if (unitId is { } unit) query = query.Where(w => w.TargetUnitId == unit);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(w => w.OpenedAt).Skip(skip).Take(take).ToListAsync(cancellationToken);
        var result = new List<RefurbishmentWorkOrderRow>(rows.Count);
        foreach (var row in rows) result.Add(await RowAsync(row, cancellationToken));
        return new(result, total, skip, take);
    }

    private async Task<RefurbishmentWorkOrderRow> RowAsync(Data.Entities.RefurbishmentWorkOrder workOrder, CancellationToken cancellationToken)
    {
        // Project entities first, then construct positional read records in memory; EF cannot translate filtering or
        // ordering over positional records constructed inside its query pipeline.
        var operationEntities = await db.RefurbishmentOperations.AsNoTracking().Where(o => o.TenantId == tenant.TenantId && o.WorkOrderId == workOrder.WorkOrderId)
            .OrderBy(o => o.Sequence).ToListAsync(cancellationToken);
        var operations = operationEntities.Select(o => new RefurbishmentOperationRow(o.OperationId, o.Sequence, o.Description,
            o.TechnicianRef, o.PlannedHours, o.ActualHours, o.Status, o.SignedBy, o.SignedAt)).ToList();
        var partEntities = await db.RefurbishmentParts.AsNoTracking().Where(p => p.TenantId == tenant.TenantId && p.WorkOrderId == workOrder.WorkOrderId)
            .OrderBy(p => p.RecordedAt).ToListAsync(cancellationToken);
        var parts = partEntities.Select(p => new RefurbishmentPartRow(p.PartId, p.Action, p.ChildUnitId,
            p.ChildItemId, p.Slot, p.Destination, p.VendorPartyRef, p.AssemblyLinkId, p.UnitCost, p.Currency, p.RecordedAt)).ToList();
        var labourEntities = await db.RefurbishmentLabour.AsNoTracking().Where(l => l.TenantId == tenant.TenantId && l.WorkOrderId == workOrder.WorkOrderId)
            .OrderBy(l => l.RecordedAt).ToListAsync(cancellationToken);
        var labour = labourEntities.Select(l => new RefurbishmentLabourRow(l.LabourId, l.OperationId,
            l.Hours, l.HourlyRate, l.Amount, l.Currency, l.TechnicianRef, l.RecordedAt)).ToList();
        var costEntities = await db.RefurbishmentCosts.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && c.WorkOrderId == workOrder.WorkOrderId)
            .OrderBy(c => c.PostedAt).ToListAsync(cancellationToken);
        var costs = costEntities.Select(c => new RefurbishmentCostRow(c.CostId, c.Kind, c.Amount,
            c.Currency, c.Description, c.PostedAt, c.EventId)).ToList();
        return new(workOrder.WorkOrderId, workOrder.WorkOrderNumber, workOrder.Kind, workOrder.TargetUnitId,
            workOrder.ItemId, workOrder.SourceRmaLineId, workOrder.Status, workOrder.CycleNumber,
            workOrder.IdentityPolicy, workOrder.MaxCycles, workOrder.OpenedAt, workOrder.OpenedBy,
            workOrder.ReleasedAt, workOrder.ReleasedBy, workOrder.ReleasedUnitId, workOrder.Grade,
            costs.Sum(c => c.Amount), costs.Select(c => c.Currency).Distinct().SingleOrDefault(), operations, parts, labour, costs);
    }
}
