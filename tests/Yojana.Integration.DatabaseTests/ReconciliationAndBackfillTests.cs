using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Backfills;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ReconciliationAndBackfillTests(IntegrationHostFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    [DatabaseFact]
    public async Task Missing_range_is_detected_backfilled_and_confirmed_by_the_next_clean_sweep()
    {
        var tenant = await fixture.NewTenantAsync("sweep");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        fixture.ReconciliationSource.Reset(connection);
        fixture.ReconciliationSource.Seed(connection, "delivery", Day,
            Message(tenant, connection, "delivery", "D-001", "S-001"),
            Message(tenant, connection, "delivery", "D-002", "S-002"));

        Guid firstRun;
        Guid backfillId;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var sweep = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
            firstRun = await sweep.RunAsync(connection, Day, Day, ReconciliationTrigger.OnDemand,
                true, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var diff = await db.ReconciliationEntityDayResults.SingleAsync(row =>
                row.ReconciliationRunId == firstRun);
            Assert.Equal(ReconciliationResultStatus.Mismatch, diff.Status);
            Assert.Equal(2, diff.SourceCount);
            Assert.Equal(0, diff.ModuleCount);
            backfillId = Assert.IsType<Guid>(diff.BackfillJobId);
            Assert.Equal(BackfillStatus.Queued, (await db.BackfillCursors.SingleAsync(row =>
                row.BackfillCursorId == backfillId)).Status);
        }

        using (var scope = fixture.ScopeFor(tenant))
            await scope.ServiceProvider.GetRequiredService<BackfillService>()
                .ProcessAsync(backfillId, null, CancellationToken.None);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);

        Guid cleanRun;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var sweep = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
            cleanRun = await sweep.RunAsync(connection, Day, Day, ReconciliationTrigger.OnDemand,
                true, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Equal(ReconciliationRunStatus.Clean, (await db.ReconciliationRuns.SingleAsync(row =>
                row.ReconciliationRunId == cleanRun)).Status);
            var job = await db.BackfillCursors.SingleAsync(row => row.BackfillCursorId == backfillId);
            Assert.Equal(BackfillStatus.Completed, job.Status);
            Assert.Equal(cleanRun, job.CompletionReconciliationRunId);
            Assert.Equal(2, await db.InboundMessages.CountAsync(row => row.ConnectionId == connection
                && row.IsLatest));
            Assert.All(await db.InboundMessages.Where(row => row.ConnectionId == connection).ToListAsync(),
                row => Assert.Equal("backfill", row.CaptureMode));
        }
    }

    [DatabaseFact]
    public async Task Location_mismatch_reports_source_on_hand_and_module_unit_count()
    {
        var tenant = await fixture.NewTenantAsync("location");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        var item = Guid.NewGuid();
        var location = Guid.NewGuid();
        fixture.ReconciliationSource.Reset(connection);
        fixture.ReconciliationSource.SetOnHand(connection,
            new ConnectorOnHandBalance(item, "FIC-ITEM-7", "Fictional actuator", location,
                "WH-A", "North store", 3));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Current.AddRange(Current(tenant, item, location), Current(tenant, item, location));
            await db.SaveChangesAsync();
        }

        using var checkScope = fixture.ScopeFor(tenant);
        var service = checkScope.ServiceProvider.GetRequiredService<ReconciliationService>();
        var runId = await service.RunLocationChecksAsync(connection, CancellationToken.None);
        var detail = Assert.IsType<ReconciliationRunDetail>(
            await service.DetailAsync(runId, CancellationToken.None));
        var mismatch = Assert.Single(detail.LocationChecks);
        Assert.Equal(3, mismatch.SourceOnHand);
        Assert.Equal(2, mismatch.ModuleUnitCount);
        Assert.Equal(ReconciliationResultStatus.Mismatch, mismatch.Status);
        Assert.Equal("FIC-ITEM-7", mismatch.ItemCode);
        Assert.Equal("WH-A", mismatch.LocationCode);
    }

    [DatabaseFact]
    public async Task Location_check_counts_a_quarantined_unit_as_warehouse_on_hand()
    {
        var tenant = await fixture.NewTenantAsync("quarantine");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        var item = Guid.NewGuid();
        var location = Guid.NewGuid();
        fixture.ReconciliationSource.Reset(connection);
        fixture.ReconciliationSource.SetOnHand(connection,
            new ConnectorOnHandBalance(item, "FIC-ITEM-Q", "Fictional inspected unit", location,
                "WH-Q", "Inspection store", 2));
        using (var settingScope = fixture.ScopeFor(tenant))
        {
            var integration = settingScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var setting = await integration.ConnectionGatewaySettings.SingleAsync(row =>
                row.ConnectionId == connection);
            setting.OnHandStatusMappingJson = """{"inspection":true,"supplier_hold":false}""";
            await integration.SaveChangesAsync();
        }
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var quarantined = Current(tenant, item, location);
            quarantined.Disposition = UnitDisposition.Returned;
            quarantined.LocationKind = LocationKind.Quarantine;
            db.Current.AddRange(Current(tenant, item, location), quarantined);
            await db.SaveChangesAsync();
        }

        using var checkScope = fixture.ScopeFor(tenant);
        var service = checkScope.ServiceProvider.GetRequiredService<ReconciliationService>();
        var runId = await service.RunLocationChecksAsync(connection, CancellationToken.None);
        var detail = Assert.IsType<ReconciliationRunDetail>(
            await service.DetailAsync(runId, CancellationToken.None));
        var comparison = Assert.Single(detail.LocationChecks);
        Assert.Equal(2, comparison.SourceOnHand);
        Assert.Equal(2, comparison.ModuleUnitCount);
        Assert.Equal(ReconciliationResultStatus.Clean, comparison.Status);
        Assert.True(fixture.ReconciliationSource.LastOnHandRequest!.WarehouseStatusMapping["inspection"]);
        Assert.False(fixture.ReconciliationSource.LastOnHandRequest.WarehouseStatusMapping["supplier_hold"]);
    }

    [DatabaseFact]
    public async Task Backfill_resumes_from_its_persisted_cursor_after_a_crash()
    {
        var tenant = await fixture.NewTenantAsync("resume");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        fixture.ReconciliationSource.Reset(connection);
        fixture.ReconciliationSource.Seed(connection, "invoice", Day,
            Message(tenant, connection, "invoice", "I-001", "S-101"),
            Message(tenant, connection, "invoice", "I-002", "S-102"),
            Message(tenant, connection, "invoice", "I-003", "S-103"));
        fixture.ReconciliationSource.FailConnection = connection;
        fixture.ReconciliationSource.FailOnReadCall = 2;
        Guid id;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var service = scope.ServiceProvider.GetRequiredService<BackfillService>();
            id = await service.StartAsync(new(connection, "invoice", Day, Day, 1),
                "user:operator", CancellationToken.None);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ProcessAsync(id, null, CancellationToken.None));
            var row = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>()
                .BackfillCursors.SingleAsync(value => value.BackfillCursorId == id);
            Assert.Equal("1", row.Cursor);
            Assert.Equal(1, row.ProcessedMessages);
        }

        using (var scope = fixture.ScopeFor(tenant))
            await scope.ServiceProvider.GetRequiredService<BackfillService>()
                .ProcessAsync(id, null, CancellationToken.None);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var row = await db.BackfillCursors.SingleAsync(value => value.BackfillCursorId == id);
            Assert.Equal(BackfillStatus.AwaitingReconciliation, row.Status);
            Assert.Equal(3, row.ProcessedMessages);
            Assert.Equal(3, await db.InboundMessages.CountAsync(value => value.ConnectionId == connection));
        }
    }

    [DatabaseFact]
    public async Task Slow_source_for_one_tenant_does_not_block_another_tenants_backfill()
    {
        var slowTenant = await fixture.NewTenantAsync("slow");
        var fastTenant = await fixture.NewTenantAsync("fast");
        var slowConnection = await fixture.AddConnectionAsync(slowTenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        var fastConnection = await fixture.AddConnectionAsync(fastTenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        fixture.ReconciliationSource.Reset(slowConnection);
        fixture.ReconciliationSource.Reset(fastConnection);
        fixture.ReconciliationSource.Seed(slowConnection, "item", Day,
            Message(slowTenant, slowConnection, "item", "A-1", "S-A"));
        fixture.ReconciliationSource.Seed(fastConnection, "item", Day,
            Message(fastTenant, fastConnection, "item", "B-1", "S-B"));
        var gate = fixture.ReconciliationSource.BlockReads(slowConnection);

        Guid slowId;
        Guid fastId;
        using (var slowStart = fixture.ScopeFor(slowTenant))
            slowId = await slowStart.ServiceProvider.GetRequiredService<BackfillService>().StartAsync(
                new(slowConnection, "item", Day, Day, 1), "user:slow", CancellationToken.None);
        using (var fastStart = fixture.ScopeFor(fastTenant))
            fastId = await fastStart.ServiceProvider.GetRequiredService<BackfillService>().StartAsync(
                new(fastConnection, "item", Day, Day, 1), "user:fast", CancellationToken.None);

        using var slowScope = fixture.ScopeFor(slowTenant);
        var slowTask = slowScope.ServiceProvider.GetRequiredService<BackfillService>()
            .ProcessAsync(slowId, null, CancellationToken.None);
        await UntilAsync(() => fixture.ReconciliationSource.ReadCallCount(slowConnection) == 1);
        using (var fastScope = fixture.ScopeFor(fastTenant))
            await fastScope.ServiceProvider.GetRequiredService<BackfillService>()
                .ProcessAsync(fastId, null, CancellationToken.None);
        using (var verifyFast = fixture.ScopeFor(fastTenant))
            Assert.Equal(BackfillStatus.AwaitingReconciliation,
                (await verifyFast.ServiceProvider.GetRequiredService<IntegrationDbContext>()
                    .BackfillCursors.SingleAsync(row => row.BackfillCursorId == fastId)).Status);
        Assert.False(slowTask.IsCompleted);
        gate.SetResult();
        await slowTask;
    }

    private static InboundAcceptance Message(TenantId tenant, Guid connection, string entity,
        string externalId, string serial)
    {
        var occurred = new DateTimeOffset(Day.ToDateTime(new TimeOnly(10, 0), DateTimeKind.Utc));
        var canonical = $$"""{"lines":[{"quantity":1,"serials":[{"value":"{{serial}}"}]}]}""";
        return new(new("1.0", Guid.CreateVersion7(), tenant.Value, connection, "CX01", entity,
                externalId, new(externalId, OpenEnum.Known("sequence")), occurred, occurred,
                OpenEnum.Known("poll"), OpenEnum.Known("complete"), $"memory:{externalId}"),
            "DeliveryPosted", canonical, canonical);
    }

    private static UnitCurrent Current(TenantId tenant, Guid item, Guid location) => new()
    {
        TenantId = tenant,
        UnitId = Guid.CreateVersion7(),
        ItemId = item,
        LifecycleStatus = LifecycleStatus.Released,
        Disposition = UnitDisposition.Available,
        LocationId = location,
        LocationKind = LocationKind.Warehouse,
        LastEventSeq = 1,
        LastEventTime = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
    };

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
