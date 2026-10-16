using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

/// <summary><see cref="AssignIdentifier"/>: the pending-identifier path (case 6) and additional identifier types on an existing unit.</summary>
public static class AssignIdentifierHandler
{
    public static async Task<AssignIdentifierResult> Handle(AssignIdentifier command, SerialisationDbContext db, IUnitEventWriter writer, IdentifierService identifiers, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var unit = await db.Units.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.UnitId, UnitEventType.IdentifierAssigned, command.EventTime);
        var replay = await db.Identifiers.AsNoTracking().Join(db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime), i => i.EventId, e => e.EventId, (i, e) => i).FirstOrDefaultAsync(cancellationToken);
        if (replay is not null)
        {
            return new AssignIdentifierResult(unit.UnitId, replay.IdentifierId, replay.NormalisedValue, unit.LifecycleStatus);
        }

        var itemId = await db.ItemAssignments.AsNoTracking().Where(a => a.TenantId == tenantId && a.UnitId == unit.UnitId && a.ValidTo == null).Select(a => (Guid?)a.ItemId).FirstOrDefaultAsync(cancellationToken);
        // Customer asset tags are scoped to the owning party (ADR-0001 customer_asset_tag); the open ownership row says who that is.
        var ownerPartyRef = await db.Episodes.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unit.UnitId && e.Kind == EpisodeKind.Ownership && e.ValidTo == null).Select(e => e.PartyRef).FirstOrDefaultAsync(cancellationToken);
        var prepared = identifiers.Prepare(command.Identifier, itemId, ownerPartyRef);
        if (prepared.Blocks)
        {
            throw new CommandRefusedException("identifier_invalid", $"{command.Identifier.Type} '{command.Identifier.RawValue}' fails validation: {string.Join(", ", prepared.Validation.Reasons)}.", "rescan or retype the value");
        }

        if (await identifiers.OpenOfTypeAsync(unit.UnitId, command.Identifier.Type, cancellationToken) is not null)
        {
            throw new CommandRefusedException("identifier_type_already_open", $"Unit {unit.UnitId} already has an open {command.Identifier.Type}.", "use CorrectIdentifier to replace it");
        }

        if (await identifiers.HolderOfAsync(prepared.Policy.Scheme, prepared.ScopeKey, prepared.Normalised, cancellationToken) is { } holder)
        {
            throw new CommandRefusedException("identifier_in_use", $"{command.Identifier.Type} '{prepared.Normalised}' is open on unit {holder.UnitId} ({holder.LifecycleStatus}).", "two records for one physical unit are joined with MergeUnits (P3); otherwise choose another value");
        }

        var wasPending = unit.LifecycleStatus == LifecycleStatus.PendingIdentifier;
        var next = wasPending ? UnitLifecycle.Require(unit.LifecycleStatus, UnitEventType.IdentifierAssigned, unit.Origin, unit.UnitId) : unit.LifecycleStatus;
        var eventId = TimeOrderedId.New(command.EventTime);
        var now = clock.UtcNow;
        var row = prepared.ToRow(tenantId, unit.UnitId, command.EventTime, command.ActorRef, now, eventId);
        await writer.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = unit.UnitId, EventTime = command.EventTime, EventType = UnitEventType.IdentifierAssigned, PresetEventId = eventId, BizStep = "commissioning",
                LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef, DocumentType = command.Document?.Type, DocumentId = command.Document?.Id, DocumentLine = command.Document?.Line,
                IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new { command_id = command.CommandId, identifier_type = command.Identifier.Type, raw_value = command.Identifier.RawValue, normalised_value = prepared.Normalised, artefacts = prepared.Artefacts, warnings = prepared.Validation.IsValid ? null : prepared.Validation.Reasons }),
                Delta = wasPending ? new CurrentStateDelta { LifecycleStatus = next, Disposition = UnitLifecycle.DispositionFor(next) } : null,
            },
        ], cancellationToken);
        db.Identifiers.Add(row);
        unit.LifecycleStatus = next;
        return new AssignIdentifierResult(unit.UnitId, row.IdentifierId, row.NormalisedValue, next);
    }
}
