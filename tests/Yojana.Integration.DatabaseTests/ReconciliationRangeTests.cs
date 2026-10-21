using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ReconciliationRangeTests(IntegrationHostFixture fixture)
{
    private static readonly DateOnly Day = new(2026, 9, 1);

    [DatabaseFact]
    public async Task Oversized_range_uses_the_connection_override_and_is_rejected_before_the_operation_lock()
    {
        var tenant = await fixture.NewTenantAsync("range");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        await SeedCatalogueAsync(tenant);
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<ITenantSettings>();
        await settings.SetAsync(ReconciliationSettings.TenantKey, "2", CancellationToken.None);
        await settings.SetAsync(ReconciliationSettings.ConnectionKey(connection), "3", CancellationToken.None);
        using (var lockScope = fixture.ScopeFor(tenant))
        {
            await using var activity = await ConnectionOperationLock.ForConnectionAsync(
                lockScope.ServiceProvider.GetRequiredService<IntegrationDbContext>(), tenant, connection, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var failure = await Assert.ThrowsAsync<ReconciliationRuleException>(() =>
                scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(connection,
                    Day, Day.AddDays(3), ReconciliationTrigger.OnDemand, true, "user:test-operator", timeout.Token));
            Assert.Equal("reconciliation_range_too_wide", failure.Rule);
            Assert.Contains("3-day", failure.Message);
        }
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.False(await db.ReconciliationRuns.AnyAsync(x => x.ConnectionId == connection));
        Assert.False(await db.BackfillCursors.AnyAsync(x => x.ConnectionId == connection));
        Assert.NotEqual(connection, fixture.ReconciliationSource.LastOnHandRequest?.ConnectionId);
        var detail = await scope.ServiceProvider.GetRequiredService<ConnectionQueries>().DetailAsync(connection, CancellationToken.None);
        Assert.Equal(3, detail.ReconciliationWindowDays);
        Assert.Equal(ConnectionStatus.Active, detail.LifecycleStatus);
        var run = await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(connection,
            Day, Day.AddDays(2), ReconciliationTrigger.OnDemand, true, "user:test-operator", CancellationToken.None);
        Assert.Equal(ReconciliationRunStatus.Clean, (await db.ReconciliationRuns.SingleAsync(x => x.ReconciliationRunId == run)).Status);
    }

    [DatabaseFact]
    public async Task Future_end_date_is_rejected_before_the_operation_lock_or_remote_work()
    {
        var tenant = await fixture.NewTenantAsync("future");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        var today = DateOnly.FromDateTime(fixture.Clock.UtcNow);
        await SeedCatalogueAsync(tenant);
        using var scope = fixture.ScopeFor(tenant);
        using (var lockScope = fixture.ScopeFor(tenant))
        {
            await using var activity = await ConnectionOperationLock.ForConnectionAsync(
                lockScope.ServiceProvider.GetRequiredService<IntegrationDbContext>(), tenant, connection, CancellationToken.None);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var failure = await Assert.ThrowsAsync<ReconciliationRuleException>(() =>
                scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(connection,
                    today, today.AddDays(1), ReconciliationTrigger.OnDemand, true, "user:test-operator", timeout.Token));
            Assert.Equal("reconciliation_range_in_future", failure.Rule);
        }
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.False(await db.ReconciliationRuns.AnyAsync(x => x.ConnectionId == connection));
        Assert.False(await db.BackfillCursors.AnyAsync(x => x.ConnectionId == connection));
        Assert.NotEqual(connection, fixture.ReconciliationSource.LastOnHandRequest?.ConnectionId);
        await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(connection,
            today, today, ReconciliationTrigger.OnDemand, true, "user:test-operator", CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Today_is_read_in_the_tenant_zone_on_both_sides_of_utc_midnight()
    {
        var tenant = await fixture.NewTenantAsync("bizday");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        await SeedCatalogueAsync(tenant);
        var original = fixture.Clock.UtcNow;
        try
        {
            // 20:00 UTC on 14 Sep is 01:30 on 15 Sep in India: the Indian day is already accepted.
            var east = new DateOnly(2026, 9, 15);
            fixture.Clock.UtcNow = new(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);
            using (var scope = fixture.ScopeFor(tenant))
            {
                await SetZoneAsync(scope, "Asia/Kolkata");
                Assert.Equal(east, (await scope.ServiceProvider.GetRequiredService<ConnectionQueries>()
                    .DetailAsync(connection, CancellationToken.None)).ReconciliationToday);
                await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(connection, east, east,
                    ReconciliationTrigger.OnDemand, true, "user:test-operator", CancellationToken.None);
            }

            // 02:00 UTC on 15 Sep is still 14 Sep in New York: 15 Sep is in the future there. The zone port reads the
            // tenant setting once per scope, so the new zone is read through a new scope.
            fixture.Clock.UtcNow = new(2026, 9, 15, 2, 0, 0, DateTimeKind.Utc);
            using (var scope = fixture.ScopeFor(tenant))
                await SetZoneAsync(scope, "America/New_York");
            using (var scope = fixture.ScopeFor(tenant))
            {
                var failure = await Assert.ThrowsAsync<ReconciliationRuleException>(() =>
                    scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(connection, east, east,
                        ReconciliationTrigger.OnDemand, true, "user:test-operator", CancellationToken.None));
                Assert.Equal("reconciliation_range_in_future", failure.Rule);
                Assert.Equal(new DateOnly(2026, 9, 14), (await scope.ServiceProvider.GetRequiredService<ConnectionQueries>()
                    .DetailAsync(connection, CancellationToken.None)).ReconciliationToday);
            }
        }
        finally
        {
            fixture.Clock.UtcNow = original;
        }
    }

    [DatabaseFact]
    public async Task Tenant_window_caps_scheduled_callers_and_paused_connections_retain_their_read_settings()
    {
        var tenant = await fixture.NewTenantAsync("tenantwindow");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        await SeedCatalogueAsync(tenant);
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(ReconciliationSettings.TenantKey, "1", CancellationToken.None);
        var failure = await Assert.ThrowsAsync<ReconciliationRuleException>(() =>
            scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(connection,
                Day, Day.AddDays(1), ReconciliationTrigger.Scheduled, true, null, CancellationToken.None));
        Assert.Equal("reconciliation_range_too_wide", failure.Rule);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        (await db.Connections.SingleAsync(x => x.ConnectionId == connection)).Status = ConnectionStatus.Paused;
        await db.SaveChangesAsync();
        var detail = await scope.ServiceProvider.GetRequiredService<ConnectionQueries>().DetailAsync(connection, CancellationToken.None);
        Assert.Equal(1, detail.ReconciliationWindowDays);
        Assert.Equal(ConnectionStatus.Paused, detail.LifecycleStatus);
    }
    private static async Task SetZoneAsync(IServiceScope scope, string zone)
    {
        var zones = scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.TimeZones.TenantTimeZoneSettings>();
        await zones.SetAsync(new(zone, [], (await zones.GetAsync(default)).Version), "actor", default);
    }

    private async Task SeedCatalogueAsync(TenantId tenant)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ConnectorCatalogue.Add(new ConnectorCatalogueEntry
        {
            TenantId = tenant, ConnectorCode = IntegrationHostFixture.FakeConnectorCode,
            DisplayName = "Fictional test ERP", CustomerCostDisclosure = "Fictional test configuration",
            Enabled = true, UpdatedAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
    }
}
