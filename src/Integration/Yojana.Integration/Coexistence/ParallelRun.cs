using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Coexistence;

/// <summary>
/// The 30/60/90-day parallel-run checklist (ADR-0001 "Coexistence design": 30 days minimum for a small installation,
/// 60–90 for a larger one; an active phase, then the incumbent read-only for the owned objects, one month-end close
/// before the owned objects are decommissioned there). Measured items are computed from what the gateway already
/// records; the rest are a person's confirmation with evidence. "Ready" is advice to people, never an automatic switch.
/// </summary>
public sealed class ParallelRun(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    CoexistenceSettings coexistence, IConnectionAuditSink audit)
{
    public const int RecommendedDays = 30;
    private const int CleanDailyChecksRequired = 7;
    private const int DeadLetterAgeDays = 7;

    private static readonly (string Code, string Label, string Detail)[] Manual =
    [
        ("active_phase_complete", "Active phase run side by side",
            "Both systems were used for the owned objects and the daily differences were reviewed."),
        ("incumbent_read_only", "Owned objects read-only in the ERP",
            "Permissions in the ERP stop users editing installed-base owner, status and contract fields."),
        ("month_end_close", "One month-end close completed",
            "A month-end close finished with the module live and the reconciliation clean afterwards."),
        ("rollback_rehearsed", "Rollback rehearsed",
            "The sync was stopped and resumed once, and the rollback runbook is filed with the customer."),
        ("users_repointed", "Users moved to the module",
            "People who maintain the owned objects are trained and use the module for them."),
    ];

