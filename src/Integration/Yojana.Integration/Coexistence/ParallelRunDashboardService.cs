using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connections;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Coexistence;

/// <summary>
/// The parallel run of the first customer (P9-T11, ADR-0001 "Coexistence design", OD-64): one dashboard over what the
/// gateway already measures (sync lag, conflicts, failed messages, nightly reconciliation), the handover of one kind of
/// record at a time, and the go-live record. Builds on the P8 checklist (<see cref="ParallelRun"/>): the plan and the
/// month-end close confirmation are shared with it. Nothing here switches the incumbent or the sync; every handover
/// and the go-live record are a person's confirmation with evidence.
/// </summary>
public sealed class ParallelRunDashboardService(IntegrationDbContext db, ITenantContext tenant, IClock clock,
    ParallelRun checklist, ConnectionQueries connections, WarrantyContractMaintenanceSettings contracts,
    IMigrationGoLiveFacts migration, IUserDirectory people, IConnectionAuditSink audit)
{
    public const int SyncLagTargetSeconds = 180;
    public static readonly TimeSpan DeltaTarget = TimeSpan.FromHours(2);
    public static readonly TimeSpan RollbackTarget = TimeSpan.FromHours(4);
    private const int DeadLetterAgeDays = 7;

    public async Task<ParallelRunDashboard> ViewAsync(Guid connectionId, CancellationToken ct)
    {
        var run = await checklist.ViewAsync(connectionId, ct);
        var plan = await PlanAsync(connectionId, ct);
        var today = Today;
        int? dayNumber = plan is null ? null : Math.Max(0, today.DayNumber - plan.StartDate.DayNumber) + 1;
        int? daysLeft = plan is null ? null : Math.Max(0, plan.StartDate.AddDays(plan.Days).DayNumber - today.DayNumber);
        var facts = await migration.ReadAsync(connectionId, ct);
        var handover = await HandoverAsync(connectionId, plan, ct);
        return new(connectionId, run.ConnectionLabel, run.Status, run.Days, run.StartDate,
            run.EarliestDecommissionDate, dayNumber, daysLeft, plan?.MonthEndCloseOn, facts.CutoverAt,
            await SyncLagAsync(connectionId, ct), await ConflictsAsync(connectionId, ct),
            await FailedMessagesAsync(connectionId, ct), await ReconciliationAsync(connectionId, plan, ct),
            handover, await GoLiveAsync(connectionId, plan, handover, facts, ct));
    }

    public async Task<ParallelRunDashboard> SetMonthEndCloseAsync(Guid connectionId, SetMonthEndClose request,
        string actor, CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        var plan = await TrackedPlanAsync(connectionId, ct);
        if (request.CloseOn < plan.StartDate || request.CloseOn > plan.StartDate.AddDays(plan.Days))
            throw new CoexistenceRuleException("month_end_close_outside_run",
                "The month-end close must fall inside the parallel run.");
        plan.MonthEndCloseOn = request.CloseOn;
        await db.SaveChangesAsync(ct);
        return await ViewAsync(connectionId, ct);
    }

    public async Task<ParallelRunDashboard> PlanHandoverAsync(Guid connectionId, string kind, PlanHandover request,
        string actor, CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        var index = StepIndex(kind);
        await TrackedPlanAsync(connectionId, ct);
        var rows = await db.ParallelRunHandovers.Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).ToListAsync(ct);
        var row = rows.FirstOrDefault(x => x.Kind == kind);
        if (row?.OwnedSince is not null)
            throw new CoexistenceRuleException("handover_already_owned", "The module already owns this kind of record.");
        // Planned dates keep the handover order: never before the kind before it, never after the kind after it.
        var before = index == 0 ? null : rows.FirstOrDefault(x => x.Kind == HandoverKinds.Steps[index - 1].Kind);
        var beforeDate = before?.OwnedSince ?? before?.PlannedOn;
        var after = index == HandoverKinds.Steps.Length - 1 ? null
            : rows.FirstOrDefault(x => x.Kind == HandoverKinds.Steps[index + 1].Kind)?.PlannedOn;
        if ((beforeDate is { } b && request.PlannedOn < b) || (after is { } a && request.PlannedOn > a))
            throw new CoexistenceRuleException("handover_plan_out_of_order",
                "Plan the kinds of record in order: installed base and cover, returns, certificates, then retire.");
        if (row is null)
        {
            row = new() { TenantId = tenant.TenantId, ConnectionId = connectionId, Kind = kind };
            db.ParallelRunHandovers.Add(row);
        }
        row.PlannedOn = request.PlannedOn;
        await db.SaveChangesAsync(ct);
        await AuditAsync(connectionId, ConnectionAuditEventType.ParallelRunHandoverPlanned, actor,
            new { Kind = kind, request.PlannedOn }, ct);
        return await ViewAsync(connectionId, ct);
    }

    public async Task<ParallelRunDashboard> ConfirmHandoverAsync(Guid connectionId, string kind, ConfirmHandover request,
        string actor, CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        StepIndex(kind);
        var evidence = CoexistenceGuard.Text(request.Evidence, 500, "parallel_run_evidence_required",
            "Give the evidence in at most 500 characters.");
        var view = await ViewAsync(connectionId, ct);
        var step = view.Handover.Single(x => x.Kind == kind);
        if (!step.CanConfirm) throw new CoexistenceRuleException(step.ConfirmRefusals[0].Code, step.ConfirmRefusals[0].Message);
        var row = await db.ParallelRunHandovers.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.Kind == kind, ct);
        if (row is null)
        {
            row = new() { TenantId = tenant.TenantId, ConnectionId = connectionId, Kind = kind };
            db.ParallelRunHandovers.Add(row);
        }
        row.OwnedSince = Today;
        row.Evidence = evidence;
        row.ConfirmedBy = actor;
        row.ConfirmedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        await AuditAsync(connectionId, ConnectionAuditEventType.ParallelRunHandoverConfirmed, actor,
            new { Kind = kind, OwnedSince = row.OwnedSince }, ct);
        return await ViewAsync(connectionId, ct);
    }

    public async Task<ParallelRunDashboard> ConfirmGoLiveItemAsync(Guid connectionId, string itemCode,
        ConfirmGoLiveItem request, string actor, CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        if (!GoLiveItemCodes.Confirmed.Contains(itemCode))
            throw new CoexistenceRuleException("go_live_item_not_confirmable", "Only the confirmed go-live items can be confirmed by hand.");
        var evidence = CoexistenceGuard.Text(request.Evidence, 500, "parallel_run_evidence_required",
            "Give the evidence in at most 500 characters.");
        await TrackedPlanAsync(connectionId, ct);
        var row = await db.ParallelRunConfirmations.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.ItemCode == itemCode, ct);
        if (row is null)
        {
            row = new() { TenantId = tenant.TenantId, ConnectionId = connectionId, ItemCode = itemCode,
                Evidence = evidence, ConfirmedBy = actor, ConfirmedAt = clock.UtcNow };
            db.ParallelRunConfirmations.Add(row);
        }
        else
        {
            row.Evidence = evidence;
            row.ConfirmedBy = actor;
            row.ConfirmedAt = clock.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await AuditAsync(connectionId, ConnectionAuditEventType.ParallelRunItemConfirmed, actor,
            new { ItemCode = itemCode }, ct);
        return await ViewAsync(connectionId, ct);
    }

    public async Task<GoLiveRecordView> IssueRecordAsync(Guid connectionId, string actor, CancellationToken ct)
    {
        CoexistenceGuard.Actor(actor);
        var view = await ViewAsync(connectionId, ct);
        if (!view.GoLive.CanIssue)
            throw new CoexistenceRuleException(view.GoLive.IssueRefusals[0].Code, view.GoLive.IssueRefusals[0].Message);
        var record = new GoLiveRecord
        {
            TenantId = tenant.TenantId, RecordId = Guid.CreateVersion7(clock.UtcNow), ConnectionId = connectionId,
            IssuedAt = clock.UtcNow, IssuedBy = actor,
            SnapshotJson = JsonSerializer.Serialize(new Snapshot(view.ConnectionLabel, view.GoLive.Items, view.Handover),
                IntegrationContracts.Json),
        };
        db.GoLiveRecords.Add(record);
        await db.SaveChangesAsync(ct);
        await AuditAsync(connectionId, ConnectionAuditEventType.GoLiveRecordIssued, actor, new { record.RecordId }, ct);
        return await RecordAsync(record.RecordId, ct);
    }

    public async Task<GoLiveRecordView> RecordAsync(Guid recordId, CancellationToken ct)
    {
        var record = await db.GoLiveRecords.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.RecordId == recordId, ct)
            ?? throw new CoexistenceRuleException("go_live_record_not_found", "The go-live record was not found.");
        var snapshot = JsonSerializer.Deserialize<Snapshot>(record.SnapshotJson, IntegrationContracts.Json)!;
        var names = await people.DisplayNamesAsync([record.IssuedBy], ct);
        return new(record.RecordId, record.ConnectionId, snapshot.ConnectionLabel, record.IssuedAt,
            names.GetValueOrDefault(record.IssuedBy) ?? "Unknown user", snapshot.Items, snapshot.Handover);
    }

    public async Task<byte[]> RecordCsvAsync(Guid recordId, CancellationToken ct)
    {
        var record = await RecordAsync(recordId, ct);
        var csv = new StringBuilder("Section,Item,Status,Detail,Evidence,Confirmed by,Confirmed at\r\n");
        foreach (var item in record.Items)
            csv.AppendJoin(',', "Go-live", Csv(item.Label), item.Status, Csv(item.Detail), Csv(item.Evidence),
                Csv(item.ConfirmedByName), Csv(item.ConfirmedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))
                .Append("\r\n");
        foreach (var step in record.Handover)
            csv.AppendJoin(',', "Handover", Csv(step.Label), step.State,
                Csv(step.OwnedSince is { } owned ? $"Module owns since {owned:yyyy-MM-dd}" : step.PlannedOn is { } planned ? $"Planned {planned:yyyy-MM-dd}" : ""),
                Csv(step.Evidence), Csv(step.ConfirmedByName),
                Csv(step.ConfirmedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))).Append("\r\n");
        csv.Append(CultureInfo.InvariantCulture, $"Issued,{Csv(record.IssuedByName)},,{record.IssuedAt:yyyy-MM-dd HH:mm} UTC,,,\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    private async Task<IReadOnlyList<HandoverStepView>> HandoverAsync(Guid connectionId, ParallelRunPlan? plan,
        CancellationToken ct)
    {
        var rows = await db.ParallelRunHandovers.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).ToListAsync(ct);
        var monthEnd = await db.ParallelRunConfirmations.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.ItemCode == GoLiveItemCodes.MonthEndClose, ct);
        var contractsInModule = (await contracts.GetAsync(ct)).MaintainedIn == WarrantyContractsMaintainedIn.Module;
        var names = await people.DisplayNamesAsync(rows.Where(x => x.ConfirmedBy != null).Select(x => x.ConfirmedBy!)
            .Distinct().ToList(), ct);
        var steps = new List<HandoverStepView>();
        var previousOwned = true;
        for (var i = 0; i < HandoverKinds.Steps.Length; i++)
        {
            var (kind, label, description) = HandoverKinds.Steps[i];
            var row = rows.FirstOrDefault(x => x.Kind == kind);
            var owned = row?.OwnedSince is not null;
            var refusals = new List<RefusalReason>();
            if (plan is null) refusals.Add(new("parallel_run_not_started", "Start the parallel run first."));
            if (owned) refusals.Add(new("handover_already_owned", "The module already owns this kind of record."));
            else if (!previousOwned)
                refusals.Add(new("handover_previous_kind_open",
                    $"Hand over {HandoverKinds.Steps[i - 1].Label.ToLowerInvariant()} first; one kind of record at a time."));
            if (!owned && kind == HandoverKinds.InstalledBaseAndCover && !contractsInModule)
                refusals.Add(new("handover_contracts_setting_incumbent",
                    "Warranty contracts are still maintained in the incumbent. Change the setting on the coexistence screen first."));
            if (!owned && kind == HandoverKinds.RetireOwnedObjects && !monthEnd)
                refusals.Add(new("handover_month_end_close_open",
                    "Retire the owned objects only after the month-end close is confirmed in both systems."));
            var state = owned ? "module_owns"
                : kind == HandoverKinds.RetireOwnedObjects && !monthEnd ? "after_close"
                : previousOwned ? "next" : "planned";
            steps.Add(new(i + 1, kind, label, description, state, row?.PlannedOn, row?.OwnedSince, row?.Evidence,
                row?.ConfirmedBy is { } by ? names.GetValueOrDefault(by) ?? "Unknown user" : null, row?.ConfirmedAt,
                Facts(kind, owned, contractsInModule), refusals.Count == 0, refusals));
            previousOwned = owned;
        }
        return steps;
    }

    private static IReadOnlyList<string> Facts(string kind, bool owned, bool contractsInModule) => kind switch
    {
        HandoverKinds.InstalledBaseAndCover =>
        [
            "The incumbent's equipment-card status is frozen; changes there are raised as conflicts",
            contractsInModule ? "Warranty contracts are maintained in the module" : "Warranty contracts are still maintained in the incumbent",
        ],
        HandoverKinds.Returns => [owned ? "The incumbent is read-only for return authorisations" : "Open returns move with the handover"],
        HandoverKinds.Certificates => ["Version 1 of every migrated certificate is marked migrated"],
        _ => ["After the month-end close passes in both systems"],
    };

    private async Task<GoLiveView> GoLiveAsync(Guid connectionId, ParallelRunPlan? plan,
        IReadOnlyList<HandoverStepView> handover, MigrationGoLiveFacts facts, CancellationToken ct)
    {
        var confirmations = await db.ParallelRunConfirmations.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && GoLiveItemCodes.Confirmed.Contains(x.ItemCode)).ToListAsync(ct);
        var names = await people.DisplayNamesAsync(confirmations.Select(x => x.ConfirmedBy).Distinct().ToList(), ct);
        var items = new List<GoLiveItemView>();

        items.Add(facts.FinalSignoff is { } signed
            ? new(GoLiveItemCodes.ValidationSignedOff, "Validation signed off", "measured",
                signed.CleanOrExplained == signed.Checks ? "done" : "fail",
                $"{signed.CleanOrExplained} of {signed.Checks} checks · {signed.SignedAt:d MMM yyyy}")
            : new(GoLiveItemCodes.ValidationSignedOff, "Validation signed off", "measured", "open",
                "No final sign-off record yet."));

        items.Add(facts.CutoverLoad is { } load
            ? new(GoLiveItemCodes.DeltaLoad, "Delta load at cutover", "measured", load.Duration <= DeltaTarget ? "done" : "fail",
                $"{load.Label} · {Duration(load.Duration)} (target under 2 h)")
            : new(GoLiveItemCodes.DeltaLoad, "Delta load at cutover", "measured", "open", "No final or delta load completed yet."));

        items.Add(facts.RollbackRehearsal is { } rollback
            ? new(GoLiveItemCodes.RollbackRehearsed, "Rollback rehearsed", "measured",
                rollback.Duration <= RollbackTarget ? "done" : "fail",
                $"{rollback.Label} undone · {Duration(rollback.Duration)} · {rollback.At:d MMM yyyy} (target under 4 h)")
            : new(GoLiveItemCodes.RollbackRehearsed, "Rollback rehearsed", "measured", "open",
                "Undo a rehearsal load to rehearse the rollback."));

        items.Add(Confirmed(GoLiveItemCodes.UsersTrained, "Users trained",
            "Who was trained, for example service desk 14 of 14, warehouse 9 of 9."));

        var returns = handover.Single(x => x.Kind == HandoverKinds.Returns);
        items.Add(new(GoLiveItemCodes.ReturnsHandover, "Returns handover", "measured",
            returns.OwnedSince is not null ? "done" : "open",
            returns.OwnedSince is { } since ? $"Module owns since {since:d MMM yyyy}"
            : returns.PlannedOn is { } planned ? $"Planned {planned:d MMM yyyy}" : "Not planned yet."));

        items.Add(Confirmed(GoLiveItemCodes.MonthEndClose, "Month-end close in both systems",
            plan?.MonthEndCloseOn is { } close ? $"Planned {close:d MMM yyyy}." : "Set the month-end close date."));

        items.Add(Confirmed(GoLiveItemCodes.CustomerSignoff, "Customer sign-off",
            "Who signed for the customer, with their role."));

        var issued = await db.GoLiveRecords.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId).OrderByDescending(x => x.IssuedAt).ThenByDescending(x => x.RecordId)
            .Select(x => new { x.RecordId, x.IssuedAt }).FirstOrDefaultAsync(ct);
        var done = items.Count(x => x.Status == "done");
        var refusals = new List<RefusalReason>();
        if (plan is null) refusals.Add(new("parallel_run_not_started", "Start the parallel run first."));
        if (done < items.Count)
            refusals.Add(new("go_live_items_open", $"{items.Count - done} of {items.Count} go-live items are not done."));
        return new(done, items.Count, issued?.IssuedAt, issued?.RecordId, refusals.Count == 0, refusals, items);

        GoLiveItemView Confirmed(string code, string label, string pending)
        {
            var row = confirmations.FirstOrDefault(x => x.ItemCode == code);
            return row is null
                ? new(code, label, "confirmed", "open", pending)
                : new(code, label, "confirmed", "done", row.Evidence, row.Evidence,
                    names.GetValueOrDefault(row.ConfirmedBy) ?? "Unknown user", row.ConfirmedAt);
        }
    }

    private async Task<SyncLagView> SyncLagAsync(Guid connectionId, CancellationToken ct)
    {
        var health = await connections.HealthAsync(connectionId, ct);
        var behind = health.RecordTypes.Where(x => x.BehindSeconds is not null).Select(x => x.BehindSeconds!.Value).ToList();
        var since = clock.UtcNow.Date;
        var today = db.InboundMessages.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.AcceptedAt >= since);
        var total = await today.CountAsync(ct);
        var within = total == 0 ? 0 : await today.CountAsync(x =>
            EF.Functions.DateDiffSecond(x.OccurredAt, x.AcceptedAt) <= SyncLagTargetSeconds, ct);
        return new(behind.Count == 0 ? null : (int)Math.Round(behind.Max()), SyncLagTargetSeconds,
            total == 0 ? null : Math.Round((decimal)within / total, 4), total);
    }

    private async Task<ConflictSummaryView> ConflictsAsync(Guid connectionId, CancellationToken ct)
    {
        var open = db.CoexistenceConflicts.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.Status != CoexistenceConflictStatus.Closed);
        // Conflicts are never auto-resolved (ADR-0001); the count is shown so the screen can say so.
        return new(await open.CountAsync(ct), await open.MinAsync(x => (DateTime?)x.DetectedAt, ct), 0);
    }

    private async Task<FailedMessageSummaryView> FailedMessagesAsync(Guid connectionId, CancellationToken ct)
    {
        var open = db.DeadLetterItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId
            && (x.Status == DeadLetterStatus.NeedsAttention || x.Status == DeadLetterStatus.RetryPending));
        var agedBefore = clock.UtcNow.AddDays(-DeadLetterAgeDays);
        return new(await open.CountAsync(ct), await open.CountAsync(x => x.CreatedAt < agedBefore, ct));
    }

    private async Task<ReconciliationSummaryView> ReconciliationAsync(Guid connectionId, ParallelRunPlan? plan,
        CancellationToken ct)
    {
        if (plan is null) return new(0, 0, 0, 0, 0, null);
        var since = plan.StartDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var runs = await db.ReconciliationRuns.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.StartedAt >= since && x.Status != ReconciliationRunStatus.Running)
            .Select(x => new { x.Status, x.ExplainedAt, x.StartedAt }).ToListAsync(ct);
        var explained = runs.Where(x => x.Status == ReconciliationRunStatus.Mismatch && x.ExplainedAt != null).ToList();
        return new(runs.Count, runs.Count(x => x.Status == ReconciliationRunStatus.Clean), explained.Count,
            runs.Count(x => x.Status == ReconciliationRunStatus.Mismatch && x.ExplainedAt == null),
            runs.Count(x => x.Status == ReconciliationRunStatus.Failed),
            explained.Count == 0 ? null : DateOnly.FromDateTime(explained.Max(x => x.StartedAt)));
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow);

    private static int StepIndex(string kind)
    {
        var index = Array.FindIndex(HandoverKinds.Steps, x => x.Kind == kind);
        return index >= 0 ? index
            : throw new CoexistenceRuleException("handover_kind_not_found", "Unknown kind of record for the handover.");
    }

    private Task<ParallelRunPlan?> PlanAsync(Guid connectionId, CancellationToken ct) =>
        db.ParallelRunPlans.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct);

    private async Task<ParallelRunPlan> TrackedPlanAsync(Guid connectionId, CancellationToken ct) =>
        await db.ParallelRunPlans.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId, ct)
        ?? throw new CoexistenceRuleException("parallel_run_not_started", "Start the parallel run first.");

    private async Task AuditAsync(Guid connectionId, ConnectionAuditEventType type, string actor, object details,
        CancellationToken ct) =>
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), connectionId, type, actor,
            clock.UtcNow, JsonSerializer.Serialize(details, IntegrationContracts.Json)), ct);

    private static string Duration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours} h {value.Minutes} m"
        : value.TotalMinutes >= 1 ? $"{(int)value.TotalMinutes} m {value.Seconds} s" : $"{value.Seconds} s";

    private static string Csv(string? value) =>
        string.IsNullOrEmpty(value) ? "" : value.IndexOfAny([',', '"', '\r', '\n']) >= 0 || value[0] is '=' or '+' or '-' or '@'
            ? "\"" + (value[0] is '=' or '+' or '-' or '@' ? "'" : "") + value.Replace("\"", "\"\"") + "\"" : value;

    private sealed record Snapshot(string ConnectionLabel, IReadOnlyList<GoLiveItemView> Items,
        IReadOnlyList<HandoverStepView> Handover);
}
