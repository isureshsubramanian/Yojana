using System.Threading.Channels;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Yojana.Integration.Contracts;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Transport;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class MessageQueueLiveTests
{
    [Fact]
    public async Task Carrier_composition_executes_its_bound_receiver_without_connecting_to_a_namespace()
    {
        var tenant = new TenantId(Guid.NewGuid());
        var receiver = new CollectingReceiver();
        using var host = MessageQueueTransport.BuildCarrierHost(tenant,
            new("message_queue", Guid.NewGuid()),
            "Endpoint=sb://fictional-relay.servicebus.windows.net/;SharedAccessKeyName=tenant;SharedAccessKey=ZmFrZQ==",
            receiver, new(false, false));
        await host.StartAsync();
        try
        {
            var message = new IntegrationTransportMessage(tenant.Value, Guid.NewGuid(), RelayConnectionId: Guid.NewGuid());
            await host.Services.GetRequiredService<IMessageBus>().InvokeAsync(message);
            Assert.Equal(message, await receiver.Messages.Reader.ReadAsync());
        }
        finally { await host.StopAsync(); }
    }

    [MessageQueueLiveFact]
    [Trait("Category", "MessageQueueLive")]
    public async Task Provisioned_topic_and_queue_deliver_through_the_wolverine_carrier()
    {
        var secret = Environment.GetEnvironmentVariable("YOJANA_MQ_CONNECTION")!;
        var tenant = new TenantId(Guid.NewGuid());
        var receiver = new CollectingReceiver();
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration["Integration:Security:KeyEncryption:Provider"] = "Local";
        builder.Configuration["Integration:Security:LocalKeyEncryption:Current:KeyId"] = "live-test";
        builder.Configuration["Integration:Security:LocalKeyEncryption:Current:KeyVersion"] = "1";
        builder.Configuration["Integration:Security:LocalKeyEncryption:Current:KeyBase64"] = Convert.ToBase64String(new byte[32]);
        builder.Configuration["Integration:Security:LocalKeyEncryption:AllowInProduction"] = "true";
        builder.Configuration["Integration:Security:LocalKeyEncryption:ProductionOverrideReason"] = "isolated opt-in live test";
        builder.Services.AddScoped<TenantContext>();
        builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddScoped<ITenantContextSetter>(sp => sp.GetRequiredService<TenantContext>());
        builder.Services.AddSingleton<IClock>(new TestClock(new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc)));
        builder.AddIntegrationSecurity();
        builder.Services.AddScoped<MessageQueueCredentials>();
        using var servicesHost = builder.Build();
        using var scope = servicesHost.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        var credential = await scope.ServiceProvider.GetRequiredService<MessageQueueCredentials>()
            .StoreAsync(new(secret, new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc)), default);
        var route = new IntegrationTransportRoute("message_queue", credential.CredentialReference);
        var prefix = route.AddressPrefix(tenant);
        var admin = new ServiceBusAdministrationClient(secret);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var topicCreated = false;
        var queueCreated = false;
        try
        {
            await admin.CreateTopicAsync(prefix + "-inbound", timeout.Token);
            topicCreated = true;
            await admin.CreateSubscriptionAsync(prefix + "-inbound", "gateway", timeout.Token);
            await admin.CreateQueueAsync(prefix + "-outbound", timeout.Token);
            queueCreated = true;
            await using var carrier = new MessageQueueTransport(servicesHost.Services.GetRequiredService<IServiceScopeFactory>(), receiver, new());
            Assert.Equal("connected", await carrier.ProbeAsync(tenant, route, timeout.Token));
            await carrier.EnsureReceivingAsync(tenant, route, timeout.Token);
            var envelope = new CanonicalEnvelope("1.0", Guid.NewGuid(), tenant.Value, Guid.NewGuid(), "CX01",
                "item", "FIC-ITEM", new("1", OpenEnum.Known("sequence")),
                new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 9, 1, 0, 1, 0, TimeSpan.Zero),
                OpenEnum.Known("poll"), OpenEnum.Known("complete"), "raw/fictional-item");
            var inbound = new IntegrationTransportMessage(tenant.Value, Guid.NewGuid(), new IntegrationTransportInbound(envelope, "ItemUpserted", "{}", "{}"));
            var outbound = new IntegrationTransportMessage(tenant.Value, Guid.NewGuid(), RelayConnectionId: Guid.NewGuid());
            await carrier.SendAsync(route, inbound, timeout.Token);
            await carrier.SendAsync(route, outbound, timeout.Token);
            var received = new[] { await receiver.Messages.Reader.ReadAsync(timeout.Token), await receiver.Messages.Reader.ReadAsync(timeout.Token) };
            Assert.Contains(received, x => x == inbound);
            Assert.Contains(received, x => x == outbound);
        }
        finally
        {
            if (queueCreated) await admin.DeleteQueueAsync(prefix + "-outbound");
            if (topicCreated) await admin.DeleteTopicAsync(prefix + "-inbound");
        }
    }

    private sealed class CollectingReceiver : IIntegrationTransportReceiver
    {
        public Channel<IntegrationTransportMessage> Messages { get; } = Channel.CreateUnbounded<IntegrationTransportMessage>();
        public async Task<IntegrationTransportReceipt> ReceiveAsync(TenantId tenant, IntegrationTransportMessage message, CancellationToken cancellationToken)
        {
            IntegrationTransportReceiver.Validate(tenant, message);
            await Messages.Writer.WriteAsync(message, cancellationToken);
            return new(message.DeliveryId, "received");
        }
    }
}

public sealed class MessageQueueLiveFactAttribute : FactAttribute
{
    public MessageQueueLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("YOJANA_MQ_CONNECTION")))
            Skip = "YOJANA_MQ_CONNECTION is not set";
    }
}
