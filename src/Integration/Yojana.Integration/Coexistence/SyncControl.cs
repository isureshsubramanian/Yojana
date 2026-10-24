using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Coexistence;

/// <summary>
/// "Stop the sync" and its reverse — the rollback lever of the coexistence design (Coexistence/ROLLBACK.md).
/// The local state and audit commit together. Remote notification control is a separate idempotent operation so
/// an unavailable remote can never delay or obscure the local safety action.
/// </summary>
public sealed class SyncControl(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IConnectionAuditSink audit, ConnectionHealthProjection health)
{
    public async Task<SyncControlResult> StopAsync(Guid connectionId, StopSync request, string actor,
        CancellationToken ct)
    {
        Actor(actor);
        var reason = RequiredText(request.Reason, 2000, "stop_reason_required",
            "Say why the sync is being stopped, in at most 2000 characters.");
        var now = clock.UtcNow;
        int waiting;
        await using (var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId,
            connectionId, ct))
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var connection = await ConnectionAsync(connectionId, ct);
            waiting = await WaitingAsync(connectionId, ct);
            if (connection.Status == ConnectionStatus.Paused)
                return new(connectionId, "stopped", connection.NotificationControlState, waiting,
                    connection.NotificationControlFailureCode, "already_stopped");
            if (connection.Status != ConnectionStatus.Active)
                throw new CommandRefusedException("connection_inactive", "Only a running connection can be stopped.");

            connection.Status = ConnectionStatus.Paused;
            connection.SyncStoppedAt = now;
            connection.SyncStoppedBy = actor;
            connection.SyncStopReason = reason;
            connection.SyncResumedAt = null;
            QueueNotificationControl(connection, NotificationControlActions.Suspend, now);
            // Saved explicitly: the state must not depend on which audit sink is configured flushing this context.
            await db.SaveChangesAsync(ct);
            await Audit(connectionId, ConnectionAuditEventType.SyncStopped, actor,
                new { Reason = reason, WaitingOutbound = waiting, Notifications = NotificationControlStates.Pending }, ct);
            await transaction.CommitAsync(ct);
        }
        await health.RefreshAsync(connectionId, ct);
        return new(connectionId, "stopped", NotificationControlStates.Pending, waiting);
    }

    public async Task<SyncControlResult> ResumeAsync(Guid connectionId, ResumeSync request, string actor,
        CancellationToken ct)
    {
        Actor(actor);
        var note = OptionalText(request.Note, 2000, "resume_note_invalid",
            "The note holds at most 2000 characters.");
        var now = clock.UtcNow;
        int waiting;
        await using (var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId,
            connectionId, ct))
        await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct))
        {
            var connection = await ConnectionAsync(connectionId, ct);
            waiting = await WaitingAsync(connectionId, ct);
            if (connection.Status == ConnectionStatus.Active)
                return new(connectionId, "running", connection.NotificationControlState, waiting,
                    connection.NotificationControlFailureCode, "already_running");
            if (connection.Status != ConnectionStatus.Paused || !connection.RegistrationCompleted)
                throw new CommandRefusedException("connection_not_stopped", "Only a stopped connection can be resumed.");

            connection.Status = ConnectionStatus.Active;
            connection.SyncResumedAt = now;
            QueueNotificationControl(connection, NotificationControlActions.Resume, now);
            await db.SaveChangesAsync(ct);
            await Audit(connectionId, ConnectionAuditEventType.SyncResumed, actor,
                new { Note = note, WaitingOutbound = waiting, Notifications = NotificationControlStates.Pending }, ct);
            await transaction.CommitAsync(ct);
        }
        await health.RefreshAsync(connectionId, ct);
        return new(connectionId, "running", NotificationControlStates.Pending, waiting);
    }

    private static void QueueNotificationControl(Connection connection, string action, DateTime now)
    {
        connection.NotificationControlState = NotificationControlStates.Pending;
        connection.NotificationControlFailureCode = null;
        connection.NotificationControlAction = action;
        connection.NotificationControlNextAttemptAt = now;
        connection.NotificationControlAttemptCount = 0;
        connection.NotificationControlRevision++;
    }

    private Task<int> WaitingAsync(Guid connectionId, CancellationToken ct) =>
        db.OutboundCommands.CountAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && (x.Status == OutboundCommandStatus.Pending || x.Status == OutboundCommandStatus.Retry
                || x.Status == OutboundCommandStatus.Processing), ct);

    private async Task<Connection> ConnectionAsync(Guid connectionId, CancellationToken ct) =>
        await db.Connections.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct)
        ?? throw new CommandRefusedException("connection_not_found", "The connection was not found.");

    private Task<ConnectionAuditEvent> Audit(Guid connectionId, ConnectionAuditEventType kind, string actor,
        object details, CancellationToken ct) => audit.AppendAsync(new(tenant.TenantId,
            Guid.CreateVersion7(clock.UtcNow), connectionId, kind, actor, clock.UtcNow,
            JsonSerializer.Serialize(details, IntegrationContracts.Json)), ct);

    private static void Actor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor))
            throw new CommandRefusedException("audit_actor_required", "An authenticated actor is required.");
    }

    private static string RequiredText(string? value, int max, string rule, string message)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > max)
            throw new CommandRefusedException(rule, message);
        return trimmed;
    }

    private static string? OptionalText(string? value, int max, string rule, string message)
    {
        var trimmed = value?.Trim();
        if (trimmed?.Length > max) throw new CommandRefusedException(rule, message);
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}

