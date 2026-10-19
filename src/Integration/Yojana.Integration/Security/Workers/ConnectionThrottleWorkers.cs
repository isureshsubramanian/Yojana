using System.Collections.Concurrent;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Security.Workers;

public interface IConnectionThrottleDelay
{
    Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemConnectionThrottleDelay : IConnectionThrottleDelay
{
    public Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public interface IConnectionThrottleWorker
{
    ValueTask<IConnectionWorkerLease> AcquireAsync(ConnectionThrottleProfile profile,
        CancellationToken cancellationToken);

    void PauseUntil(DateTime resumeAtUtc);
}

public interface IConnectionThrottleWorkerRegistry
{
    IConnectionThrottleWorker For(ConnectionWorkerKey workerKey);
}

public sealed class ConnectionThrottleWorkerRegistry(
    IConnectionWorkerIsolation isolation,
    IConnectionDailyBudgetStore dailyBudgets,
    IConnectionCallBudgetAlertPublisher alerts,
    IClock clock,
    IConnectionThrottleDelay delay) : IConnectionThrottleWorkerRegistry
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, ConnectionThrottleWorker> workers = new();

    public IConnectionThrottleWorker For(ConnectionWorkerKey workerKey) =>
        workers.GetOrAdd(workerKey, key => new ConnectionThrottleWorker(key, isolation,
            new ConnectionDailyBudget(dailyBudgets, alerts, clock), clock, delay));
}

internal sealed class ConnectionThrottleWorker(
    ConnectionWorkerKey workerKey,
    IConnectionWorkerIsolation isolation,
    ConnectionDailyBudget dailyBudget,
    IClock clock,
    IConnectionThrottleDelay delay) : IConnectionThrottleWorker
{
    private readonly object sync = new();
    private TokenBucket? secondBucket;
    private TokenBucket? minuteBucket;
    private (int? PerSecond, int? PerMinute, int Burst)? configuredRates;
    private DateTime pausedUntilUtc = DateTime.MinValue;

    public async ValueTask<IConnectionWorkerLease> AcquireAsync(ConnectionThrottleProfile profile,
        CancellationToken cancellationToken)
    {
        profile.Validate();
        var lane = await isolation.AcquireAsync(workerKey, profile.MaxConcurrentCalls, cancellationToken);
        try
        {
            await AwaitRateAsync(profile, cancellationToken);
            await dailyBudget.ReserveAsync(workerKey, profile, cancellationToken);
            return lane;
        }
        catch
        {
            await lane.DisposeAsync();
            throw;
        }
    }

    public void PauseUntil(DateTime resumeAtUtc)
    {
        if (resumeAtUtc.Kind != DateTimeKind.Utc)
            resumeAtUtc = DateTime.SpecifyKind(resumeAtUtc, DateTimeKind.Utc);
        lock (sync)
        {
            if (resumeAtUtc > pausedUntilUtc) pausedUntilUtc = resumeAtUtc;
        }
    }

    private async Task AwaitRateAsync(ConnectionThrottleProfile profile,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan wait;
            lock (sync)
            {
                var now = clock.UtcNow;
                Configure(profile, now);
                var pause = pausedUntilUtc > now ? pausedUntilUtc - now : TimeSpan.Zero;
                var seconds = secondBucket?.WaitForToken(now) ?? TimeSpan.Zero;
                var minutes = minuteBucket?.WaitForToken(now) ?? TimeSpan.Zero;
                wait = Max(pause, seconds, minutes);
                if (wait <= TimeSpan.Zero)
                {
                    secondBucket?.Take();
                    minuteBucket?.Take();
                    return;
                }
            }
            await delay.WaitAsync(wait, cancellationToken);
        }
    }

    private void Configure(ConnectionThrottleProfile profile, DateTime now)
    {
        var rates = (profile.RequestsPerSecond, profile.RequestsPerMinute, profile.Burst);
        if (rates == configuredRates) return;
        secondBucket = profile.RequestsPerSecond is { } perSecond
            ? new TokenBucket(perSecond, TimeSpan.FromSeconds(1), profile.Burst, now)
            : null;
        minuteBucket = profile.RequestsPerMinute is { } perMinute
            ? new TokenBucket(perMinute, TimeSpan.FromMinutes(1), perMinute, now)
            : null;
        configuredRates = rates;
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second, TimeSpan third) =>
        first >= second && first >= third ? first : second >= third ? second : third;

    private sealed class TokenBucket(int tokensPerPeriod, TimeSpan period, int capacity, DateTime now)
    {
        private readonly double tokensPerSecond = tokensPerPeriod / period.TotalSeconds;
        private readonly int capacity = capacity;
        private double tokens = capacity;
        private DateTime lastRefillUtc = now;

        public TimeSpan WaitForToken(DateTime now)
        {
            Refill(now);
            if (tokens >= 1) return TimeSpan.Zero;
            return TimeSpan.FromSeconds((1 - tokens) / tokensPerSecond);
        }

        public void Take() => tokens--;

        private void Refill(DateTime now)
        {
            if (now <= lastRefillUtc) return;
            tokens = Math.Min(capacity, tokens + (now - lastRefillUtc).TotalSeconds * tokensPerSecond);
            lastRefillUtc = now;
        }
    }
}
