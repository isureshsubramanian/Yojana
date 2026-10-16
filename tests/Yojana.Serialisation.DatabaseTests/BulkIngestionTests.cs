using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T06: the set-based path commissions thousands of serials in one transaction and is idempotent.</summary>
[Collection("serialisation host")]
public sealed class BulkIngestionTests(SerialisationHostFixture fixture)
{
    private static BulkIngestion.Batch ReceiptBatch(TenantId tenant, Guid item, Guid receipt, string prefix, int from, int count, DateTime at)
    {
        var events = new List<UnitEventDraft>(count);
        var units = new List<Unit>(count);
        var identifiers = new List<UnitIdentifier>(count);
        var assignments = new List<UnitItemAssignment>(count);
        var episodes = new List<CustodyEpisode>(count);
        var location = Guid.NewGuid();
        var entity = Guid.NewGuid();
        for (var n = from; n < from + count; n++)
        {
            var unit = TimeOrderedId.New(at);
            var eventId = TimeOrderedId.New(at);
            var serial = $"{prefix}-{n:D6}";
            // The event id of the creating event is known up front (the writer keeps the caller's id for dependents).
            events.Add(new UnitEventDraft
            {
                UnitId = unit, EventTime = at, EventType = UnitEventType.Commissioned, LegalEntityId = entity, ActorRef = "receiving",
                DocumentType = "receipt", DocumentId = receipt, DocumentLine = 3, LocationId = location,
                IdempotencyKey = IdempotencyKey.For("cx01", "receipt", receipt, 3, unit, UnitEventType.Commissioned, at),
                Delta = new CurrentStateDelta { Create = new CreateCurrentState(item, LifecycleStatus.Released, UnitDisposition.Available), LocationId = location, LocationKind = LocationKind.Warehouse, CustodyKind = EpisodeKind.Custody },
            });
            units.Add(new Unit { TenantId = tenant, UnitId = unit, LifecycleStatus = LifecycleStatus.Released, Origin = UnitOrigin.Purchased, SerialisationPoint = SerialisationPoint.Receipt, RetentionClass = "standard_10y", CreatedAt = at, CreatedEventId = eventId });
            identifiers.Add(new UnitIdentifier { TenantId = tenant, IdentifierId = TimeOrderedId.New(at), UnitId = unit, Type = IdentifierType.InternalSerial, Scheme = IdentifierScheme.Internal, ScopeKey = item.ToString("N"), RawValue = serial, NormalisedValue = serial.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant(), ValidFrom = at, ActorRef = "receiving", RecordedTime = at, EventId = eventId });
            assignments.Add(new UnitItemAssignment { TenantId = tenant, AssignmentId = TimeOrderedId.New(at), UnitId = unit, ItemId = item, ValidFrom = at, Reason = ItemAssignmentReason.Initial, EventId = eventId });
            episodes.Add(new CustodyEpisode { TenantId = tenant, EpisodeId = TimeOrderedId.New(at), UnitId = unit, Kind = EpisodeKind.Custody, PartyType = PartyType.Us, LocationId = location, LocationKind = LocationKind.Warehouse, Reason = EpisodeReason.Receipt, Source = EpisodeSource.Receipt, Confidence = Confidence.High, ValidFrom = at, OpeningDocumentType = "receipt", OpeningDocumentId = receipt, OpeningDocumentLine = 3, OpeningEventId = eventId });
        }

        return new BulkIngestion.Batch(events, units, identifiers, assignments, episodes);
    }

