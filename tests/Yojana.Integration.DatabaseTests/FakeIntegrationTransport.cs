using System.Collections.Concurrent;
using Yojana.Integration.Contracts;
using Yojana.Integration.Transport;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

public sealed class FakeIntegrationTransport : IIntegrationTransport
{
    private readonly ConcurrentQueue<(IntegrationTransportRoute Route, IntegrationTransportMessage Message)> deliveries = new();
    public Task SendAsync(IntegrationTransportRoute route, IntegrationTransportMessage message, CancellationToken cancellationToken)
    {
        deliveries.Enqueue((route, message));
        return Task.CompletedTask;
    }
    public Task EnsureReceivingAsync(TenantId tenant, IntegrationTransportRoute route, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<string> ProbeAsync(TenantId tenant, IntegrationTransportRoute route, CancellationToken cancellationToken) => Task.FromResult("connected");

    public async Task<(IntegrationTransportRoute Route, IntegrationTransportMessage Message)> NextAsync(TenantId tenant)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            if (deliveries.TryDequeue(out var delivery))
            {
                if (delivery.Message.TenantId == tenant.Value) return delivery;
                deliveries.Enqueue(delivery);
            }
            await Task.Delay(20, timeout.Token);
        }
    }
}
