using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Attributes;
using Wolverine.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Transport;

[NonTransactional]
public static class DispatchIntegrationTransportHandler
{
    public static Task Handle(DispatchIntegrationTransport command, ITenantContext tenant,
        IIntegrationTransport transport, CancellationToken cancellationToken)
    {
        if (tenant.TenantId.Value != command.Message.TenantId)
            throw new InvalidOperationException("transport_tenant_mismatch");
        return transport.SendAsync(command.Route, command.Message, cancellationToken);
    }
}

public sealed class IntegrationTransportReceiver(IServiceScopeFactory scopes) : IIntegrationTransportReceiver
{
    public async Task<IntegrationTransportReceipt> ReceiveAsync(TenantId tenant, IntegrationTransportMessage message,
        CancellationToken cancellationToken)
    {
        Validate(tenant, message);
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        if (message.Inbound is { } inbound)
        {
            var accepted = await scope.ServiceProvider.GetRequiredService<InboundMessageStore>()
                .Accept(new(inbound.Envelope, inbound.MessageType, inbound.CanonicalMessage, inbound.RawPayload),
                    cancellationToken, useLocalTransport: true);
            // A redelivery may find the producer's already-accepted row. Translation is independently idempotent
            // and must still be triggered: the initial durable hand-off may have used the queue route.
            await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<GatewayTranslationResult>(
                new TranslateInboundMessage(accepted.InboundMessageId),
                new DeliveryOptions { TenantId = tenant.ToString() }, cancellationToken);
            return new(message.DeliveryId, accepted.Outcome, accepted.InboundMessageId);
        }
        var relay = await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(message.RelayConnectionId!.Value, cancellationToken);
        return new(message.DeliveryId, "relayed", Relay: new(relay.Completed, relay.Retrying, relay.Parked));
    }

    public static void Validate(TenantId tenant, IntegrationTransportMessage message)
    {
        if (tenant.IsEmpty || tenant.Value != message.TenantId
            || message.Inbound is { } inbound && inbound.Envelope.TenantId != tenant.Value)
            throw new InvalidOperationException("transport_tenant_mismatch");
        if (message.DeliveryId == Guid.Empty || (message.Inbound is null) == (message.RelayConnectionId is null)
            || message.RelayConnectionId == Guid.Empty)
            throw new InvalidOperationException("transport_message_invalid");
    }
}

public sealed class IntegrationRelayDispatcher(ITenantContext tenant, IIntegrationRouteResolver routes,
    IDbContextOutbox<IntegrationDbContext> outbox, OutboundRelay relay)
{
    public async Task DispatchAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var route = await routes.ResolveAsync(cancellationToken);
        if (route.Mode == IntegrationTransportRoute.InProcess)
        {
            await relay.RelayConnectionAsync(connectionId, cancellationToken);
            return;
        }
        await outbox.PublishAsync(new DispatchIntegrationTransport(route,
            new(tenant.TenantId.Value, Guid.CreateVersion7(), RelayConnectionId: connectionId)),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        await outbox.SaveChangesAndFlushMessagesAsync(MultiFlushMode.AllowMultiples, cancellationToken);
    }
}
