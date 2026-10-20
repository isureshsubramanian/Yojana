using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Coexistence;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Gateway;
using Yojana.Integration.Outbox;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.DatabaseTests;

public sealed partial class IntegrationEdgeCaseTests
{
    [DatabaseFact]
    public async Task Outage_exhausts_retry_age_records_health_and_operator_retry_posts_once()
    {
        // ADR-0002 AI 15 / Reliability 9: outage beyond retry window, durable DLQ and recovery.
        var (tenant, connection) = await GenericAsync();
        fixture.Connector.Configure(FakeOutboundBehaviour.Fail);
        var commandId = await PublishReplacementAsync(tenant, connection);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(OutboxRelaySettings.ConnectionKey(connection), "1", default);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        var command = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == commandId);
        await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(connection, new(2026, 9, 10), new(2026, 9, 10), ReconciliationTrigger.Scheduled, false, null, default);
        Assert.Equal("healthy", (await scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>().RefreshAsync(connection, default)).Status);
        var start = fixture.Clock.UtcNow;
        db.ConnectionPollWatermarks.Add(new() { TenantId = tenant, ConnectionId = connection, EntityType = "invoice", LastPolledAt = start, NextPollAt = start.AddMinutes(1), IntervalSeconds = 60, OverlapSeconds = 120, LastError = "Fictional remote unavailable" });
        await db.SaveChangesAsync();
        var sawDegraded = false;
        var sawDown = false;
        var attempts = 0;
        while (fixture.Clock.UtcNow < start.AddDays(1))
        {
            Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Retrying);
            attempts++;
            await db.Entry(command).ReloadAsync();
            Assert.Equal(OutboundCommandStatus.Retry, command.Status);
            Assert.True(command.NextAttemptAt > fixture.Clock.UtcNow);
            Assert.Equal(0, (await relay.RelayConnectionAsync(connection, default)).Retrying);
            var projected = await scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>().RefreshAsync(connection, default);
            sawDegraded |= projected.Status == "degraded";
            sawDown |= projected.Status == "down";
            fixture.Clock.UtcNow = command.NextAttemptAt!.Value;
        }
        Assert.True(attempts > 3);
        Assert.True(sawDegraded);
        Assert.True(sawDown);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        var letter = await db.DeadLetterItems.SingleAsync(x => x.SourceId == commandId);
        Assert.Equal("max_outbox_age", letter.ReasonCode);
        Assert.Equal(DeadLetterStatus.NeedsAttention, letter.Status);
        var health = await scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>().RefreshAsync(connection, default);
        Assert.Equal("down", health.Status);
        Assert.Equal(1, health.OpenDeadLetters);
        Assert.True(await db.ConnectionEvents.AnyAsync(x => x.ConnectionId == connection && x.EventType == "HealthStatusChanged" && x.DetailsJson.Contains("\"down\"")));
        fixture.Connector.Configure(FakeOutboundBehaviour.Succeed);
        var service = scope.ServiceProvider.GetRequiredService<DeadLetterService>();
        await service.MapAndRetryAsync(letter.DeadLetterItemId, new("party", "FIC-CUSTOMER", Guid.NewGuid()), Actor, default);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Completed);
        Assert.Equal(0, (await relay.RelayConnectionAsync(connection, default)).Completed);
        Assert.Equal(1, fixture.Connector.FindCallsFor(connection));
        Assert.Equal(1, fixture.Connector.CreateCallsFor(connection));
        await db.Entry(command).ReloadAsync();
        Assert.Equal(OutboundCommandStatus.Done, command.Status);
        Assert.Single(await db.ExternalIdMaps.Where(x => x.ConnectionId == connection && x.EntityType == "document").ToListAsync());
    }

    [DatabaseFact]
    public async Task Stop_during_inflight_failure_keeps_command_queued_until_resume_then_posts_once()
    {
        // ADR-0001 coexistence / T18: stop during an outbound call preserves queued work for resume.
        var (tenant, connection) = await GenericAsync();
        fixture.Connector.Configure(FakeOutboundBehaviour.Fail);
        var commandId = await PublishReplacementAsync(tenant, connection);
        using (var setup = fixture.ScopeFor(tenant))
        {
            var db = setup.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var row = await db.Connections.SingleAsync(x => x.ConnectionId == connection);
            row.RegistrationCompleted = true;
            await db.SaveChangesAsync();
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Connector.BeforeCreate = async () => { entered.TrySetResult(); await release.Task; };
        using var sending = fixture.ScopeFor(tenant);
        var inFlight = sending.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var stopping = fixture.ScopeFor(tenant);
        var stop = stopping.ServiceProvider.GetRequiredService<SyncControl>().StopAsync(connection, new("Fictional maintenance"), Actor, default);
        Assert.False(inFlight.IsCompleted);
        release.SetResult();
        await inFlight;
        Assert.Equal("stopped", (await stop).SyncState);
        fixture.Connector.BeforeCreate = null;
        using var check = fixture.ScopeFor(tenant);
        var dbCheck = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var command = await dbCheck.OutboundCommands.SingleAsync(x => x.OutboundCommandId == commandId);
        Assert.Equal(OutboundCommandStatus.Retry, command.Status);
        fixture.Clock.UtcNow = command.NextAttemptAt!.Value.AddSeconds(1);
        var relay = check.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(0, (await relay.RelayConnectionAsync(connection, default)).Completed);
        fixture.Connector.Configure(FakeOutboundBehaviour.Succeed);
        await check.ServiceProvider.GetRequiredService<SyncControl>().ResumeAsync(connection, new("Maintenance complete"), Actor, default);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Completed);
        Assert.Equal(0, (await relay.RelayConnectionAsync(connection, default)).Completed);
        Assert.Equal(1, fixture.Connector.CreateCallsFor(connection));
        Assert.Equal(1, fixture.Connector.FindCallsFor(connection));
    }

    private async Task<Guid> PublishReplacementAsync(TenantId tenant, Guid connection)
    {
        var eventId = Guid.CreateVersion7();
        var unit = Guid.CreateVersion7();
        var header = new PublishedEventHeader(ReplacementShipped.Schema, ReplacementShipped.Version, eventId, unit,
            At.UtcDateTime, At.UtcDateTime, 1, Guid.NewGuid(), Actor, null);
        var message = new ReplacementShipped(header, Guid.NewGuid(), "FIC-REP-1", Guid.NewGuid(), Guid.NewGuid(), unit,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "claim", Guid.NewGuid(), "FIC-CLAIM-1",
            "approved replacement", "zero", 0, "GBP", "standard", new DateOnly(2027, 9, 1), "new", false, 0);
        using var scope = fixture.ScopeFor(tenant);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        Assert.Equal(1, await bus.InvokeAsync<int>(message, new DeliveryOptions { TenantId = tenant.ToString() }));
        Assert.Equal(0, await bus.InvokeAsync<int>(message, new DeliveryOptions { TenantId = tenant.ToString() }));
        return await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().OutboundCommands
            .Where(x => x.ConnectionId == connection).Select(x => x.OutboundCommandId).SingleAsync();
    }
}
