using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Projections;

// TIGHT COUPLING (declared, ADR-0001 "Integration boundaries and coupling", coupling 2)
// WHAT:  UnitEventWriter.Append(...) invokes UnitCurrentStateProjector.Apply(...) in the same
//        transaction and the same ordering (events for one unit are applied in seq order); the
//        projector reads unit_current.last_event_seq and refuses an out-of-order apply.
// WHY:   unit_current is the row every operational query (ATP, picking, entitlement check, RMA receipt)
//        reads; an asynchronous projection would let a shipment be picked twice or a returned unit be
//        allocated before the return is visible. The ledger is the truth; the projection must be
//        read-your-writes consistent with it.
// WHAT BREAKS IF SEPARATED: stale current state under load; double allocation of a serial; the
//        replay-and-diff healer would become the primary path instead of a safety net. The async
//        projections (dashboards, passport exports) are deliberately NOT part of this coupling and
//        may lag. Change the seq contract and this comment together, on both sides.
// Engine: SQL Server / Azure SQL; the pairing relies on a single local transaction, never on a distributed one.

/// <summary>
/// Applies <see cref="CurrentStateDelta"/>s to <c>unit_current</c> in seq order (see the coupling comment above and in
/// <see cref="UnitEventWriter"/>). The row is tracked by the module's DbContext; its <c>rowversion</c> is the
/// concurrency token, so two units of work racing on one unit fail at <c>SaveChanges</c> with
/// <see cref="DbUpdateConcurrencyException"/> and one of them retries from a fresh read. The same fold is used by the
/// replay-and-diff healer, which is why it is a pure function of (row, delta).
/// </summary>
public sealed class UnitCurrentStateProjector(SerialisationDbContext db)
{
    /// <summary>Attributes key under which the writer stores the delta an event carried.</summary>
    public const string StateKey = "_state";
    public const string ProjectsStateKey = "_projects_current_state";

    /// <summary>Reads the current rows of many units into the unit of work, so the applies that follow find them without a query each.</summary>
    public async Task PreloadAsync(TenantId tenant, IReadOnlyCollection<Guid> unitIds, CancellationToken cancellationToken)
    {
        if (unitIds.Count < 2) return;
        foreach (var ids in unitIds.Chunk(2000))
        {
            await db.Current.Where(c => c.TenantId == tenant && ids.Contains(c.UnitId)).LoadAsync(cancellationToken);
        }
    }

    public async Task ApplyAsync(TenantId tenant, Guid unitId, IReadOnlyList<(UnitEvent Event, CurrentStateDelta? Delta)> events, CancellationToken cancellationToken)
    {
        // FindAsync answers from the unit of work when the row is tracked (a preload, or an earlier apply) and queries otherwise.
        var current = await db.Current.FindAsync([tenant, unitId], cancellationToken);
        foreach (var (@event, delta) in events)
        {
            if (current is not null && @event.Seq <= current.LastEventSeq)
            {
                throw new OutOfOrderApplyException(unitId, current.LastEventSeq, @event.Seq);
            }

            if (delta is null)
            {
                if (current is null)
                {
                    throw new ProjectionStateException($"Unit {unitId}: event {@event.EventType} carries no state but the unit has no current state yet.");
                }

                current.LastEventSeq = @event.Seq;
                current.LastEventTime = @event.EventTime;
                continue;
            }

            if (delta.Create is not null)
            {
                if (current is not null)
                {
                    throw new ProjectionStateException($"Unit {unitId}: event {@event.EventType} creates current state but a row exists at seq {current.LastEventSeq}.");
                }

                current = new UnitCurrent
                {
                    TenantId = tenant,
                    UnitId = unitId,
                    ItemId = delta.Create.ItemId,
                    ModelVersionId = delta.Create.ModelVersionId,
                    LotId = delta.Create.LotId,
                    LifecycleStatus = delta.Create.LifecycleStatus,
                    Disposition = delta.Create.Disposition,
                    LastEventSeq = @event.Seq,
                    LastEventTime = @event.EventTime,
                };
                db.Current.Add(current);
            }
            else if (current is null)
            {
                throw new ProjectionStateException($"Unit {unitId}: event {@event.EventType} changes current state but the unit has no row; the first event must carry Create.");
            }

            Fold(current, delta);
            current.LastEventSeq = @event.Seq;
            current.LastEventTime = @event.EventTime;
        }
    }