    [DatabaseFact]
    public async Task Ten_thousand_serials_commission_in_one_transaction_and_a_replay_inserts_nothing()
    {
        var tenant = await fixture.NewTenantAsync("bk");
        var item = Guid.NewGuid();
        var receipt = Guid.NewGuid();
        var at = new DateTime(2026, 10, 7, 9, 41, 0, DateTimeKind.Utc);
        var batch = ReceiptBatch(tenant, item, receipt, "BK" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(), 1, 10_000, at);
        // Dependents must name the creating event's id: the batch builder used its own ids, so align them here as a command would.
        batch = Align(batch);

        BulkIngestion.Result first;
        var clock = Stopwatch.StartNew();
        using (var scope = fixture.ScopeFor(tenant))
        {
            first = await scope.ServiceProvider.GetRequiredService<BulkIngestion>().IngestAsync(batch, CancellationToken.None);
        }

        clock.Stop();
        Assert.Equal(10_000, first.EventsInserted);
        Assert.Equal(0, first.EventsDuplicate);
        Assert.Equal(10_000, first.UnitsInserted);
        Assert.Equal(10_000, first.IdentifiersInserted);
        Assert.Equal(10_000, first.EpisodesInserted);
        Assert.Equal(10_000, first.CurrentInserted);
        Assert.Equal(first.FirstSeq + 9_999, first.LastSeq);
        // Development-machine evidence only (Express under emulation); the SLO is measured on the shipping edition in P1-T21.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"10,000-serial receipt took {clock.Elapsed}");

        using (var scope = fixture.ScopeFor(tenant))
        {
            var replay = await scope.ServiceProvider.GetRequiredService<BulkIngestion>().IngestAsync(batch, CancellationToken.None);
            Assert.Equal(0, replay.EventsInserted);
            Assert.Equal(10_000, replay.EventsDuplicate);
            Assert.Equal(0, replay.UnitsInserted);
            Assert.Equal(0, replay.CurrentInserted + replay.CurrentUpdated);
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(10_000, await db.Units.CountAsync(u => u.CreatedAt == at));
            Assert.Equal(10_000, await db.Current.CountAsync(c => c.ItemId == item));
            var one = await db.Current.AsNoTracking().FirstAsync(c => c.ItemId == item);
            Assert.Equal(LocationKind.Warehouse, one.LocationKind);
            Assert.Equal(await db.Events.Where(e => e.UnitId == one.UnitId).MaxAsync(e => e.Seq), one.LastEventSeq);
        }
    }

    [DatabaseFact]
    public async Task Set_validation_finds_batch_duplicates_and_identifiers_open_on_other_units()
    {
        var tenant = await fixture.NewTenantAsync("bv");
        var item = Guid.NewGuid();
        var at = new DateTime(2026, 10, 8, 9, 0, 0, DateTimeKind.Utc);
        var prefix = "BV" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        using (var scope = fixture.ScopeFor(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<BulkIngestion>().IngestAsync(Align(ReceiptBatch(tenant, item, Guid.NewGuid(), prefix, 1, 50, at)), CancellationToken.None);
        }

        var next = Align(ReceiptBatch(tenant, item, Guid.NewGuid(), prefix, 40, 20, at.AddDays(1))); // 40..59: eleven collide with 40..50
        var last = next.Identifiers[^1];
        var repeated = new UnitIdentifier { TenantId = last.TenantId, IdentifierId = TimeOrderedId.New(), UnitId = TimeOrderedId.New(), Type = last.Type, Scheme = last.Scheme, ScopeKey = last.ScopeKey, RawValue = last.RawValue, NormalisedValue = last.NormalisedValue, ValidFrom = last.ValidFrom, ActorRef = last.ActorRef, RecordedTime = last.RecordedTime, EventId = last.EventId };
        var withBatchDuplicate = next.Identifiers.Append(repeated).ToList();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var conflicts = await scope.ServiceProvider.GetRequiredService<BulkIngestion>().ValidateIdentifiersAsync(withBatchDuplicate, CancellationToken.None);
            Assert.Equal(11, conflicts.Count(c => c.Reason == "already_open_on_another_unit"));
            Assert.All(conflicts.Where(c => c.Reason == "already_open_on_another_unit"), c => Assert.NotNull(c.ExistingUnitId));
            var dup = Assert.Single(conflicts, c => c.Reason == "duplicate_in_batch");
            Assert.Equal(next.Identifiers[^1].NormalisedValue, dup.NormalisedValue);
        }
    }

