using System.Collections.Concurrent;
using System.Diagnostics;

namespace Yojana.Bench;

/// <summary>
/// Counts the SQL statements sent, and the time spent waiting on them, while one measured command runs. It listens to
/// the client driver's diagnostic events, so it sees every statement whichever API (EF Core, bulk copy, raw commands)
/// sent it. Only statements sent from the async flow that started the counter count, so background handlers running
/// at the same time (the search re-indexer, outbox delivery) are left out.
/// </summary>
public sealed class SqlCommandCounter : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private readonly IDisposable allListeners;
    private readonly List<IDisposable> subscriptions = [];
    private readonly ConcurrentDictionary<Guid, long> started = new();
    private readonly ConcurrentBag<(double Ms, string Text)> timed = [];
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
        if (value.Key.EndsWith("WriteCommandBefore", StringComparison.Ordinal))
        {
            started[operation] = Stopwatch.GetTimestamp();
        }
        else if (value.Key.EndsWith("WriteCommandAfter", StringComparison.Ordinal) || value.Key.EndsWith("WriteCommandError", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref commands);
            if (started.TryRemove(operation, out var at))
            {
                var ticks = Stopwatch.GetTimestamp() - at;
                Interlocked.Add(ref elapsedTicks, ticks);
                var text = (value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value) as System.Data.Common.DbCommand)?.CommandText ?? "";
                timed.Add((Stopwatch.GetElapsedTime(0, ticks).TotalMilliseconds, text));
            }
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
