using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Projections;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T04: the writer and the inline projector (coupling 2) behave as the ADR states.</summary>
[Collection("serialisation host")]
public sealed class UnitEventWriterTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();

    private static UnitEventDraft Draft(Guid unit, DateTime at, string type, CurrentStateDelta? delta, string? key = null, string source = "test") => new()
    {
        UnitId = unit,
        EventTime = at,
        EventType = type,
        LegalEntityId = Entity,
        ActorRef = "test",
        IdempotencyKey = key ?? IdempotencyKey.ForCommand(source, Guid.NewGuid(), unit, type, at),
        Delta = delta,
    };

    private static CurrentStateDelta Commission(Guid item) => new() { Create = new CreateCurrentState(item, LifecycleStatus.PreRelease, UnitDisposition.InProgress) };

    [DatabaseFact]
    public async Task Append_writes_events_in_seq_order_and_projects_them_in_the_same_unit_of_work()
    {
        var tenant = await fixture.NewTenantAsync("w");
        var unit = TimeOrderedId.New();
        var item = Guid.NewGuid();
        var t0 = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var result = await writer.AppendAsync(
            [
                Draft(unit, t0.AddHours(2), UnitEventType.Released, new CurrentStateDelta { LifecycleStatus = LifecycleStatus.Released, Disposition = UnitDisposition.Available, LocationId = Guid.NewGuid(), LocationKind = LocationKind.Warehouse }),
                Draft(unit, t0, UnitEventType.Commissioned, Commission(item)),
                Draft(unit, t0.AddHours(1), UnitEventType.Assembled, null, source: "mes"),
            ], CancellationToken.None);
            Assert.Equal(3, result.Written.Count);
            Assert.Empty(result.Duplicates);
            // Nothing is visible until the caller commits: the projection is part of the same unit of work.
            Assert.Null(await db.Current.AsNoTracking().FirstOrDefaultAsync(c => c.UnitId == unit));
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var events = await db.Events.AsNoTracking().Where(e => e.UnitId == unit).OrderBy(e => e.Seq).ToListAsync();
            Assert.Equal([UnitEventType.Commissioned, UnitEventType.Assembled, UnitEventType.Released], events.Select(e => e.EventType));
            Assert.True(events[0].Seq < events[1].Seq && events[1].Seq < events[2].Seq);
            Assert.Equal(events.Select(e => e.EventTime).OrderBy(x => x), events.Select(e => e.EventTime));
            var current = await db.Current.AsNoTracking().SingleAsync(c => c.UnitId == unit);
            Assert.Equal(LifecycleStatus.Released, current.LifecycleStatus);
            Assert.Equal(UnitDisposition.Available, current.Disposition);
            Assert.Equal(item, current.ItemId);
            Assert.Equal(LocationKind.Warehouse, current.LocationKind);
            Assert.Equal(events[2].Seq, current.LastEventSeq);
            Assert.Equal(events[2].EventTime, current.LastEventTime);
            Assert.NotEmpty(current.RowVersion);
            // The delta travels with the event, so a replay needs no event semantics.
            Assert.Equal(LifecycleStatus.Released, UnitCurrentStateProjector.DeltaOf(events[2])!.LifecycleStatus);
            Assert.Null(UnitCurrentStateProjector.DeltaOf(events[1]));
            var history = await scope.ServiceProvider.GetRequiredService<UnitHistoryQuery>().OldestFirstAsync(unit, CancellationToken.None);
            Assert.Equal(3, history.Count);
        }
    }

    [DatabaseFact]
    public async Task A_repeated_idempotency_key_at_the_same_business_time_is_a_no_op()
    {
        var tenant = await fixture.NewTenantAsync("i");
        var unit = TimeOrderedId.New();
        var at = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc);
        var key = IdempotencyKey.For("cx01", "receipt", Guid.NewGuid(), 3, unit, UnitEventType.Commissioned, at);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            var first = await writer.AppendAsync([Draft(unit, at, UnitEventType.Commissioned, Commission(Guid.NewGuid()), key), Draft(unit, at, UnitEventType.Commissioned, Commission(Guid.NewGuid()), key)], CancellationToken.None);
            Assert.Single(first.Written);
            Assert.Single(first.Duplicates);
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            var again = await writer.AppendAsync([Draft(unit, at, UnitEventType.Commissioned, Commission(Guid.NewGuid()), key)], CancellationToken.None);
            Assert.Empty(again.Written);
            Assert.Single(again.Duplicates);
            // A different business time with the same key is a new event by definition; here it is also out of order
            // for the projection only if its seq is lower, which a fresh append never is.
            var later = await writer.AppendAsync([Draft(unit, at.AddDays(1), UnitEventType.FlagSet, new CurrentStateDelta { OverlayFlags = [OverlayFlag.Recalled] }, key)], CancellationToken.None);
            Assert.Single(later.Written);
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(2, await db.Events.CountAsync(e => e.UnitId == unit));
            Assert.Equal("[\"recalled\"]", (await db.Current.AsNoTracking().SingleAsync(c => c.UnitId == unit)).OverlayFlags);
        }
    }

    [DatabaseFact]
    public async Task Batched_idempotency_reads_match_exact_key_and_business_time_pairs()
    {
        var tenant = await fixture.NewTenantAsync("ip");
        var firstUnit = TimeOrderedId.New();
        var secondUnit = TimeOrderedId.New();
        var firstAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var secondAt = firstAt.AddMonths(1);
        const string firstKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string secondKey = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        using (var scope = fixture.ScopeFor(tenant))
        {
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            await writer.AppendAsync(
            [
                Draft(firstUnit, firstAt, UnitEventType.Commissioned, Commission(Guid.NewGuid()), firstKey),
                Draft(secondUnit, secondAt, UnitEventType.Commissioned, Commission(Guid.NewGuid()), secondKey),
            ], CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }

        using var check = fixture.ScopeFor(tenant);
        var reader = check.ServiceProvider.GetRequiredService<UnitEventIdempotencyReader>();
        Assert.Empty(await reader.SeenAsync(tenant,
            [(firstKey, secondAt), (secondKey, firstAt)], CancellationToken.None));
        var exact = await reader.SeenAsync(tenant,
            [(firstKey, firstAt), (secondKey, secondAt)], CancellationToken.None);
        Assert.Equal(2, exact.Count);
    }

    [DatabaseFact]
    public async Task An_append_with_a_base_order_writes_exactly_one_sparse_reference()
    {
        var tenant = await fixture.NewTenantAsync("wbase");
        var unit = TimeOrderedId.New();
        var order = new DocumentRef("sales_order", Guid.NewGuid(), 4);
        var at = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await scope.ServiceProvider.GetRequiredService<IUnitEventWriter>().AppendAsync(
            [
                Draft(unit, at, UnitEventType.Commissioned, Commission(Guid.NewGuid())) with
                {
                    Attributes = JsonObjectText.Of(new { base_order = order }),
                },
            ], CancellationToken.None);
            Assert.Single(db.ChangeTracker.Entries<LedgerBaseOrderReference>());
            await db.SaveChangesAsync();
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var reference = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
                .LedgerBaseOrderReferences.AsNoTracking().SingleAsync();
            Assert.Equal(order.Type, reference.BaseDocumentType);
            Assert.Equal(order.Id, reference.BaseDocumentId);
            Assert.Equal(order.Line, reference.BaseDocumentLine);
            Assert.Equal(unit, reference.UnitId);
            Assert.Equal(at, reference.EventTime);
        }
    }

    [DatabaseFact]
    public async Task The_projector_refuses_an_out_of_order_apply_and_a_change_before_creation()
    {
        var tenant = await fixture.NewTenantAsync("o");
        var unit = TimeOrderedId.New();
        var at = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Utc);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var projector = scope.ServiceProvider.GetRequiredService<UnitCurrentStateProjector>();
        var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();

        await Assert.ThrowsAsync<ProjectionStateException>(() => writer.AppendAsync([Draft(unit, at, UnitEventType.Released, new CurrentStateDelta { LifecycleStatus = LifecycleStatus.Released })], CancellationToken.None));
        db.ChangeTracker.Clear();

        await writer.AppendAsync([Draft(unit, at, UnitEventType.Commissioned, Commission(Guid.NewGuid()))], CancellationToken.None);
        await db.SaveChangesAsync();
        var current = await db.Current.SingleAsync(c => c.UnitId == unit);
        var stale = new UnitEvent { TenantId = tenant, EventTime = at, EventId = TimeOrderedId.New(), UnitId = unit, Seq = current.LastEventSeq, EventType = UnitEventType.Released, LegalEntityId = Entity, ActorRef = "test", IdempotencyKey = "stale" };
        var ex = await Assert.ThrowsAsync<OutOfOrderApplyException>(() => projector.ApplyAsync(tenant, unit, [(stale, new CurrentStateDelta { LifecycleStatus = LifecycleStatus.Released })], CancellationToken.None));
        Assert.Equal(current.LastEventSeq, ex.AttemptedSeq);
    }

    [DatabaseFact]
    public async Task Two_units_of_work_racing_on_one_unit_are_serialised_by_the_rowversion()
    {
        var tenant = await fixture.NewTenantAsync("r");
        var unit = TimeOrderedId.New();
        var at = new DateTime(2026, 10, 4, 9, 0, 0, DateTimeKind.Utc);
        using (var scope = fixture.ScopeFor(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<IUnitEventWriter>().AppendAsync([Draft(unit, at, UnitEventType.Commissioned, Commission(Guid.NewGuid()))], CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }

        using var first = fixture.ScopeFor(tenant);
        using var second = fixture.ScopeFor(tenant);
        await first.ServiceProvider.GetRequiredService<IUnitEventWriter>().AppendAsync([Draft(unit, at.AddMinutes(1), UnitEventType.Released, new CurrentStateDelta { LifecycleStatus = LifecycleStatus.Released })], CancellationToken.None);
        await second.ServiceProvider.GetRequiredService<IUnitEventWriter>().AppendAsync([Draft(unit, at.AddMinutes(2), UnitEventType.FlagSet, new CurrentStateDelta { OverlayFlags = [OverlayFlag.Lost] })], CancellationToken.None);
        await first.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync());

        using var check = fixture.ScopeFor(tenant);
        var db = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var current = await db.Current.AsNoTracking().SingleAsync(c => c.UnitId == unit);
        Assert.Equal(LifecycleStatus.Released, current.LifecycleStatus);
        Assert.Equal("[]", current.OverlayFlags);
        Assert.Equal(2, await db.Events.CountAsync(e => e.UnitId == unit));
    }

    [DatabaseFact]
    public async Task History_orders_by_business_time_then_recorded_time_then_seq()
    {
        var tenant = await fixture.NewTenantAsync("h");
        var unit = TimeOrderedId.New();
        var at = new DateTime(2026, 10, 5, 9, 0, 0, DateTimeKind.Utc);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            await writer.AppendAsync([Draft(unit, at.AddDays(2), UnitEventType.Commissioned, Commission(Guid.NewGuid()))], CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            // A back-dated fact recorded later: earlier business time, later recorded time and seq.
            var writer = scope.ServiceProvider.GetRequiredService<IUnitEventWriter>();
            await writer.AppendAsync([Draft(unit, at, UnitEventType.IdentifierAssigned, null)], CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            var history = await scope.ServiceProvider.GetRequiredService<UnitHistoryQuery>().OldestFirstAsync(unit, CancellationToken.None);
            Assert.Equal([UnitEventType.IdentifierAssigned, UnitEventType.Commissioned], history.Select(e => e.EventType));
            Assert.True(history[0].Seq > history[1].Seq, "seq follows recording order, history follows business time");
        }
    }

    [Fact]
    public void Idempotency_keys_are_deterministic_and_sixty_four_hex_characters()
    {
        var unit = Guid.NewGuid();
        var doc = Guid.NewGuid();
        var at = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
        var a = IdempotencyKey.For("cx01", "delivery", doc, 4, unit, UnitEventType.Shipped, at);
        var b = IdempotencyKey.For("cx01", "delivery", doc, 4, unit, UnitEventType.Shipped, at);
        var c = IdempotencyKey.For("cx01", "delivery", doc, 4, unit, UnitEventType.Shipped, at.AddSeconds(1));
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal(64, a.Length);
        Assert.All(a, ch => Assert.True(Uri.IsHexDigit(ch) && !char.IsUpper(ch)));
    }
}
