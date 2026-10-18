using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Rmas;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P6-T22: RMA list seek paging and the transactionally maintained pipeline projection.</summary>
[Collection("serialisation host")]
public sealed class RmaVolumeTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("55555555-0000-0000-0000-00000000d022");
    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();

    [DatabaseFact]
    public async Task Cursor_pages_exceptions_then_oldest_without_a_count_or_duplicate()
    {
        var tenant = await fixture.NewTenantAsync("rma-cursor");
        var rows = Enumerable.Range(0, 4).Select(index => NewRma(tenant, index)).ToList();
        rows[3].ExceptionCount = 1;
        rows[3].HasException = true;
        using (var seed = fixture.ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Rmas.AddRange(rows);
            db.RmaLines.Add(new RmaLine
            {
                TenantId = tenant,
                RmaLineId = Guid.NewGuid(),
                RmaId = rows[3].RmaId,
                Sequence = 1,
                RequestedUnitId = Guid.NewGuid(),
                OwnershipEpisodeId = Guid.NewGuid(),
                OriginalSaleDocumentType = "delivery",
                OriginalSaleDocumentId = Guid.NewGuid(),
                CoverageFamily = CoverageFamilyCode.Base,
                ClaimLineKind = ClaimLineKind.Parts,
                ReceiptOutcome = ReturnOutcome.Mismatch,
                IsException = true,
            });
            await db.SaveChangesAsync();
        }
        await RebuildAsync(tenant);

        using var scope = fixture.ScopeFor(tenant);
        var queries = scope.ServiceProvider.GetRequiredService<RmaQueries>();
        var first = await queries.ListAsync(new(null, null, null, null), At.AddDays(10), null, 2, CancellationToken.None);
        Assert.True(first.HasMore);
        Assert.NotNull(first.Continuation);
        Assert.Equal([rows[3].RmaId, rows[0].RmaId], first.Rows.Select(row => row.RmaId));

        var second = await queries.ListAsync(new(null, null, null, null), At.AddDays(10), first.Continuation, 2, CancellationToken.None);
        Assert.False(second.HasMore);
        Assert.Null(second.Continuation);
        Assert.Equal([rows[1].RmaId, rows[2].RmaId], second.Rows.Select(row => row.RmaId));
        Assert.Empty(first.Rows.Select(row => row.RmaId).Intersect(second.Rows.Select(row => row.RmaId)));
        Assert.Equal(4, first.Summary.Single(row => row.Stage == RmaStage.Requested).Count);
        Assert.Equal(1, first.Summary.Single(row => row.Stage == RmaStage.Requested).ExceptionCount);
    }

    [DatabaseFact]
    public async Task Moves_across_slots_keep_each_stripe_equal_to_its_source_and_rebuild_replays()
    {
        var tenant = await fixture.NewTenantAsync("rma-count");
        var rows = Enumerable.Range(0, 200).Select(index => NewRma(tenant, index)).ToList();
        using (var seed = fixture.ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Rmas.AddRange(rows);
            await db.SaveChangesAsync();
        }
        await RebuildAsync(tenant);

        await Task.WhenAll(rows.Select(row => MoveAsync(tenant, row.RmaId)));

        using (var verify = fixture.ScopeFor(tenant))
        {
            var db = verify.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await AssertCountersMatchSourceAsync(db);
            Assert.True(rows.Select(row => RmaStageCounterSlot.For(row.RmaId)).Distinct().Count() > 1);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE serialisation.rma_stage_counter SET document_count = document_count + 7
                WHERE tenant_id = {tenant.Value} AND stage = {RmaStage.Authorised};
                """);
        }

        var commandId = Guid.NewGuid();
        var rebuilt = await InvokeAsync<RebuildRmaStageCountersResult>(tenant,
            new RebuildRmaStageCounters(commandId, "browser supplied actor is ignored by the endpoint"));
        Assert.False(rebuilt.Replayed);
        Assert.Equal(200, rebuilt.Summary.Single(row => row.Stage == RmaStage.Authorised).Count);
        using (var verify = fixture.ScopeFor(tenant))
            await AssertCountersMatchSourceAsync(verify.ServiceProvider.GetRequiredService<SerialisationDbContext>());
        var replay = await InvokeAsync<RebuildRmaStageCountersResult>(tenant,
            new RebuildRmaStageCounters(commandId, "test administrator"));
        Assert.True(replay.Replayed);
    }

    [DatabaseFact]
    public async Task Concurrent_document_closures_move_the_stage_counter_once()
    {
        var tenant = await fixture.NewTenantAsync("rma-close-race");
        var rma = NewRma(tenant, 0);
        rma.Stage = RmaStage.Dispositioned;
        rma.DispositionedAt = At.AddHours(1);
        using (var seed = fixture.ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Rmas.Add(rma);
            db.RmaLines.Add(new RmaLine
            {
                TenantId = tenant,
                RmaLineId = Guid.NewGuid(),
                RmaId = rma.RmaId,
                Sequence = 1,
                RequestedUnitId = Guid.NewGuid(),
                OwnershipEpisodeId = Guid.NewGuid(),
                OriginalSaleDocumentType = "delivery",
                OriginalSaleDocumentId = Guid.NewGuid(),
                CoverageFamily = CoverageFamilyCode.Base,
                ClaimLineKind = ClaimLineKind.Parts,
                Disposition = RmaDisposition.Scrap,
            });
            await db.SaveChangesAsync();
        }
        await RebuildAsync(tenant);

        var results = await Task.WhenAll(
            InvokeAsync<CloseRmaResult>(tenant, new CloseRma(Guid.NewGuid(), rma.RmaId, At.AddHours(2), "test supervisor")),
            InvokeAsync<CloseRmaResult>(tenant, new CloseRma(Guid.NewGuid(), rma.RmaId, At.AddHours(2), "test supervisor")));

        Assert.All(results, result => Assert.True(result.Closed));
        using var verify = fixture.ScopeFor(tenant);
        var verifyDb = verify.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(RmaStage.Closed, (await verifyDb.Rmas.SingleAsync(row => row.RmaId == rma.RmaId)).Stage);
        Assert.Equal(0, await verifyDb.RmaStageCounters.Where(row => row.Stage == RmaStage.Dispositioned).SumAsync(row => row.DocumentCount));
        Assert.Equal(1, await verifyDb.RmaStageCounters.Where(row => row.Stage == RmaStage.Closed).SumAsync(row => row.DocumentCount));
    }

    [DatabaseFact]
    public async Task Forty_parallel_rma_requests_keep_striped_counters_equal_to_source_without_deadlock()
    {
        const int count = 40;
        var tenant = await fixture.NewTenantAsync("rma-request-parallel");
        var item = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var location = Guid.NewGuid();
        var serials = Enumerable.Range(0, count).Select(index => $"PAR-{index:D3}-{Guid.NewGuid():N}").ToList();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Purchased, serials.Select(serial => new CommissionLine(
                [new IdentifierInput(IdentifierType.InternalSerial, serial)])).ToList(), At.AddDays(-10), "test", Entity,
            LocationId: location, LocationKind: LocationKind.Warehouse));
        var units = commissioned.Lines.Select(line => line.UnitId!.Value).ToList();
        Assert.Equal(count, units.Count);

        var shipmentRef = new DocumentRef("delivery", Guid.NewGuid());
        var shipment = new ShipmentDocument(shipmentRef, customer, At.AddDays(-8), serials.Select((serial, index) =>
            new DocumentLine(index + 1, item, 1, [new DocumentSerial(serial)])).ToList(), location);
        Documents.Add(shipment);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(new(Guid.NewGuid(), Entity, shipment.PostedAt, "sales-test"), shipment.Document));
        var delivery = new DeliveryConfirmation(new("delivery_confirmation", Guid.NewGuid()), shipment.Document,
            At.AddDays(-7), location);
        Documents.Add(delivery);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesDeliveryConfirmed(new(Guid.NewGuid(), Entity, delivery.DeliveredAt, "carrier-test"), delivery.Document));
        await RebuildAsync(tenant); // Pre-create the tenant's 96 rows; bootstrap is deliberately not the hot path under test.

        var results = await Task.WhenAll(units.Select(unitId => InvokeAsync<RequestRmaResult>(tenant,
            new RequestRma(Guid.NewGuid(), DateOnly.FromDateTime(At), customer, customer, Entity, Entity, "AQ", "EUR",
                [new RmaRequestLineInput(UnitId: unitId, RequestedAmount: 10m)], At, "service desk"))));

        Assert.Equal(count, results.Select(result => result.RmaId).Distinct().Count());
        using var verify = fixture.ScopeFor(tenant);
        var db = verify.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertCountersMatchSourceAsync(db);
        Assert.Equal(count, await db.RmaStageCounters.Where(row => row.Stage == RmaStage.Requested)
            .SumAsync(row => row.DocumentCount));
    }

    [DatabaseFact]
    public async Task Requested_counter_accepts_two_hundred_concurrent_striped_increments_without_bootstrap_locking()
    {
        var tenant = await fixture.NewTenantAsync("rma-request-count");
        await RebuildAsync(tenant);

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => IncrementRequestedAsync(tenant, Guid.NewGuid())));

        using var verify = fixture.ScopeFor(tenant);
        var count = await verify.ServiceProvider.GetRequiredService<SerialisationDbContext>().RmaStageCounters
            .Where(row => row.Stage == RmaStage.Requested).SumAsync(row => row.DocumentCount);
        Assert.Equal(200, count);
    }

    private async Task MoveAsync(TenantId tenant, Guid rmaId)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var rma = await db.Rmas.SingleAsync(row => row.RmaId == rmaId);
        var counters = scope.ServiceProvider.GetRequiredService<RmaStageCounterWriter>();
        await RmaStateMachine.TransitionAsync(rma, RmaStage.Authorised, counters, CancellationToken.None);
        rma.AuthorisedAt = At.AddHours(1);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task IncrementRequestedAsync(TenantId tenant, Guid rmaId)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await scope.ServiceProvider.GetRequiredService<RmaStageCounterWriter>().AddRequestedAsync(rmaId, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static async Task AssertCountersMatchSourceAsync(SerialisationDbContext db)
    {
        var rmas = await db.Rmas.AsNoTracking().Select(row => new { row.RmaId, row.Stage, row.HasException }).ToListAsync();
        var source = rmas.GroupBy(row => (row.Stage, Slot: RmaStageCounterSlot.For(row.RmaId)))
            .ToDictionary(group => group.Key,
                group => (Documents: group.Count(), Exceptions: group.Count(row => row.HasException)));
        var counters = await db.RmaStageCounters.AsNoTracking().ToListAsync();
        Assert.Equal(RmaStage.All.Count * RmaStageCounterSlot.Count, counters.Count);
        foreach (var counter in counters)
        {
            var fact = source.GetValueOrDefault((counter.Stage, counter.Slot));
            Assert.Equal(fact.Documents, counter.DocumentCount);
            Assert.Equal(fact.Exceptions, counter.ExceptionCount);
        }
    }

    private Task<RebuildRmaStageCountersResult> RebuildAsync(TenantId tenant) =>
        InvokeAsync<RebuildRmaStageCountersResult>(tenant, new RebuildRmaStageCounters(Guid.NewGuid(), "test administrator"));

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static Rma NewRma(TenantId tenant, int index) => new()
    {
        TenantId = tenant,
        RmaId = Guid.NewGuid(),
        CommandId = Guid.NewGuid(),
        Stage = RmaStage.Requested,
        RequestDate = DateOnly.FromDateTime(At.AddMinutes(index)),
        ReturningPartyRef = Guid.NewGuid(),
        SettlementPartyRef = Guid.NewGuid(),
        ServiceEntityId = Guid.NewGuid(),
        ObligorEntityId = Guid.NewGuid(),
        ServiceCountry = "AQ",
        Currency = "EUR",
        RequestedAt = At.AddMinutes(index),
        RequestedBy = "test requester",
        LastActorRef = "test requester",
        LastEventId = Guid.NewGuid(),
        LastModifiedAt = At,
        LastModifiedBy = "test requester",
    };
}
