using System.Collections.Concurrent;
using System.Diagnostics;

namespace Yojana.Bench;

/// <summary>
/// Counts the SQL statements sent, and the time spent waiting on them, while one measured command runs. It listens to
/// the client driver's diagnostic events, so it sees every statement whichever API (EF Core, raw commands) sent it.
/// A bulk copy's data stream and a transaction commit raise no command event: they show up in <see cref="Gaps"/> as
/// time between statements, next to the statement that preceded them. Only events from the async flow that started
/// the counter count, so background handlers running at the same time (the search re-indexer, outbox delivery) are
/// left out.
/// </summary>
public sealed class SqlCommandCounter : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly IDisposable allListeners;
    private readonly List<IDisposable> subscriptions = [];
    private readonly ConcurrentDictionary<Guid, long> started = new();
    private readonly ConcurrentBag<(double Ms, string Text)> timed = [];
    private readonly ConcurrentQueue<(long At, string Kind, string Text)> timeline = new();
    private readonly long origin = Stopwatch.GetTimestamp();
    private static readonly AsyncLocal<SqlCommandCounter?> Measured = new();
    private int commands;
    private long elapsedTicks;

    private SqlCommandCounter()
    {
        Measured.Value = this;
        allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public static SqlCommandCounter Start() => new();

    public int Commands => Volatile.Read(ref commands);

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(0, Interlocked.Read(ref elapsedTicks));

    /// <summary>The slowest statements seen, with the start of their text, for finding where a command's time goes.</summary>
    public IReadOnlyList<string> Slowest(int count) =>
        timed.OrderByDescending(t => t.Ms).Take(count).Select(t => string.Join(' ', t.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) is var flat && flat.Length > 300 ? $"{t.Ms:0} ms: {flat[..300]}…" : $"{t.Ms:0} ms: {flat}").ToList();

    /// <summary>
    /// The longest stretches with no statement in flight, from the start of the counter to <paramref name="end"/>: client
    /// work (EF Core, the bus), bulk copy streams and commits. Each names the event before and after it.
    /// </summary>
    public IReadOnlyList<string> Gaps(int count, long end)
    {
        var events = timeline.OrderBy(e => e.At).ToList();
        var gaps = new List<(double Ms, string Text)>();
        var inFlight = 0;
        var idleSince = origin;
        var before = "start";
        foreach (var e in events)
        {
            if (e.Kind == "before")
            {
                if (inFlight++ == 0) gaps.Add((Stopwatch.GetElapsedTime(idleSince, e.At).TotalMilliseconds, $"after [{before}] before [{e.Text}]"));
            }
            else if (e.Kind == "after")
            {
                inFlight = Math.Max(0, inFlight - 1);
                if (inFlight == 0) { idleSince = e.At; before = e.Text; }
            }
            else
            {
                gaps.Add((Stopwatch.GetElapsedTime(idleSince, e.At).TotalMilliseconds, $"after [{before}] before [{e.Text}]"));
                idleSince = e.At;
                before = e.Text;
            }
        }

        gaps.Add((Stopwatch.GetElapsedTime(idleSince, end).TotalMilliseconds, $"after [{before}] to the end"));
        var total = gaps.Sum(g => g.Ms);
        return [$"{total:0} ms with no statement in flight; longest:", .. gaps.OrderByDescending(g => g.Ms).Take(count).Select(g => $"{g.Ms:0} ms {g.Text}")];
    }

    private static string Short(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length > 70 ? flat[..70] + "…" : flat;
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name == "SqlClientDiagnosticListener")
        {
            lock (subscriptions) subscriptions.Add(listener.Subscribe(this));
        }
    }

    public void OnNext(KeyValuePair<string, object?> value)
    {
        if (!ReferenceEquals(Measured.Value, this)) return;
        var operation = value.Value?.GetType().GetProperty("OperationId")?.GetValue(value.Value) as Guid? ?? Guid.Empty;
        var now = Stopwatch.GetTimestamp();
        var command = value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value) as System.Data.Common.DbCommand;
        if (value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal))
        {
            started[operation] = now;
            timeline.Enqueue((now, "before", Short(command?.CommandText ?? "")));
        }
        else if (value.Key.EndsWith("WriteCommandAfter", StringComparison.Ordinal) || value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal))
        {
            timeline.Enqueue((now, "after", Short(command?.CommandText ?? "")));
            Interlocked.Increment(ref commands);
            if (started.TryRemove(operation, out var at))
            {
                var ticks = now - at;
                Interlocked.Add(ref elapsedTicks, ticks);
                var text = command?.CommandText ?? "";
                timed.Add((Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds, text));
            }
        }
        else if (!value.Key.Contains("Command", StringComparison.Ordinal))
        {
            // Connection open/close and transaction commit/rollback: points on the timeline, named by the event.
            timeline.Enqueue((now, "point", value.Key.Replace("Microsoft.Data.SqlClient.", "", StringComparison.Ordinal)));
        }
    }

    public void OnCompleted() { }

    public void OnError(Exception error) { }

    public void Dispose()
    {
        if (ReferenceEquals(Measured.Value, this)) Measured.Value = null;
        allListeners.Dispose();
        lock (subscriptions) subscriptions.ForEach(s => s.Dispose());
    }
}
