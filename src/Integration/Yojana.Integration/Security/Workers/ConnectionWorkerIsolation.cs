using System.Collections.Concurrent;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Security.Workers;

public readonly record struct ConnectionWorkerKey(TenantId TenantId, Guid ConnectionId)
{
    public override string ToString() => $"{TenantId}:{ConnectionId:D}";
}

public interface IConnectionWorkerLease : IAsyncDisposable
{
    ConnectionWorkerKey WorkerKey { get; }
}

public interface IConnectionWorkerIsolation
{
    ValueTask<IConnectionWorkerLease> AcquireAsync(ConnectionWorkerKey workerKey,
        CancellationToken cancellationToken);
}

/// <summary>
/// Gives each connection an independent concurrency lane. P8-T11 composes its per-connection token bucket inside the
/// acquired lease; the bucket must not be shared between keys or used as a replacement for this concurrency boundary.
/// </summary>
public sealed class ConnectionWorkerIsolation(int maximumConcurrencyPerConnection = 1) : IConnectionWorkerIsolation
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, SemaphoreSlim> lanes = new();

    public async ValueTask<IConnectionWorkerLease> AcquireAsync(ConnectionWorkerKey workerKey,
        CancellationToken cancellationToken)
    {
        if (workerKey.TenantId.IsEmpty || workerKey.ConnectionId == Guid.Empty)
            throw new ArgumentException("Tenant and connection identifiers are required.", nameof(workerKey));
        if (maximumConcurrencyPerConnection < 1)
            throw new InvalidOperationException("Maximum concurrency per connection must be at least one.");
        var lane = lanes.GetOrAdd(workerKey,
            _ => new SemaphoreSlim(maximumConcurrencyPerConnection, maximumConcurrencyPerConnection));
        await lane.WaitAsync(cancellationToken);
        return new Lease(workerKey, lane);
    }

    private sealed class Lease(ConnectionWorkerKey workerKey, SemaphoreSlim lane) : IConnectionWorkerLease
    {
        private int released;
        public ConnectionWorkerKey WorkerKey { get; } = workerKey;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) lane.Release();
            return ValueTask.CompletedTask;
        }
    }
}
