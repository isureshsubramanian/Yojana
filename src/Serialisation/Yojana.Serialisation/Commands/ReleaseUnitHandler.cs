using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Commands;

/// <summary>A rule that may stop a release (Identity rule 12 completeness arrives with P1-T12; later phases add theirs).</summary>
public interface IReleaseGuard
{
    /// <summary>Null when the release may proceed; otherwise the refusal.</summary>
    Task<CommandRefusedException?> CheckAsync(Unit unit, Guid itemId, DateTime eventTime, CancellationToken cancellationToken);
}

/// <summary>
/// <see cref="ReleaseUnit"/>: <c>pre_release → released</c> on the release signature; the as-built (open AssemblyLinks),
/// identifiers, item and test results are frozen into an immutable BuildRecord version (certificate version 1 reads it).
/// </summary>
public static class ReleaseUnitHandler
{
    public static async Task<ReleaseUnitResult> Handle(ReleaseUnit command, SerialisationDbContext db, IUnitEventWriter writer, IEnumerable<IReleaseGuard> guards, ITenantContext tenant, IClock clock, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var unit = await db.Units.FirstOrDefaultAsync(u => u.TenantId == tenantId && u.UnitId == command.UnitId, cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {command.UnitId} does not exist.");
        var key = IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, command.CommandId, command.UnitId, UnitEventType.Released, command.EventTime);
        var replayed = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == command.EventTime, cancellationToken);
        if (replayed is not null)
        {
            var existingRecord = await db.BuildRecords.AsNoTracking().FirstAsync(b => b.TenantId == tenantId && b.ReleaseEventId == replayed.EventId, cancellationToken);
            return new ReleaseUnitResult(unit.UnitId, existingRecord.BuildRecordId, existingRecord.Version, unit.LifecycleStatus);
        }

        var next = UnitLifecycle.Require(unit.LifecycleStatus, UnitEventType.Released, unit.Origin, unit.UnitId);
        var assignment = await db.ItemAssignments.AsNoTracking().FirstOrDefaultAsync(a => a.TenantId == tenantId && a.UnitId == unit.UnitId && a.ValidTo == null, cancellationToken)
            ?? throw new CommandRefusedException("no_item_assignment", $"Unit {unit.UnitId} has no current item assignment.");
        foreach (var guard in guards)
        {
            if (await guard.CheckAsync(unit, assignment.ItemId, command.EventTime, cancellationToken) is { } refusal)
            {
                throw refusal;
            }
        }

        var eventId = TimeOrderedId.New(command.EventTime);
        var record = await BuildRecords.FreezeAsync(db, tenantId, unit, assignment, eventId, command.EventTime, command.ReleaseSignatureRef, command.Revision, command.FacilityId, command.TestResultsJson, cancellationToken);
        var version = record.Version;
        var children = record.Payload.Contains("\"as_built\":[]", StringComparison.Ordinal) ? 0 : 1;
        await writer.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = unit.UnitId, EventTime = command.EventTime, EventType = UnitEventType.Released, PresetEventId = eventId, BizStep = "inspecting", Disposition = UnitDisposition.Active,
                LegalEntityId = command.LegalEntityId, ActorRef = command.ActorRef, DocumentType = command.Document?.Type, DocumentId = command.Document?.Id, DocumentLine = command.Document?.Line,
                IdempotencyKey = key,
                Attributes = JsonObjectText.Of(new
                {
                    command_id = command.CommandId, item_id = assignment.ItemId, build_record_id = record.BuildRecordId, build_record_version = version, payload_hash = record.PayloadHash, release_signature_ref = command.ReleaseSignatureRef, has_components = children == 1,
                    identifiers = (await db.Identifiers.AsNoTracking().Where(i => i.TenantId == tenantId && i.UnitId == unit.UnitId && i.ValidTo == null).ToListAsync(cancellationToken)).Select(i => new { type = i.Type, scheme = i.Scheme, value = i.NormalisedValue, issuer_party_ref = i.IssuerPartyRef }).ToList(),
                }),
                Delta = new CurrentStateDelta { LifecycleStatus = next, Disposition = UnitLifecycle.DispositionFor(next) },
            },
        ], cancellationToken);
        unit.LifecycleStatus = next;
        return new ReleaseUnitResult(unit.UnitId, record.BuildRecordId, version, next);
    }
}
