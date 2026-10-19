using Yojana.Integration.Inbox;

namespace Yojana.Integration.Reconciliation;

public sealed record ConnectorEntityDayDigest(
    string Entity,
    DateOnly BusinessDate,
    long Count,
    decimal Quantity,
    long DistinctSerials,
    string KeyFieldHash);

public sealed record ConnectorOnHandBalance(
    Guid ItemId,
    string ItemCode,
    string? ItemName,
    Guid LocationId,
    string LocationCode,
    string? LocationName,
    decimal OnHand);

public sealed record ConnectorOnHandRequest(
    Guid ConnectionId,
    IReadOnlyDictionary<string, bool> WarehouseStatusMapping);

public sealed record ConnectorRangePage(
    IReadOnlyList<InboundAcceptance> Messages,
    string? NextCursor,
    long? TotalRecords = null);

/// <summary>
/// Connector read boundary shared by reconciliation and backfill. Polling may use the same adapter transport, but
/// this contract has no webhook or poll-watermark dependency, so a polling-only connection can run it unchanged.
/// </summary>
public interface IConnectorReconciliationSource
{
    Task<IReadOnlyList<ConnectorEntityDayDigest>> EntityDayDigestsAsync(Guid connectionId,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken);

    Task<IReadOnlyList<ConnectorOnHandBalance>> OnHandAsync(ConnectorOnHandRequest request,
        CancellationToken cancellationToken);

    Task<bool> SupportsRangedReadsAsync(Guid connectionId, string entity,
        CancellationToken cancellationToken);

    Task<ConnectorRangePage> ReadRangePageAsync(Guid connectionId, string entity,
        DateOnly rangeStart, DateOnly rangeEnd, string? cursor, int pageSize,
        CancellationToken cancellationToken);
}

public sealed class UnavailableConnectorReconciliationSource : IConnectorReconciliationSource
{
    private static InvalidOperationException Unavailable() =>
        new("No reconciliation source is registered for this connection.");

    public Task<IReadOnlyList<ConnectorEntityDayDigest>> EntityDayDigestsAsync(Guid connectionId,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<ConnectorEntityDayDigest>>(Unavailable());

    public Task<IReadOnlyList<ConnectorOnHandBalance>> OnHandAsync(ConnectorOnHandRequest request,
        CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<ConnectorOnHandBalance>>(Unavailable());

    public Task<bool> SupportsRangedReadsAsync(Guid connectionId, string entity,
        CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<ConnectorRangePage> ReadRangePageAsync(Guid connectionId, string entity,
        DateOnly rangeStart, DateOnly rangeEnd, string? cursor, int pageSize,
        CancellationToken cancellationToken) =>
        Task.FromException<ConnectorRangePage>(Unavailable());
}
