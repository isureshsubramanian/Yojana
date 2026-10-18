using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Condition;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Refurbishments;

public sealed class RefurbishmentService(
    SerialisationDbContext db,
    IUnitEventWriter eventWriter,
    EpisodeService episodes,
    ILifeEpisodeWriter lifeEpisodes,
    RefurbishmentPolicySettings policySettings,
    EffectiveItemProfiles itemProfiles,
    IdentifierService identifiers,
    SerialRangeService serialRanges,
    BulkIngestion bulk,
    SerialReuseSettings serialReuse,
    IEnumerable<IReleaseGuard> releaseGuards,
    EntitlementLedger entitlementLedger,
    CorrectionLedger corrections,
    DocumentNumbers numbers,
    ITenantContext tenant,
    IClock clock)
{
    private const string Source = "refurbishment.command";

    public async Task<OpenRefurbishmentResult> OpenAsync(OpenRefurbishment command, CancellationToken cancellationToken) =>
        await OpenCoreAsync(command, null, null, cancellationToken);

    public async Task<OpenReworkResult> OpenReworkAsync(OpenRework command, CancellationToken cancellationToken)
    {
        if (command.TargetUnitIds.Count == 0 || command.TargetUnitIds.Count != command.TargetUnitIds.Distinct().Count())
            throw new CommandRefusedException("rework_selection_invalid", "Rework requires one or more distinct units.");
        if (!ReworkSelectionKind.All.Contains(command.SelectionKind))
            throw new CommandRefusedException("rework_selection_kind_unknown", "Rework selection must be a production order, item or serial list.");
        var selectionReference = Required(command.SelectionReference, "selection_reference", 200);
        var results = new List<OpenRefurbishmentResult>(command.TargetUnitIds.Count);
        foreach (var unitId in command.TargetUnitIds)
        {
            if (await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenant.TenantId && e.UnitId == unitId
                && (e.EventType == UnitEventType.Shipped || e.EventType == UnitEventType.Delivered), cancellationToken))
                throw new CommandRefusedException("rework_after_sale", $"Unit {unitId} has already shipped; pre-sale rework cannot open for it.");
            results.Add(await OpenCoreAsync(new OpenRefurbishment(command.CommandId, unitId, RefurbishmentKind.Rework,
                command.OpenedAt, command.ActorRef, command.LegalEntityId, command.RepairLocationId,
                SourceDocument: command.SourceDocument), command.SelectionKind, selectionReference, cancellationToken));
        }

        return new(results, results.All(result => result.Replayed));
    }

    private async Task<OpenRefurbishmentResult> OpenCoreAsync(OpenRefurbishment command, string? selectionKind,
        string? selectionReference, CancellationToken cancellationToken)
    {
        if (!RefurbishmentKind.All.Contains(command.Kind))
            throw new CommandRefusedException("refurbishment_kind_unknown", $"'{command.Kind}' is not a refurbishment kind.");
        var tenantId = tenant.TenantId;
        if (await db.RefurbishmentWorkOrders.AsNoTracking().FirstOrDefaultAsync(w => w.TenantId == tenantId
            && w.OpenCommandId == command.CommandId && w.TargetUnitId == command.TargetUnitId, cancellationToken) is { } replay)
            return OpenResult(replay, true);
        var unit = await db.Units.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.TargetUnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.TargetUnitId} does not exist.");
        if (unit.LifecycleStatus is LifecycleStatus.Void or LifecycleStatus.Scrapped or LifecycleStatus.ScrappedPreRelease
            or LifecycleStatus.Decommissioned or LifecycleStatus.Merged)
            throw new CommandRefusedException("unit_not_refurbishable", $"Unit {unit.UnitId} is {unit.LifecycleStatus}.");
        var owner = await episodes.OpenAsync(unit.UnitId, EpisodeKind.Ownership, cancellationToken);
        if (owner?.PartyType != PartyType.Us)
            throw new CommandRefusedException("unit_not_ours", $"Unit {unit.UnitId} must be owned by us before refurbishment opens.", "complete the return ownership episode first");
        var custody = await episodes.OpenAsync(unit.UnitId, EpisodeKind.Custody, cancellationToken);
        if (custody?.LocationKind is not (LocationKind.Quarantine or LocationKind.Warehouse or LocationKind.InRepair))
            throw new CommandRefusedException("refurbishment_custody_invalid", $"Unit {unit.UnitId} is in '{custody?.LocationKind ?? "(none)"}', not quarantine, warehouse or in repair.");
        if (await db.RefurbishmentWorkOrders.AnyAsync(w => w.TenantId == tenantId && w.TargetUnitId == unit.UnitId && w.Status == RefurbishmentStatus.Open, cancellationToken))
            throw new CommandRefusedException("refurbishment_already_open", $"Unit {unit.UnitId} already has an open refurbishment work order.");
        var itemId = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.UnitId == unit.UnitId && a.ValidTo == null)
            .Select(a => (Guid?)a.ItemId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("no_item_assignment", $"Unit {unit.UnitId} has no current item assignment.");
        var profile = await itemProfiles.GetAsync(itemId, command.OpenedAt, cancellationToken)
            ?? throw new CommandRefusedException("item_unknown", $"Item {itemId} is not known to the host.");
        var policy = RefurbishmentPolicySettings.Resolve(await policySettings.GetAsync(cancellationToken), itemId, profile.ItemClass);
        var priorCycles = await db.RefurbishmentWorkOrders.CountAsync(w => w.TenantId == tenantId && w.TargetUnitId == unit.UnitId
            && w.Status == RefurbishmentStatus.Released && w.Kind != RefurbishmentKind.Rework, cancellationToken);
        var cycle = command.Kind == RefurbishmentKind.Rework ? 1 : priorCycles + 1;
        if (command.Kind != RefurbishmentKind.Rework && cycle > policy.MaxCycles)
            throw new CommandRefusedException("max_refurb_cycles", $"Unit {unit.UnitId} would enter cycle {cycle}; the item-class maximum is {policy.MaxCycles}.");

        var workOrderId = TimeOrderedId.New(command.OpenedAt);
        var eventId = TimeOrderedId.New(command.OpenedAt);
        var document = new DocumentRef("refurbishment_work_order", workOrderId);
        var identityPolicy = command.Kind == RefurbishmentKind.Rework ? RefurbishmentIdentity.SameUnitSameSerial : policy.Identity;
        var episodeKind = command.Kind == RefurbishmentKind.Rework ? LifeEpisodeKind.Rework
            : command.Kind == RefurbishmentKind.Remanufacture ? LifeEpisodeKind.Remanufacture : LifeEpisodeKind.Refurbish;
        var lifeEpisodeId = await lifeEpisodes.OpenAsync(new OpenLifeEpisode(unit.UnitId, episodeKind, command.OpenedAt,
            command.ActorRef, eventId, workOrderId, cycle), cancellationToken);
        var workOrder = new RefurbishmentWorkOrder
        {
            TenantId = tenantId, WorkOrderId = workOrderId, OpenCommandId = command.CommandId,
            WorkOrderNumber = await numbers.NextAsync(DocumentNumbers.Refurbishment, command.OpenedAt, cancellationToken), Kind = command.Kind,
            TargetUnitId = unit.UnitId, ItemId = itemId, SourceRmaLineId = command.SourceRmaLineId,
            SourceDocumentType = command.SourceDocument?.Type, SourceDocumentId = command.SourceDocument?.Id,
            SourceDocumentLine = command.SourceDocument?.Line, SelectionKind = selectionKind,
            SelectionReference = selectionReference, Status = RefurbishmentStatus.Open, CycleNumber = cycle,
            IdentityPolicy = identityPolicy, MaxCycles = policy.MaxCycles, ReplacementPolicyCode = policy.ReplacementPolicyCode,
            LifeEpisodeId = lifeEpisodeId, OpenedAt = command.OpenedAt, OpenedBy = command.ActorRef, OpeningEventId = eventId,
            LastModifiedAt = clock.UtcNow, LastModifiedBy = command.ActorRef,
        };
        db.RefurbishmentWorkOrders.Add(workOrder);
        var drafts = new List<UnitEventDraft>();
        if (custody.LocationKind != LocationKind.InRepair)
        {
            var move = await episodes.PrepareCustodyMoveAsync(unit.UnitId,
                new CustodyTarget(LocationKind.InRepair, command.RepairLocationId ?? custody.LocationId, Reason: command.Kind, Source: "refurbishment"),
                UnitEventType.CustodyChanged, IdempotencyKey.Derive(Key(command.CommandId, unit.UnitId, RefurbishmentEventType.Opened, command.OpenedAt), "custody"),
                command.OpenedAt, command.ActorRef, command.LegalEntityId, document,
                new Dictionary<string, object?> { ["work_order_id"] = workOrderId }, null, true, cancellationToken);
            drafts.Add(move.Draft);
        }
        drafts.Add(Draft(unit.UnitId, eventId, command.CommandId, RefurbishmentEventType.Opened, command.OpenedAt,
            command.ActorRef, command.LegalEntityId, document, new
            {
                work_order_id = workOrderId, work_order_number = workOrder.WorkOrderNumber, kind = command.Kind,
                cycle_number = cycle, identity_policy = identityPolicy, max_cycles = policy.MaxCycles,
                source_rma_line_id = command.SourceRmaLineId,
            }));
        await eventWriter.AppendAsync(drafts, cancellationToken);
        return OpenResult(workOrder, false);
    }

    public async Task<RefurbishmentOperationResult> AddOperationAsync(AddRefurbishmentOperation command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.RefurbishmentOperations.AsNoTracking().FirstOrDefaultAsync(o => o.TenantId == tenantId && o.CommandId == command.CommandId, cancellationToken) is { } replay)
            return OperationResult(replay, true);
        var workOrder = await OpenWorkOrderAsync(command.WorkOrderId, cancellationToken);
        DocumentVersion.RequireExpected(command.ExpectedVersion, workOrder.RowVersion, workOrder.LastModifiedBy, workOrder.LastModifiedAt);
        if (command.Sequence <= 0) throw new CommandRefusedException("operation_sequence_invalid", "Operation sequence must be positive.");
        if (await db.RefurbishmentOperations.AnyAsync(o => o.TenantId == tenantId && o.WorkOrderId == workOrder.WorkOrderId && o.Sequence == command.Sequence, cancellationToken))
            throw new CommandRefusedException("operation_sequence_in_use", $"Operation sequence {command.Sequence} already exists.");
        if (command.PlannedHours < 0) throw new CommandRefusedException("operation_hours_invalid", "Planned hours cannot be negative.");
        var description = Required(command.Description, "operation_description", 500);
        var eventId = TimeOrderedId.New(command.EventTime);
        var row = new RefurbishmentOperation
        {
            TenantId = tenantId, OperationId = TimeOrderedId.New(command.EventTime), WorkOrderId = workOrder.WorkOrderId,
            CommandId = command.CommandId, Sequence = command.Sequence, Description = description,
            TechnicianRef = command.TechnicianRef?.Trim(), PlannedHours = command.PlannedHours,
            Status = RefurbishmentOperationStatus.Open, AddedEventId = eventId,
        };
        db.RefurbishmentOperations.Add(row);
        Touch(workOrder, command.ActorRef);
        await eventWriter.AppendAsync([Draft(workOrder.TargetUnitId, eventId, command.CommandId,
            RefurbishmentEventType.OperationAdded, command.EventTime, command.ActorRef, command.LegalEntityId,
            Document(workOrder), new { work_order_id = workOrder.WorkOrderId, operation_id = row.OperationId, row.Sequence, description, technician_ref = row.TechnicianRef, planned_hours = row.PlannedHours })], cancellationToken);
        return OperationResult(row, false);
    }

    public async Task<RefurbishmentOperationResult> CompleteOperationAsync(CompleteRefurbishmentOperation command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.RefurbishmentOperations.AsNoTracking().FirstOrDefaultAsync(o => o.TenantId == tenantId && o.CompleteCommandId == command.CommandId, cancellationToken) is { } replay)
            return OperationResult(replay, true);
        var workOrder = await OpenWorkOrderAsync(command.WorkOrderId, cancellationToken);
        var row = await db.RefurbishmentOperations.FirstOrDefaultAsync(o => o.TenantId == tenantId && o.WorkOrderId == workOrder.WorkOrderId && o.OperationId == command.OperationId, cancellationToken)
            ?? throw new CommandRefusedException("operation_unknown", $"Operation {command.OperationId} does not exist on this work order.");
        if (row.Status == RefurbishmentOperationStatus.Completed) throw new CommandRefusedException("operation_already_completed", "The operation is already completed.");
        if (command.ActualHours < 0) throw new CommandRefusedException("operation_hours_invalid", "Actual hours cannot be negative.");
        var eventId = TimeOrderedId.New(command.CompletedAt);
        row.ActualHours = command.ActualHours;
        row.Status = RefurbishmentOperationStatus.Completed;
        row.CompleteCommandId = command.CommandId;
        row.SignedBy = command.ActorRef;
        row.SignedAt = command.CompletedAt;
        row.CompletedEventId = eventId;
        Touch(workOrder, command.ActorRef);
        await eventWriter.AppendAsync([Draft(workOrder.TargetUnitId, eventId, command.CommandId,
            RefurbishmentEventType.OperationCompleted, command.CompletedAt, command.ActorRef, command.LegalEntityId,
            Document(workOrder), new { work_order_id = workOrder.WorkOrderId, operation_id = row.OperationId, actual_hours = command.ActualHours, signed_by = command.ActorRef })], cancellationToken);
        return OperationResult(row, false);
    }

    public async Task<RefurbishmentPartResult> RemovePartAsync(RemoveRefurbishmentPart command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.RefurbishmentParts.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.CommandId == command.CommandId, cancellationToken) is { } replay)
            return PartResult(replay, true);
        var workOrder = await OpenWorkOrderAsync(command.WorkOrderId, cancellationToken);
        DocumentVersion.RequireExpected(command.ExpectedVersion, workOrder.RowVersion, workOrder.LastModifiedBy, workOrder.LastModifiedAt);
        if (!RemovedPartDestination.All.Contains(command.Destination))
            throw new CommandRefusedException("removed_part_destination_unknown", $"'{command.Destination}' is not spares, scrap or vendor return.");
        if (command.Destination == RemovedPartDestination.VendorReturn && command.VendorPartyRef is null)
            throw new CommandRefusedException("vendor_required", "A vendor-return part needs the vendor party.");
        var link = await db.AssemblyLinks.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == tenantId && l.LinkId == command.AssemblyLinkId
            && l.ParentUnitId == workOrder.TargetUnitId && l.RemovedAt == null, cancellationToken)
            ?? throw new CommandRefusedException("assembly_link_unknown", $"Open assembly link {command.AssemblyLinkId} is not on this unit.");
        await RecordDisassemblyHandler.Handle(new RecordDisassembly(command.CommandId, workOrder.TargetUnitId, [link.LinkId],
            command.EventTime, command.ActorRef, command.LegalEntityId, "refurbishment", Document(workOrder)), db, eventWriter, tenant, cancellationToken);
        // CONTRACT with RecordDisassemblyHandler: its tracked close must reach SQL before EpisodeService checks whether
        // the child is still embedded. Separating these calls makes a physically removed part fail its custody move.
        await db.SaveChangesAsync(cancellationToken);

        var drafts = new List<UnitEventDraft>();
        if (link.ChildUnitId is { } childUnitId)
        {
            await EnsureOwnedByUsAsync(childUnitId, command.CommandId, command.EventTime, command.ActorRef,
                command.LegalEntityId, Document(workOrder), drafts, cancellationToken);
            var child = await db.Units.FirstAsync(u => u.TenantId == tenantId && u.UnitId == childUnitId, cancellationToken);
            var destination = command.Destination switch
            {
                RemovedPartDestination.Spares => LocationKind.Warehouse,
                RemovedPartDestination.Scrap => LocationKind.Scrapped,
                _ => LocationKind.Vendor,
            };
            var move = await episodes.PrepareCustodyMoveAsync(childUnitId,
                new CustodyTarget(destination, command.DestinationLocationId, command.VendorPartyRef,
                    destination == LocationKind.Vendor ? PartyType.Vendor : null, command.Destination, "refurbishment"),
                UnitEventType.CustodyChanged, IdempotencyKey.Derive(Key(command.CommandId, childUnitId, RefurbishmentEventType.PartRemoved, command.EventTime), "custody"),
                command.EventTime, command.ActorRef, command.LegalEntityId, Document(workOrder),
                new Dictionary<string, object?> { ["work_order_id"] = workOrder.WorkOrderId, ["assembly_link_id"] = link.LinkId },
                null, false, cancellationToken);
            drafts.Add(move.Draft);
            if (command.Destination == RemovedPartDestination.Scrap)
            {
                var next = UnitLifecycle.Require(child.LifecycleStatus, UnitEventType.Scrapped, child.Origin, child.UnitId);
                child.LifecycleStatus = next;
                drafts.Add(Draft(childUnitId, TimeOrderedId.New(command.EventTime), command.CommandId,
                    UnitEventType.Scrapped, command.EventTime, command.ActorRef, command.LegalEntityId,
                    Document(workOrder), new { work_order_id = workOrder.WorkOrderId, assembly_link_id = link.LinkId },
                    new CurrentStateDelta { LifecycleStatus = next, Disposition = UnitLifecycle.DispositionFor(next) }));
            }
        }

        var eventId = TimeOrderedId.New(command.EventTime);
        var row = new RefurbishmentPart
        {
            TenantId = tenantId, PartId = TimeOrderedId.New(command.EventTime), WorkOrderId = workOrder.WorkOrderId,
            CommandId = command.CommandId, Action = RefurbishmentPartAction.Removed, ChildUnitId = link.ChildUnitId,
            ChildItemId = link.ChildItemId, ChildLotId = link.ChildLotId, Quantity = link.Quantity, Slot = link.Slot,
            Destination = command.Destination, DestinationLocationId = command.DestinationLocationId,
            VendorPartyRef = command.VendorPartyRef, AssemblyLinkId = link.LinkId, RecordedAt = command.EventTime, EventId = eventId,
        };
        db.RefurbishmentParts.Add(row);
        workOrder.AsBuiltChanged = true;
        Touch(workOrder, command.ActorRef);
        drafts.Add(Draft(workOrder.TargetUnitId, eventId, command.CommandId, RefurbishmentEventType.PartRemoved,
            command.EventTime, command.ActorRef, command.LegalEntityId, Document(workOrder), new
            {
                work_order_id = workOrder.WorkOrderId, part_id = row.PartId, assembly_link_id = link.LinkId,
                child_unit_id = link.ChildUnitId, child_item_id = link.ChildItemId, slot = link.Slot, destination = command.Destination,
            }));
        await eventWriter.AppendAsync(drafts, cancellationToken);
        return PartResult(row, false);
    }

    public async Task<RefurbishmentPartResult> InstallPartAsync(InstallRefurbishmentPart command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.RefurbishmentParts.AsNoTracking().FirstOrDefaultAsync(p => p.TenantId == tenantId && p.CommandId == command.CommandId, cancellationToken) is { } replay)
            return PartResult(replay, true);
        var workOrder = await OpenWorkOrderAsync(command.WorkOrderId, cancellationToken);
        DocumentVersion.RequireExpected(command.ExpectedVersion, workOrder.RowVersion, workOrder.LastModifiedBy, workOrder.LastModifiedAt);
        if (command.UnitCost < 0) throw new CommandRefusedException("part_cost_invalid", "Installed part cost cannot be negative.");
        var currency = Currency(command.Currency);
        var custodyDrafts = new List<UnitEventDraft>();
        if (command.Part.ChildUnitId is { } childUnitId)
        {
            await EnsureOwnedByUsAsync(childUnitId, command.CommandId, command.EventTime, command.ActorRef,
                command.LegalEntityId, Document(workOrder), custodyDrafts, cancellationToken);
            var childCustody = await episodes.OpenAsync(childUnitId, EpisodeKind.Custody, cancellationToken);
            if (childCustody?.LocationKind != LocationKind.InRepair)
            {
                var targetCustody = await episodes.OpenAsync(workOrder.TargetUnitId, EpisodeKind.Custody, cancellationToken);
                var move = await episodes.PrepareCustodyMoveAsync(childUnitId,
                    new CustodyTarget(LocationKind.InRepair, command.RepairLocationId ?? targetCustody?.LocationId,
                        Reason: "component_install", Source: "refurbishment"), UnitEventType.CustodyChanged,
                    IdempotencyKey.Derive(Key(command.CommandId, childUnitId, RefurbishmentEventType.PartInstalled, command.EventTime), "custody"),
                    command.EventTime, command.ActorRef, command.LegalEntityId, Document(workOrder),
                    new Dictionary<string, object?> { ["work_order_id"] = workOrder.WorkOrderId }, null, true, cancellationToken);
                custodyDrafts.Add(move.Draft);
            }
        }
        if (custodyDrafts.Count > 0) await eventWriter.AppendAsync(custodyDrafts, cancellationToken);
        var assembly = await RecordAssemblyHandler.Handle(new RecordAssembly(command.CommandId, workOrder.TargetUnitId,
            [command.Part], command.EventTime, command.ActorRef, command.LegalEntityId, Document: Document(workOrder)),
            db, eventWriter, tenant, cancellationToken);
        var linkId = assembly.LinkIds.Single();
        var eventId = TimeOrderedId.New(command.EventTime);
        var row = new RefurbishmentPart
        {
            TenantId = tenantId, PartId = TimeOrderedId.New(command.EventTime), WorkOrderId = workOrder.WorkOrderId,
            CommandId = command.CommandId, Action = RefurbishmentPartAction.Installed, ChildUnitId = command.Part.ChildUnitId,
            ChildItemId = command.Part.ChildUnitId is null ? command.Part.ChildItemId : null, ChildLotId = command.Part.ChildLotId,
            Quantity = command.Part.Quantity, Slot = command.Part.Slot, AssemblyLinkId = linkId,
            UnitCost = command.UnitCost, Currency = currency, RecordedAt = command.EventTime, EventId = eventId,
        };
        db.RefurbishmentParts.Add(row);
        workOrder.AsBuiltChanged = true;
        Touch(workOrder, command.ActorRef);
        await eventWriter.AppendAsync([Draft(workOrder.TargetUnitId, eventId, command.CommandId,
            RefurbishmentEventType.PartInstalled, command.EventTime, command.ActorRef, command.LegalEntityId,
            Document(workOrder), new { work_order_id = workOrder.WorkOrderId, part_id = row.PartId, assembly_link_id = linkId, child_unit_id = row.ChildUnitId, child_item_id = row.ChildItemId, slot = row.Slot, unit_cost = row.UnitCost, currency })], cancellationToken);
        if (command.UnitCost > 0)
            await AddCostAsync(workOrder, command.CommandId, RefurbishmentCostKind.Part, command.UnitCost, currency,
                $"Installed part {row.PartId:N}", command.EventTime, command.ActorRef, command.LegalEntityId, cancellationToken);
        return PartResult(row, false);
    }

    public async Task<RefurbishmentLabourResult> RecordLabourAsync(RecordRefurbishmentLabour command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (await db.RefurbishmentLabour.AsNoTracking().FirstOrDefaultAsync(l => l.TenantId == tenantId && l.CommandId == command.CommandId, cancellationToken) is { } replay)
            return new(replay.WorkOrderId, replay.LabourId, replay.Hours, replay.HourlyRate, replay.Amount, replay.Currency, replay.CostId, replay.EventId, true);
        var workOrder = await OpenWorkOrderAsync(command.WorkOrderId, cancellationToken);
        DocumentVersion.RequireExpected(command.ExpectedVersion, workOrder.RowVersion, workOrder.LastModifiedBy, workOrder.LastModifiedAt);
        if (command.Hours <= 0 || command.HourlyRate < 0) throw new CommandRefusedException("labour_amount_invalid", "Labour hours must be positive and the hourly rate cannot be negative.");
        if (command.OperationId is { } operationId && !await db.RefurbishmentOperations.AnyAsync(o => o.TenantId == tenantId && o.WorkOrderId == workOrder.WorkOrderId && o.OperationId == operationId, cancellationToken))
            throw new CommandRefusedException("operation_unknown", $"Operation {operationId} does not exist on this work order.");
        var amount = decimal.Round(command.Hours * command.HourlyRate, 2, MidpointRounding.AwayFromZero);
        var currency = Currency(command.Currency);
        var cost = await AddCostAsync(workOrder, command.CommandId, RefurbishmentCostKind.Labour, amount, currency,
            $"Labour by {command.ActorRef}", command.EventTime, command.ActorRef, command.LegalEntityId, cancellationToken);
        var row = new RefurbishmentLabour
        {
            TenantId = tenantId, LabourId = TimeOrderedId.New(command.EventTime), WorkOrderId = workOrder.WorkOrderId,
            CommandId = command.CommandId, OperationId = command.OperationId, Hours = command.Hours,
            HourlyRate = command.HourlyRate, Amount = amount, Currency = currency, TechnicianRef = command.ActorRef,
            RecordedAt = command.EventTime, EventId = cost.EventId, CostId = cost.CostId,
        };
        db.RefurbishmentLabour.Add(row);
        Touch(workOrder, command.ActorRef);
        return new(workOrder.WorkOrderId, row.LabourId, row.Hours, row.HourlyRate, row.Amount, row.Currency, row.CostId, row.EventId, false);
    }

    public async Task<RefurbishmentCostResult> RecordCostAsync(RecordRefurbishmentCost command, CancellationToken cancellationToken)
    {
        if (await db.RefurbishmentCosts.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CommandId == command.CommandId, cancellationToken) is { } replay)
            return CostResult(replay, true);
        var workOrder = await OpenWorkOrderAsync(command.WorkOrderId, cancellationToken);
        DocumentVersion.RequireExpected(command.ExpectedVersion, workOrder.RowVersion, workOrder.LastModifiedBy, workOrder.LastModifiedAt);
        if (!RefurbishmentCostKind.All.Contains(command.Kind)) throw new CommandRefusedException("refurbishment_cost_kind_unknown", $"'{command.Kind}' is not a refurbishment cost kind.");
        if (command.Amount < 0) throw new CommandRefusedException("refurbishment_cost_invalid", "Refurbishment cost cannot be negative.");
        if (command.Kind == RefurbishmentCostKind.GradedReceipt && await db.RefurbishmentCosts.AnyAsync(c => c.TenantId == tenant.TenantId && c.WorkOrderId == workOrder.WorkOrderId && c.Kind == RefurbishmentCostKind.GradedReceipt, cancellationToken))
            throw new CommandRefusedException("graded_receipt_value_exists", "The work order already has its graded receipt value.");
        Touch(workOrder, command.ActorRef);
        return CostResult(await AddCostAsync(workOrder, command.CommandId, command.Kind, command.Amount,
            Currency(command.Currency), Required(command.Description, "cost_description", 500), command.EventTime,
            command.ActorRef, command.LegalEntityId, cancellationToken), false);
    }

    private async Task<RefurbishmentCost> AddCostAsync(RefurbishmentWorkOrder workOrder, Guid commandId, string kind,
        decimal amount, string currency, string description, DateTime eventTime, string actorRef, Guid legalEntityId,
        CancellationToken cancellationToken)
    {
        if (await db.RefurbishmentCosts.AnyAsync(c => c.TenantId == tenant.TenantId
            && c.WorkOrderId == workOrder.WorkOrderId && c.Currency != currency, cancellationToken))
            throw new CommandRefusedException("refurbishment_currency_mixed", "One work order cannot roll costs in from multiple currencies.");
        var eventId = TimeOrderedId.New(eventTime);
        var row = new RefurbishmentCost
        {
            TenantId = tenant.TenantId, CostId = TimeOrderedId.New(eventTime), WorkOrderId = workOrder.WorkOrderId,
            CommandId = commandId, Kind = kind, Amount = amount, Currency = currency, Description = description,
            PostedAt = eventTime, PostedBy = actorRef, EventId = eventId,
        };
        db.RefurbishmentCosts.Add(row);
        await eventWriter.AppendAsync([Draft(workOrder.TargetUnitId, eventId, commandId, RefurbishmentEventType.CostPosted,
            eventTime, actorRef, legalEntityId, Document(workOrder), new
            {
                work_order_id = workOrder.WorkOrderId, cost_id = row.CostId, kind, amount, currency, description,
            })], cancellationToken);
        return row;
    }

    public async Task<ReleaseRefurbishmentResult> ReleaseAsync(ReleaseRefurbishment command, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var workOrder = await db.RefurbishmentWorkOrders.FirstOrDefaultAsync(w => w.TenantId == tenantId && w.WorkOrderId == command.WorkOrderId, cancellationToken)
            ?? throw new CommandRefusedException("refurbishment_unknown", $"Refurbishment work order {command.WorkOrderId} does not exist.");
        if (workOrder.ReleaseCommandId == command.CommandId && workOrder.Status == RefurbishmentStatus.Released)
            return await ReleaseResultAsync(workOrder, true, cancellationToken);
        if (workOrder.Status != RefurbishmentStatus.Open)
            throw new CommandRefusedException("refurbishment_not_open", $"Work order {workOrder.WorkOrderNumber} is {workOrder.Status}.");
        if (!ConditionGrades.IsKnown(command.Grade) || command.Grade is ConditionGrades.New or ConditionGrades.Defective or ConditionGrades.Scrap)
            throw new CommandRefusedException("release_grade_invalid", "A refurbishment release grade must be refurbished A, B, C, used, open box or remanufactured.");
        if (await db.RefurbishmentOperations.AnyAsync(o => o.TenantId == tenantId && o.WorkOrderId == workOrder.WorkOrderId && o.Status != RefurbishmentOperationStatus.Completed, cancellationToken))
            throw new CommandRefusedException("operations_incomplete", "Every refurbishment operation must be completed and signed before release.");
        if (workOrder.CycleNumber > workOrder.MaxCycles)
            throw new CommandRefusedException("max_refurb_cycles", $"Cycle {workOrder.CycleNumber} exceeds the work order maximum of {workOrder.MaxCycles}.");
        var profile = await itemProfiles.GetAsync(workOrder.ItemId, command.ReleasedAt, cancellationToken)
            ?? throw new CommandRefusedException("item_unknown", $"Item {workOrder.ItemId} is not known to the host.");
        var returnedAt = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == workOrder.TargetUnitId
            && e.Kind == EpisodeKind.Ownership && e.PartyType != PartyType.Us && e.ValidTo != null)
            .MaxAsync(e => (DateTime?)e.ValidTo, cancellationToken);
        if (profile.RequiresSanitisation && returnedAt is { } returned
            && !await db.SanitisationRecords.AnyAsync(r => r.TenantId == tenantId && r.UnitId == workOrder.TargetUnitId
                && r.SanitisedAt > returned && r.SanitisedAt <= command.ReleasedAt, cancellationToken))
            throw new CommandRefusedException("sanitisation_required", "This item class requires a sanitisation record after the unit came back.", "record sanitisation before release");
        if (!await db.TestRecords.AnyAsync(r => r.TenantId == tenantId && r.UnitId == workOrder.TargetUnitId
            && r.TestKind == RefurbishmentTestKind.KeyFunctions && r.Result == UnitTestResult.Pass
            && r.TestedAt >= workOrder.OpenedAt && r.TestedAt <= command.ReleasedAt, cancellationToken))
            throw new CommandRefusedException("passing_key_function_test_required", "At least one passing key-function test after work opened is required.");

        var costs = await db.RefurbishmentCosts.AsNoTracking().Where(c => c.TenantId == tenantId && c.WorkOrderId == workOrder.WorkOrderId).ToListAsync(cancellationToken);
        var currencies = costs.Select(c => c.Currency).Distinct(StringComparer.Ordinal).ToList();
        if (currencies.Count > 1) throw new CommandRefusedException("refurbishment_currency_mixed", "One work order cannot roll costs in from multiple currencies.");
        var currency = currencies.SingleOrDefault() ?? "XXX";
        var totalCost = costs.Sum(c => c.Amount);
        var releaseEventId = TimeOrderedId.New(command.ReleasedAt);
        var sourceUnit = await db.Units.FirstAsync(u => u.TenantId == tenantId && u.UnitId == workOrder.TargetUnitId, cancellationToken);
        var releasedUnitId = workOrder.TargetUnitId;

        if (workOrder.IdentityPolicy == RefurbishmentIdentity.SameUnitNewSerial)
        {
            await ReserialiseAsync(workOrder, sourceUnit, command, profile, releaseEventId, cancellationToken);
        }
        else if (workOrder.IdentityPolicy == RefurbishmentIdentity.NewUnit)
        {
            releasedUnitId = await CreateDerivedUnitAsync(workOrder, sourceUnit, command, profile, releaseEventId, cancellationToken);
        }

        await GradeUnitHandler.Handle(new GradeUnit(command.CommandId, releasedUnitId, command.Grade, command.ValuationRule,
            command.ReleasedAt, command.ActorRef, command.LegalEntityId, command.FCode, command.CCode, Document(workOrder)),
            db, eventWriter, tenant, cancellationToken);

        var drafts = new List<UnitEventDraft>();
        await MoveInstalledChildrenToWarehouseAsync(workOrder, command, drafts, cancellationToken);
        var releasedCustody = await episodes.OpenAsync(releasedUnitId, EpisodeKind.Custody, cancellationToken);
        if (releasedCustody?.LocationKind != LocationKind.Warehouse || releasedCustody.LocationId != command.WarehouseLocationId)
        {
            var move = await episodes.PrepareCustodyMoveAsync(releasedUnitId,
                new CustodyTarget(LocationKind.Warehouse, command.WarehouseLocationId, Reason: "refurbishment_release", Source: "refurbishment"),
                UnitEventType.CustodyChanged, IdempotencyKey.Derive(Key(command.CommandId, releasedUnitId, RefurbishmentEventType.Released, command.ReleasedAt), "custody"),
                command.ReleasedAt, command.ActorRef, command.LegalEntityId, Document(workOrder),
                new Dictionary<string, object?> { ["work_order_id"] = workOrder.WorkOrderId }, null, true, cancellationToken);
            drafts.Add(move.Draft);
        }
        if (workOrder.IdentityPolicy == RefurbishmentIdentity.NewUnit)
        {
            var oldCustody = await episodes.OpenAsync(sourceUnit.UnitId, EpisodeKind.Custody, cancellationToken);
            if (oldCustody?.LocationKind != LocationKind.Warehouse || oldCustody.LocationId != command.WarehouseLocationId)
            {
                var oldMove = await episodes.PrepareCustodyMoveAsync(sourceUnit.UnitId,
                    new CustodyTarget(LocationKind.Warehouse, command.WarehouseLocationId, Reason: "remanufactured", Source: "refurbishment"),
                    UnitEventType.CustodyChanged, IdempotencyKey.Derive(Key(command.CommandId, sourceUnit.UnitId, RefurbishmentEventType.Released, command.ReleasedAt), "old-custody"),
                    command.ReleasedAt, command.ActorRef, command.LegalEntityId, Document(workOrder),
                    new Dictionary<string, object?> { ["work_order_id"] = workOrder.WorkOrderId, ["derived_unit_id"] = releasedUnitId }, null, true, cancellationToken);
                drafts.Add(oldMove.Draft);
            }
            var next = UnitLifecycle.Require(sourceUnit.LifecycleStatus, UnitEventType.Decommissioned, sourceUnit.Origin, sourceUnit.UnitId);
            sourceUnit.LifecycleStatus = next;
            drafts.Add(Draft(sourceUnit.UnitId, TimeOrderedId.New(command.ReleasedAt), command.CommandId,
                UnitEventType.Decommissioned, command.ReleasedAt, command.ActorRef, command.LegalEntityId,
                Document(workOrder), new { work_order_id = workOrder.WorkOrderId, derived_unit_id = releasedUnitId },
                new CurrentStateDelta { LifecycleStatus = next, Disposition = UnitLifecycle.DispositionFor(next) }));
        }

        drafts.Add(Draft(releasedUnitId, releaseEventId, command.CommandId, RefurbishmentEventType.Released,
            command.ReleasedAt, command.ActorRef, command.LegalEntityId, Document(workOrder), new
            {
                work_order_id = workOrder.WorkOrderId, work_order_number = workOrder.WorkOrderNumber,
                source_unit_id = workOrder.TargetUnitId, released_unit_id = releasedUnitId, kind = workOrder.Kind,
                grade = command.Grade, cycle_number = workOrder.CycleNumber, identity_policy = workOrder.IdentityPolicy,
                total_cost = totalCost, currency,
            }));
        if (workOrder.Kind == RefurbishmentKind.Rework && workOrder.AsBuiltChanged)
        {
            drafts.Add(Draft(releasedUnitId, TimeOrderedId.New(command.ReleasedAt), command.CommandId,
                RefurbishmentEventType.ReleaseRecordReissueRequested, command.ReleasedAt, command.ActorRef,
                command.LegalEntityId, Document(workOrder), new { work_order_id = workOrder.WorkOrderId, reason = "as_built_changed" }));
        }
        await eventWriter.AppendAsync(drafts, cancellationToken);
        await lifeEpisodes.CloseAsync(new CloseLifeEpisode(workOrder.LifeEpisodeId, command.ReleasedAt, releaseEventId), cancellationToken);
        workOrder.Status = RefurbishmentStatus.Released;
        workOrder.ReleaseCommandId = command.CommandId;
        workOrder.ReleasedAt = command.ReleasedAt;
        workOrder.ReleasedBy = command.ActorRef;
        workOrder.ReleasedEventId = releaseEventId;
        workOrder.ReleasedUnitId = releasedUnitId;
        workOrder.Grade = command.Grade;
        Touch(workOrder, command.ActorRef);
        return new(workOrder.WorkOrderId, workOrder.WorkOrderNumber, workOrder.TargetUnitId, releasedUnitId,
            workOrder.IdentityPolicy, command.Grade, workOrder.CycleNumber, totalCost, currency, releaseEventId, false);
    }

    public async Task<CancelRefurbishmentResult> CancelAsync(CancelRefurbishment command, CancellationToken cancellationToken)
    {
        var workOrder = await db.RefurbishmentWorkOrders.FirstOrDefaultAsync(w => w.TenantId == tenant.TenantId && w.WorkOrderId == command.WorkOrderId, cancellationToken)
            ?? throw new CommandRefusedException("refurbishment_unknown", $"Refurbishment work order {command.WorkOrderId} does not exist.");
        if (workOrder.CancelCommandId == command.CommandId && workOrder.Status == RefurbishmentStatus.Cancelled)
            return new(workOrder.WorkOrderId, workOrder.Status, workOrder.CancelledEventId!.Value, true);
        if (workOrder.Status != RefurbishmentStatus.Open)
            throw new CommandRefusedException("refurbishment_not_open", $"Work order {workOrder.WorkOrderNumber} is {workOrder.Status}.");
        var reason = Required(command.Reason, "cancellation_reason", 500);
        var eventId = TimeOrderedId.New(command.CancelledAt);
        var drafts = new List<UnitEventDraft>();
        var custody = await episodes.OpenAsync(workOrder.TargetUnitId, EpisodeKind.Custody, cancellationToken);
        if (custody?.LocationKind == LocationKind.InRepair)
        {
            var move = await episodes.PrepareCustodyMoveAsync(workOrder.TargetUnitId,
                new CustodyTarget(LocationKind.Warehouse, command.WarehouseLocationId, Reason: "refurbishment_cancelled", Source: "refurbishment"),
                UnitEventType.CustodyChanged, IdempotencyKey.Derive(Key(command.CommandId, workOrder.TargetUnitId, RefurbishmentEventType.Cancelled, command.CancelledAt), "custody"),
                command.CancelledAt, command.ActorRef, command.LegalEntityId, Document(workOrder),
                new Dictionary<string, object?> { ["work_order_id"] = workOrder.WorkOrderId }, null, true, cancellationToken);
            drafts.Add(move.Draft);
        }
        drafts.Add(Draft(workOrder.TargetUnitId, eventId, command.CommandId, RefurbishmentEventType.Cancelled,
            command.CancelledAt, command.ActorRef, command.LegalEntityId, Document(workOrder),
            new { work_order_id = workOrder.WorkOrderId, reason }));
        await eventWriter.AppendAsync(drafts, cancellationToken);
        await lifeEpisodes.CloseAsync(new CloseLifeEpisode(workOrder.LifeEpisodeId, command.CancelledAt, eventId), cancellationToken);
        workOrder.Status = RefurbishmentStatus.Cancelled;
        workOrder.CancelCommandId = command.CommandId;
        workOrder.CancelledAt = command.CancelledAt;
        workOrder.CancelledBy = command.ActorRef;
        workOrder.CancellationReason = reason;
        workOrder.CancelledEventId = eventId;
        Touch(workOrder, command.ActorRef);
        return new(workOrder.WorkOrderId, workOrder.Status, eventId, false);
    }

    private async Task ReserialiseAsync(RefurbishmentWorkOrder workOrder, Unit unit, ReleaseRefurbishment command,
        ItemSerialisationProfile profile, Guid eventId, CancellationToken cancellationToken)
    {
        var open = await identifiers.OpenOfTypeAsync(unit.UnitId, IdentifierType.InternalSerial, cancellationToken)
            ?? throw new CommandRefusedException("internal_serial_required", $"Unit {unit.UnitId} has no open internal serial.");
        var serial = command.NewSerial ?? await AllocateSerialAsync(workOrder, command, profile, cancellationToken);
        var prepared = identifiers.Prepare(new IdentifierInput(IdentifierType.InternalSerial, serial, open.IssuerPartyRef,
            open.Qualifier, open.EnterpriseIdentifier), workOrder.ItemId, null);
        if (prepared.Blocks) throw new CommandRefusedException("identifier_invalid", $"The new serial fails validation: {string.Join(", ", prepared.Validation.Reasons)}.");
        if (prepared.Normalised == open.NormalisedValue) throw new CommandRefusedException("new_serial_unchanged", "The new serial normalises to the current serial.");
        if (await identifiers.HolderOfAsync(prepared.Policy.Scheme, prepared.ScopeKey, prepared.Normalised, cancellationToken) is { } holder && holder.UnitId != unit.UnitId)
            throw new CommandRefusedException("identifier_in_use", $"Serial '{prepared.Normalised}' is already open on unit {holder.UnitId}.");
        var replacement = prepared.ToRow(tenant.TenantId, unit.UnitId, command.ReleasedAt, command.ActorRef, clock.UtcNow, eventId);
        replacement.SupersedesIdentifierId = open.IdentifierId;
        open.ValidTo = command.ReleasedAt;
        open.SupersededByIdentifierId = replacement.IdentifierId;
        open.CorrectionReason = "refurbishment";
        db.Identifiers.Add(replacement);
        await serialRanges.RecordCommissionedAsync(workOrder.ItemId, [(unit.UnitId, replacement.NormalisedValue)], Document(workOrder), cancellationToken);
    }

    private async Task<Guid> CreateDerivedUnitAsync(RefurbishmentWorkOrder workOrder, Unit sourceUnit,
        ReleaseRefurbishment command, ItemSerialisationProfile profile, Guid releaseEventId, CancellationToken cancellationToken)
    {
        var serial = command.NewSerial ?? await AllocateSerialAsync(workOrder, command, profile, cancellationToken);
        var commissionId = DerivedGuid(command.CommandId, "commission");
        var result = await CommissionUnitsHandler.Handle(new CommissionUnits(commissionId, workOrder.ItemId,
            UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            command.ReleasedAt, command.ActorRef, command.LegalEntityId, Document: Document(workOrder),
            LocationId: command.WarehouseLocationId, LocationKind: LocationKind.InRepair, Source: "refurbishment"),
            db, eventWriter, bulk, identifiers, itemProfiles, serialRanges, serialReuse, tenant, clock, cancellationToken);
        var unitId = result.Lines.Single().UnitId
            ?? throw new CommandRefusedException("derived_unit_not_created", "The remanufactured unit could not be created.");
        await db.SaveChangesAsync(cancellationToken);
        var derived = await db.Units.FirstAsync(u => u.TenantId == tenant.TenantId && u.UnitId == unitId, cancellationToken);
        derived.DerivedFromUnitId = sourceUnit.UnitId;
        await db.SaveChangesAsync(cancellationToken);
        await ReleaseUnitHandler.Handle(new ReleaseUnit(DerivedGuid(command.CommandId, "release-unit"), unitId,
            command.ReleasedAt, command.ActorRef, command.LegalEntityId, $"refurbishment:{command.ActorRef}", Document(workOrder)),
            db, eventWriter, releaseGuards, tenant, clock, cancellationToken);
        var ownership = await episodes.PrepareOwnershipOpenAsync(unitId,
            new OwnershipTarget(command.LegalEntityId, PartyType.Us, "remanufacture", "refurbishment"),
            IdempotencyKey.Derive(Key(command.CommandId, unitId, RefurbishmentEventType.Released, command.ReleasedAt), "ownership"),
            command.ReleasedAt, command.ActorRef, command.LegalEntityId, Document(workOrder), null, cancellationToken);
        var ownershipDrafts = new List<UnitEventDraft>();
        if (ownership.CloseDraft is not null) ownershipDrafts.Add(ownership.CloseDraft);
        ownershipDrafts.Add(ownership.Draft);
        await eventWriter.AppendAsync(ownershipDrafts, cancellationToken);
        var newEpisodeId = await lifeEpisodes.OpenAsync(new OpenLifeEpisode(unitId, LifeEpisodeKind.Remanufacture,
            command.ReleasedAt, command.ActorRef, releaseEventId, workOrder.WorkOrderId, workOrder.CycleNumber), cancellationToken);
        await lifeEpisodes.CloseAsync(new CloseLifeEpisode(newEpisodeId, command.ReleasedAt, releaseEventId), cancellationToken);

        var live = await db.Entitlements.Where(e => e.TenantId == tenant.TenantId && e.UnitId == sourceUnit.UnitId
            && e.Direction == EntitlementDirection.Outbound && e.Status != EntitlementStatus.Closed).ToListAsync(cancellationToken);
        if (live.Count > 0 && string.IsNullOrWhiteSpace(workOrder.ReplacementPolicyCode))
            throw new CommandRefusedException("replacement_policy_required", "New-unit remanufacture has live entitlements but no ReplacementPolicy is named in the item-class policy.");
        foreach (var entitlement in live)
        {
            var transferId = DerivedGuid(command.CommandId, entitlement.EntitlementId.ToString("N"));
            await TransferEntitlementHandler.Handle(new TransferEntitlement(transferId, entitlement.EntitlementId,
                unitId, null, DateOnly.FromDateTime(command.ReleasedAt), command.ReleasedAt, command.ActorRef,
                workOrder.ReplacementPolicyCode), db, entitlementLedger, tenant, clock, corrections, cancellationToken);
        }
        return unitId;
    }

    private async Task<string> AllocateSerialAsync(RefurbishmentWorkOrder workOrder, ReleaseRefurbishment command,
        ItemSerialisationProfile profile, CancellationToken cancellationToken)
    {
        var allocated = await serialRanges.AllocateAsync(new AllocateSerialRange(DerivedGuid(command.CommandId, "serial"),
            profile.SerialScheme, 1, command.ReleasedAt, command.ActorRef, ItemId: workOrder.ItemId,
            AllocatedTo: Document(workOrder), Note: "refurbishment release"), cancellationToken);
        return allocated.StartValue ?? allocated.Serials.Single();
    }

    private async Task MoveInstalledChildrenToWarehouseAsync(RefurbishmentWorkOrder workOrder,
        ReleaseRefurbishment command, List<UnitEventDraft> drafts, CancellationToken cancellationToken)
    {
        var childIds = await db.RefurbishmentParts.AsNoTracking().Where(p => p.TenantId == tenant.TenantId
            && p.WorkOrderId == workOrder.WorkOrderId && p.Action == RefurbishmentPartAction.Installed && p.ChildUnitId != null)
            .Select(p => p.ChildUnitId!.Value).Distinct().ToListAsync(cancellationToken);
        foreach (var childId in childIds)
        {
            var custody = await episodes.OpenAsync(childId, EpisodeKind.Custody, cancellationToken);
            if (custody?.LocationKind == LocationKind.Warehouse && custody.LocationId == command.WarehouseLocationId) continue;
            var move = await episodes.PrepareCustodyMoveAsync(childId,
                new CustodyTarget(LocationKind.Warehouse, command.WarehouseLocationId, Reason: "component_release", Source: "refurbishment"),
                UnitEventType.CustodyChanged, IdempotencyKey.Derive(Key(command.CommandId, childId, RefurbishmentEventType.Released, command.ReleasedAt), "child-custody"),
                command.ReleasedAt, command.ActorRef, command.LegalEntityId, Document(workOrder),
                new Dictionary<string, object?> { ["work_order_id"] = workOrder.WorkOrderId, ["installed_in_unit_id"] = workOrder.TargetUnitId }, null, true, cancellationToken);
            drafts.Add(move.Draft);
        }
    }

    private async Task EnsureOwnedByUsAsync(Guid unitId, Guid commandId, DateTime at, string actorRef,
        Guid legalEntityId, DocumentRef document, List<UnitEventDraft> drafts, CancellationToken cancellationToken)
    {
        var owner = await episodes.OpenAsync(unitId, EpisodeKind.Ownership, cancellationToken);
        if (owner?.PartyType == PartyType.Us) return;
        if (owner is not null) throw new CommandRefusedException("part_not_ours", $"Part unit {unitId} is owned by {owner.PartyType}, not us.");
        var opened = await episodes.PrepareOwnershipOpenAsync(unitId,
            new OwnershipTarget(legalEntityId, PartyType.Us, "component_stock", "refurbishment"),
            IdempotencyKey.Derive(commandId.ToString("N"), $"ownership:{unitId:N}"), at, actorRef,
            legalEntityId, document, null, cancellationToken);
        if (opened.CloseDraft is not null) drafts.Add(opened.CloseDraft);
        drafts.Add(opened.Draft);
    }

    private async Task<RefurbishmentWorkOrder> OpenWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        var workOrder = await db.RefurbishmentWorkOrders.FirstOrDefaultAsync(w => w.TenantId == tenant.TenantId && w.WorkOrderId == workOrderId, cancellationToken)
            ?? throw new CommandRefusedException("refurbishment_unknown", $"Refurbishment work order {workOrderId} does not exist.");
        if (workOrder.Status != RefurbishmentStatus.Open)
            throw new CommandRefusedException("refurbishment_not_open", $"Work order {workOrder.WorkOrderNumber} is {workOrder.Status}.");
        return workOrder;
    }

    private async Task<ReleaseRefurbishmentResult> ReleaseResultAsync(RefurbishmentWorkOrder workOrder, bool replayed, CancellationToken cancellationToken)
    {
        var costs = await db.RefurbishmentCosts.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && c.WorkOrderId == workOrder.WorkOrderId).ToListAsync(cancellationToken);
        return new(workOrder.WorkOrderId, workOrder.WorkOrderNumber, workOrder.TargetUnitId,
            workOrder.ReleasedUnitId ?? workOrder.TargetUnitId, workOrder.IdentityPolicy, workOrder.Grade!, workOrder.CycleNumber,
            costs.Sum(c => c.Amount), costs.Select(c => c.Currency).Distinct().SingleOrDefault() ?? "XXX",
            workOrder.ReleasedEventId!.Value, replayed);
    }

    private static DocumentRef Document(RefurbishmentWorkOrder workOrder) => new("refurbishment_work_order", workOrder.WorkOrderId);

    private static OpenRefurbishmentResult OpenResult(RefurbishmentWorkOrder workOrder, bool replayed) =>
        new(workOrder.WorkOrderId, workOrder.WorkOrderNumber, workOrder.TargetUnitId, workOrder.Kind,
            workOrder.CycleNumber, workOrder.LifeEpisodeId, replayed);

    private static RefurbishmentOperationResult OperationResult(RefurbishmentOperation row, bool replayed) =>
        new(row.WorkOrderId, row.OperationId, row.Sequence, row.Status, row.PlannedHours, row.ActualHours,
            row.SignedBy, row.SignedAt, replayed);

    private static RefurbishmentPartResult PartResult(RefurbishmentPart row, bool replayed) =>
        new(row.WorkOrderId, row.PartId, row.Action, row.ChildUnitId, row.ChildItemId, row.Slot,
            row.Destination, row.AssemblyLinkId, row.EventId, replayed);

    private static RefurbishmentCostResult CostResult(RefurbishmentCost row, bool replayed) =>
        new(row.WorkOrderId, row.CostId, row.Kind, row.Amount, row.Currency, row.EventId, replayed);

    private void Touch(RefurbishmentWorkOrder workOrder, string actorRef)
    {
        workOrder.LastModifiedBy = actorRef;
        workOrder.LastModifiedAt = clock.UtcNow;
    }

    private static string Required(string value, string field, int maximum)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > maximum)
            throw new CommandRefusedException($"{field}_invalid", $"{field.Replace('_', ' ')} must contain 1 to {maximum} characters.");
        return trimmed;
    }

    private static string Currency(string value)
    {
        var currency = value.Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            throw new CommandRefusedException("currency_invalid", "Currency must be a three-letter ISO code.");
        return currency;
    }

    private static Guid DerivedGuid(Guid commandId, string suffix)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(commandId.ToString("N") + "|" + suffix));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string Key(Guid commandId, Guid unitId, string eventType, DateTime at) =>
        IdempotencyKey.ForCommand(Source, commandId, unitId, eventType, at);

    private static UnitEventDraft Draft(Guid unitId, Guid eventId, Guid commandId, string eventType, DateTime at,
        string actorRef, Guid legalEntityId, DocumentRef document, object attributes, CurrentStateDelta? delta = null) => new()
    {
        UnitId = unitId, PresetEventId = eventId, EventType = eventType, EventTime = at, ActorRef = actorRef,
        LegalEntityId = legalEntityId, DocumentType = document.Type, DocumentId = document.Id,
        DocumentLine = document.Line, IdempotencyKey = Key(commandId, unitId, eventType, at),
        Attributes = JsonObjectText.Of(attributes), Delta = delta,
    };
}
