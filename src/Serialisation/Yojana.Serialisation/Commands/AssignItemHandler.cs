using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.Commands;

/// <summary><see cref="AssignItem"/>: effective-dated item and model version with a reason (case 42). Stock reclassification of a unit in stock is `CorrectItem` (P3), not this.</summary>
public static class AssignItemHandler
{
    private static readonly string[] Reasons = [ItemAssignmentReason.Upgrade, ItemAssignmentReason.Conversion, ItemAssignmentReason.Relabel];

    public static async Task<AssignItemResult> Handle(AssignItem command, SerialisationDbContext db, IUnitEventWriter writer, EffectiveItemProfiles items, ITenantContext tenant, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (!Reasons.Contains(command.Reason))
        {
            throw new CommandRefusedException("reason_unknown", $"Reason '{command.Reason}' is not one of {string.Join(", ", Reasons)} (initial is set by commissioning).");
        }

        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, unit.UnitId, UnitEventType.ItemAssigned, command.EventTime);
        var replayed = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime, cancellationToken);
        if (replayed is not null)
        {
            var opened = await db.ItemAssignments.AsNoTracking().FirstAsync(a => a.TenantId == tenantId && a.EventId == replayed.EventId, cancellationToken);
            var closedBefore = await db.ItemAssignments.AsNoTracking().FirstAsync(a => a.TenantId == tenantId && a.UnitId == unit.UnitId && a.ValidTo == opened.ValidFrom, cancellationToken);
            return new AssignItemResult(unit.UnitId, closedBefore.AssignmentId, opened.AssignmentId, closedBefore.ItemId, opened.ItemId);
        }

        var profile = await items.GetAsync(command.NewItemId, command.EventTime, cancellationToken)
            ?? throw new CommandRefusedException("item_unknown", $"Item {command.NewItemId} is not known to the host.");
        if (profile.TrackingMode != ItemSerialisationProfile.TrackingModes.Serial)
        {
            throw new CommandRefusedException("item_not_serial_tracked", $"Item {profile.ItemCode} is {profile.TrackingMode}-tracked.");
        }

        var current = await db.ItemAssignments.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.UnitId == unit.UnitId && a.ValidTo == null, cancellationToken)
            ?? throw new CommandRefusedException("no_item_assignment", $"Unit {unit.UnitId} has no current item assignment.");
        if (current.ItemId == command.NewItemId && current.ModelVersionId == command.ModelVersionId)
        {
            throw new CommandRefusedException("no_change", "The unit already has that item and model version.");
        }

        if (command.EventTime < current.ValidFrom)
        {
            throw new CommandRefusedException("effective_before_current", $"The change is dated {command.EventTime:O}, before the current assignment started ({current.ValidFrom:O}).", "date the change after the current assignment, or correct history through the workbench (P3)");
        }

        var eventId = TimeOrderedId.New(command.EventTime);
        current.ValidTo = command.EventTime;
        var next = new UnitItemAssignment { TenantId = tenantId, AssignmentId = TimeOrderedId.New(command.EventTime), UnitId = unit.UnitId, ItemId = command.NewItemId, ModelVersionId = command.ModelVersionId, ValidFrom = command.EventTime, Reason = command.Reason, EventId = eventId };
        db.ItemAssignments.Add(next);
        await writer.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = unit.UnitId, EventTime = command.EventTime, EventType = UnitEventType.ItemAssigned, PresetEventId = eventId, BizStep = command.Reason == ItemAssignmentReason.Relabel ? "relabeling" : "transforming",
                LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef, DocumentType = command.Document?.Type, DocumentId = command.Document?.Id, DocumentLine = command.Document?.Line, IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new { command_id = command.CommandId, reason = command.Reason, note = command.Note, before = new { item_id = current.ItemId, model_version_id = current.ModelVersionId }, after = new { item_id = command.NewItemId, model_version_id = command.ModelVersionId, item_code = profile.ItemCode } }),
                Delta = new CurrentStateDelta { ItemId = command.NewItemId, ModelVersionId = command.ModelVersionId },
            },
        ], cancellationToken);
        return new AssignItemResult(unit.UnitId, current.AssignmentId, next.AssignmentId, current.ItemId, command.NewItemId);
    }
}
