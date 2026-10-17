using System.Collections.Concurrent;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

/// <summary>
/// Development/test handoff for the sibling-owned replacement document. The merge replaces this registration with
/// the P5 replacement implementation; RMA deliberately knows only the returned intent id.
/// </summary>
public sealed class InMemoryReplacementIntentPort : IReplacementIntentPort
{
    public ConcurrentQueue<(Guid IntentId, ReplacementIntent Intent)> Intents { get; } = new();

    public Task<Guid> RecordAsync(ReplacementIntent request, CancellationToken cancellationToken)
    {
        var existing = Intents.FirstOrDefault(x => x.Intent.CommandId == request.CommandId && x.Intent.RmaLineId == request.RmaLineId);
        if (existing != default) return Task.FromResult(existing.IntentId);
        var id = TimeOrderedId.New(request.EventTime);
        Intents.Enqueue((id, request));
        return Task.FromResult(id);
    }
}
