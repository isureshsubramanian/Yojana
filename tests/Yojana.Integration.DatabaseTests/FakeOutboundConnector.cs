using System.Collections.Concurrent;
using Yojana.Integration.Outbox;

namespace Yojana.Integration.DatabaseTests;

public enum FakeOutboundBehaviour
{
    Succeed,
    Fail,
    TimeoutAfterCreate,
    Reject,
    Unsupported,
    HoldForReview,
}

public sealed class FakeOutboundConnector : IOutboundConnector, IOutboundReviewTracker
{
    private readonly ConcurrentDictionary<string, OutboundReviewState> reviews = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, int> reviewChecks = new();
    public int ReviewChecksFor(Guid connectionId) => reviewChecks.GetValueOrDefault(connectionId);
    public bool FailReviewCheck { get; set; }

    /// <summary>What the remote reviewer did with the held record named by its pending key.</summary>
    public void Review(string pendingRemoteKey, OutboundReviewState state) => reviews[pendingRemoteKey] = state;
    private readonly ConcurrentDictionary<string, OutboundDocument> documents = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, byte> timedOutConnections = new();
    private readonly ConcurrentDictionary<Guid, int> findCalls = new();
    private readonly ConcurrentDictionary<Guid, int> createCalls = new();
    private readonly ConcurrentDictionary<Guid, Guid?> lastIdempotencyKeys = new();

    public Func<Task>? BeforeCreate { get; set; }

    public string ConnectorCode => IntegrationHostFixture.FakeConnectorCode;
    public OutboundConnectorCapabilities Capabilities { get; private set; } = new(true);
    public FakeOutboundBehaviour Behaviour { get; private set; }
    public int FindCallsFor(Guid connectionId) => findCalls.GetValueOrDefault(connectionId);
    public int CreateCallsFor(Guid connectionId) => createCalls.GetValueOrDefault(connectionId);
    public Guid? LastIdempotencyKeyFor(Guid connectionId) => lastIdempotencyKeys.GetValueOrDefault(connectionId);

    public void Configure(FakeOutboundBehaviour behaviour, bool acceptsIdempotencyKey = true)
    {
        documents.Clear();
        Behaviour = behaviour;
        Capabilities = new(acceptsIdempotencyKey);
        findCalls.Clear();
        createCalls.Clear();
        lastIdempotencyKeys.Clear();
        timedOutConnections.Clear();
        reviews.Clear();
        reviewChecks.Clear();
        FailReviewCheck = false;
        BeforeCreate = null;
    }

    public Task<OutboundDocument?> FindByReferenceAsync(OutboundLookup lookup,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        findCalls.AddOrUpdate(lookup.ConnectionId, 1, (_, count) => count + 1);
        return Task.FromResult(documents.GetValueOrDefault(lookup.ErpReferenceText));
    }

    public async Task<OutboundCreateResult> CreateAsync(OutboundCreateRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeCreate is { } before) await before();
        createCalls.AddOrUpdate(request.ConnectionId, 1, (_, count) => count + 1);
        lastIdempotencyKeys[request.ConnectionId] = request.IdempotencyKey;
        if (Behaviour == FakeOutboundBehaviour.Fail) throw new HttpRequestException("Fictional ERP unavailable.");
        if (Behaviour == FakeOutboundBehaviour.Reject)
            return OutboundCreateResult.Rejected("The fictional posting period is closed.");
        if (Behaviour == FakeOutboundBehaviour.Unsupported)
            return OutboundCreateResult.Unsupported("This connection does not support the document.");
        if (Behaviour == FakeOutboundBehaviour.HoldForReview)
            return OutboundCreateResult.AwaitingReview("HELD-" + request.ErpReferenceText,
                "A fictional reviewer must add the held record.");
        var document = documents.GetOrAdd(request.ErpReferenceText,
            _ => new($"ERP-{documents.Count + 1}", $"v{documents.Count + 1}"));
        if (Behaviour == FakeOutboundBehaviour.TimeoutAfterCreate
            && timedOutConnections.TryAdd(request.ConnectionId, 0))
            throw new TimeoutException("The reply was lost after creation.");
        return OutboundCreateResult.Created(document.ErpExternalId, document.ErpChangeMarker);
    }

    public Task<OutboundReviewState> CheckAsync(OutboundReviewLookup lookup, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        reviewChecks.AddOrUpdate(lookup.ConnectionId, 1, (_, count) => count + 1);
        if (FailReviewCheck) throw new HttpRequestException("Fictional ERP unavailable.");
        return Task.FromResult(reviews.GetValueOrDefault(lookup.PendingRemoteKey) ?? OutboundReviewState.Pending);
    }
}
