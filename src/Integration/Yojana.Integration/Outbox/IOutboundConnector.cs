namespace Yojana.Integration.Outbox;

public sealed record OutboundConnectorCapabilities(bool AcceptsIdempotencyKey, bool OwnsConnectionLane = false);

public sealed record OutboundLookup(
    Guid ConnectionId,
    string CommandType,
    string ErpReferenceText);

public sealed record OutboundCreateRequest(
    Guid ConnectionId,
    Guid OutboundCommandId,
    string CommandType,
    string ErpReferenceText,
    string Payload,
    Guid? IdempotencyKey);

public sealed record OutboundDocument(string ErpExternalId, string? ErpChangeMarker);

public sealed record OutboundCreateResult(
    string Outcome,
    OutboundDocument? Document = null,
    string? Reason = null)
{
    public static OutboundCreateResult Created(string externalId, string? changeMarker = null) =>
        new(OutboundConnectorOutcome.Created, new(externalId, changeMarker));

    public static OutboundCreateResult Rejected(string reason) =>
        new(OutboundConnectorOutcome.Rejected, Reason: reason);

    public static OutboundCreateResult Unsupported(string reason) =>
        new(OutboundConnectorOutcome.Unsupported, Reason: reason);
}

public static class OutboundConnectorOutcome
{
    public const string Created = "created";
    public const string AlreadyExisted = "already_existed";
    public const string Rejected = "rejected";
    public const string Unsupported = "unsupported";
    public const string Expired = "expired";
    public const string Manual = "manual";
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
}

public sealed class OutboundConnectorResolver(IEnumerable<IOutboundConnector> connectors) : IOutboundConnectorResolver
{
    private readonly Dictionary<string, IOutboundConnector> byCode = connectors.ToDictionary(
        connector => connector.ConnectorCode, StringComparer.Ordinal);

    public IOutboundConnector Resolve(string connectorCode) => byCode.TryGetValue(connectorCode, out var connector)
        ? connector
        : throw new InvalidOperationException($"No outbound connector is registered for code '{connectorCode}'.");
}

public interface IOutboundCommandParkingSink
{
    Task ParkAsync(Yojana.Integration.Data.Entities.OutboundCommandRow command, string reasonCode,
        string failure, CancellationToken cancellationToken);
}

public interface IInboundMessageParkingSink
{
    Task<Guid> ParkInboundAsync(Yojana.Integration.Data.Entities.InboundMessage message, string reasonCode,
        string failure, CancellationToken cancellationToken);
}
