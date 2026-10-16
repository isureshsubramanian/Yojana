using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

/// <summary>
/// <see cref="CorrectIdentifier"/> (Identity rule 5, cases 4, 5, 100): a new row with the reason replaces the open row of
/// that type, which closes pointing at its successor; the unit key, the events and the stock stay untouched. A unit that
/// has shipped needs a second approver (ADR-0001 correction table); the approval policy as tenant data arrives in P3.
/// </summary>
public static class CorrectIdentifierHandler
{
    public static async Task<CorrectIdentifierResult> Handle(CorrectIdentifier command, SerialisationDbContext db, IUnitEventWriter writer, IdentifierService identifiers, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (!IdentifierCorrectionReason.All.Contains(command.Reason))
        {
            throw new CommandRefusedException("reason_unknown", $"Reason '{command.Reason}' is not one of {string.Join(", ", IdentifierCorrectionReason.All)}.");
        }

        var unit = await db.Units.AsNoTracking().FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.UnitId, UnitEventType.IdentifierCorrected, command.EventTime);
        var replayed = await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime, cancellationToken);
        var open = await identifiers.OpenOfTypeAsync(unit.UnitId, command.IdentifierType, cancellationToken);
        if (replayed)
        {
            var current = open ?? throw new CommandRefusedException("replay_inconsistent", "The correction was recorded but the unit has no open identifier of that type.");
            var previous = await db.Identifiers.AsNoTracking().FirstAsync(i => i.TenantId == tenantId && i.SupersededByIdentifierId == current.IdentifierId, cancellationToken);
            return new CorrectIdentifierResult(unit.UnitId, previous.IdentifierId, current.IdentifierId, previous.NormalisedValue, current.NormalisedValue);
        }

        if (open is null)
        {
            throw new CommandRefusedException("no_open_identifier", $"Unit {unit.UnitId} has no open {command.IdentifierType} to correct.", "use AssignIdentifier to add one");
        }

        var itemId = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.UnitId == unit.UnitId && a.ValidTo == null).Select(a => (Guid?)a.ItemId).FirstOrDefaultAsync(cancellationToken);
        // Customer asset tags are scoped to the owning party (ADR-0001 customer_asset_tag); the open ownership row says who that is.
        var ownerPartyRef = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unit.UnitId && e.Kind == EpisodeKind.Ownership && e.ValidTo == null).Select(e => e.PartyRef).FirstOrDefaultAsync(cancellationToken);
        var prepared = identifiers.Prepare(new IdentifierInput(command.IdentifierType, command.NewRawValue, command.IssuerPartyRef ?? open.IssuerPartyRef, command.Qualifier ?? open.Qualifier, open.EnterpriseIdentifier), itemId, ownerPartyRef);
        if (prepared.Blocks)
        {
            throw new CommandRefusedException("identifier_invalid", $"{command.IdentifierType} '{command.NewRawValue}' fails validation: {string.Join(", ", prepared.Validation.Reasons)}.", "rescan or retype the value");
        }

        if (prepared.Normalised == open.NormalisedValue && prepared.ScopeKey == open.ScopeKey)
        {
            throw new CommandRefusedException("no_change", "The corrected value normalises to the current value.", "nothing to correct; check the raw value");
        }

        if (await identifiers.HolderOfAsync(prepared.Policy.Scheme, prepared.ScopeKey, prepared.Normalised, cancellationToken) is { } holder && holder.UnitId != unit.UnitId)
        {
            throw new CommandRefusedException("identifier_in_use", $"'{prepared.Normalised}' is open on unit {holder.UnitId} ({holder.LifecycleStatus}).", "if the two units are crossed use SwapIdentifiers (P3); if they are the same physical unit use MergeUnits (P3)");
        }

        var shipped = await db.Events.AsNoTracking().AnyAsync(e => e.TenantId == tenantId && e.UnitId == unit.UnitId && e.EventType == UnitEventType.Shipped, cancellationToken);
        if (shipped && string.IsNullOrWhiteSpace(command.ApproverRef))
        {
            throw new CommandRefusedException("approval_required", "The unit has appeared on an external document; a second approver is required.", "route through the correction workbench with an approver");
        }

        var eventId = TimeOrderedId.New(command.EventTime);
        var now = clock.UtcNow;
        var replacement = prepared.ToRow(tenantId, unit.UnitId, command.EventTime, command.ActorRef, now, eventId);
        replacement.SupersedesIdentifierId = open.IdentifierId;
        open.ValidTo = command.EventTime;
        open.SupersededByIdentifierId = replacement.IdentifierId;
        open.CorrectionReason = command.Reason;
        db.Identifiers.Add(replacement);
        await writer.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = unit.UnitId, EventTime = command.EventTime, EventType = UnitEventType.IdentifierCorrected, PresetEventId = eventId,
                LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef, IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new
                {
                    command_id = command.CommandId, identifier_type = command.IdentifierType, reason = command.Reason, approver_ref = command.ApproverRef, note = command.Note,
                    before = new { identifier_id = open.IdentifierId, raw_value = open.RawValue, normalised_value = open.NormalisedValue },
                    after = new { identifier_id = replacement.IdentifierId, raw_value = replacement.RawValue, normalised_value = replacement.NormalisedValue },
                    initiated_via = "command",
                }),
                Delta = null,
            },
        ], cancellationToken);
        return new CorrectIdentifierResult(unit.UnitId, open.IdentifierId, replacement.IdentifierId, open.NormalisedValue, replacement.NormalisedValue);
    }
}
