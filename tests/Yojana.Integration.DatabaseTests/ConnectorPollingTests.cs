using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ConnectorPollingTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Modified_since_poll_passes_the_persisted_watermark_and_overlap_to_the_connector()
    {
        var tenant = await fixture.NewTenantAsync("poll");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(),
            IntegrationHostFixture.FakeConnectorCode);
        var item = "ITEM-POLL-" + Guid.NewGuid().ToString("N")[..6];
        var envelope = new CanonicalEnvelope("1.0", Guid.CreateVersion7(), tenant.Value, connection, "CX01",
            "item", item, new("2026-09-01T08:01:00Z", OpenEnum.Known("timestamp")),
            new(2026, 9, 1, 8, 1, 0, TimeSpan.Zero), new(2026, 9, 1, 8, 1, 1, TimeSpan.Zero),
            OpenEnum.Known("poll"), OpenEnum.Known("complete"), "raw/poll");
        var message = new CanonicalMessage<ItemUpserted>(envelope, nameof(ItemUpserted),
            new(item, "Fictional polled item", OpenEnum.Known("serial"), "EA", false));
        fixture.Poller.NextResult = new([new InboundAcceptance(envelope, nameof(ItemUpserted),
            JsonSerializer.Serialize(message, IntegrationContracts.Json), "{}")], "2026-09-01T08:01:00Z");
        fixture.Poller.Requests.Clear();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ConnectionPollWatermarks.Add(new ConnectionPollWatermark
        {
            TenantId = tenant, ConnectionId = connection, EntityType = "item", Watermark = "2026-09-01T08:00:00Z",
            OverlapSeconds = 120, IntervalSeconds = 60, NextPollAt = fixture.Clock.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ConnectionPollingService>()
            .PollDueAsync(CancellationToken.None));
        var request = Assert.Single(fixture.Poller.Requests);
        Assert.Equal(TimeSpan.FromMinutes(2), request.OverlapWindow);
        Assert.Equal("2026-09-01T08:00:00Z", request.Watermark);
        Assert.Equal("2026-09-01T08:01:00Z", (await db.ConnectionPollWatermarks.SingleAsync()).Watermark);
    }

    [DatabaseFact]
    public async Task Two_connector_connections_are_dispatched_to_their_own_adapters()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("route");
        var fakeConnection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(),
            IntegrationHostFixture.FakeConnectorCode);
        var cx01Connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(), "CX01");
        fixture.Poller.Requests.Clear();
        fixture.Poller.NextResult = new([], null);

        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(cx01Connection,
            new(simulator.BaseUri, "FICTIONAL_DEMO", "10.0 FP 2308"), CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var credentialStore = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(
            new Cx01Credential("integration-user", "not-logged"), IntegrationContracts.Json);
        await credentialStore.SaveAsync(await protector.EncryptAsync(tenant, cx01Connection,
            Cx01Client.CredentialPurpose, plaintext, null, fixture.Clock.UtcNow.AddDays(30),
            CancellationToken.None), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(
            new(tenant, cx01Connection), Cx01Capabilities.For("10.0 FP 2308", false).Throttle,
            CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var gateways = await db.ConnectionGatewaySettings
            .Where(row => row.ConnectionId == fakeConnection || row.ConnectionId == cx01Connection)
            .ToListAsync();
        foreach (var gateway in gateways)
        {
            gateway.SyncStartMode = "today";
            gateway.SyncStartConfiguredAt = fixture.Clock.UtcNow;
        }
        db.ConnectionPollWatermarks.AddRange(
            Due(tenant, fakeConnection, "deliveries", fixture.Clock.UtcNow),
            Due(tenant, cx01Connection, "deliveries", fixture.Clock.UtcNow));
        await db.SaveChangesAsync();

        var adapters = scope.ServiceProvider.GetRequiredService<IConnectorAdapterResolver>();
        Assert.Same(fixture.Poller, adapters.Poller(IntegrationHostFixture.FakeConnectorCode));
        Assert.IsType<Cx01Adapter>(adapters.Poller("CX01"));
        var outbound = scope.ServiceProvider.GetRequiredService<IOutboundConnectorResolver>();
        Assert.Same(fixture.Connector, outbound.Resolve(IntegrationHostFixture.FakeConnectorCode));
        Assert.IsType<Cx01Adapter>(outbound.Resolve("CX01"));

        var accepted = await scope.ServiceProvider.GetRequiredService<ConnectionPollingService>()
            .PollDueAsync(CancellationToken.None);
        var pollStates = await db.ConnectionPollWatermarks.Where(row => row.ConnectionId == fakeConnection
                || row.ConnectionId == cx01Connection).ToListAsync();
        Assert.All(pollStates, state => Assert.Null(state.LastError));
        Assert.Equal(2, accepted);
        Assert.Equal(fakeConnection, Assert.Single(fixture.Poller.Requests).ConnectionId);
        Assert.Equal(1, simulator.LoginCount);
        Assert.Equal(2, await db.InboundMessages.CountAsync(row => row.ConnectionId == cx01Connection));
        Assert.Equal(0, await db.InboundMessages.CountAsync(row => row.ConnectionId == fakeConnection));
    }

    private static ConnectionPollWatermark Due(Yojana.Platform.Contracts.Tenancy.TenantId tenant,
        Guid connectionId, string entity, DateTime now) => new()
    {
        TenantId = tenant,
        ConnectionId = connectionId,
        EntityType = entity,
        OverlapSeconds = 120,
        IntervalSeconds = 60,
        NextPollAt = now.AddMinutes(-1),
    };
}
