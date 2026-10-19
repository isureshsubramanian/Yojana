using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Security.Workers;

public interface IConnectionBusyObserver
{
    Task PausedAsync(ConnectionWorkerKey key, TimeSpan duration, CancellationToken ct);
}

/// <summary>Transport reports only the fact of a busy response, never its headers, URI or body.</summary>
public sealed class ConnectionBusyAuditObserver(IServiceScopeFactory scopes, IClock clock) : IConnectionBusyObserver
{
    public async Task PausedAsync(ConnectionWorkerKey key, TimeSpan duration, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(key.TenantId);
        await scope.ServiceProvider.GetRequiredService<IConnectionAuditSink>().AppendAsync(new(key.TenantId,
            Guid.CreateVersion7(clock.UtcNow), key.ConnectionId, ConnectionAuditEventType.CallRateSlowedOnBusy,
            "system:connection-throttle", clock.UtcNow,
            System.Text.Json.JsonSerializer.Serialize(new { PauseSeconds = duration.TotalSeconds })), ct);
    }
}
