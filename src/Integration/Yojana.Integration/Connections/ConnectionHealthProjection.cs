using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Connections;

/// <summary>Minute-based, tenant-scoped projection. Reads never aggregate inbox/outbox history.</summary>
public sealed class ConnectionHealthProjection(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    ConnectionHealthSettings settings, OutboxRelaySettings outboxSettings, IConnectionAuditSink audit, IServiceProvider services)
{
    private static readonly HashSet<string> InboundTypes =
    [
        nameof(ItemUpserted), nameof(PartyUpserted), nameof(GoodsReceiptPosted), nameof(ProductionCompleted),
        nameof(DeliveryPosted), nameof(InvoicePosted), nameof(ReturnReceived), nameof(TransferPosted),
        nameof(InventoryAdjusted), nameof(WarrantyLineSold), nameof(DocumentCancelled),
    ];

    public async Task RefreshTenantAsync(CancellationToken ct)
    {
        Guid? after = null;
        while (true)
        {
            var query = db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && (x.Status != ConnectionStatus.Archived || !db.ConnectionHealth.Any(health =>
                    health.TenantId == x.TenantId && health.ConnectionId == x.ConnectionId && health.Status == "archived")));
            if (after is { } lastId) query = query.Where(x => x.ConnectionId.CompareTo(lastId) > 0);
            var ids = await query.OrderBy(x => x.ConnectionId).Select(x => x.ConnectionId).Take(50).ToListAsync(ct);
            foreach (var id in ids) await RefreshAsync(id, ct);
            if (ids.Count < 50) return;
            after = ids[^1];
        }
    }

    public async Task<ConnectionHealth> RefreshAsync(Guid connectionId, CancellationToken ct)
    {
        await using var projectionLease = await ConnectionOperationLock.AcquireAsync(db, tenant.TenantId,
            $"health:{connectionId:N}", ct);
        var connection = await db.Connections.AsNoTracking().SingleAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct);
        var row = await db.ConnectionHealth.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct);
        if (row is null)
        {
            row = new() { TenantId = tenant.TenantId, ConnectionId = connectionId };
            db.ConnectionHealth.Add(row);
        }
        var previous = row.Status;
        var priorCredentialDue = row.CredentialReplaceBy <= row.UpdatedAt.AddDays(row.CredentialWarnDays);
        var now = clock.UtcNow;
        var thresholds = await settings.GetAsync(connectionId, ct);
        var maxAge = await outboxSettings.MaxAgeAsync(connectionId, ct);
        var polls = await db.ConnectionPollWatermarks.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).ToListAsync(ct);
        var sources = await db.ConnectionNotificationSources.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).ToListAsync(ct);
        var watchdog = await db.ConnectionNotificationWatchdogs.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId, ct);
        var lifecycle = services.GetKeyedService<IConnectionLifecycleAdapter>(connection.ConnectorCode);
        var evidence = lifecycle is null ? null : await lifecycle.LatestProbeAsync(connectionId, ct);
        var streamSupported = evidence?.NotificationsSupported != false;
        row.NotificationState = !streamSupported ? "not_supported_by_remote_release"
            : watchdog?.StreamState is "active" or "stopped" or "resumed_by_watchdog" ? watchdog.StreamState
            : sources.Any(x => x.Enabled) ? "active" : "not_subscribed";
        if (connection.Status == ConnectionStatus.Archived) row.NotificationState = "not_subscribed";
        var inbound = db.InboundMessages.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId);
        row.LastInboundAt = await inbound.MaxAsync(x => (DateTime?)x.AcceptedAt, ct);
        var today = now.Date;
        var tomorrow = today.AddDays(1);
        var firstDay = today.AddDays(-29);
        var completedDays = JsonSerializer.Deserialize<List<DailyInboundCount>>(row.CompletedInboundDaysJson,
            IntegrationContracts.Json)!;
        completedDays.RemoveAll(day => day.Day < firstDay);
        // AcceptedAt is the server's receipt time, not the remote event date. Fold each completed UTC day once;
        // bootstrap (or recovery after downtime) scans at most 29 completed days. Ordinary minute refreshes scan
        // today only. Completed counts are frozen; today's counts and day rollover lag by at most one job interval.
        var foldFrom = row.InboundCountDateUtc is { } countedDate && countedDate > firstDay ? countedDate : firstDay;
        if (foldFrom < today)
        {
            var folded = await inbound.Where(x => x.AcceptedAt >= foldFrom && x.AcceptedAt < today)
                .GroupBy(x => x.AcceptedAt.Date)
                .Select(group => new { Day = group.Key, Count = group.LongCount() }).ToListAsync(ct);
            completedDays.AddRange(folded.Select(day => new DailyInboundCount(day.Day, day.Count)));
        }
        row.InboundCountDateUtc = today;
        row.CompletedInboundDaysJson = JsonSerializer.Serialize(completedDays, IntegrationContracts.Json);
        var counts = await inbound.Where(x => x.AcceptedAt >= today && x.AcceptedAt < tomorrow)
            .GroupBy(x => x.MessageType).Select(group => new
            {
                MessageType = group.Key,
                Today = group.LongCount(),
            }).ToListAsync(ct);
        row.ReceivedToday = counts.Sum(x => x.Today);
        row.ReceivedLast30Days = completedDays.Sum(x => x.Count) + row.ReceivedToday;
        var waiting = db.OutboundCommands.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && (x.Status == OutboundCommandStatus.Pending
                || x.Status == OutboundCommandStatus.Retry || x.Status == OutboundCommandStatus.Processing));
        row.OutboxDepth = await waiting.CountAsync(ct);
        row.OldestWaitingAt = await waiting.MinAsync(x => (DateTime?)x.CreatedAt, ct);
        var letters = db.DeadLetterItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && (x.Status == DeadLetterStatus.NeedsAttention
                || x.Status == DeadLetterStatus.RetryPending));
        var attention = await letters.GroupBy(x => x.RecordType)
            .Select(group => new { RecordType = group.Key, Count = group.Count() }).ToListAsync(ct);
        row.OpenDeadLetters = attention.Sum(x => x.Count);
        row.OldestDeadLetterAt = await letters.MinAsync(x => (DateTime?)x.CreatedAt, ct);
        row.OpenManualTasks = await db.ManualOutboundTasks.AsNoTracking().CountAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId && x.Status == "open", ct);
        var check = await db.ReconciliationRuns.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.Trigger == ReconciliationTrigger.Scheduled
                && x.CompletedAt != null)
            .OrderByDescending(x => x.CompletedAt).ThenByDescending(x => x.ReconciliationRunId).FirstOrDefaultAsync(ct);
        row.LastDailyCheckResult = check?.Status;
        row.LastDailyCheckAt = check?.CompletedAt;
        row.DailyCheckDifferences = (check?.MismatchCount ?? 0) + (check?.LocationMismatchCount ?? 0);
        var throttle = await db.ConnectionThrottleProfiles.AsNoTracking().SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId, ct);
        row.CallsUsed = throttle?.BudgetDateUtc == DateOnly.FromDateTime(now) ? throttle.UsedCalls : 0;
        row.CallsCap = throttle?.RequestsPerDay;
        row.CallAlertPercent = throttle?.AlertThresholdPercent ?? 80;
        var credentials = await db.ConnectionCredentials.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.RotatedAt == null && x.RetiredAt == null).ToListAsync(ct);
        row.CredentialReplaceBy = credentials.Select(x => (DateTime?)(x.ExpiresAt < x.RotationDueAt
            ? x.ExpiresAt.Value : x.RotationDueAt)).Min();
        row.CredentialWarnDays = thresholds.CredentialWarnDays;
        row.CheckedUpTo = polls.Count == 0 || polls.Any(x => x.LastPolledAt == null)
            ? null : polls.Min(x => x.LastPolledAt);
        row.MaxOutboxAgeSeconds = checked((int)maxAge.TotalSeconds);
        var smokeFailed = connection.LastSmokeReadSucceeded == false;
        var pollFailed = polls.Any(x => x.LastError != null);
        var outboundFailed = await waiting.AnyAsync(x => x.LastFailure != null, ct);
        row.LastErrorCode = smokeFailed ? "smoke_read_failed" : pollFailed ? "poll_failed"
            : check?.Status == "failed" ? "daily_check_failed" : outboundFailed ? "outbound_retry_failed" : null;
        row.LastErrorMessage = row.LastErrorCode switch
        {
            "smoke_read_failed" => "The read-only connection check failed. Verify connection settings and permissions, then retry.",
            "poll_failed" => "A scheduled read failed; its checked-up-to marker has not advanced.",
            "outbound_retry_failed" => "A waiting outbound command failed and will be retried within its maximum age.",
            "daily_check_failed" => "The daily check failed. Review its report before activation.",
            _ => null,
        };
        var credentialDue = row.CredentialReplaceBy <= now.AddDays(thresholds.CredentialWarnDays);
        row.Status = ConnectionHealthRules.Derive(new(connection.Status, smokeFailed, row.CheckedUpTo,
            polls.Count > 0, row.LastDailyCheckResult, row.LastDailyCheckAt, row.OpenDeadLetters,
            row.OpenManualTasks, row.OldestWaitingAt, maxAge, row.NotificationState, credentialDue,
            row.CallsCap is { } cap && row.CallsUsed * 100L >= cap * (long)row.CallAlertPercent, pollFailed, connection.CreatedAt), thresholds, now);
        row.RelayConnected = false;
        var relayProfile = services.GetKeyedService<Yojana.Integration.Relays.IRelayConnectorProfile>(connection.ConnectorCode);
        if (connection.Status == ConnectionStatus.Active && relayProfile is not null)
        {
            try
            {
                var relay = await relayProfile.OptionsAsync(connectionId, ct);
                if (relay.Transport == "site_relay")
                {
                    var relayCutoff = now.AddSeconds(-relay.OfflineAfterSeconds);
                    row.RelayConnected = await db.SiteRelays.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
                        && x.Status == "active" && x.LastSeenAt >= relayCutoff, ct);
                    if (!row.RelayConnected)
                    {
                        row.Status = "down";
                        row.LastErrorCode = "relay_offline";
                        row.LastErrorMessage = "The site relay is offline; queued work will retry after it reconnects.";
                    }
                }
            }
            catch (InvalidOperationException) when (!connection.RegistrationCompleted) { }
        }
        row.IsLive = connection.Status == ConnectionStatus.Active && row.Status != "down" && !smokeFailed
            && row.LastDailyCheckResult == "clean" && row.LastDailyCheckAt > now.AddHours(-thresholds.HealthySweepHours);
        // Message types (canonical), rather than remote entity names, join counters to poll declarations.
        var types = await db.ConnectionCapabilities.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).Select(x => x.CanonicalMessage).ToListAsync(ct);
        var records = polls.Select(poll => new RecordTypeHealth(poll.CanonicalMessage ?? poll.EntityType,
            row.NotificationState == "not_supported_by_remote_release" ? row.NotificationState
                : sources.Any(source => source.Source == poll.EntityType && source.Enabled) ? row.NotificationState : "not_subscribed",
            poll.Watermark, poll.LastPolledAt, poll.LastPolledAt is { } at ? Math.Max(0, (now - at).TotalSeconds) : null,
            poll.IntervalSeconds, poll.OverlapSeconds,
            counts.FirstOrDefault(x => x.MessageType == (poll.CanonicalMessage ?? poll.EntityType))?.Today ?? 0,
            attention.FirstOrDefault(x => x.RecordType == (poll.CanonicalMessage ?? poll.EntityType))?.Count ?? 0, poll.EntityType)).ToList();
        records.AddRange(types.Where(type => !records.Any(record => record.RecordType == type)
                && InboundTypes.Contains(type))
            .Select(type => new RecordTypeHealth(type, "not_subscribed", null, row.CheckedUpTo,
                row.CheckedUpTo is { } checkedAt ? Math.Max(0, (now - checkedAt).TotalSeconds) : null, 0, 0,
                counts.FirstOrDefault(x => x.MessageType == type)?.Today ?? 0,
                attention.FirstOrDefault(x => x.RecordType == type)?.Count ?? 0)));
        row.RecordTypesJson = JsonSerializer.Serialize(records, IntegrationContracts.Json);
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        if (previous != row.Status)
            await Audit(ConnectionAuditEventType.HealthStatusChanged,
                JsonSerializer.Serialize(new { Previous = previous, Current = row.Status }), ct);
        if (credentialDue && !priorCredentialDue)
            await Audit(ConnectionAuditEventType.PasswordDue, "{}", ct);
        return row;

        Task<ConnectionAuditEvent> Audit(ConnectionAuditEventType kind, string details, CancellationToken token) =>
            audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(now), connectionId, kind,
                "system:connection-health", now, details), token);
    }
    private sealed record DailyInboundCount(DateTime Day, long Count);
}

[DisallowConcurrentExecution]
public sealed class ConnectionHealthJob(ITenantContextSetter setter, ConnectionHealthProjection projection) : TenantJob(setter)
{
    public const string JobName = "integration.connection-health";
    public const string Cron = "45 * * * * ?";
    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        projection.RefreshTenantAsync(cancellationToken);
}
