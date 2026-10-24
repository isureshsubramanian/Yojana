using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Notifications;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ConnectionSyncStartTests(IntegrationHostFixture fixture)
{
    public static IEnumerable<object[]> ConnectorModes()
    {
        foreach (var connector in new[] { "CX01", "CX02", "CX04" })
        foreach (var mode in new[] { "today", "date", "full_history" })
            yield return [connector, mode];
    }

    [DatabaseTheory]
    [MemberData(nameof(ConnectorModes))]
    public async Task Every_connector_seeds_each_start_mode_in_its_own_format_and_leaves_master_data_full(
        string connector, string mode)
    {
        var tenant = await fixture.NewTenantAsync("synst");
        var connectionId = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(), connector);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var connection = await db.Connections.SingleAsync(row => row.ConnectionId == connectionId);
        var (master, document) = connector switch
        {
            "CX01" => ("items", "deliveries"),
            "CX02" => ("customers", "sales_invoices"),
            _ => ("parties", "shipments"),
        };
        db.ConnectionPollWatermarks.AddRange(Row(master, "ItemUpserted"), Row(document, "DeliveryPosted"));
        await db.SaveChangesAsync();
        var date = new DateOnly(2026, 9, 1);
        await scope.ServiceProvider.GetRequiredService<ConnectionOnboarding>().ApplyAsync(connection,
            new(mode, mode == "date" ? date : null, true), null, "fictional-administrator", default);
        var rows = await db.ConnectionPollWatermarks.OrderBy(row => row.EntityType).ToListAsync();
        Assert.Null(rows.Single(row => row.EntityType == master).Watermark);
        var documentRow = rows.Single(row => row.EntityType == document);
        if (mode == "full_history") Assert.Null(documentRow.Watermark);
        else
        {
            var expected = mode == "today" ? fixture.Clock.UtcNow
                : new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
            var seed = scope.ServiceProvider.GetRequiredKeyedService<IConnectorWatermarkSeed>(connector);
            Assert.Equal(seed.WatermarkAt(expected), documentRow.Watermark);
        }

        ConnectionPollWatermark Row(string entity, string canonical) => new()
        {
            TenantId = tenant, ConnectionId = connectionId, EntityType = entity,
            CanonicalMessage = canonical, IntervalSeconds = 60, OverlapSeconds = 60,
            NextPollAt = fixture.Clock.UtcNow,
        };
    }

    [DatabaseFact]
    public async Task Saving_a_start_never_moves_an_existing_watermark_back()
    {
        var tenant = await fixture.NewTenantAsync("synpr");
        var id = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(), "CX01");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var seed = scope.ServiceProvider.GetRequiredKeyedService<IConnectorWatermarkSeed>("CX01");
        var current = seed.WatermarkAt(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc));
        db.ConnectionPollWatermarks.Add(new()
        {
            TenantId = tenant, ConnectionId = id, EntityType = "deliveries", CanonicalMessage = "DeliveryPosted",
            Watermark = current, IntervalSeconds = 60, NextPollAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ConnectionOnboarding>().ApplyAsync(
            await db.Connections.SingleAsync(row => row.ConnectionId == id),
            new("date", new(2026, 9, 1), false), null, "fictional-administrator", default);
        Assert.Equal(current, (await db.ConnectionPollWatermarks.SingleAsync()).Watermark);
    }

    [DatabaseTheory]
    [InlineData("CX01", "items")]
    [InlineData("CX02", "customers")]
    [InlineData("CX04", "parties")]
    public async Task Master_data_uses_the_selected_start_when_full_history_is_turned_off(
        string connector, string entity)
    {
        var tenant = await fixture.NewTenantAsync("synmd");
        var id = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(), connector);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ConnectionPollWatermarks.Add(new()
        {
            TenantId = tenant, ConnectionId = id, EntityType = entity, CanonicalMessage = "ItemUpserted",
            IntervalSeconds = 60, NextPollAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<ConnectionOnboarding>().ApplyAsync(
            await db.Connections.SingleAsync(row => row.ConnectionId == id),
            new("date", new(2026, 9, 1), false), null, "fictional-administrator", default);
        var seed = scope.ServiceProvider.GetRequiredKeyedService<IConnectorWatermarkSeed>(connector);
        Assert.Equal(seed.WatermarkAt(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)),
            (await db.ConnectionPollWatermarks.SingleAsync()).Watermark);
    }

    [DatabaseFact]
    public async Task Document_poll_waits_for_sync_start_while_master_data_can_read_in_full()
    {
        var tenant = await fixture.NewTenantAsync("synpl");
        var id = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(), IntegrationHostFixture.FakeConnectorCode);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ConnectionPollWatermarks.AddRange(Due("documents", "DeliveryPosted"), Due("items", "ItemUpserted"));
        await db.SaveChangesAsync();
        fixture.Poller.Requests.Clear();
        fixture.Poller.NextResult = new([], null);
        await scope.ServiceProvider.GetRequiredService<ConnectionPollingService>().PollDueAsync(default);
        Assert.Equal("items", Assert.Single(fixture.Poller.Requests).EntityType);

        ConnectionPollWatermark Due(string entity, string canonical) => new()
        {
            TenantId = tenant, ConnectionId = id, EntityType = entity, CanonicalMessage = canonical,
            IntervalSeconds = 60, NextPollAt = fixture.Clock.UtcNow.AddMinutes(-1),
        };
    }

    [DatabaseFact]
    public async Task Rebaseline_moves_forward_and_audits_but_refuses_a_backwards_move()
    {
        var tenant = await fixture.NewTenantAsync("synbl");
        var id = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(), "CX01");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var seed = scope.ServiceProvider.GetRequiredKeyedService<IConnectorWatermarkSeed>("CX01");
        var now = fixture.Clock.UtcNow;
        db.ConnectionPollWatermarks.Add(new()
        {
            TenantId = tenant, ConnectionId = id, EntityType = "deliveries", CanonicalMessage = "DeliveryPosted",
            Watermark = seed.WatermarkAt(now.AddDays(-2)),
            IntervalSeconds = 60, NextPollAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<ConnectionOnboarding>();
        var connection = await db.Connections.SingleAsync(row => row.ConnectionId == id);
        var forward = now.AddDays(-1);
        var result = await service.RebaselineAsync(connection,
            new(["deliveries"], forward, "Fictional cut-off correction"), "fictional-administrator", default);
        Assert.True(result.ChangesInGapSkipped);
        Assert.Equal(seed.WatermarkAt(forward), (await db.ConnectionPollWatermarks.SingleAsync()).Watermark);
        Assert.Contains(await db.ConnectionEvents.ToListAsync(), row =>
            row.EventType == "ConnectionSyncBaselined" && row.ActorRef == "fictional-administrator");
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => service.RebaselineAsync(connection,
            new(["deliveries"], now.AddDays(-1.5), "Backwards"),
            "fictional-administrator", default));
        Assert.Equal("sync_baseline_backwards", refused.Rule);
    }
}

public sealed class DatabaseTheoryAttribute : TheoryAttribute
{
    public DatabaseTheoryAttribute()
    {
        if (IntegrationHostFixture.ConnectionString is null) Skip = "YOJANA_TEST_SQL is not set";
    }
}
