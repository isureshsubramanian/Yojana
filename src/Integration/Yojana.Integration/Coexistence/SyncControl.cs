using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Coexistence;

/// <summary>
/// "Stop the sync" and its reverse — the rollback lever of the coexistence design (Coexistence/ROLLBACK.md).
/// Stopping parks the connection as paused: every poller, watchdog, relay, backfill, sweep and outbox worker already
/// works only on active connections, so no module write reaches the remote and nothing is read from it until a
/// person resumes. Nothing is deleted: waiting outbound commands stay queued (and age into the dead-letter queue
/// under max_outbox_age if the stop is long), and polling catches up from its watermark on resume.
/// </summary>
public sealed class SyncControl(IntegrationDbContext db, ITenantContext tenant, IClock clock, IServiceProvider services,
    ConnectionHealthProjection health, IConnectionAuditSink audit)
{
    public async Task<SyncControlResult> StopAsync(Guid connectionId, StopSync request, string actor, CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        var reason = CoexistenceGuard.Text(request.Reason, 2000, "stop_reason_required",
            "Say why the sync is being stopped, in at most 2000 characters.");
        Connection connection;
        await using (var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, connectionId, ct))
        {
            connection = await ConnectionAsync(connectionId, ct);
            if (connection.Status == ConnectionStatus.Paused)
                return new(connectionId, "stopped", false, await WaitingAsync(connectionId, ct), "already_stopped");
            if (connection.Status != ConnectionStatus.Active)
                throw new CoexistenceRuleException("connection_inactive", "Only a running connection can be stopped.");
            // The local stop is committed first and never waits on the remote: rollback must work when the remote
            // is unreachable, which is exactly when someone may need it.
            connection.Status = ConnectionStatus.Paused;
            await db.SaveChangesAsync(ct);
        }
        var held = await RemoteAsync(connection.ConnectorCode, connectionId, suspend: true, ct);
        var waiting = await WaitingAsync(connectionId, ct);
        await Audit(connectionId, ConnectionAuditEventType.SyncStopped, actor,
            new { Reason = reason, WaitingOutbound = waiting, NotificationsHeld = held }, ct);
        await health.RefreshAsync(connectionId, ct);
        return new(connectionId, "stopped", held, waiting, held ? null : "notifications_not_held");
    }

    public async Task<SyncControlResult> ResumeAsync(Guid connectionId, ResumeSync request, string actor, CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        if (request.Note is { Length: > 2000 })
            throw new CoexistenceRuleException("resume_note_invalid", "The note holds at most 2000 characters.");
        Connection connection;
        await using (var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, connectionId, ct))
        {
            connection = await ConnectionAsync(connectionId, ct);
            if (connection.Status == ConnectionStatus.Active)
                return new(connectionId, "running", true, await WaitingAsync(connectionId, ct), "already_running");
            if (connection.Status != ConnectionStatus.Paused || !connection.RegistrationCompleted)
                throw new CoexistenceRuleException("connection_not_stopped", "Only a stopped connection can be resumed.");
            connection.Status = ConnectionStatus.Active;
            await db.SaveChangesAsync(ct);
        }
        var released = await RemoteAsync(connection.ConnectorCode, connectionId, suspend: false, ct);
        var waiting = await WaitingAsync(connectionId, ct);
        await Audit(connectionId, ConnectionAuditEventType.SyncResumed, actor,
            new { request.Note, WaitingOutbound = waiting, NotificationsReleased = released }, ct);
        await health.RefreshAsync(connectionId, ct);
        // A notification stream that could not be released is picked up by the watchdog on its next run.
        return new(connectionId, "running", released, waiting, released ? null : "notifications_left_to_watchdog");
    }

    private async Task<bool> RemoteAsync(string connectorCode, Guid connectionId, bool suspend, CancellationToken ct)
    {
        var adapter = services.GetKeyedService<IConnectionLifecycleAdapter>(connectorCode);
        if (adapter is null) return false;
        try
        {
            if (suspend) await adapter.SuspendNotificationsAsync(connectionId, ct);
            else await adapter.ResumeNotificationsAsync(connectionId, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Remote text can carry connection details; the result and audit say only that it did not happen.
            return false;
        }
    }

    private Task<int> WaitingAsync(Guid connectionId, CancellationToken ct) =>
        db.OutboundCommands.CountAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && (x.Status == OutboundCommandStatus.Pending || x.Status == OutboundCommandStatus.Retry
                || x.Status == OutboundCommandStatus.Processing), ct);

    private async Task<Connection> ConnectionAsync(Guid connectionId, CancellationToken ct) =>
        await db.Connections.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId, ct)
        ?? throw new CoexistenceRuleException("connection_not_found", "The connection was not found.");

    private Task<ConnectionAuditEvent> Audit(Guid connectionId, ConnectionAuditEventType kind, string actor, object details,
        CancellationToken ct) => audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), connectionId,
            kind, actor, clock.UtcNow, JsonSerializer.Serialize(details, IntegrationContracts.Json)), ct);
}
