using Microsoft.EntityFrameworkCore;
using Quartz;
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
}

public sealed class ConnectionPollingService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IEnumerable<IConnectorPoller> pollers,
    InboundMessageStore inbox)
{
    public const int BatchSize = 1_024;

    public async Task<int> PollDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var due = await db.ConnectionPollWatermarks.Where(x => x.TenantId == tenant.TenantId
                && x.NextPollAt <= now)
            .OrderBy(x => x.NextPollAt).ThenBy(x => x.ConnectionId).ThenBy(x => x.EntityType)
            .Take(100).ToListAsync(cancellationToken);
        var connectorCodes = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && due.Select(d => d.ConnectionId).Contains(x.ConnectionId) && x.Status == ConnectionStatus.Active)
            .ToDictionaryAsync(x => x.ConnectionId, x => x.ConnectorCode, cancellationToken);
        var accepted = 0;
        foreach (var state in due)
        {
            if (!connectorCodes.TryGetValue(state.ConnectionId, out var code)) continue;
            var poller = pollers.SingleOrDefault(x => StringComparer.Ordinal.Equals(x.ConnectorCode, code));
            if (poller is null)
            {
                state.LastError = "connector_poller_unavailable";
                state.NextPollAt = now.AddSeconds(state.IntervalSeconds);
                continue;
            }
            try
            {
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
                state.NextPollAt = now.AddSeconds(state.IntervalSeconds);
                state.Revision++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                state.LastError = Error(exception);
                state.NextPollAt = now.AddSeconds(Math.Min(state.IntervalSeconds, 300));
                state.Revision++;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return accepted;
    }

    private static string Error(Exception exception)
    {
        var value = $"{exception.GetType().Name}: {exception.Message}";
        return value.Length <= 2000 ? value : value[..2000];
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
