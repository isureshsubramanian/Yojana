using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Selection;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class LedgerBaseOrderBackfillTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Job_resumes_is_idempotent_and_selection_is_equal_before_during_and_after_backfill()
    {
        var tenant = await fixture.NewTenantAsync("basefill");
        var documents = fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
        var prefix = "BF" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var item = Guid.NewGuid();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), At,
            [new DocumentLine(1, item, 2, [new DocumentSerial($"{prefix}-1"), new DocumentSerial($"{prefix}-2")],
                Guid.NewGuid(), LocationKind.Warehouse)]);
        documents.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new PurchasingReceiptPosted(new ThinEnvelope(Guid.NewGuid(), Entity, At, "backfill-test"), receipt.Document));

        var order = new DocumentRef("sales_order", Guid.NewGuid(), 5);
        var shipment = new ShipmentDocument(new DocumentRef("delivery", Guid.NewGuid()), Guid.NewGuid(), At.AddDays(1),
            [new DocumentLine(1, item, 2, [new DocumentSerial($"{prefix}-1"), new DocumentSerial($"{prefix}-2")])],
            BaseOrder: order);
        documents.Add(shipment);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(new ThinEnvelope(Guid.NewGuid(), Entity, At.AddDays(1), "backfill-test"), shipment.Document));

        using (var reset = fixture.ScopeFor(tenant))
        {
            var db = reset.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(2, await db.LedgerBaseOrderReferences.CountAsync(row => row.BaseDocumentId == order.Id));
            await db.LedgerBaseOrderReferences.Where(row => row.BaseDocumentId == order.Id).ExecuteDeleteAsync();
        }

        Assert.Equal(2, await ResolveAsync(tenant, order)); // no checkpoint: bounded legacy path

        LedgerBaseOrderBackfillProgress stopped;
        using (var firstRun = fixture.ScopeFor(tenant))
        {
            stopped = await firstRun.ServiceProvider.GetRequiredService<LedgerBaseOrderBackfill>()
                .RunAsync(CancellationToken.None, maximumBatches: 1);
        }

        Assert.Equal(LedgerBaseOrderBackfillStatus.Running, stopped.Status);
        Assert.Equal(1, stopped.RowsDone);
        Assert.NotNull(stopped.CheckpointMonth);
        Assert.Equal(2, await ResolveAsync(tenant, order)); // side table plus current/older-month fallback

        LedgerBaseOrderBackfillProgress resumed;
        using (var secondRun = fixture.ScopeFor(tenant))
        {
            resumed = await secondRun.ServiceProvider.GetRequiredService<LedgerBaseOrderBackfill>()
                .RunAsync(CancellationToken.None, maximumBatches: 1);
        }

        Assert.Equal(2, resumed.RowsDone); // proves the durable cursor resumed instead of restarting row one

        LedgerBaseOrderBackfillProgress complete;
        using (var finalRun = fixture.ScopeFor(tenant))
        {
            complete = await finalRun.ServiceProvider.GetRequiredService<LedgerBaseOrderBackfill>()
                .RunAsync(CancellationToken.None);
        }

        Assert.Equal(LedgerBaseOrderBackfillStatus.Complete, complete.Status);
        Assert.Equal(complete.RowsDone, complete.EstimatedRows);
        Assert.Equal(2, await ResolveAsync(tenant, order));

        using (var check = fixture.ScopeFor(tenant))
        {
            var service = check.ServiceProvider.GetRequiredService<LedgerBaseOrderBackfill>();
            var replay = await service.RunAsync(CancellationToken.None);
            Assert.Equal(complete.Status, replay.Status);
            Assert.Equal(complete.RowsDone, replay.RowsDone);
            Assert.Equal(complete.RowsWritten, replay.RowsWritten);
            Assert.Equal(complete.EstimatedRows, replay.EstimatedRows);
            Assert.Equal(complete.CompletedAt!.Value, replay.CompletedAt!.Value, TimeSpan.FromMilliseconds(1));
            Assert.Equal(2, await check.ServiceProvider.GetRequiredService<SerialisationDbContext>()
                .LedgerBaseOrderReferences.CountAsync(row => row.BaseDocumentId == order.Id));
        }
    }

    [DatabaseFact]
    public async Task Concurrent_first_runs_share_one_checkpoint_without_a_primary_key_failure()
    {
        var tenant = await fixture.NewTenantAsync("base-race");
        using var firstScope = fixture.ScopeFor(tenant);
        using var secondScope = fixture.ScopeFor(tenant);
        var first = firstScope.ServiceProvider.GetRequiredService<LedgerBaseOrderBackfill>()
            .RunAsync(CancellationToken.None, maximumBatches: 1);
        var second = secondScope.ServiceProvider.GetRequiredService<LedgerBaseOrderBackfill>()
            .RunAsync(CancellationToken.None, maximumBatches: 1);

        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.Equal(LedgerBaseOrderBackfillStatus.Complete, result.Status));
        using var check = fixture.ScopeFor(tenant);
        Assert.Equal(1, await check.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .LedgerBaseOrderBackfillCheckpoints.CountAsync());
    }

    [DatabaseFact]
    public async Task Recurring_job_is_registered_per_tenant()
    {
        var tenant = await fixture.NewTenantAsync("basejob");
        var scheduler = await fixture.Services.GetRequiredService<ISchedulerFactory>().GetScheduler();
        await fixture.Services.GetRequiredService<RecurringTenantJobScheduler>()
            .EnsureScheduledAsync(scheduler, [tenant], CancellationToken.None);
        var detail = await scheduler.GetJobDetail(TenantJob.KeyFor(LedgerBaseOrderBackfillJob.JobName, tenant));
        Assert.NotNull(detail);
        Assert.Equal(typeof(LedgerBaseOrderBackfillJob), detail.JobType);
    }

    private async Task<int> ResolveAsync(TenantId tenant, DocumentRef order)
    {
        var result = await InvokeAsync<ResolveSelectionSetResult>(tenant,
            new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByDocument(order), "backfill-test"));
        return result.Resolved;
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }
}