/// <summary>Owns remote suspend/resume attempts. The persisted desired action makes every retry idempotent.</summary>
public sealed class SyncNotificationControl(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IServiceProvider services)
{
    public static readonly TimeSpan DefaultRemoteTimeout = TimeSpan.FromSeconds(15);

    public async Task<int> ProcessDueAsync(CancellationToken ct, TimeSpan? remoteTimeout = null)
    {
        var due = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.NotificationControlAction != null && x.NotificationControlNextAttemptAt <= clock.UtcNow)
            .OrderBy(x => x.NotificationControlNextAttemptAt).ThenBy(x => x.ConnectionId)
            .Select(x => x.ConnectionId).Take(20).ToListAsync(ct);
        var processed = 0;
        foreach (var id in due)
        {
            if (await ProcessAsync(id, remoteTimeout ?? DefaultRemoteTimeout, ct)) processed++;
        }
        return processed;
    }

    private async Task<bool> ProcessAsync(Guid connectionId, TimeSpan remoteTimeout, CancellationToken ct)
    {
        // The connection lock is held only to read and to record, never across the remote call: stop and resume take
        // the same lock and must answer at once even while the remote is slow (ADR-0016). The revision check after
        // the call discards an outcome that a newer stop or resume has overtaken.
        string action, connectorCode;
        long revision;
        await using (var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId,
            connectionId, ct))
        {
            var due = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.NotificationControlAction != null
                && x.NotificationControlNextAttemptAt <= clock.UtcNow, ct);
            if (due is null) return false;
            (action, revision, connectorCode) = (due.NotificationControlAction!, due.NotificationControlRevision,
                due.ConnectorCode);
        }

        var adapter = services.GetKeyedService<IConnectionLifecycleAdapter>(connectorCode);
        var supported = adapter is not null && await adapter.SupportsNotificationControlAsync(connectionId, ct);
        string? failureCode = null;
        if (supported)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(remoteTimeout);
                if (action == NotificationControlActions.Suspend)
                    await adapter!.SuspendNotificationsAsync(connectionId, timeout.Token);
                else
                    await adapter!.ResumeNotificationsAsync(connectionId, timeout.Token);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                failureCode = exception is OperationCanceledException
                    ? "timeout" : Yojana.Relay.Contracts.RemoteFailureClassifier.Classify(exception).Code;
            }
        }

        await using (var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId,
            connectionId, ct))
        {
            var connection = await db.Connections.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId, ct);
            if (connection is null || connection.NotificationControlRevision != revision
                || connection.NotificationControlAction != action)
                return false;
            if (!supported)
                Complete(connection, NotificationControlStates.NotSupported, null);
            else if (failureCode is null)
                Complete(connection, action == NotificationControlActions.Suspend
                    ? NotificationControlStates.Held : NotificationControlStates.NotHeld, null);
            else
            {
                connection.NotificationControlState = action == NotificationControlActions.Suspend
                    ? NotificationControlStates.NotHeld : NotificationControlStates.Held;
                connection.NotificationControlFailureCode = failureCode;
                connection.NotificationControlAttemptCount++;
                connection.NotificationControlNextAttemptAt =
                    clock.UtcNow.Add(Backoff(connection.NotificationControlAttemptCount));
            }
            await db.SaveChangesAsync(ct);
        }
        return true;
    }

    private static void Complete(Connection connection, string state, string? failureCode)
    {
        connection.NotificationControlState = state;
        connection.NotificationControlFailureCode = failureCode;
        connection.NotificationControlAction = null;
        connection.NotificationControlNextAttemptAt = null;
    }

    internal static TimeSpan Backoff(int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Pow(2, Math.Clamp(attempt, 1, 10)), 900));
}

[DisallowConcurrentExecution]
public sealed class SyncNotificationControlJob(ITenantContextSetter setter, SyncNotificationControl control)
    : TenantJob(setter)
{
    public const string JobName = "integration.sync-notification-control";
    public const string Cron = "0/10 * * * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        control.ProcessDueAsync(cancellationToken);
}