    public async Task<ParallelRunView> ViewAsync(Guid connectionId, CancellationToken ct)
    {
        var connection = await ConnectionAsync(connectionId, ct);
        var plan = await db.ParallelRunPlans.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct);
        var confirmations = await db.ParallelRunConfirmations.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).ToListAsync(ct);
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var items = new List<ParallelRunItem>();

        var elapsed = plan is null ? 0 : Math.Max(0, today.DayNumber - plan.StartDate.DayNumber);
        items.Add(new("elapsed", "Parallel-run period elapsed", "measured",
            plan is not null && elapsed >= plan.Days ? "pass" : "pending",
            plan is null ? "Start the parallel run to begin counting." : $"{Math.Min(elapsed, plan.Days)} of {plan.Days} days."));

        var since = clock.UtcNow.Date.AddDays(-(CleanDailyChecksRequired - 1));
        var runs = await db.ReconciliationRuns.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.StartedAt >= since && x.Status != ReconciliationRunStatus.Running)
            .Select(x => new { x.StartedAt, x.Status }).ToListAsync(ct);
        var cleanDays = runs.GroupBy(x => x.StartedAt.Date)
            .Count(day => day.All(run => run.Status == ReconciliationRunStatus.Clean));
        var anyBad = runs.Any(x => x.Status != ReconciliationRunStatus.Clean);
        items.Add(new("daily_check_clean", $"Daily check clean for {CleanDailyChecksRequired} days", "measured",
            cleanDays >= CleanDailyChecksRequired && !anyBad ? "pass" : anyBad ? "fail" : "pending",
            $"{cleanDays} clean day(s) of the last {CleanDailyChecksRequired}."));

        var agedBefore = clock.UtcNow.AddDays(-DeadLetterAgeDays);
        var aged = await db.DeadLetterItems.CountAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && (x.Status == DeadLetterStatus.NeedsAttention || x.Status == DeadLetterStatus.RetryPending)
            && x.CreatedAt < agedBefore, ct);
        items.Add(new("no_aged_dead_letters", $"No message waiting longer than {DeadLetterAgeDays} days", "measured",
            aged == 0 ? "pass" : "fail", aged == 0 ? "None." : $"{aged} message(s) need attention."));

        var open = await db.CoexistenceConflicts.CountAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.Status != CoexistenceConflictStatus.Closed, ct);
        items.Add(new("conflicts_resolved", "Conflict review queue empty", "measured", open == 0 ? "pass" : "fail",
            open == 0 ? "None open." : $"{open} conflict(s) wait for a decision."));

        var reader = coexistence.Reader(connection.ConnectorCode);
        if (reader is null)
        {
            items.Add(new("drift_scan_recent", "Drift scan completed recently", "measured", "not_applicable",
                "This connector has no drift scan."));
        }
        else
        {
            var completed = await db.CoexistenceScanStates.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.RecordType == reader.RecordType)
                .Select(x => x.LastCompletedAt).SingleOrDefaultAsync(ct);
            var recent = completed is { } at && at >= clock.UtcNow.AddDays(-2);
            items.Add(new("drift_scan_recent", "Drift scan completed recently", "measured", recent ? "pass" : "pending",
                completed is null ? "No full pass yet." : $"Last full pass {completed:yyyy-MM-dd HH:mm} UTC."));
        }

        foreach (var (code, label, detail) in Manual)
        {
            var confirmed = confirmations.FirstOrDefault(x => x.ItemCode == code);
            items.Add(new(code, label, "confirmed", confirmed is null ? "pending" : "pass", detail,
                confirmed?.Evidence, confirmed?.ConfirmedBy, confirmed?.ConfirmedAt));
        }

        var status = plan is null ? "not_started"
            : items.All(x => x.Status is "pass" or "not_applicable") ? "ready" : "in_progress";
        return new(connectionId, connection.DisplayLabel, status, plan?.Days, plan?.StartDate,
            plan?.StartDate.AddDays(plan.Days), RecommendedDays, items);
    }

    public async Task<ParallelRunView> StartAsync(Guid connectionId, StartParallelRun request, string actor,
        CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        if (request.Days is not (30 or 60 or 90))
            throw new CoexistenceRuleException("parallel_run_days_invalid", "Choose 30, 60 or 90 days.");
        var connection = await ConnectionAsync(connectionId, ct);
        if (connection.Status == ConnectionStatus.Archived)
            throw new CoexistenceRuleException("connection_archived", "An archived connection has no parallel run.");
        var today = DateOnly.FromDateTime(clock.UtcNow);
        var start = request.StartDate ?? today;
        if (start > today.AddDays(30) || start < today.AddDays(-90))
            throw new CoexistenceRuleException("parallel_run_start_invalid", "Start within 90 days back and 30 days ahead.");
        var plan = await db.ParallelRunPlans.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct);
        if (plan is null)
        {
            db.ParallelRunPlans.Add(new()
            {
                TenantId = tenant.TenantId, ConnectionId = connectionId, Days = request.Days, StartDate = start,
                StartedBy = actor, StartedAt = clock.UtcNow,
            });
        }
        else
        {
            // Re-planning keeps every confirmation: evidence already given stays true when the period changes.
            plan.Days = request.Days;
            plan.StartDate = start;
        }
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), connectionId,
            ConnectionAuditEventType.ParallelRunStarted, actor, clock.UtcNow,
            JsonSerializer.Serialize(new { request.Days, StartDate = start, Replanned = plan is not null },
                IntegrationContracts.Json)), ct);
        return await ViewAsync(connectionId, ct);
    }

    public async Task<ParallelRunView> ConfirmAsync(Guid connectionId, string itemCode, ConfirmParallelRunItem request,
        string actor, CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        if (!Manual.Any(x => x.Code == itemCode))
            throw new CoexistenceRuleException("parallel_run_item_not_found", "Only the confirmed items can be confirmed by hand.");
        var evidence = CoexistenceGuard.Text(request.Evidence, 500, "parallel_run_evidence_required",
            "Give the evidence in at most 500 characters.");
        await ConnectionAsync(connectionId, ct);
        if (!await db.ParallelRunPlans.AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId, ct))
            throw new CoexistenceRuleException("parallel_run_not_started", "Start the parallel run first.");
        var row = await db.ParallelRunConfirmations.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.ItemCode == itemCode, ct);
        if (row is null)
        {
            row = new() { TenantId = tenant.TenantId, ConnectionId = connectionId, ItemCode = itemCode, Evidence = evidence,
                ConfirmedBy = actor, ConfirmedAt = clock.UtcNow };
            db.ParallelRunConfirmations.Add(row);
        }
        else
        {
            row.Evidence = evidence;
            row.ConfirmedBy = actor;
            row.ConfirmedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), connectionId,
            ConnectionAuditEventType.ParallelRunItemConfirmed, actor, clock.UtcNow,
            JsonSerializer.Serialize(new { ItemCode = itemCode }, IntegrationContracts.Json)), ct);
        return await ViewAsync(connectionId, ct);
    }

    private async Task<Connection> ConnectionAsync(Guid connectionId, CancellationToken ct) =>
        await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct)
        ?? throw new CoexistenceRuleException("connection_not_found", "The connection was not found.");
}
