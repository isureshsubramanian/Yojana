using Microsoft.Extensions.DependencyInjection;

namespace Yojana.Integration.Outbox;

public sealed record OutboundConnectorCapabilities(bool AcceptsIdempotencyKey, bool OwnsConnectionLane = false);

public sealed record OutboundLookup(
    Guid ConnectionId,
    string CommandType,
    string ErpReferenceText, string? Payload = null);

public sealed record OutboundCreateRequest(
    Guid ConnectionId,
    Guid OutboundCommandId,
    string CommandType,
    string ErpReferenceText,
    string Payload,
    Guid? IdempotencyKey);

public sealed record OutboundDocument(string ErpExternalId, string? ErpChangeMarker,
    IReadOnlyList<OutboundDocument>? RelatedDocuments = null);

public sealed record OutboundCreateResult(
    string Outcome,
    OutboundDocument? Document = null,
    string? Reason = null,
    string? PendingRemoteKey = null)
{
    public static OutboundCreateResult Created(string externalId, string? changeMarker = null) =>
        new(OutboundConnectorOutcome.Created, new(externalId, changeMarker));

    public static OutboundCreateResult Rejected(string reason) =>
        new(OutboundConnectorOutcome.Rejected, Reason: reason);

    public static OutboundCreateResult Unsupported(string reason) =>
        new(OutboundConnectorOutcome.Unsupported, Reason: reason);

    /// <summary>
    /// The remote holds the record for a person to review (for example an approval procedure the integration cannot
    /// trigger). The command waits; the connector's review tracker later reports it posted, rejected or withdrawn.
    /// </summary>
    public static OutboundCreateResult AwaitingReview(string pendingRemoteKey, string reason) =>
        new(OutboundConnectorOutcome.AwaitingReview, Reason: reason, PendingRemoteKey: pendingRemoteKey);
}

public static class OutboundConnectorOutcome
{
    public const string Created = "created";
    public const string AlreadyExisted = "already_existed";
    public const string Rejected = "rejected";
    public const string Unsupported = "unsupported";
    public const string Expired = "expired";
    public const string Manual = "manual";
    public const string AwaitingReview = "awaiting_review";
    public const string PostedAfterReview = "posted_after_review";
}

public sealed record OutboundReviewLookup(
    Guid ConnectionId,
    string CommandType,
    string ErpReferenceText,
    string PendingRemoteKey, string? Payload = null, Guid OutboundCommandId = default);

public static class OutboundReviewStates
{
    public const string Pending = "pending";
    public const string Posted = "posted";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
}

public sealed record OutboundReviewState(string State, OutboundDocument? Document = null, string? Reason = null,
    string? PendingRemoteKey = null)
{
    public static OutboundReviewState Pending { get; } = new(OutboundReviewStates.Pending);
    public static OutboundReviewState Posted(OutboundDocument document) => new(OutboundReviewStates.Posted, document);
    public static OutboundReviewState Rejected(string reason) => new(OutboundReviewStates.Rejected, Reason: reason);
    public static OutboundReviewState Withdrawn(string reason) => new(OutboundReviewStates.Withdrawn, Reason: reason);
}

/// <summary>
/// Optional connector capability, registered keyed by connector code. Only a connector that can answer
/// <see cref="OutboundCreateResult.AwaitingReview"/> registers one; it reads remote state and never writes.
/// </summary>
public interface IOutboundReviewTracker
{
    Task<OutboundReviewState> CheckAsync(OutboundReviewLookup lookup, CancellationToken cancellationToken);
}

/// <summary>
/// Narrow outbound boundary. A connector translates the canonical payload and owns only remote lookup/create I/O;
/// relay state, retry policy, mappings and dead letters remain in the gateway.
/// </summary>
public interface IOutboundConnector
{
    string ConnectorCode { get; }
    OutboundConnectorCapabilities Capabilities { get; }
    Task<OutboundDocument?> FindByReferenceAsync(OutboundLookup lookup, CancellationToken cancellationToken);
    Task<OutboundCreateResult> CreateAsync(OutboundCreateRequest request, CancellationToken cancellationToken);
}

public interface IOutboundConnectorResolver
{
    IOutboundConnector Resolve(string connectorCode);
    IOutboundReviewTracker? ResolveReviewTracker(string connectorCode);
}

public sealed class OutboundConnectorResolver(IServiceProvider services) : IOutboundConnectorResolver
{
    public IOutboundConnector Resolve(string connectorCode) =>
        services.GetRequiredKeyedService<IOutboundConnector>(connectorCode);

    public IOutboundReviewTracker? ResolveReviewTracker(string connectorCode) =>
        services.GetKeyedService<IOutboundReviewTracker>(connectorCode);
}

public interface IOutboundCommandParkingSink
{
    Task ParkAsync(Yojana.Integration.Data.Entities.OutboundCommandRow command, string reasonCode,
        string failure, CancellationToken cancellationToken);
}

/// <summary>The hand-entry task that tells a person a record waits for them in the remote system.</summary>
public interface IOutboundReviewTaskSink
{
    Task OpenAsync(Yojana.Integration.Data.Entities.OutboundCommandRow command, string note,
        CancellationToken cancellationToken);
    Task CloseAsync(Yojana.Integration.Data.Entities.OutboundCommandRow command, OutboundDocument document,
        CancellationToken cancellationToken);
}

public interface IInboundMessageParkingSink
{
    Task<Guid> ParkInboundAsync(Yojana.Integration.Data.Entities.InboundMessage message, string reasonCode,
        string failure, CancellationToken cancellationToken);
}
