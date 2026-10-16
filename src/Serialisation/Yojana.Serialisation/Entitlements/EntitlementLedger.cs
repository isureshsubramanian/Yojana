using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// Writes an entitlement fact to the unit's ledger with the coverage it leaves behind: <c>live_entitlement_ids</c> and
/// <c>warranty_end_max</c> on <c>unit_current</c> travel as the event's delta, so the inline projection (coupling 2)
/// keeps them read-your-writes consistent with the entitlement rows written in the same unit of work. The coverage
/// before the fact is recorded under <c>coverage_before</c>, so <see cref="PublishedEventMapper"/> can publish
/// <c>Entitlement.CoverageChanged</c> from the event alone.
/// </summary>
public sealed class EntitlementLedger(SerialisationDbContext db, IUnitEventWriter writer, ITenantContext tenant)
{
    public static string Key(Guid commandId, Guid unitId, string eventType, DateTime at) => IdempotencyKey.ForCommand(CommissionUnitsHandler.Source, commandId, unitId, eventType, at);

    /// <summary>The event a command already wrote, for replays.</summary>
    public Task<UnitEvent?> ReplayedAsync(string key, DateTime at, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.IdempotencyKey == key && e.EventTime == at, cancellationToken);
    }

    public async Task<Entitlement> LoadAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return await db.Entitlements.FirstOrDefaultAsync(e => e.TenantId == tenantId && e.EntitlementId == entitlementId, cancellationToken)
            ?? throw new CommandRefusedException("entitlement_unknown", $"Entitlement {entitlementId} does not exist.");
    }

    /// <summary>Moves the entitlement through the state machine or refuses with the rule that stops it.</summary>
    public static void Transition(Entitlement entitlement, string eventType)
    {
        var next = EntitlementStateMachine.Next(entitlement.Status, eventType)
            ?? throw new CommandRefusedException(entitlement.Status == EntitlementStatus.Closed ? "entitlement_closed" : "transition_not_allowed",
                $"Entitlement {entitlement.EntitlementId} is {entitlement.Status}{(entitlement.ClosedReason is null ? "" : " (" + entitlement.ClosedReason + ")")}; {eventType} is not allowed from there.",
                entitlement.Status == EntitlementStatus.Closed ? "a closed entitlement is never reopened; create a new one that references it" : null);
        entitlement.Status = next;
    }

    public async Task<Guid> AppendAsync(Entitlement entitlement, string eventType, string key, DateTime at, string actorRef, object attributes, DocumentRef? document, CancellationToken cancellationToken, Guid? presetEventId = null)
    {
        // Saved first, so the coverage read below sees this entitlement's new state together with the unit's others.
        await db.SaveChangesAsync(cancellationToken);
        var tenantId = tenant.TenantId;
        var live = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == entitlement.UnitId && e.Status != EntitlementStatus.Closed)
            .Select(e => new { e.EntitlementId, e.EndDate }).ToListAsync(cancellationToken);
        // Tracked, so a row this unit of work already changed answers with its in-memory coverage.
        var current = await db.Current.FirstOrDefaultAsync(c => c.TenantId == tenantId && c.UnitId == entitlement.UnitId, cancellationToken);
        var recorded = JsonSerializer.SerializeToNode(attributes, LedgerJson.Options)!.AsObject();
        recorded["coverage_before"] = JsonSerializer.SerializeToNode(new
        {
            live_entitlement_ids = (current is null ? [] : JsonSerializer.Deserialize<List<Guid>>(current.LiveEntitlementIds, LedgerJson.Options) ?? []).Order().ToList(),
            warranty_end_max = current?.WarrantyEndMax,
        }, LedgerJson.Options);
        var eventId = presetEventId ?? TimeOrderedId.New(at);
        await writer.AppendAsync(
        [
            new UnitEventDraft
            {
                UnitId = entitlement.UnitId, EventTime = at, EventType = eventType, PresetEventId = eventId, LegalEntityId = entitlement.ObligorEntityId, ActorRef = actorRef,
                DocumentType = document?.Type, DocumentId = document?.Id, DocumentLine = document?.Line, IdempotencyKey = key, Attributes = JsonObjectText.Parse(recorded.ToJsonString()),
                Delta = new CurrentStateDelta { LiveEntitlementIds = live.Select(l => l.EntitlementId).Order().ToList(), WarrantyEndMax = new Optional<DateOnly?>(live.Max(l => l.EndDate)) },
            },
        ], cancellationToken);
        return eventId;
    }
}
