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

    ValueTask<IConnectionWorkerLease> AcquireAsync(ConnectionWorkerKey workerKey, int maximumConcurrency,
        CancellationToken cancellationToken);
}

/// <summary>
/// Gives each connection an independent concurrency lane. P8-T11 composes its per-connection token bucket inside the
/// acquired lease; the bucket must not be shared between keys or used as a replacement for this concurrency boundary.
/// </summary>
public sealed class ConnectionWorkerIsolation(int maximumConcurrencyPerConnection = 1) :
    IConnectionWorkerIsolation, IDisposable
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, Lane> lanes = new();

    public async ValueTask<IConnectionWorkerLease> AcquireAsync(ConnectionWorkerKey workerKey,
        CancellationToken cancellationToken) =>
        await AcquireAsync(workerKey, maximumConcurrencyPerConnection, cancellationToken);

    public async ValueTask<IConnectionWorkerLease> AcquireAsync(ConnectionWorkerKey workerKey,
        int maximumConcurrency, CancellationToken cancellationToken)
    {
        if (workerKey.TenantId.IsEmpty || workerKey.ConnectionId == Guid.Empty)
            throw new ArgumentException("Tenant and connection identifiers are required.", nameof(workerKey));
        if (maximumConcurrency < 1)
            throw new InvalidOperationException("Maximum concurrency per connection must be at least one.");
        var lane = lanes.GetOrAdd(workerKey, _ => new Lane(maximumConcurrency));
        var semaphore = await lane.EnterAsync(maximumConcurrency, cancellationToken);
        return new Lease(workerKey, lane, semaphore);
    }

    public void Dispose()
    {
        foreach (var lane in lanes.Values) lane.Dispose();
    }

    private sealed class Lane(int maximumConcurrency) : IDisposable
    {
        private readonly object sync = new();
        private SemaphoreSlim semaphore = new(maximumConcurrency, maximumConcurrency);
        private int configuredMaximum = maximumConcurrency;
        private int desiredMaximum = maximumConcurrency;
        private int active;
        private int waiters;

        public async Task<SemaphoreSlim> EnterAsync(int requestedMaximum, CancellationToken cancellationToken)
        {
            SemaphoreSlim selected;
            lock (sync)
            {
                desiredMaximum = requestedMaximum;
                ReconfigureWhenIdle();
                selected = semaphore;
                waiters++;
            }

            try
            {
                await selected.WaitAsync(cancellationToken);
            }
            catch
            {
                lock (sync)
                {
                    waiters--;
                    ReconfigureWhenIdle();
                }
                throw;
            }

            lock (sync)
            {
                waiters--;
                active++;
            }
            return selected;
        }

        public void Exit(SemaphoreSlim selected)
        {
            lock (sync)
            {
                active--;
                selected.Release();
                ReconfigureWhenIdle();
            }
        }

        private void ReconfigureWhenIdle()
        {
            if (active != 0 || waiters != 0 || desiredMaximum == configuredMaximum) return;
            semaphore.Dispose();
            semaphore = new SemaphoreSlim(desiredMaximum, desiredMaximum);
            configuredMaximum = desiredMaximum;
        }

        public void Dispose() => semaphore.Dispose();
    }

    private sealed class Lease : IConnectionWorkerLease
    {
        private int released;
        private readonly Lane lane;
        private readonly SemaphoreSlim semaphore;
        public ConnectionWorkerKey WorkerKey { get; }

        public Lease(ConnectionWorkerKey workerKey, Lane lane, SemaphoreSlim semaphore)
        {
            WorkerKey = workerKey;
            this.lane = lane;
            this.semaphore = semaphore;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref released, 1) == 0) lane.Exit(semaphore);
            return ValueTask.CompletedTask;
        }
    }
}
