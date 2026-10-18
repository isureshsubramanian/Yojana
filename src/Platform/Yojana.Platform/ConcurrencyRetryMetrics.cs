using Microsoft.EntityFrameworkCore;

namespace Yojana.Platform;

/// <summary>
/// Process-local counter for the platform-wide optimistic-concurrency policy. It is intentionally a cheap monotonic
/// metric: the load harness samples a before/after delta, while normal production telemetry may scrape the same value.
/// </summary>
public static class ConcurrencyRetryMetrics
{
    private static long observed;
    private static long exhausted;

    public static long Observed => Interlocked.Read(ref observed);
    public static long Exhausted => Interlocked.Read(ref exhausted);

    public static bool Observe(DbUpdateConcurrencyException exception)
    {
        Interlocked.Increment(ref observed);
        return true;
    }

    internal static void ObserveExhausted() => Interlocked.Increment(ref exhausted);
}
