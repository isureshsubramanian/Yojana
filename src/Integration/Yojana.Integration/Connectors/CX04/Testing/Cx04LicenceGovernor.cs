namespace Yojana.Integration.Connectors.CX04.Testing;

internal sealed class Cx04LicenceGovernor(Cx04SimulatorOptions options) : IDisposable
{
    private readonly object requestLock = new();
    private readonly Queue<DateTimeOffset> requestTimes = [];
    private readonly SemaphoreSlim parallel = new(options.LicenceProfile.ParallelRequests);
    private int queued;

    public TimeSpan LastThrottleDelay { get; private set; }
    public int DelayedRequests { get; private set; }
    public int DeclinedRequests { get; private set; }
    public int ProcessedRequests { get; private set; }
    public int QueuedRequests => Volatile.Read(ref queued);

    public async Task<Cx04GovernorLease?> EnterAsync(CancellationToken cancellationToken)
    {
        var queueLimit = options.QueueLimitReading == Cx04QueueLimitReading.FixedTwenty
            ? 20 : options.LicenceProfile.MaximumQueueSize;
        var entered = parallel.Wait(0, cancellationToken);
        if (!entered)
        {
            if (Interlocked.Increment(ref queued) > queueLimit)
            {
                Interlocked.Decrement(ref queued);
                DeclinedRequests++;
                return null;
            }

            try
            {
                entered = await parallel.WaitAsync(options.QueuedRequestLifetime, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref queued);
            }

            if (!entered)
            {
                DeclinedRequests++;
                return null;
            }
        }

        var delay = CountAndCalculateDelay();
        if (delay > TimeSpan.Zero)
        {
            LastThrottleDelay = delay;
            DelayedRequests++;
            await options.DelayAsync(delay, cancellationToken);
        }

        ProcessedRequests++;
        return new Cx04GovernorLease(parallel);
    }

    private TimeSpan CountAndCalculateDelay()
    {
        lock (requestLock)
        {
            var now = options.TimeProvider.GetUtcNow();
            var start = now - TimeSpan.FromMinutes(1);
            while (requestTimes.TryPeek(out var expired) && expired <= start) requestTimes.Dequeue();

            var used = requestTimes.Count;
            var limit = options.LicenceProfile.RequestsPerMinute;
            var threshold = limit / 2;
            var delay = TimeSpan.Zero;
            if (used >= threshold && used < limit && requestTimes.TryPeek(out var oldest))
            {
                var left = TimeSpan.FromMinutes(1) - (now - oldest);
                var stillAllowed = limit - used;
                if (left > TimeSpan.Zero && stillAllowed > 0)
                    delay = TimeSpan.FromTicks(left.Ticks / stillAllowed);
            }

            requestTimes.Enqueue(now);
            return delay;
        }
    }

    public void Dispose() => parallel.Dispose();
}

internal sealed class Cx04GovernorLease(SemaphoreSlim parallel) : IDisposable
{
    private int disposed;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) parallel.Release();
    }
}
