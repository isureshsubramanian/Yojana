using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Coexistence;

/// <summary>The conflict review queue: list, read and decide. Every decision is a person's, with a note.</summary>
public sealed class CoexistenceConflicts(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    IConnectionAuditSink audit)
{
    public async Task<CoexistenceConflictPage> ListAsync(Guid? connectionId, string? status, string? cursor, int take,
        CancellationToken ct)
    {
        take = Math.Clamp(take, 1, 200);
        var query = db.CoexistenceConflicts.AsNoTracking().Where(x => x.TenantId == tenant.TenantId);
        if (connectionId is { } id) query = query.Where(x => x.ConnectionId == id);
        query = status switch
        {
            null or "" or "unresolved" => query.Where(x => x.Status != CoexistenceConflictStatus.Closed),
            "all" => query,
            CoexistenceConflictStatus.Open or CoexistenceConflictStatus.AwaitingRemoteRevert
                or CoexistenceConflictStatus.Closed => query.Where(x => x.Status == status),
            _ => throw new CoexistenceRuleException("conflict_status_invalid", "Unknown conflict status filter."),
        };
        if (!string.IsNullOrEmpty(cursor))
        {
            var parts = cursor.Split('_');
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParse(parts[1], out var after))
                throw new CoexistenceRuleException("cursor_invalid", "The conflict cursor is invalid.");
            var at = new DateTime(ticks, DateTimeKind.Utc);
            query = query.Where(x => x.DetectedAt < at || (x.DetectedAt == at && x.ConflictId.CompareTo(after) < 0));
        }
        var rows = await query.OrderByDescending(x => x.DetectedAt).ThenByDescending(x => x.ConflictId)
            .Take(take + 1).ToListAsync(ct);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(take);
        var labels = await LabelsAsync(rows.Select(x => x.ConnectionId), ct);
        var next = more ? rows[^1].DetectedAt.Ticks.ToString(CultureInfo.InvariantCulture) + "_" + rows[^1].ConflictId.ToString("N") : null;
        return new(rows.Select(x => View(x, labels)).ToList(), next);
    }

    public async Task<CoexistenceConflictView> GetAsync(Guid conflictId, CancellationToken ct)
    {
        var row = await db.CoexistenceConflicts.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConflictId == conflictId, ct)
            ?? throw new CoexistenceRuleException("conflict_not_found", "The conflict was not found.");
        return View(row, await LabelsAsync([row.ConnectionId], ct));
    }

    public async Task<CoexistenceConflictView> ResolveAsync(Guid conflictId, ResolveCoexistenceConflict request,
        string actor, CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        var note = CoexistenceGuard.Text(request.Note, 2000, "conflict_note_required",
            "Say why, in at most 2000 characters.");
        if (request.Decision is not (CoexistenceConflictDecision.KeepModule or CoexistenceConflictDecision.AcceptRemote
            or CoexistenceConflictDecision.Dismiss))
            throw new CoexistenceRuleException("conflict_decision_invalid", "Choose keep_module, accept_remote or dismiss.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var conflict = await db.CoexistenceConflicts.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConflictId == conflictId, ct)
            ?? throw new CoexistenceRuleException("conflict_not_found", "The conflict was not found.");
        if (conflict.Status == CoexistenceConflictStatus.Closed)
            throw new CoexistenceRuleException("conflict_closed", "The conflict is already closed.");
        // HIDDEN COUPLING with ApplyService.Delta: its owner findings have no drift-scan record behind them, so a remote
        // revert task would never be closed by a scan. Keeping the module owner is a dismissal for them.
        if (conflict.Kind == CoexistenceConflictKind.MigrationOwnerDiffers
            && request.Decision == CoexistenceConflictDecision.KeepModule)
            throw new CoexistenceRuleException("conflict_decision_invalid",
                "For an owner found by a delta load, accept the source owner (and record it in the module) or dismiss to keep the module owner.");
        if (conflict.Status == CoexistenceConflictStatus.AwaitingRemoteRevert
            && request.Decision == CoexistenceConflictDecision.KeepModule)
            throw new CoexistenceRuleException("conflict_awaiting_revert", "The remote revert is already requested.");
        var now = clock.UtcNow;
        conflict.Decision = request.Decision;
        conflict.DecisionNote = note;
        conflict.DecidedBy = actor;
        conflict.DecidedAt = now;
        if (request.Decision == CoexistenceConflictDecision.KeepModule)
        {
            var task = new ManualOutboundTask
            {
                TenantId = tenant.TenantId, ManualOutboundTaskId = Guid.CreateVersion7(now),
                ConnectionId = conflict.ConnectionId, CommandType = ManualTaskCommandType.RevertRemoteChange,
                Desk = "integration", Status = ManualTaskStatus.Open, CreatedAt = now,
                Draft = $"On {conflict.RecordLabel}, set {conflict.FieldName} back to "
                    + $"'{conflict.BaselineValue ?? "(empty)"}' in the ERP. It was changed there to "
                    + $"'{conflict.RemoteValue ?? "(empty)"}', but this field is kept in the service module while both "
                    + "systems run. The next drift scan closes this task when the value is back.",
                Note = note,
            };
            db.ManualOutboundTasks.Add(task);
            conflict.ManualTaskId = task.ManualOutboundTaskId;
            conflict.Status = CoexistenceConflictStatus.AwaitingRemoteRevert;
        }
        else
        {
            // Accepting or dismissing makes the current remote value the baseline, so the same edit is not raised
            // again. Any change the module itself needs is made in the module by a person; nothing is copied here.
            var baseline = await db.CoexistenceBaselines.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == conflict.ConnectionId && x.RecordType == conflict.RecordType
                && x.RecordKey == conflict.RecordKey && x.FieldName == conflict.FieldName, ct);
            if (baseline is not null)
            {
                baseline.Value = conflict.RemoteValue;
                baseline.Source = CoexistenceDriftService.SourceDecision;
                baseline.UpdatedAt = now;
            }
            if (conflict.ManualTaskId is { } taskId)
            {
                var task = await db.ManualOutboundTasks.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
                    && x.ManualOutboundTaskId == taskId && x.Status == ManualTaskStatus.Open, ct);
                if (task is not null)
                {
                    task.Status = ManualTaskStatus.Completed;
                    task.CompletedBy = actor;
                    task.CompletedAt = now;
                    task.Note = "Superseded: the remote value was accepted.";
                }
            }
            conflict.Status = CoexistenceConflictStatus.Closed;
            conflict.ClosedAt = now;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(now), conflict.ConnectionId,
            ConnectionAuditEventType.CoexistenceConflictResolved, actor, now, JsonSerializer.Serialize(new
            {
                conflict.ConflictId, conflict.RecordType, conflict.RecordKey, conflict.FieldName, request.Decision,
            }, IntegrationContracts.Json)), ct);
        return View(conflict, await LabelsAsync([conflict.ConnectionId], ct));
    }

    private async Task<Dictionary<Guid, string>> LabelsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && list.Contains(x.ConnectionId))
            .ToDictionaryAsync(x => x.ConnectionId, x => x.DisplayLabel, ct);
    }

    private static CoexistenceConflictView View(CoexistenceConflict x, IReadOnlyDictionary<Guid, string> labels) =>
        new(x.ConflictId, x.ConnectionId, labels.GetValueOrDefault(x.ConnectionId, "Connection"), x.RecordType,
            x.RecordKey, x.RecordLabel, x.Kind, x.FieldName, x.BaselineValue, x.RemoteValue, x.Status, x.DetectedAt,
            x.LastSeenAt, x.IncumbentChangedAt, x.Decision, x.DecisionNote, x.DecidedBy, x.DecidedAt,
            x.ManualTaskId, x.ClosedAt);
}
