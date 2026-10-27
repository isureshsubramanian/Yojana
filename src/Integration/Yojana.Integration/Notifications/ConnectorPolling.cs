using Yojana.Integration.Connections;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Integration.Connectors;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Notifications;

public sealed record ConnectorPollRequest(
    TenantId TenantId,
    Guid ConnectionId,
    string EntityType,
    string? Watermark,
    TimeSpan OverlapWindow,
    int MaximumEvents);

public sealed record ConnectorPollResult(
    IReadOnlyList<InboundAcceptance> Events,
    string? NextWatermark);

/// <summary>Connector boundary for modified-since polling. Implementations translate to canonical before returning.</summary>
public interface IConnectorPoller
{
    string ConnectorCode { get; }
    Task<ConnectorPollResult> PollAsync(ConnectorPollRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// The watermark that makes the next poll start at <paramref name="sourceLocalTime"/>, a time on the remote's own
    /// clock (the migration hand-over after a final load, OD-60). Null when the connector cannot start from a time; the
    /// caller then leaves the watermark as it is and says so.
    /// </summary>
    Task<string?> StartingWatermarkAsync(Guid connectionId, DateTime sourceLocalTime,
        CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

/// <summary>Connector-owned translation between UTC start instants and opaque remote watermark formats.</summary>
public interface IConnectorWatermarkSeed
{
    string ConnectorCode { get; }
    string WatermarkAt(DateTime utcInstant);
    DateTime? InstantOf(string watermark);
    bool IsMasterData(string entityType);
    Task<Connections.FullHistoryEstimate> EstimateFullHistoryAsync(Guid connectionId,
        CancellationToken cancellationToken) => Task.FromResult(new Connections.FullHistoryEstimate("unknown", null));
}

public sealed class ConnectionPollingService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IConnectorAdapterResolver adapters,
    InboundMessageStore inbox)
{
    public const int BatchSize = 1_024;

    public async Task<int> PollDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var due = await db.ConnectionPollWatermarks.Where(x => x.TenantId == tenant.TenantId
                && x.NextPollAt <= now && db.Connections.Any(connection => connection.TenantId == x.TenantId
                    && connection.ConnectionId == x.ConnectionId && connection.Status == ConnectionStatus.Active)
                && (x.Watermark != null && x.Watermark != ""
                    || x.CanonicalMessage == "ItemUpserted" || x.CanonicalMessage == "PartyUpserted"
                    || x.CanonicalMessage == "BomUpserted"
                    || db.ConnectionGatewaySettings.Any(setting => setting.TenantId == x.TenantId
                        && setting.ConnectionId == x.ConnectionId && setting.SyncStartMode != null)))
            .OrderBy(x => x.NextPollAt).ThenBy(x => x.ConnectionId).ThenBy(x => x.EntityType)
            .Take(100).ToListAsync(cancellationToken);
        var connectorCodes = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && due.Select(d => d.ConnectionId).Contains(x.ConnectionId) && x.Status == ConnectionStatus.Active)
            .ToDictionaryAsync(x => x.ConnectionId, x => x.ConnectorCode, cancellationToken);
        var accepted = 0;
        foreach (var state in due)
        {
            await using var activity = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, state.ConnectionId, cancellationToken);
            if (!connectorCodes.TryGetValue(state.ConnectionId, out var code)) continue;
            if (!await db.Connections.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == state.ConnectionId && x.Status == ConnectionStatus.Active, cancellationToken)) continue;
            try
            {
                var poller = adapters.Poller(code);
                var result = await poller.PollAsync(new(tenant.TenantId, state.ConnectionId, state.EntityType,
                    state.Watermark, TimeSpan.FromSeconds(state.OverlapSeconds), BatchSize), cancellationToken);
                foreach (var item in result.Events)
                {
                    var outcome = await inbox.Accept(item, cancellationToken);
                    if (outcome.Outcome == InboundAcceptanceResult.Accepted) accepted++;
                }
                state.Watermark = result.NextWatermark ?? state.Watermark;
                state.LastPolledAt = now;
                state.LastError = null;
                state.LastErrorCode = null;
                state.NextPollAt = now.AddSeconds(state.IntervalSeconds);
                state.Revision++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                var failure = Yojana.Relay.Contracts.RemoteFailureClassifier.Classify(exception);
                state.LastErrorCode = failure.Code;
                state.LastError = failure.Detail;
                state.NextPollAt = now.AddSeconds(Math.Min(state.IntervalSeconds, 300));
                state.Revision++;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return accepted;
    }

}

[DisallowConcurrentExecution]
public sealed class ConnectionPollingJob(ITenantContextSetter setter, ConnectionPollingService polling) : TenantJob(setter)
{
    public const string JobName = "integration.connection-polling";
    public const string Cron = "0 * * * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        polling.PollDueAsync(cancellationToken);
}
