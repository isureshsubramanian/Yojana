using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.ErpChange;

public sealed class ErpChangeRunService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    ConnectionCommands connections,
    IMigrationReconciliationPort units,
    IUserDirectory people,
    ErpChangeDualRunService dualRun,
    ErpChangeCutoverService cutover)
{
    public async Task<ErpChangeRunbookView> StartAsync(StartErpChangeRequest request, string actor,
        CancellationToken cancellationToken)
    {
        if (await db.ErpChangeRuns.AnyAsync(row => row.TenantId == tenant.TenantId
            && row.Status == ErpChangeRunStatus.Active, cancellationToken))
            throw new ErpChangeRuleException("erp_change_active_exists", "Only one ERP change may be active for a tenant.");
        if (!await db.Connections.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == request.SourceConnectionId && row.Status == ConnectionStatus.Active,
                cancellationToken))
            throw new ErpChangeRuleException("erp_change_source_not_found", "The active source connection was not found.");
        var registration = await connections.RegisterAsync(request.NewConnection, actor, cancellationToken);
        if (registration.ConnectionId == request.SourceConnectionId)
            throw new ErpChangeRuleException("erp_change_connections_same", "The source and target connections must differ.");
        var targetSettings = await db.ConnectionGatewaySettings.SingleAsync(row =>
            row.TenantId == tenant.TenantId && row.ConnectionId == registration.ConnectionId,
            cancellationToken);
        targetSettings.ReadOnly = true;
        targetSettings.OutboundMode = "none";
        var now = clock.UtcNow;
        var run = new ErpChangeRun
        {
            TenantId = tenant.TenantId,
            ErpChangeRunId = TimeOrderedId.New(now),
            SourceConnectionId = request.SourceConnectionId,
            TargetConnectionId = registration.ConnectionId,
            CurrentStep = ErpChangeStep.Register,
            Status = ErpChangeRunStatus.Active,
            StartedBy = actor,
            StartedAt = now,
        };
        db.ErpChangeRuns.Add(run);
        db.ErpChangeTransitions.Add(new()
        {
            TenantId = tenant.TenantId,
            ErpChangeTransitionId = TimeOrderedId.New(now),
            ErpChangeRunId = run.ErpChangeRunId,
            FromStep = 0,
            ToStep = ErpChangeStep.Register,
            Kind = "started",
            ActorRef = actor,
            OccurredAt = now,
        });
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException)
        {
            throw new ErpChangeRuleException("erp_change_active_exists", "Only one ERP change may be active for a tenant.");
        }
        return await DetailAsync(run.ErpChangeRunId, cancellationToken);
    }

    public async Task<ErpChangeRunbookView?> ActiveAsync(CancellationToken cancellationToken)
    {
        var id = await db.ErpChangeRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.Status == ErpChangeRunStatus.Active)
            .Select(row => (Guid?)row.ErpChangeRunId).SingleOrDefaultAsync(cancellationToken);
        return id is null ? null : await DetailAsync(id.Value, cancellationToken);
    }

    public async Task<ErpChangeRunbookView> DetailAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await RunAsync(runId, cancellationToken);
        var connectionIds = new[] { run.SourceConnectionId, run.TargetConnectionId };
        var connectionRows = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && connectionIds.Contains(row.ConnectionId))
            .Join(db.ConnectionGatewaySettings.AsNoTracking(), connection => new
                { connection.TenantId, connection.ConnectionId }, setting => new
                { setting.TenantId, setting.ConnectionId }, (connection, setting) => new ConnectionProjection
                {
                    ConnectionId = connection.ConnectionId,
                    Name = connection.DisplayLabel,
                    ConnectorCode = connection.ConnectorCode,
                    Status = connection.Status,
                    ReadOnly = setting.ReadOnly,
                    OutboundMode = setting.OutboundMode,
                }).ToListAsync(cancellationToken);
        var health = await db.ConnectionHealth.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && connectionIds.Contains(row.ConnectionId))
            .ToDictionaryAsync(row => row.ConnectionId, row => row.Status, cancellationToken);
        var checks = await db.ErpChangeStepChecks.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId)
            .OrderByDescending(row => row.CheckedAt).ThenByDescending(row => row.ErpChangeStepCheckId)
            .ToListAsync(cancellationToken);
        var latestChecks = checks.GroupBy(row => row.Step).Select(group => group.First()).OrderBy(row => row.Step)
            .ToList();
        var transitions = await db.ErpChangeTransitions.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ErpChangeRunId == runId)
            .OrderBy(row => row.OccurredAt).ThenBy(row => row.ErpChangeTransitionId)
            .ToListAsync(cancellationToken);
        var actorRefs = latestChecks.Select(row => row.CheckedBy)
            .Concat(transitions.Select(row => row.ActorRef)).Append(run.StartedBy)
            .Concat(run.ClosedBy is null ? [] : [run.ClosedBy]).Distinct().ToList();
        var names = await people.DisplayNamesAsync(actorRefs, cancellationToken);
        var differences = db.ErpChangeStockDifferences.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.ErpChangeRunId == runId && row.IsCurrent);
        var source = connectionRows.Single(row => row.ConnectionId == run.SourceConnectionId);
        var target = connectionRows.Single(row => row.ConnectionId == run.TargetConnectionId);
        return new(run.ErpChangeRunId, run.Status, run.CurrentStep,
            View(source, health.GetValueOrDefault(source.ConnectionId) ?? "unknown"),
            View(target, health.GetValueOrDefault(target.ConnectionId) ?? "unknown"),
            Name(run.StartedBy, names), run.StartedAt,
            run.ClosedBy is null ? null : Name(run.ClosedBy, names), run.ClosedAt, run.CloseReason,
            latestChecks.Select(row => new ErpChangeStepCheckView(row.Step, row.Passed,
                JsonSerializer.Deserialize<Dictionary<string, int>>(row.CountsJson) ?? [], row.Detail,
                row.CheckedAt, Name(row.CheckedBy, names))).ToList(),
            transitions.Select(row => new ErpChangeTransitionView(row.FromStep, row.ToStep, row.Kind,
                row.OccurredAt, Name(row.ActorRef, names), row.Reason)).ToList(),
            run.UnitsCompared,
            await differences.CountAsync(row => row.Status == ErpChangeDifferenceStatus.Open, cancellationToken),
            await differences.CountAsync(row => row.Status == ErpChangeDifferenceStatus.Open
                && row.HasLiveEntitlement, cancellationToken),
            await differences.CountAsync(row => row.Status == ErpChangeDifferenceStatus.Resolved, cancellationToken),
            await db.ErpChangeMasterDataExceptions.CountAsync(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId && row.Status == ErpChangeExceptionStatus.Open,
                cancellationToken),
            await dualRun.SummaryAsync(runId, cancellationToken),
            (await cutover.ViewAsync(runId, cancellationToken)).Items,
            await cutover.ArchiveStateAsync(runId, cancellationToken));
    }

    public async Task<ErpChangeStepCheckView> CheckAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        var (passed, counts, detail) = run.CurrentStep switch
        {
            ErpChangeStep.Register => await CheckRegisterAsync(run, cancellationToken),
            ErpChangeStep.MasterData => await CheckMasterDataAsync(run, cancellationToken),
            ErpChangeStep.Stock => await CheckStockAsync(run, cancellationToken),
            ErpChangeStep.DualRun => await CheckDualRunAsync(run, cancellationToken),
            ErpChangeStep.Cutover => await CheckCutoverAsync(run, cancellationToken),
            ErpChangeStep.Archive => await CheckArchiveAsync(run, cancellationToken),
            _ => throw new ErpChangeRuleException("erp_change_step_invalid", "The ERP change step is invalid."),
        };
        var row = new ErpChangeStepCheck
        {
            TenantId = tenant.TenantId,
            ErpChangeStepCheckId = TimeOrderedId.New(clock.UtcNow),
            ErpChangeRunId = runId,
            Step = run.CurrentStep,
            Passed = passed,
            CountsJson = JsonSerializer.Serialize(counts),
            Detail = detail,
            CheckedBy = actor,
            CheckedAt = clock.UtcNow,
        };
        db.ErpChangeStepChecks.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        var names = await people.DisplayNamesAsync([actor], cancellationToken);
        return new(row.Step, row.Passed, counts, row.Detail, row.CheckedAt, Name(actor, names));
    }

    public async Task<ErpChangeRunbookView> AdvanceAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        var check = await db.ErpChangeStepChecks.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId && row.Step == run.CurrentStep)
            .OrderByDescending(row => row.CheckedAt).ThenByDescending(row => row.ErpChangeStepCheckId)
            .FirstOrDefaultAsync(cancellationToken);
        if (check?.Passed != true)
            throw new ErpChangeRuleException("erp_change_exit_unmet", "Check and pass the current step's exit criterion before advancing.");
        var stillPassed = run.CurrentStep switch
        {
            ErpChangeStep.Register => (await CheckRegisterAsync(run, cancellationToken)).Passed,
            ErpChangeStep.MasterData => (await CheckMasterDataAsync(run, cancellationToken)).Passed,
            ErpChangeStep.Stock => (await CheckStockAsync(run, cancellationToken)).Passed,
            ErpChangeStep.DualRun => (await CheckDualRunAsync(run, cancellationToken)).Passed,
            ErpChangeStep.Cutover => (await CheckCutoverAsync(run, cancellationToken)).Passed,
            ErpChangeStep.Archive => (await CheckArchiveAsync(run, cancellationToken)).Passed,
            _ => false,
        };
        if (!stillPassed)
            throw new ErpChangeRuleException("erp_change_exit_changed", "The exit criterion changed after it was checked; check the step again.");
        var from = run.CurrentStep;
        if (from == ErpChangeStep.Archive)
        {
            run.Status = ErpChangeRunStatus.Completed;
            run.ClosedBy = actor;
            run.ClosedAt = clock.UtcNow;
            run.CloseReason = "completed";
        }
        else run.CurrentStep++;
        db.ErpChangeTransitions.Add(new()
        {
            TenantId = tenant.TenantId,
            ErpChangeTransitionId = TimeOrderedId.New(clock.UtcNow),
            ErpChangeRunId = runId,
            FromStep = from,
            ToStep = run.CurrentStep,
            Kind = from == ErpChangeStep.Archive ? "completed" : "advanced",
            ActorRef = actor,
            OccurredAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(runId, cancellationToken);
    }

    public async Task<ErpChangeRunbookView> StartReverseAsync(Guid runId,
        StartReverseErpChangeRequest request, string actor, CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep < ErpChangeStep.Cutover || run.CutoverAt is null)
            throw new ErpChangeRuleException("erp_change_reverse_before_cutover",
                "A reverse ERP change is available only after outbound cutover.");
        var previousSource = await db.Connections.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == run.SourceConnectionId, cancellationToken)
            ?? throw new ErpChangeRuleException("erp_change_source_not_found", "The former source connection was not found.");
        if (previousSource.Status == ConnectionStatus.Archived)
            throw new ErpChangeRuleException("erp_change_reverse_source_archived",
                "The former source is archived; register a new connection before starting an ERP change back.");
        if (previousSource.Status != ConnectionStatus.Active)
            throw new ErpChangeRuleException("erp_change_reverse_source_inactive",
                "The former source must be active before starting an ERP change back.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        run.Status = ErpChangeRunStatus.Completed;
        run.ClosedBy = actor;
        run.ClosedAt = clock.UtcNow;
        run.CloseReason = "reverse_erp_change_started";
        db.ErpChangeTransitions.Add(new()
        {
            TenantId = tenant.TenantId,
            ErpChangeTransitionId = TimeOrderedId.New(clock.UtcNow),
            ErpChangeRunId = runId,
            FromStep = run.CurrentStep,
            ToStep = run.CurrentStep,
            Kind = "reversed",
            ActorRef = actor,
            OccurredAt = clock.UtcNow,
            Reason = "A fresh ERP change was started with the connections swapped.",
        });
        await db.SaveChangesAsync(cancellationToken);
        var targetSettings = await db.ConnectionGatewaySettings.SingleAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == previousSource.ConnectionId, cancellationToken);
        targetSettings.ReadOnly = true;
        targetSettings.OutboundMode = "none";
        var reverse = new ErpChangeRun
        {
            TenantId = tenant.TenantId,
            ErpChangeRunId = TimeOrderedId.New(clock.UtcNow),
            SourceConnectionId = run.TargetConnectionId,
            TargetConnectionId = run.SourceConnectionId,
            CurrentStep = ErpChangeStep.Register,
            Status = ErpChangeRunStatus.Active,
            StartedBy = actor,
            StartedAt = clock.UtcNow,
        };
        db.ErpChangeRuns.Add(reverse);
        db.ErpChangeTransitions.Add(new()
        {
            TenantId = tenant.TenantId,
            ErpChangeTransitionId = TimeOrderedId.New(clock.UtcNow),
            ErpChangeRunId = reverse.ErpChangeRunId,
            FromStep = 0,
            ToStep = ErpChangeStep.Register,
            Kind = "started",
            ActorRef = actor,
            OccurredAt = clock.UtcNow,
            Reason = "reverse_erp_change",
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await DetailAsync(reverse.ErpChangeRunId, cancellationToken);
    }

    public async Task<ErpChangeRunbookView> CancelAsync(Guid runId, CancelErpChangeRequest request,
        string actor, CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep >= 5)
            throw new ErpChangeRuleException("erp_change_cancel_after_cutover_forbidden", "After cutover, use the controlled reverse migration procedure.");
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000)
            throw new ErpChangeRuleException("erp_change_cancel_reason_required", "Cancellation requires a reason of at most 2,000 characters.");
        var target = await db.Connections.SingleAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == run.TargetConnectionId, cancellationToken);
        var settings = await db.ConnectionGatewaySettings.SingleAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == run.TargetConnectionId, cancellationToken);
        target.Status = ConnectionStatus.Paused;
        settings.ReadOnly = true;
        settings.OutboundMode = "none";
        run.Status = ErpChangeRunStatus.Cancelled;
        run.ClosedBy = actor;
        run.ClosedAt = clock.UtcNow;
        run.CloseReason = request.Reason.Trim();
        db.ErpChangeTransitions.Add(new()
        {
            TenantId = tenant.TenantId,
            ErpChangeTransitionId = TimeOrderedId.New(clock.UtcNow),
            ErpChangeRunId = runId,
            FromStep = run.CurrentStep,
            ToStep = run.CurrentStep,
            Kind = "cancelled",
            ActorRef = actor,
            OccurredAt = clock.UtcNow,
            Reason = run.CloseReason,
        });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(runId, cancellationToken);
    }

    private async Task<(bool Passed, Dictionary<string, int> Counts, string Detail)> CheckRegisterAsync(
        ErpChangeRun run, CancellationToken cancellationToken)
    {
        var target = await db.Connections.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == run.TargetConnectionId, cancellationToken);
        var settings = await db.ConnectionGatewaySettings.AsNoTracking().SingleAsync(row =>
            row.TenantId == tenant.TenantId && row.ConnectionId == run.TargetConnectionId, cancellationToken);
        var healthy = await db.ConnectionHealth.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == run.TargetConnectionId && row.IsLive, cancellationToken);
        var safe = settings.ReadOnly && settings.OutboundMode == "none";
        var counts = new Dictionary<string, int>
        {
            ["healthy"] = healthy ? 1 : 0,
            ["outbound_enabled"] = safe ? 0 : 1,
        };
        return (target.Status == ConnectionStatus.Active && healthy && safe, counts,
            healthy && safe ? "The new connection is healthy, read-only and has no outbound enabled."
                : "The new connection must be active and healthy, read-only, and have outbound mode none.");
    }

    private async Task<(bool Passed, Dictionary<string, int> Counts, string Detail)> CheckDualRunAsync(
        ErpChangeRun run, CancellationToken cancellationToken)
    {
        var summary = await dualRun.SummaryAsync(run.ErpChangeRunId, cancellationToken);
        var passed = run.DualRunStartedAt is not null && summary.ElapsedDays >= ErpChangeDualRunService.MinimumDays
            && summary.ConsecutiveCleanDays >= summary.CleanDaysRequired && summary.AgedOpenDivergences == 0;
        return (passed, new()
        {
            ["elapsed_days"] = summary.ElapsedDays,
            ["minimum_days"] = ErpChangeDualRunService.MinimumDays,
            ["clean_days"] = summary.ConsecutiveCleanDays,
            ["clean_days_required"] = summary.CleanDaysRequired,
            ["open_divergences"] = summary.OpenDivergences,
            ["aged_open_divergences"] = summary.AgedOpenDivergences,
        }, passed ? "The 30-day minimum and the tenant's consecutive clean-day period have both passed."
            : "The dual run needs at least 30 elapsed days, the tenant's clean days in a row, and no overdue open divergence.");
    }

    private async Task<(bool Passed, Dictionary<string, int> Counts, string Detail)> CheckCutoverAsync(
        ErpChangeRun run, CancellationToken cancellationToken)
    {
        var checklist = await cutover.ViewAsync(run.ErpChangeRunId, cancellationToken);
        var open = checklist.Items.Count(row => row.State != ErpChangeChecklistState.Passed
            && !row.Code.StartsWith("first_outbound:", StringComparison.Ordinal));
        return (checklist.CanAdvance, new()
        {
            ["items"] = checklist.Items.Count,
            ["open_items"] = open,
        }, checklist.CanAdvance ? "The outbound cutover checklist is complete."
            : "Complete every system check or recorded confirmation in the outbound cutover checklist.");
    }

    private async Task<(bool Passed, Dictionary<string, int> Counts, string Detail)> CheckArchiveAsync(
        ErpChangeRun run, CancellationToken cancellationToken)
    {
        var state = await cutover.ArchiveStateAsync(run.ErpChangeRunId, cancellationToken);
        var passed = state.SourceArchived && state.ActiveConnections == 1;
        return (passed, new()
        {
            ["active_connections"] = state.ActiveConnections,
            ["retained_mappings"] = state.RetainedMappings,
            ["retained_inbound_messages"] = state.RetainedInboundMessages,
        }, passed ? "The former source is archived as readable history and the target is active."
            : "Archive the former source and leave the target as the one active connection in this change.");
    }

    private async Task<(bool Passed, Dictionary<string, int> Counts, string Detail)> CheckMasterDataAsync(
        ErpChangeRun run, CancellationToken cancellationToken)
    {
        var usage = await units.MasterReferenceUsageAsync(cancellationToken);
        var targetIds = (await db.ExternalIdMaps.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == run.TargetConnectionId && row.ValidTo == null
                && (row.EntityType == "item" || row.EntityType == "party"))
            .Select(row => new { Kind = row.EntityType, row.ModuleId }).ToListAsync(cancellationToken))
            .Select(row => (row.Kind, row.ModuleId)).ToHashSet();
        var missing = usage.Count(row => row.LiveUnits > 0
            && !targetIds.Contains((row.Kind, row.ReferenceId)));
        var open = await db.ErpChangeMasterDataExceptions.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == run.ErpChangeRunId && row.Status == ErpChangeExceptionStatus.Open,
            cancellationToken);
        var blocking = await db.ErpChangeMasterDataExceptions.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == run.ErpChangeRunId && row.Status == ErpChangeExceptionStatus.Open
            && row.LiveUnitsAffected > 0, cancellationToken);
        var counts = new Dictionary<string, int>
        {
            ["remap_completed"] = run.MasterDataRemappedAt is null ? 0 : 1,
            ["live_references"] = usage.Count(row => row.LiveUnits > 0),
            ["missing_mappings"] = missing,
            ["open_exceptions"] = open,
            ["blocking_exceptions"] = blocking,
        };
        var passed = run.MasterDataRemappedAt is not null && missing == 0 && blocking == 0;
        return (passed, counts, passed
            ? "Every live item and party is mapped or has a completed manual decision."
            : "Run the master-data remap, then resolve every live missing mapping or open exception.");
    }

    private async Task<(bool Passed, Dictionary<string, int> Counts, string Detail)> CheckStockAsync(
        ErpChangeRun run, CancellationToken cancellationToken)
    {
        var differences = db.ErpChangeStockDifferences.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.ErpChangeRunId == run.ErpChangeRunId && row.IsCurrent);
        var compared = run.UnitsCompared;
        var openCovered = await differences.CountAsync(row => row.Status == ErpChangeDifferenceStatus.Open
            && row.HasLiveEntitlement, cancellationToken);
        var residual = await differences.CountAsync(row => row.Status == ErpChangeDifferenceStatus.Open,
            cancellationToken);
        var signed = await db.ErpChangeStockSignoffs.AnyAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == run.ErpChangeRunId, cancellationToken);
        var counts = new Dictionary<string, int>
        {
            ["stock_compared"] = run.StockComparedAt is null ? 0 : 1,
            ["units_compared"] = compared,
            ["covered_differences_open"] = openCovered,
            ["residual_differences"] = residual,
            ["residual_signed_off"] = signed ? 1 : 0,
        };
        var passed = run.StockComparedAt is not null && openCovered == 0 && signed;
        return (passed, counts, passed
            ? "No covered unit has an unresolved difference and the residual list is signed off."
            : "Reconcile stock, resolve every covered-unit difference and have the inventory controller sign off the residual list.");
    }

    private async Task<ErpChangeRun> ActiveRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await RunAsync(runId, cancellationToken);
        if (run.Status != ErpChangeRunStatus.Active)
            throw new ErpChangeRuleException("erp_change_run_closed", "The ERP change run is closed.");
        return run;
    }

    private async Task<ErpChangeRun> RunAsync(Guid runId, CancellationToken cancellationToken) =>
        await db.ErpChangeRuns.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId, cancellationToken)
        ?? throw new ErpChangeRuleException("erp_change_run_not_found", "The ERP change run was not found.");

    private static ErpChangeConnectionView View(ConnectionProjection row, string health) => new(
        row.ConnectionId, row.Name, row.ConnectorCode, row.Status, health, row.ReadOnly, row.OutboundMode);

    private static string Name(string actor, IReadOnlyDictionary<string, string> names) =>
        names.GetValueOrDefault(actor) ?? "Former or system user";

    private sealed class ConnectionProjection
    {
        public Guid ConnectionId { get; init; }
        public required string Name { get; init; }
        public required string ConnectorCode { get; init; }
        public required string Status { get; init; }
        public bool ReadOnly { get; init; }
        public required string OutboundMode { get; init; }
    }
}