    [DatabaseFact]
    public async Task A_later_bulk_event_updates_current_state_and_an_older_seq_never_overwrites_a_newer_row()
    {
        var tenant = await fixture.NewTenantAsync("bu");
        var item = Guid.NewGuid();
        var at = new DateTime(2026, 10, 9, 9, 0, 0, DateTimeKind.Utc);
        var batch = Align(ReceiptBatch(tenant, item, Guid.NewGuid(), "BU" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(), 1, 5, at));
        using (var scope = fixture.ScopeFor(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<BulkIngestion>().IngestAsync(batch, CancellationToken.None);
        }

        // A shipment of the five units through the bulk path: no new units, five events, five current rows updated.
        var delivery = Guid.NewGuid();
        var shipment = batch.Units.Select(u => new UnitEventDraft
        {
            UnitId = u.UnitId, EventTime = at.AddDays(2), EventType = UnitEventType.Shipped, LegalEntityId = Guid.NewGuid(), ActorRef = "shipping", DocumentType = "delivery", DocumentId = delivery,
            IdempotencyKey = IdempotencyKey.For("cx01", "delivery", delivery, 1, u.UnitId, UnitEventType.Shipped, at.AddDays(2)),
            Delta = new CurrentStateDelta { Disposition = UnitDisposition.InTransit, LocationKind = LocationKind.InTransit, LocationId = (Guid?)null },
        }).ToList();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var result = await scope.ServiceProvider.GetRequiredService<BulkIngestion>().IngestAsync(new BulkIngestion.Batch(shipment, [], [], [], []), CancellationToken.None);
            Assert.Equal(5, result.EventsInserted);
            Assert.Equal(0, result.CurrentInserted);
            Assert.Equal(5, result.CurrentUpdated);
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var rows = await db.Current.AsNoTracking().Where(c => c.ItemId == item).ToListAsync();
            Assert.All(rows, r => { Assert.Equal(UnitDisposition.InTransit, r.Disposition); Assert.Null(r.LocationId); Assert.Equal(LocationKind.InTransit, r.LocationKind); });
            // Simulate a stale projection row racing ahead: bump last_event_seq beyond anything issued, then ingest once more.
            var victim = batch.Units[0].UnitId;
            await db.Database.ExecuteSqlAsync($"UPDATE [serialisation].[unit_current] SET last_event_seq = last_event_seq + 1000000 WHERE unit_id = {victim}");
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var flag = new UnitEventDraft { UnitId = batch.Units[0].UnitId, EventTime = at.AddDays(3), EventType = UnitEventType.FlagSet, LegalEntityId = Guid.NewGuid(), ActorRef = "test", IdempotencyKey = IdempotencyKey.ForCommand("t", Guid.NewGuid(), batch.Units[0].UnitId, UnitEventType.FlagSet, at.AddDays(3)), Delta = new CurrentStateDelta { OverlayFlags = [OverlayFlag.Lost] } };
            var result = await scope.ServiceProvider.GetRequiredService<BulkIngestion>().IngestAsync(new BulkIngestion.Batch([flag], [], [], [], []), CancellationToken.None);
            Assert.Equal(1, result.EventsInserted);
            Assert.Equal(0, result.CurrentUpdated);
            Assert.Equal(1, result.CurrentOutOfOrder); // the event is in the ledger; the healer reconciles the row
        }
    }

    /// <summary>Gives every dependent row the id the ingestion will assign to its creating event: the draft's key is the join.</summary>
    private static BulkIngestion.Batch Align(BulkIngestion.Batch batch)
    {
        // Ingestion assigns event ids itself; dependents must therefore reference the draft, not an id. Until the command
        // layer (P1-T09) exists, tests align by unit: one creating event per unit.
        var eventIdByUnit = batch.Units.ToDictionary(u => u.UnitId, u => u.CreatedEventId);
        var events = batch.Events.Select(e => e with { PresetEventId = eventIdByUnit.TryGetValue(e.UnitId, out var id) ? id : null }).ToList();
        return batch with { Events = events };
    }
}