    /// <summary>The pure fold: applies one delta to a row. Shared with the healer so both compute the same state.</summary>
    public static void Fold(UnitCurrent row, CurrentStateDelta delta)
    {
        if (delta.LifecycleStatus is not null) row.LifecycleStatus = delta.LifecycleStatus;
        if (delta.Disposition is not null) row.Disposition = delta.Disposition;
        if (delta.ConditionGrade is not null) row.ConditionGrade = delta.ConditionGrade;
        if (delta.ItemId is not null) row.ItemId = delta.ItemId.Value;
        if (delta.ClearModelVersionId) row.ModelVersionId = null;
        else if (delta.ModelVersionId is not null) row.ModelVersionId = delta.ModelVersionId;
        if (delta.LotId is not null) row.LotId = delta.LotId;
        if (delta.LocationId is { } location) row.LocationId = location.Value;
        if (delta.LocationKind is { } locationKind) row.LocationKind = locationKind.Value;
        if (delta.CustodyPartyRef is { } custodyParty) row.CustodyPartyRef = custodyParty.Value;
        if (delta.CustodyKind is { } custodyKind) row.CustodyKind = custodyKind.Value;
        if (delta.OpenCustodyEpisodeId is { } custodyEpisode) row.OpenCustodyEpisodeId = custodyEpisode.Value;
        if (delta.OwnerPartyRef is { } owner) row.OwnerPartyRef = owner.Value;
        if (delta.OpenOwnershipEpisodeId is { } ownershipEpisode) row.OpenOwnershipEpisodeId = ownershipEpisode.Value;
        if (delta.OverlayFlags is not null) row.OverlayFlags = JsonSerializer.Serialize(delta.OverlayFlags, LedgerJson.Options);
        if (delta.LiveEntitlementIds is not null) row.LiveEntitlementIds = JsonSerializer.Serialize(delta.LiveEntitlementIds, LedgerJson.Options);
        if (delta.WarrantyEndMax is { } warrantyEnd) row.WarrantyEndMax = warrantyEnd.Value;
        if (delta.Attributes is not null) row.Attributes = MergeAttributes(row.Attributes, delta.Attributes);
    }

    /// <summary>Reads the delta the writer stored in an event's attributes; null when the event carried none.</summary>
    public static CurrentStateDelta? DeltaOf(UnitEventRow @event)
    {
        if (@event.Attributes is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(@event.Attributes);
        return doc.RootElement.TryGetProperty(StateKey, out var state) ? state.Deserialize<CurrentStateDelta>(LedgerJson.Options) : null;
    }

    /// <summary>True unless the writer explicitly persisted this event as a state-neutral audit fact.</summary>
    public static bool ProjectsCurrentState(string? attributes)
    {
        if (attributes is null) return true;
        using var doc = JsonDocument.Parse(attributes);
        return !doc.RootElement.TryGetProperty(ProjectsStateKey, out var projects)
            || projects.ValueKind != JsonValueKind.False;
    }

    private static string MergeAttributes(string? existing, IReadOnlyDictionary<string, JsonElement?> changes)
    {
        var merged = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (existing is not null)
        {
            using var doc = JsonDocument.Parse(existing);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                merged[property.Name] = property.Value.Clone();
            }
        }

        foreach (var (key, value) in changes)
        {
            if (value is null) merged.Remove(key); else merged[key] = value.Value;
        }

        return JsonSerializer.Serialize(merged, LedgerJson.Options);
    }
}
