using System.Collections.Concurrent;
using Yojana.Serialisation.Certificates;

namespace Yojana.Serialisation.DatabaseTests;

public sealed class TestCertificateRunChunkObserver : ICertificateRunChunkObserver
{
    private readonly ConcurrentDictionary<Guid, int> failures = new();

    public Guid? FailCommandId { get; set; }
    public int FailBeforeChunk { get; set; }

    public Task StartingAsync(Guid commandId, int chunkNumber, int unitCount, CancellationToken cancellationToken)
    {
        if (commandId == FailCommandId && chunkNumber == FailBeforeChunk && failures.TryAdd(commandId, chunkNumber))
            throw new SimulatedCertificateChunkFailureException();
        return Task.CompletedTask;
    }

    public void Reset()
    {
        FailCommandId = null;
        FailBeforeChunk = 0;
        failures.Clear();
    }
}

public sealed class SimulatedCertificateChunkFailureException : Exception;
