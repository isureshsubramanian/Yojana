using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Refurbishments;

public sealed class RefurbishmentQueries(SerialisationDbContext db, EffectiveItemProfiles itemProfiles, ITenantContext tenant, IClock clock)
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
        var now = clock.UtcNow;
        var profile = await itemProfiles.GetAsync(workOrder.ItemId, now, cancellationToken);
        var returnedAt = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenant.TenantId
            && e.UnitId == workOrder.TargetUnitId && e.Kind == EpisodeKind.Ownership
            && e.PartyType != PartyType.Us && e.ValidTo != null)
            .MaxAsync(e => (DateTime?)e.ValidTo, cancellationToken);
        var sanitisationRequired = profile?.RequiresSanitisation == true && returnedAt is not null;
        var returned = returnedAt.GetValueOrDefault();
        var sanitisationPresent = !sanitisationRequired || await db.SanitisationRecords.AsNoTracking().AnyAsync(r =>
            r.TenantId == tenant.TenantId && r.UnitId == workOrder.TargetUnitId
            && r.SanitisedAt > returned && r.SanitisedAt <= now, cancellationToken);
        var passingTest = await db.TestRecords.AsNoTracking().AnyAsync(r => r.TenantId == tenant.TenantId
            && r.UnitId == workOrder.TargetUnitId && r.TestKind == RefurbishmentTestKind.KeyFunctions
            && r.Result == UnitTestResult.Pass && r.TestedAt >= workOrder.OpenedAt && r.TestedAt <= now, cancellationToken);
        var incompleteOperations = operations.Count(o => o.Status != RefurbishmentOperationStatus.Completed);
        var releaseChecks = new List<RefurbishmentReleaseCheck>
        {
            new("operations", "Operations signed", incompleteOperations == 0 ? "Every operation is complete." : $"{incompleteOperations} operation(s) remain open.", incompleteOperations == 0, true),
            new("sanitisation", "Sanitisation record", sanitisationRequired
                ? sanitisationPresent ? "A post-return sanitisation record is present." : "This item class needs a post-return sanitisation record."
                : "This item does not require a post-return sanitisation record.", sanitisationPresent, sanitisationRequired),
            new("key_function_test", "Key-function test", passingTest ? "A passing test was recorded after work opened." : "Record a passing key-function test after work opened.", passingTest, true),
            new("cycles", "Refurbishment cycles", $"Cycle {workOrder.CycleNumber} of {workOrder.MaxCycles}.", workOrder.CycleNumber <= workOrder.MaxCycles, true),
        };
        var targetSerial = await SerialAtAsync(workOrder.TargetUnitId, workOrder.OpenedAt, cancellationToken);
        var releasedSerial = workOrder.ReleasedUnitId is not { } releasedUnitId ? null
            : await SerialAtAsync(releasedUnitId, workOrder.ReleasedAt!.Value, cancellationToken);
        var currentLocationId = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenant.TenantId
            && e.UnitId == workOrder.TargetUnitId && e.Kind == EpisodeKind.Custody && e.ValidTo == null)
            .Select(e => e.LocationId).SingleOrDefaultAsync(cancellationToken);
        return new(workOrder.WorkOrderId, workOrder.WorkOrderNumber, workOrder.Kind, workOrder.TargetUnitId,
            workOrder.ItemId, workOrder.SourceRmaLineId, workOrder.Status, workOrder.CycleNumber,
            workOrder.IdentityPolicy, workOrder.MaxCycles, workOrder.OpenedAt, workOrder.OpenedBy,
            workOrder.ReleasedAt, workOrder.ReleasedBy, workOrder.ReleasedUnitId, workOrder.Grade,
            costs.Sum(c => c.Amount), costs.Select(c => c.Currency).Distinct().SingleOrDefault(), operations, parts, labour, costs,
            targetSerial, releasedSerial, currentLocationId, releaseChecks);
    }

    private Task<string?> SerialAtAsync(Guid unitId, DateTime effectiveAt, CancellationToken cancellationToken) =>
        db.Identifiers.AsNoTracking().Where(identifier => identifier.TenantId == tenant.TenantId
                && identifier.UnitId == unitId && identifier.Type == IdentifierType.InternalSerial
                && identifier.ValidFrom <= effectiveAt
                && (identifier.ValidTo == null || identifier.ValidTo > effectiveAt))
            .OrderByDescending(identifier => identifier.ValidFrom)
            .Select(identifier => identifier.RawValue)
            .FirstOrDefaultAsync(cancellationToken);
}
