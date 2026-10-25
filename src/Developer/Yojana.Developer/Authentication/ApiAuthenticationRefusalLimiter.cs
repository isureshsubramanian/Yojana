using System.Collections.Concurrent;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Developer.Authentication;

public sealed class ApiAuthenticationOptions
{
    public const string SectionName = "Developer:Authentication";
    public int RefusalLimitPerMinute { get; init; } = 20;

    public void Validate()
    {
        if (RefusalLimitPerMinute is < 1 or > 10_000)
            throw new InvalidOperationException(
                $"{SectionName}:RefusalLimitPerMinute must be between 1 and 10000.");
    }
}

/// <summary>
/// Bounds rejected API authentication attempts before either authentication path opens a database connection. A
/// successful exchange refunds its reservation, so ordinary client traffic never consumes the refusal budget.
/// </summary>
public sealed class ApiAuthenticationRefusalLimiter(IClock clock, ApiAuthenticationOptions options)
{
    private static readonly TimeSpan WindowLength = TimeSpan.FromMinutes(1);
    private const int MaximumTrackedAddresses = 10_000;
    private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);

    public ApiAuthenticationAttempt Acquire(string? clientAddress)
    {
        var key = string.IsNullOrWhiteSpace(clientAddress) ? "unknown" : clientAddress;
        if (windows.Count > MaximumTrackedAddresses) PruneExpired();
        var window = windows.GetOrAdd(key, _ => new Window(clock.UtcNow));
        lock (window)
        {
            ResetExpired(window);
            var retryAfter = window.StartedAt + WindowLength - clock.UtcNow;
            // Only completed refusals count. In-flight requests must not: a busy integration sends many concurrent
            // valid requests from one address, and counting them would throttle traffic that is allowed. Concurrent
            // bad requests can overshoot the limit by at most the number in flight, which is bounded by the server.
            if (window.Refusals >= options.RefusalLimitPerMinute)
                return ApiAuthenticationAttempt.Throttled(retryAfter);
            window.Reservations++;
            return ApiAuthenticationAttempt.Permitted(this, window, window.Generation);
        }
    }

    private void Complete(Window window, long generation, bool refused)
    {
        lock (window)
        {
            ResetExpired(window);
            if (window.Generation != generation) return;
            window.Reservations--;
            if (refused) window.Refusals++;
        }
    }

    /// <summary>Bounds memory when many distinct addresses arrive; idle windows are dropped once they expire.</summary>
    private void PruneExpired()
    {
        foreach (var (key, window) in windows)
        {
            bool expired;
            lock (window) expired = window.Reservations == 0 && clock.UtcNow >= window.StartedAt + WindowLength;
            if (expired) windows.TryRemove(new KeyValuePair<string, Window>(key, window));
        }
    }

    private void ResetExpired(Window window)
    {
        if (clock.UtcNow < window.StartedAt + WindowLength) return;
        window.StartedAt = clock.UtcNow;
        window.Refusals = 0;
        window.Reservations = 0;
        window.Generation++;
    }

    internal sealed class Window(DateTime startedAt)
    {
        public DateTime StartedAt { get; set; } = startedAt;
        public int Refusals { get; set; }
        public int Reservations { get; set; }
        public long Generation { get; set; }
    }

    public sealed class ApiAuthenticationAttempt : IDisposable
    {
        private readonly ApiAuthenticationRefusalLimiter? owner;
        private readonly Window? window;
        private readonly long generation;
        private bool completed;

        private ApiAuthenticationAttempt(ApiAuthenticationRefusalLimiter owner, Window window, long generation)
        {
            this.owner = owner;
            this.window = window;
            this.generation = generation;
            Allowed = true;
        }

        private ApiAuthenticationAttempt(TimeSpan retryAfter)
        {
            RetryAfter = retryAfter <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : retryAfter;
        }

        public bool Allowed { get; }
        public TimeSpan RetryAfter { get; }

        public void Refused() => Complete(true);
        public void Succeeded() => Complete(false);
        public void Dispose() => Complete(false);

        private void Complete(bool refused)
        {
            if (completed || owner is null || window is null) return;
            completed = true;
            owner.Complete(window, generation, refused);
        }

        internal static ApiAuthenticationAttempt Permitted(ApiAuthenticationRefusalLimiter owner, Window window,
            long generation) => new(owner, window, generation);

        internal static ApiAuthenticationAttempt Throttled(TimeSpan retryAfter) => new(retryAfter);
    }
}
