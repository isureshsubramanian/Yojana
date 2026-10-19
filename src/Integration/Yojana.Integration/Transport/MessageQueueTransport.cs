using System.Collections.Concurrent;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.AzureServiceBus;
using Wolverine.Configuration;
using Yojana.Integration.Contracts;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Transport;

public sealed record MessageQueueReceiveOptions(bool ReceiveInbound = true, bool ReceiveOutbound = true);

/// <summary>Each immutable tenant route has a carrier runtime. The product's SQL outbox retains work until broker ack;
/// receive acknowledgement follows the receiver's SQL commit. No broker participates in a database transaction.</summary>
public sealed class MessageQueueTransport(IServiceScopeFactory scopes, IIntegrationTransportReceiver receiver,
    MessageQueueReceiveOptions receiveOptions) : IIntegrationTransport, IAsyncDisposable
{
    private readonly ConcurrentDictionary<(TenantId, Guid), Carrier> carriers = new();
    private readonly ConcurrentDictionary<(TenantId, Guid), SemaphoreSlim> creation = new();

    public async Task SendAsync(IntegrationTransportRoute route, IntegrationTransportMessage message,
        CancellationToken cancellationToken)
    {
        var tenant = new TenantId(message.TenantId);
        IntegrationTransportReceiver.Validate(tenant, message);
        if (route.Mode == IntegrationTransportRoute.InProcess)
        {
            await receiver.ReceiveAsync(tenant, message, cancellationToken);
            return;
        }
        try
        {
            var carrier = await GetAsync(tenant, route, cancellationToken);
            var endpoint = message.Inbound is null ? carrier.Outbound : carrier.Inbound;
            // Inline sending awaits broker acknowledgement. The outer SQL-backed Wolverine handler retries on failure.
            await carrier.Host.Services.GetRequiredService<IMessageBus>().EndpointFor(endpoint).SendAsync(message,
                new DeliveryOptions { TenantId = tenant.ToString() });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw Sanitise(exception); }
    }

    public async Task EnsureReceivingAsync(TenantId tenant, IntegrationTransportRoute route,
        CancellationToken cancellationToken)
    {
        if (route.Mode == IntegrationTransportRoute.InProcess) return;
        try
        {
            var state = await ProbeAsync(tenant, route, cancellationToken);
            if (state != "connected") throw new InvalidOperationException("The message queue transport is not ready.");
            _ = await GetAsync(tenant, route, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { throw Sanitise(exception); }
    }

    public async Task<string> ProbeAsync(TenantId tenant, IntegrationTransportRoute route,
        CancellationToken cancellationToken)
    {
        if (route.Mode == IntegrationTransportRoute.InProcess) return "connected";
        try
        {
            var secret = await ReadSecretAsync(tenant, route, cancellationToken);
            if (secret is null) return "not_configured";
            var admin = new ServiceBusAdministrationClient(secret);
            var prefix = route.AddressPrefix(tenant);
            if (!(await admin.TopicExistsAsync(prefix + "-inbound", cancellationToken)).Value
                || !(await admin.SubscriptionExistsAsync(prefix + "-inbound", "gateway", cancellationToken)).Value
                || !(await admin.QueueExistsAsync(prefix + "-outbound", cancellationToken)).Value)
                return "not_configured";
            return "connected";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (UnauthorizedAccessException) { return "auth_failed"; }
        catch (Azure.RequestFailedException exception) when (exception.Status is 401 or 403) { return "auth_failed"; }
        catch (System.Security.Cryptography.CryptographicException) { return "auth_failed"; }
        catch { return "unavailable"; }
    }

    private async Task<Carrier> GetAsync(TenantId tenant, IntegrationTransportRoute route,
        CancellationToken cancellationToken)
    {
        var reference = route.CredentialReference
            ?? throw new InvalidOperationException("The message queue transport is not configured.");
        if (carriers.TryGetValue((tenant, reference), out var existing)) return existing;
        var gate = creation.GetOrAdd((tenant, reference), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (carriers.TryGetValue((tenant, reference), out existing)) return existing;
            var secret = await ReadSecretAsync(tenant, route, cancellationToken)
                ?? throw new InvalidOperationException("The message queue transport is not configured.");
            var prefix = route.AddressPrefix(tenant);
            var host = BuildCarrierHost(tenant, route, secret, receiver, receiveOptions);
            try { await host.StartAsync(cancellationToken); }
            catch { host.Dispose(); throw; }
            var carrier = new Carrier(host, new Uri($"asb://topic/{prefix}-inbound"),
                new Uri($"asb://queue/{prefix}-outbound"));
            carriers[(tenant, reference)] = carrier;
            return carrier;
        }
        finally { gate.Release(); }
    }

    internal static IHost BuildCarrierHost(TenantId tenant, IntegrationTransportRoute route, string secret,
        IIntegrationTransportReceiver receiver, MessageQueueReceiveOptions receiveOptions)
    {
        var prefix = route.AddressPrefix(tenant);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true, ApplicationName = typeof(MessageQueueTransport).Assembly.GetName().Name,
        });
        // Carrier diagnostics never receive a logging provider: third-party exception text can contain credentials.
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new MessageQueueReceiverBinding(tenant, receiver));
        builder.UseWolverine(options =>
        {
            options.ServiceName = prefix;
            options.UseRuntimeCompilation();
            options.Discovery.DisableConventionalDiscovery();
            options.Discovery.IncludeType(typeof(MessageQueueDeliveryHandler));
            options.UseAzureServiceBus(secret).SystemQueuesAreEnabled(false);
            var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();
            transport.AutoProvision = false;
            var inbound = transport.Topics[prefix + "-inbound"];
            inbound.Mode = EndpointMode.Inline;
            var outbound = transport.Queues[prefix + "-outbound"];
            outbound.Mode = EndpointMode.Inline;
            if (receiveOptions.ReceiveInbound)
            {
                var subscription = inbound.FindOrCreateSubscription("gateway");
                subscription.IsListener = true;
                subscription.Mode = EndpointMode.Inline;
            }
            outbound.IsListener = receiveOptions.ReceiveOutbound;
        });
        return builder.Build();
    }

    private async Task<string?> ReadSecretAsync(TenantId tenant, IntegrationTransportRoute route,
        CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
        return await scope.ServiceProvider.GetRequiredService<MessageQueueCredentials>()
            .ReadSecretAsync(route.CredentialReference!.Value, cancellationToken);
    }

    private static IntegrationTransportUnavailableException Sanitise(Exception exception) => new(
        exception is UnauthorizedAccessException
            || exception is Azure.RequestFailedException { Status: 401 or 403 }
                ? "Message queue transport authentication failed."
                : "Message queue transport is unavailable; the durable dispatch remains retryable.");

    public async ValueTask DisposeAsync()
    {
        foreach (var carrier in carriers.Values)
        {
            await carrier.Host.StopAsync();
            carrier.Host.Dispose();
        }
        foreach (var gate in creation.Values) gate.Dispose();
    }

    private sealed record Carrier(IHost Host, Uri Inbound, Uri Outbound);
}

public sealed record MessageQueueReceiverBinding(TenantId Tenant, IIntegrationTransportReceiver Receiver);

// Explicitly registered only in the carrier runtime; the product runtime has a different receive boundary.
[WolverineIgnore]
[NonTransactional]
public static class MessageQueueDeliveryHandler
{
    public static Task<IntegrationTransportReceipt> Handle(IntegrationTransportMessage message, MessageQueueReceiverBinding binding,
        CancellationToken cancellationToken) => binding.Receiver.ReceiveAsync(binding.Tenant, message, cancellationToken);
}
