using System.Text.Json;

namespace Yojana.Integration.Connectors.CX02.Testing;

public sealed record Cx02SimulatorEntity(IReadOnlyDictionary<string, object?> Fields)
{
    public static Cx02SimulatorEntity From(object value) => new(
        JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(value))!);
}

public sealed record Cx02DeliveredNotification(
    string Channel,
    Uri Destination,
    string Body,
    int Attempt,
    DateTimeOffset SentAt);

public sealed class Cx02SimulatorRequestGate
{
    private TaskCompletionSource pause = Completed();

    public void Pause() => Interlocked.Exchange(ref pause,
        new(TaskCreationOptions.RunContinuationsAsynchronously));

    public void Resume() => Volatile.Read(ref pause).TrySetResult();

    internal Task WaitAsync(CancellationToken cancellationToken) =>
        Volatile.Read(ref pause).Task.WaitAsync(cancellationToken);

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }
}
