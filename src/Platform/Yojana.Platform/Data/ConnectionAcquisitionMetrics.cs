using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Yojana.Platform.Data;

/// <summary>
/// Process-level timing around <see cref="DbConnection.OpenAsync"/>. For pooled SQL connections this is the observable
/// acquisition wait (including a physical connect when the pool has to grow), exposed as cumulative counters so the
/// benchmark can take a before/after delta without coupling application code to the load harness.
/// </summary>
public static class ConnectionAcquisitionMetrics
{
    private static long count;
    private static long elapsedTicks;
    private static long maximumTicks;

    internal static void Observe(TimeSpan elapsed)
    {
        Interlocked.Increment(ref count);
        Interlocked.Add(ref elapsedTicks, elapsed.Ticks);
        var observed = Volatile.Read(ref maximumTicks);
        while (elapsed.Ticks > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximumTicks, elapsed.Ticks, observed);
            if (previous == observed) break;
            observed = previous;
        }
    }

    public static ConnectionAcquisitionSnapshot Snapshot() => new(
        Volatile.Read(ref count),
        TimeSpan.FromTicks(Volatile.Read(ref elapsedTicks)).TotalMilliseconds,
        TimeSpan.FromTicks(Volatile.Read(ref maximumTicks)).TotalMilliseconds);
}

public sealed record ConnectionAcquisitionSnapshot(long Count, double TotalMilliseconds, double MaximumMilliseconds);

internal sealed class ConnectionAcquisitionInterceptor : DbConnectionInterceptor
{
    public static readonly ConnectionAcquisitionInterceptor Instance = new();
    private readonly ConcurrentDictionary<Guid, long> started = new();

    public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result)
    {
        started[eventData.ConnectionId] = Stopwatch.GetTimestamp();
        return result;
    }

    public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection,
        ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        started[eventData.ConnectionId] = Stopwatch.GetTimestamp();
        return ValueTask.FromResult(result);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Finish(eventData.ConnectionId);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Finish(eventData.ConnectionId);
        return Task.CompletedTask;
    }

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) =>
        started.TryRemove(eventData.ConnectionId, out _);

    public override Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        started.TryRemove(eventData.ConnectionId, out _);
        return Task.CompletedTask;
    }

    private void Finish(Guid connectionId)
    {
        if (started.TryRemove(connectionId, out var at))
            ConnectionAcquisitionMetrics.Observe(Stopwatch.GetElapsedTime(at));
    }
}
