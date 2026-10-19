using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Transport;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class TransportTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Default_transport_preserves_single_acceptance_and_single_outbound_create()
    {
        var tenant = await fixture.NewTenantAsync("tr-local");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        fixture.Connector.Configure(FakeOutboundBehaviour.Succeed);
        using var scope = fixture.ScopeFor(tenant);
        var store = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var acceptance = Acceptance(tenant, connection, "1");
        var first = await store.Accept(acceptance, default);
        Assert.Equal("accepted", first.Outcome);
        Assert.Equal("already_processed", (await store.Accept(acceptance, default)).Outcome);
        await AddOutboundAsync(scope.ServiceProvider, tenant, connection);
        var dispatcher = scope.ServiceProvider.GetRequiredService<IntegrationRelayDispatcher>();
        await dispatcher.DispatchAsync(connection, default);
        await dispatcher.DispatchAsync(connection, default);
        Assert.Equal(1, fixture.Connector.CreateCallsFor(connection));
        Assert.Equal(1, fixture.Connector.FindCallsFor(connection));
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(1, await db.InboundMessages.CountAsync());
        Assert.Equal(1, await db.IdempotencyIndex.CountAsync());
        Assert.Equal(OutboundCommandStatus.Done, await db.OutboundCommands.Select(x => x.Status).SingleAsync());
    }

    [DatabaseFact]
    public async Task Queue_redelivery_uses_the_same_inbox_supersede_and_query_before_create_paths()
    {
        var tenant = await fixture.NewTenantAsync("tr-queue");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        fixture.Connector.Configure(FakeOutboundBehaviour.TimeoutAfterCreate);
        using var scope = fixture.ScopeFor(tenant);
        await EnableQueueAsync(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var acceptance = Acceptance(tenant, connection, "2");
        var first = await store.Accept(acceptance, default);
        var delivery = await fixture.Transport.NextAsync(tenant);
        Assert.Equal("message_queue", delivery.Route.Mode);
        var receiver = fixture.Services.GetRequiredService<IIntegrationTransportReceiver>();
        Assert.Equal("already_processed", (await receiver.ReceiveAsync(tenant, delivery.Message, default)).Outcome);
        Assert.Equal("already_processed", (await receiver.ReceiveAsync(tenant, delivery.Message, default)).Outcome);
        Assert.Equal(first.InboundMessageId, (await store.Accept(acceptance, default)).InboundMessageId);
        Assert.Equal("already_processed", (await store.Accept(acceptance, default)).Outcome);
        // Message identity also dedupes a replay whose sender has changed the marker.
        var changedMarker = acceptance with { Envelope = acceptance.Envelope with { ErpChangeMarker = new("999", OpenEnum.Known("sequence")) } };
        Assert.Equal("already_processed", (await store.Accept(changedMarker, default)).Outcome);
        Assert.Equal("superseded", (await store.Accept(Acceptance(tenant, connection, "1"), default)).Outcome);
        var newer = await store.Accept(Acceptance(tenant, connection, "3"), default);
        var nextDelivery = await fixture.Transport.NextAsync(tenant);
        await receiver.ReceiveAsync(tenant, nextDelivery.Message, default);
        await AddOutboundAsync(scope.ServiceProvider, tenant, connection);
        await scope.ServiceProvider.GetRequiredService<IntegrationRelayDispatcher>().DispatchAsync(connection, default);
        var outbound = await fixture.Transport.NextAsync(tenant);
        Assert.Equal(connection, outbound.Message.RelayConnectionId);
        await receiver.ReceiveAsync(tenant, outbound.Message, default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var command = await db.OutboundCommands.AsNoTracking().SingleAsync();
        Assert.Equal(OutboundCommandStatus.Retry, command.Status);
        fixture.Clock.UtcNow = DateTime.SpecifyKind(command.NextAttemptAt!.Value.AddSeconds(1), DateTimeKind.Utc);
        await receiver.ReceiveAsync(tenant, outbound.Message, default);
        await receiver.ReceiveAsync(tenant, outbound.Message, default);
        Assert.Equal(1, fixture.Connector.CreateCallsFor(connection));
        Assert.Equal(2, fixture.Connector.FindCallsFor(connection));
        Assert.Equal(3, await db.InboundMessages.CountAsync());
        Assert.Equal(3, await db.IdempotencyIndex.CountAsync());
        Assert.Equal(newer.InboundMessageId, await db.InboundMessages.Where(x => x.IsLatest).Select(x => x.InboundMessageId).SingleAsync());
        Assert.Equal(OutboundCommandStatus.Done, await db.OutboundCommands.Select(x => x.Status).SingleAsync());
    }

    [DatabaseFact]
    public async Task Switching_mid_stream_keeps_queued_work_on_the_old_route_and_accepts_overlap_once()
    {
        var tenant = await fixture.NewTenantAsync("tr-switch");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        fixture.Connector.Configure(FakeOutboundBehaviour.Succeed);
        using var scope = fixture.ScopeFor(tenant);
        var queue = await EnableQueueAsync(scope.ServiceProvider);
        var store = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var first = Acceptance(tenant, connection, "1");
        await store.Accept(first, default);
        var delayed = await fixture.Transport.NextAsync(tenant);
        await AddOutboundAsync(scope.ServiceProvider, tenant, connection);
        await scope.ServiceProvider.GetRequiredService<IntegrationRelayDispatcher>().DispatchAsync(connection, default);
        var delayedOutbound = await fixture.Transport.NextAsync(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<IntegrationTransportSettings>();
        var local = await settings.UpdateAsync(new("in_process", null, queue.Version), "actor:server", default);
        Assert.Contains(delayed.Route, local.DrainingRoutes);
        Assert.Equal(queue.Route, delayedOutbound.Route);
        Assert.Equal("already_processed", (await store.Accept(first, default)).Outcome);
        var second = await store.Accept(Acceptance(tenant, connection, "2"), default);
        await scope.ServiceProvider.GetRequiredService<IntegrationRelayDispatcher>().DispatchAsync(connection, default);
        var receiver = fixture.Services.GetRequiredService<IIntegrationTransportReceiver>();
        await receiver.ReceiveAsync(tenant, delayed.Message, default);
        await receiver.ReceiveAsync(tenant, delayed.Message, default);
        await receiver.ReceiveAsync(tenant, delayedOutbound.Message, default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(2, await db.InboundMessages.CountAsync());
        Assert.Equal(2, await db.IdempotencyIndex.CountAsync());
        Assert.Equal(second.InboundMessageId, await db.InboundMessages.Where(x => x.IsLatest).Select(x => x.InboundMessageId).SingleAsync());
        Assert.Equal(1, fixture.Connector.CreateCallsFor(connection));
        Assert.Equal(1, fixture.Connector.FindCallsFor(connection));
    }

    [DatabaseFact]
    public async Task Queue_first_receipt_and_concurrent_redelivery_accept_once()
    {
        var tenant = await fixture.NewTenantAsync("tr-first");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        var acceptance = Acceptance(tenant, connection, "1");
        var message = new IntegrationTransportMessage(tenant.Value, acceptance.Envelope.MessageId,
            Inbound: new(acceptance.Envelope, acceptance.MessageType, acceptance.CanonicalMessage, acceptance.RawPayload));
        var receiver = fixture.Services.GetRequiredService<IIntegrationTransportReceiver>();
        var replies = await Task.WhenAll(receiver.ReceiveAsync(tenant, message, default), receiver.ReceiveAsync(tenant, message, default));
        Assert.Single(replies, x => x.Outcome == "accepted");
        Assert.Single(replies, x => x.Outcome == "already_processed");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(1, await db.InboundMessages.CountAsync());
        Assert.Equal(1, await db.IdempotencyIndex.CountAsync());
        Assert.Equal("already_processed", (await scope.ServiceProvider.GetRequiredService<InboundMessageStore>()
            .Accept(acceptance, default)).Outcome);
    }

    [DatabaseFact]
    public async Task Queue_credentials_and_settings_are_tenant_isolated_and_secrets_are_not_plaintext()
    {
        var firstTenant = await fixture.NewTenantAsync("tr-secret");
        var otherTenant = await fixture.NewTenantAsync("tr-other");
        using var firstScope = fixture.ScopeFor(firstTenant);
        var settings = await EnableQueueAsync(firstScope.ServiceProvider);
        var reference = settings.Route.CredentialReference!.Value;
        var db = firstScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var secret = await db.ConnectionCredentials.SingleAsync(x => x.Purpose == MessageQueueCredentials.Purpose);
        Assert.NotEmpty(secret.Ciphertext);
        Assert.DoesNotContain("Endpoint=", System.Text.Encoding.UTF8.GetString(secret.Ciphertext));
        var storedSetting = await firstScope.ServiceProvider.GetRequiredService<ITenantSettings>()
            .GetAsync(IntegrationTransportSettings.SettingKey, default);
        Assert.DoesNotContain("SharedAccessKey", storedSetting!);
        using var otherScope = fixture.ScopeFor(otherTenant);
        Assert.Equal(IntegrationTransportRoute.Local, await otherScope.ServiceProvider.GetRequiredService<IIntegrationRouteResolver>().ResolveAsync(default));
        await Assert.ThrowsAsync<ArgumentException>(() => otherScope.ServiceProvider.GetRequiredService<IntegrationTransportSettings>()
            .UpdateAsync(new("message_queue", reference, "missing"), "actor:server", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Services.GetRequiredService<IIntegrationTransportReceiver>()
            .ReceiveAsync(otherTenant, new(firstTenant.Value, Guid.NewGuid(), RelayConnectionId: Guid.NewGuid()), default));
    }

    private async Task<IntegrationTransportSettingsView> EnableQueueAsync(IServiceProvider services)
    {
        var credential = await services.GetRequiredService<MessageQueueCredentials>().StoreAsync(new(
            "Endpoint=sb://fictional-relay.servicebus.windows.net/;SharedAccessKeyName=tenant;SharedAccessKey=ZmFrZQ==",
            fixture.Clock.UtcNow.AddDays(90)), default);
        var settings = services.GetRequiredService<IntegrationTransportSettings>();
        return await settings.UpdateAsync(new("message_queue", credential.CredentialReference,
            (await settings.ReadAsync(default)).Version), "actor:server", default);
    }

    private static InboundAcceptance Acceptance(TenantId tenant, Guid connection, string marker)
    {
        var envelope = new CanonicalEnvelope("1.0", Guid.CreateVersion7(), tenant.Value, connection,
            IntegrationHostFixture.FakeConnectorCode, "item", "FIC-ITEM", new(marker, OpenEnum.Known("sequence")),
            new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero),
            OpenEnum.Known("poll"), OpenEnum.Known("complete"), "raw/fictional-item");
        var message = new CanonicalMessage<ItemUpserted>(envelope, nameof(ItemUpserted),
            new("FIC-ITEM", "Fictional item", OpenEnum.Known("serial"), "EA", false));
        return new(envelope, nameof(ItemUpserted), JsonSerializer.Serialize(message, IntegrationContracts.Json), "{}");
    }

    private async Task AddOutboundAsync(IServiceProvider services, TenantId tenant, Guid connection)
    {
        var db = services.GetRequiredService<IntegrationDbContext>();
        db.OutboundCommands.Add(new OutboundCommandRow
        {
            TenantId = tenant, OutboundCommandId = Guid.CreateVersion7(), ConnectionId = connection,
            CommandType = "CreateServiceOrder", IdempotencyKey = Guid.NewGuid(), ModuleDocumentRef = $"service/{Guid.NewGuid():N}",
            ErpReferenceText = $"YOJ-{Guid.NewGuid():N}", Payload = "{}", Status = OutboundCommandStatus.Pending,
            CreatedAt = fixture.Clock.UtcNow.AddMinutes(-1),
        });
        await db.SaveChangesAsync();
    }
}
