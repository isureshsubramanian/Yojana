using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connections;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Outbox;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Integration.Verification;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.ErpChange;

public sealed class ErpChangeCutoverService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    OutboundRelay relay,
    IOutboundCommandParkingSink parking,
    ReconciliationService reconciliation,
    ConnectionCommands connections,
    IUserDirectory people,
    ConnectorAcceptanceService acceptance,
    AcceptanceGateSettings acceptanceSettings)
{
    public async Task<CutoverChecklistView> BeginAsync(Guid runId, BeginCutoverRequest request,
        string actor, CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep != ErpChangeStep.Cutover)
            throw new ErpChangeRuleException("erp_change_step_invalid", "Outbound cutover begins in step 5.");
        if (request.TargetOutboundMode is not ("reference_only" or "documents"))
            throw new ErpChangeRuleException("erp_change_outbound_mode_invalid",
                "Choose reference_only or documents for the new connection.");
        var source = await SettingsAsync(run.SourceConnectionId, cancellationToken);
        source.OutboundMode = "none";
        var target = await SettingsAsync(run.TargetConnectionId, cancellationToken);
        target.ReadOnly = true;
        target.OutboundMode = "none";
        run.TargetOutboundMode = request.TargetOutboundMode;
        await UpsertAsync(runId, "freeze_source", ErpChangeChecklistState.Passed,
            "Outbound is disabled on the source connection.", actor, "outbound_mode=none", cancellationToken);
        await UpsertAsync(runId, "drain_outbox", ErpChangeChecklistState.Waiting,
            "Queued commands must complete on the target or become manual tasks.", null, null, cancellationToken);
        await UpsertAsync(runId, "export_pending_actions", ErpChangeChecklistState.Passed,
            "The pending return and claim action export is ready.", actor, "generated from unfinished outbound commands",
            cancellationToken);
        await UpsertAsync(runId, "enable_target", ErpChangeChecklistState.Waiting,
            "The target remains read-only until validation is complete.", null, null, cancellationToken);
        await UpsertAsync(runId, "reconcile_after_cutover", ErpChangeChecklistState.Waiting,
            "Run reconciliation after enabling the target.", null, null, cancellationToken);
        await UpsertAsync(runId, "first_outbound_confirmed", ErpChangeChecklistState.Waiting,
            "Confirm the first target documents.", null, null, cancellationToken);
        var targetConnector = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == run.TargetConnectionId).Select(row => row.ConnectorCode)
            .SingleAsync(cancellationToken);
        if (targetConnector is "CX02" or "CX04")
        {
            var accepted = await acceptance.PassedAsync(run.TargetConnectionId, cancellationToken);
            await UpsertAsync(runId, "info:acceptance_passed",
                accepted ? ErpChangeChecklistState.Passed : ErpChangeChecklistState.Waiting,
                accepted ? "This tenant has passed a customer-sandbox acceptance run for the target connection."
                    : "This tenant has not passed a customer-sandbox acceptance run for the target connection.",
                accepted ? actor : null, accepted ? "acceptance_run=passed" : null, cancellationToken);
        }
        foreach (var commandType in await RequiredOutboundTypesAsync(run.TargetConnectionId, cancellationToken))
            await UpsertAsync(runId, ValidationCode(commandType), ErpChangeChecklistState.Waiting,
                $"Validate {commandType} on the target test company or record manual confirmation.",
                null, null, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(runId, cancellationToken);
    }

    public async Task<CutoverChecklistView> RecordValidationAsync(Guid runId,
        RecordOutboundValidationRequest request, string actor, CancellationToken cancellationToken)
    {
        var run = await CutoverRunAsync(runId, cancellationToken);
        var required = await RequiredOutboundTypesAsync(run.TargetConnectionId, cancellationToken);
        if (!required.Contains(request.CommandType, StringComparer.Ordinal))
            throw new ErpChangeRuleException("erp_change_outbound_type_invalid",
                "The command type is not an outbound capability of the target connection.");
        if (request.Method is not (ErpChangeValidationMethod.TestCompany or ErpChangeValidationMethod.Manual)
            || string.IsNullOrWhiteSpace(request.Evidence) || request.Evidence.Length > 2000)
            throw new ErpChangeRuleException("erp_change_validation_evidence_required",
                "Record the test-company result or a manual confirmation with evidence of at most 2,000 characters.");
        await UpsertAsync(runId, ValidationCode(request.CommandType),
            request.Passed ? ErpChangeChecklistState.Passed : ErpChangeChecklistState.Failed,
            request.Passed ? $"{request.CommandType} validation passed."
                : $"{request.CommandType} validation failed.", actor,
            $"{request.Method}: {request.Evidence.Trim()}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(runId, cancellationToken);
    }

    public async Task<CutoverChecklistView> EnableTargetAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await CutoverRunAsync(runId, cancellationToken);
        var targetConnector = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == run.TargetConnectionId).Select(row => row.ConnectorCode)
            .SingleAsync(cancellationToken);
        if (targetConnector is "CX02" or "CX04")
        {
            var accepted = await acceptance.PassedAsync(run.TargetConnectionId, cancellationToken);
            if (accepted)
                await UpsertAsync(runId, "info:acceptance_passed", ErpChangeChecklistState.Passed,
                    "This tenant has passed a customer-sandbox acceptance run for the target connection.",
                    actor, "acceptance_run=passed", cancellationToken);
            if (!accepted && await acceptanceSettings.RequiredAsync(targetConnector, cancellationToken))
                throw new CommandRefusedException("acceptance_required_before_go_live",
                    "The target connection must pass its customer-sandbox acceptance run before go-live.",
                    "Open the target connection's Verification record and complete a passing acceptance run.");
        }
        var required = await RequiredOutboundTypesAsync(run.TargetConnectionId, cancellationToken);
        var passed = await db.ErpChangeChecklistItems.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.State == ErpChangeChecklistState.Passed
            && required.Select(ValidationCode).Contains(row.Code), cancellationToken);
        if (passed != required.Count)
            throw new ErpChangeRuleException("erp_change_outbound_validation_open",
                "Validate every target outbound command type before enabling outbound.");
        var target = await SettingsAsync(run.TargetConnectionId, cancellationToken);
        target.ReadOnly = false;
        target.OutboundMode = run.TargetOutboundMode
            ?? throw new ErpChangeRuleException("erp_change_cutover_not_started", "Start the cutover first.");
        run.CutoverAt ??= clock.UtcNow;
        await UpsertAsync(runId, "enable_target", ErpChangeChecklistState.Passed,
            $"The target is read-write with outbound mode {target.OutboundMode}.", actor,
            $"read_only=false; outbound_mode={target.OutboundMode}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(runId, cancellationToken);
    }

    public async Task<CutoverChecklistView> DrainAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await CutoverRunAsync(runId, cancellationToken);
        if (run.CutoverAt is null)
            throw new ErpChangeRuleException("erp_change_target_not_enabled",
                "Enable the validated target before draining queued commands to it.");
        var unfinished = await db.OutboundCommands.Where(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == run.SourceConnectionId && row.Status != OutboundCommandStatus.Done)
            .ToListAsync(cancellationToken);
        foreach (var command in unfinished)
            command.ConnectionId = run.TargetConnectionId;
        var ids = unfinished.Select(row => row.OutboundCommandId).ToList();
        if (ids.Count > 0)
        {
            await db.DeadLetterItems.Where(row => row.TenantId == tenant.TenantId
                    && row.SourceKind == DeadLetterSourceKind.OutboundCommand && ids.Contains(row.SourceId))
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.ConnectionId,
                    run.TargetConnectionId), cancellationToken);
            await db.ManualOutboundTasks.Where(row => row.TenantId == tenant.TenantId
                    && row.OutboundCommandId != null && ids.Contains(row.OutboundCommandId.Value))
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.ConnectionId,
                    run.TargetConnectionId), cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        for (var pass = 0; pass < 5; pass++)
        {
            var result = await relay.RelayConnectionAsync(run.TargetConnectionId, cancellationToken);
            if (result.Retrying == 0 && result.AwaitingReview == 0) break;
        }
        var parked = await db.OutboundCommands.Where(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == run.TargetConnectionId && row.Status == OutboundCommandStatus.Parked)
            .ToListAsync(cancellationToken);
        foreach (var command in parked)
        {
            var hasTask = await db.ManualOutboundTasks.AnyAsync(row => row.TenantId == tenant.TenantId
                && row.OutboundCommandId == command.OutboundCommandId, cancellationToken);
            if (!hasTask)
                await parking.ParkAsync(command, "cutover_manual",
                    "The cutover converted this parked command to a manual task.", cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        var waiting = await WaitingCommandsAsync(run.TargetConnectionId, cancellationToken);
        var unownedParked = await UnownedParkedAsync(run.TargetConnectionId, cancellationToken);
        await UpsertAsync(runId, "drain_outbox",
            waiting == 0 && unownedParked == 0 ? ErpChangeChecklistState.Passed : ErpChangeChecklistState.Waiting,
            waiting == 0 && unownedParked == 0
                ? "Every queued command completed or has a manual task."
                : $"{waiting} command(s) are still queued and {unownedParked} parked command(s) have no manual task.",
            waiting == 0 && unownedParked == 0 ? actor : null,
            waiting == 0 && unownedParked == 0 ? $"manual_tasks={parked.Count}" : null,
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(runId, cancellationToken);
    }

    public async Task<CutoverChecklistView> ConvertParkedAsync(Guid runId,
        ConvertParkedOutboundRequest request, string actor, CancellationToken cancellationToken)
    {
        var run = await CutoverRunAsync(runId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > 2000)
            throw new ErpChangeRuleException("erp_change_manual_reason_required",
                "Converting a parked command requires a reason of at most 2,000 characters.");
        var command = await db.OutboundCommands.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.OutboundCommandId == request.OutboundCommandId
            && (row.ConnectionId == run.SourceConnectionId || row.ConnectionId == run.TargetConnectionId)
            && row.Status == OutboundCommandStatus.Parked, cancellationToken)
            ?? throw new ErpChangeRuleException("erp_change_parked_command_not_found",
                "The parked outbound command was not found.");
        command.ConnectionId = run.TargetConnectionId;
        await parking.ParkAsync(command, "cutover_manual", request.Reason.Trim(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(runId, cancellationToken);
    }

    public async Task<CutoverChecklistView> ConfirmFirstOutboundAsync(Guid runId,
        ConfirmFirstOutboundRequest request, string actor, CancellationToken cancellationToken)
    {
        _ = await CutoverRunAsync(runId, cancellationToken);
        if (string.IsNullOrWhiteSpace(request.CommandType) || request.CommandType.Length > 120
            || string.IsNullOrWhiteSpace(request.ExternalDocumentRef) || request.ExternalDocumentRef.Length > 320
            || string.IsNullOrWhiteSpace(request.Evidence) || request.Evidence.Length > 2000)
            throw new ErpChangeRuleException("erp_change_first_outbound_evidence_required",
                "Record the target document type, reference and evidence.");
        await UpsertAsync(runId, FirstOutboundCode(request.CommandType), ErpChangeChecklistState.Passed,
            $"The first {request.CommandType} was confirmed on the target.", actor,
            $"{request.ExternalDocumentRef.Trim()}: {request.Evidence.Trim()}", cancellationToken);
        var confirmations = await db.ErpChangeChecklistItems.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.Code.StartsWith("first_outbound:")
            && row.State == ErpChangeChecklistState.Passed, cancellationToken);
        await UpsertAsync(runId, "first_outbound_confirmed", ErpChangeChecklistState.Passed,
            $"{confirmations + 1} first outbound document confirmation(s) are recorded.", actor,
            request.ExternalDocumentRef.Trim(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(runId, cancellationToken);
    }

    public async Task<CutoverChecklistView> ReconcileAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await CutoverRunAsync(runId, cancellationToken);
        if (run.CutoverAt is null)
            throw new ErpChangeRuleException("erp_change_target_not_enabled", "Enable the target before reconciliation.");
        var day = DateOnly.FromDateTime(clock.UtcNow);
        var targetRun = await reconciliation.RunAsync(run.TargetConnectionId, day, day,
            ReconciliationTrigger.OnDemand, includeEntityDays: true, actor, cancellationToken);
        var status = await db.ReconciliationRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ReconciliationRunId == targetRun).Select(row => row.Status).SingleAsync(cancellationToken);
        var completed = status is ReconciliationRunStatus.Clean or ReconciliationRunStatus.Mismatch;
        await UpsertAsync(runId, "reconcile_after_cutover",
            completed ? ErpChangeChecklistState.Passed : ErpChangeChecklistState.Failed,
            status == ReconciliationRunStatus.Clean ? "The post-cutover reconciliation is clean."
                : completed ? "The post-cutover reconciliation ran and its mismatches remain in the reconciliation desk."
                : $"The post-cutover reconciliation finished with status {status}.",
            actor, $"reconciliation_run={targetRun:N}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(runId, cancellationToken);
    }

    public async Task<byte[]> PendingActionsCsvAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await RunAsync(runId, cancellationToken);
        var rows = await db.OutboundCommands.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && (row.ConnectionId == run.SourceConnectionId || row.ConnectionId == run.TargetConnectionId)
                && row.Status != OutboundCommandStatus.Done
                && (row.ModuleDocumentRef.StartsWith("rma/") || row.ModuleDocumentRef.StartsWith("claim/")))
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.OutboundCommandId)
            .Select(row => new PendingProjection
            {
                Kind = row.ModuleDocumentRef.StartsWith("rma/") ? "RMA" : "Claim",
                Reference = row.ModuleDocumentRef,
                CommandType = row.CommandType,
                Status = row.Status,
                CreatedAt = row.CreatedAt,
            }).ToListAsync(cancellationToken);
        var csv = new StringBuilder("Kind,Reference,Pending action,Status,Created at UTC\r\n");
        foreach (var row in rows)
            csv.AppendJoin(',', row.Kind, Csv(row.Reference), row.CommandType, row.Status,
                row.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)).Append("\r\n");
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
    }

    public async Task<ArchiveErpChangeResult> ArchiveAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep != ErpChangeStep.Archive || run.CutoverAt is null)
            throw new ErpChangeRuleException("erp_change_step_invalid", "The source is archived in step 6 after cutover.");
        var result = await connections.ArchiveAsync(run.SourceConnectionId,
            new ArchiveConnection(), actor, isAdministrator: false, cancellationToken);
        if (!result.Archived)
            return new(runId, false, await ArchiveStateAsync(runId, cancellationToken), result.CleanupErrorCode);
        await db.ExternalIdMaps.Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == run.SourceConnectionId && row.ValidTo != null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ValidTo, run.CutoverAt), cancellationToken);
        run.ArchivedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new(runId, true, await ArchiveStateAsync(runId, cancellationToken));
    }

    public async Task<CutoverChecklistView> ViewAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await RunAsync(runId, cancellationToken);
        var rows = await db.ErpChangeChecklistItems.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId).OrderBy(row => row.Code).ToListAsync(cancellationToken);
        var names = await NamesAsync(rows.Select(row => row.CompletedBy), cancellationToken);
        var items = rows.Select(row => new CutoverChecklistItemView(row.Code, row.State, row.Detail,
            row.Evidence, row.CompletedBy is null ? null
                : names.GetValueOrDefault(row.CompletedBy) ?? "Former or system user", row.CompletedAt)).ToList();
        var required = items.Where(row => !row.Code.StartsWith("first_outbound:", StringComparison.Ordinal)
            && !row.Code.StartsWith("info:", StringComparison.Ordinal)).ToList();
        return new(runId, run.CutoverAt, items,
            required.Count > 0 && required.All(row => row.State == ErpChangeChecklistState.Passed));
    }

    public async Task<ParkedOutboundPage> ParkedAsync(Guid runId, string? cursor, int requestedTake,
        CancellationToken cancellationToken)
    {
        var run = await RunAsync(runId, cancellationToken);
        var take = Math.Clamp(requestedTake, 1, 100);
        var query = from command in db.OutboundCommands.AsNoTracking()
                    from letter in db.DeadLetterItems.AsNoTracking().Where(row =>
                        row.TenantId == command.TenantId
                        && row.SourceKind == DeadLetterSourceKind.OutboundCommand
                        && row.SourceId == command.OutboundCommandId).DefaultIfEmpty()
                    where command.TenantId == tenant.TenantId
                        && command.ConnectionId == run.TargetConnectionId
                        && command.Status == OutboundCommandStatus.Parked
                    select new ParkedProjection
                    {
                        OutboundCommandId = command.OutboundCommandId,
                        DocumentKind = command.CommandType,
                        DocumentReference = command.ErpReferenceText,
                        Payload = command.Payload,
                        ParkedReason = letter != null ? letter.Failure
                            : command.LastFailure ?? "The outbound command is parked.",
                        ParkedAt = letter != null ? letter.UpdatedAt
                            : command.CompletedAt ?? command.LastAttemptAt ?? command.CreatedAt,
                        OwnedByManualTask = db.ManualOutboundTasks.Any(task =>
                            task.TenantId == command.TenantId
                            && task.OutboundCommandId == command.OutboundCommandId),
                    };
        if (DecodeParkedCursor(cursor) is { } key)
            query = query.Where(row => row.ParkedAt < key.ParkedAt
                || (row.ParkedAt == key.ParkedAt && row.OutboundCommandId.CompareTo(key.Id) < 0));
        var rows = await query.OrderByDescending(row => row.ParkedAt)
            .ThenByDescending(row => row.OutboundCommandId).Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).Select(row => new ParkedOutboundRow(row.OutboundCommandId,
            row.DocumentKind, row.DocumentReference, UnitCount(row.DocumentKind, row.Payload),
            row.ParkedReason, row.ParkedAt, row.OwnedByManualTask)).ToList();
        return new(page, rows.Count > take && page.Count != 0
            ? EncodeParkedCursor(new(page[^1].ParkedAt, page[^1].OutboundCommandId)) : null);
    }

    public async Task<ArchiveStateView> ArchiveStateAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await RunAsync(runId, cancellationToken);
        var source = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == run.SourceConnectionId).Select(row => new ConnectionProjection
            { Status = row.Status, ArchivedAt = row.ArchivedAt }).SingleAsync(cancellationToken);
        var pair = new[] { run.SourceConnectionId, run.TargetConnectionId };
        return new(source.Status == ConnectionStatus.Archived, source.ArchivedAt,
            await db.Connections.CountAsync(row => row.TenantId == tenant.TenantId
                && pair.Contains(row.ConnectionId) && row.Status == ConnectionStatus.Active, cancellationToken),
            await db.ExternalIdMaps.CountAsync(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == run.SourceConnectionId, cancellationToken),
            await db.InboundMessages.CountAsync(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == run.SourceConnectionId, cancellationToken));
    }

    private async Task<IReadOnlyList<string>> RequiredOutboundTypesAsync(Guid connectionId,
        CancellationToken cancellationToken) => await db.ConnectionCapabilities.AsNoTracking()
        .Where(row => row.TenantId == tenant.TenantId && row.ConnectionId == connectionId
            && OutboundCapabilityTypes.All.Contains(row.CanonicalMessage)
            && row.SupportLevel != CapabilitySupport.Unsupported)
        .Select(row => row.CanonicalMessage).Distinct().OrderBy(row => row).ToListAsync(cancellationToken);

    private async Task<int> WaitingCommandsAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.OutboundCommands.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == connectionId && (row.Status == OutboundCommandStatus.Pending
                || row.Status == OutboundCommandStatus.Retry || row.Status == OutboundCommandStatus.Processing
                || row.Status == OutboundCommandStatus.AwaitingReview), cancellationToken);

    private async Task<int> UnownedParkedAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.OutboundCommands.CountAsync(command => command.TenantId == tenant.TenantId
            && command.ConnectionId == connectionId && command.Status == OutboundCommandStatus.Parked
            && !db.ManualOutboundTasks.Any(task => task.TenantId == command.TenantId
                && task.OutboundCommandId == command.OutboundCommandId), cancellationToken);

    private async Task UpsertAsync(Guid runId, string code, string state, string detail,
        string? actor, string? evidence, CancellationToken cancellationToken)
    {
        var row = db.ErpChangeChecklistItems.Local.FirstOrDefault(value => value.TenantId == tenant.TenantId
                && value.ErpChangeRunId == runId && value.Code == code)
            ?? await db.ErpChangeChecklistItems.SingleOrDefaultAsync(value => value.TenantId == tenant.TenantId
                && value.ErpChangeRunId == runId && value.Code == code, cancellationToken);
        if (row is null)
        {
            row = new()
            {
                TenantId = tenant.TenantId,
                ErpChangeChecklistItemId = TimeOrderedId.New(clock.UtcNow),
                ErpChangeRunId = runId,
                Code = code,
                State = state,
                Detail = detail,
            };
            db.ErpChangeChecklistItems.Add(row);
        }
        row.State = state;
        row.Detail = detail;
        row.Evidence = evidence;
        row.CompletedBy = state == ErpChangeChecklistState.Passed ? actor : null;
        row.CompletedAt = state == ErpChangeChecklistState.Passed ? clock.UtcNow : null;
    }

    private async Task<ErpChangeRun> CutoverRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep != ErpChangeStep.Cutover)
            throw new ErpChangeRuleException("erp_change_step_invalid", "This action belongs to cutover step 5.");
        if (run.TargetOutboundMode is null)
            throw new ErpChangeRuleException("erp_change_cutover_not_started", "Start the cutover checklist first.");
        return run;
    }

    private async Task<ErpChangeRun> ActiveRunAsync(Guid runId, CancellationToken cancellationToken) =>
        await db.ErpChangeRuns.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.Status == ErpChangeRunStatus.Active, cancellationToken)
        ?? throw new ErpChangeRuleException("erp_change_run_not_found", "The active ERP change was not found.");

    private async Task<ErpChangeRun> RunAsync(Guid runId, CancellationToken cancellationToken) =>
        await db.ErpChangeRuns.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId, cancellationToken)
        ?? throw new ErpChangeRuleException("erp_change_run_not_found", "The ERP change was not found.");

    private Task<ConnectionGatewaySetting> SettingsAsync(Guid connectionId, CancellationToken cancellationToken) =>
        db.ConnectionGatewaySettings.SingleAsync(row => row.TenantId == tenant.TenantId
            && row.ConnectionId == connectionId, cancellationToken);

    private async Task<Dictionary<string, string>> NamesAsync(IEnumerable<string?> actors,
        CancellationToken cancellationToken)
    {
        var refs = actors.OfType<string>().Distinct().ToList();
        return refs.Count == 0 ? [] : new(await people.DisplayNamesAsync(refs, cancellationToken));
    }

    private static string ValidationCode(string commandType) => $"validate:{commandType}";
    private static string FirstOutboundCode(string commandType) => $"first_outbound:{commandType}";
    private static string Csv(string value) => '"' + value.Replace("\"", "\"\"") + '"';

    private static int UnitCount(string commandType, string payload)
    {
        try
        {
            return commandType switch
            {
                nameof(CreateReplacementDelivery) or nameof(ChangeStockStatusOrLocation)
                    or nameof(CreateServiceOrder) => 1,
                nameof(CreateCreditMemo) => CreditMemoUnitCount(JsonSerializer.Deserialize<CreateCreditMemo>(
                    payload, IntegrationContracts.Json)),
                _ => 0,
            };
        }
        catch (JsonException) { return 0; }
    }

    private static int CreditMemoUnitCount(CreateCreditMemo? command)
    {
        if (command is null) return 0;
        var unitIds = command.Lines.Where(line => line.UnitId is not null)
            .Select(line => line.UnitId!.Value).Distinct().Count();
        var serials = command.Lines.Where(line => line.UnitId is null)
            .SelectMany(line => line.Serials ?? []).Select(serial => serial.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Count();
        return unitIds + serials;
    }

    private static string EncodeParkedCursor(ParkedCursor cursor) => Convert.ToBase64String(
        Encoding.UTF8.GetBytes($"{cursor.ParkedAt.Ticks}|{cursor.Id:D}"));

    private static ParkedCursor? DecodeParkedCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('|');
            return parts.Length == 2 && long.TryParse(parts[0], out var ticks)
                && Guid.TryParse(parts[1], out var id)
                ? new(new DateTime(ticks, DateTimeKind.Utc), id) : null;
        }
        catch (FormatException) { return null; }
    }

    private sealed class PendingProjection
    {
        public required string Kind { get; init; }
        public required string Reference { get; init; }
        public required string CommandType { get; init; }
        public required string Status { get; init; }
        public DateTime CreatedAt { get; init; }
    }

    private sealed class ConnectionProjection
    {
        public required string Status { get; init; }
        public DateTime? ArchivedAt { get; init; }
    }

    private sealed class ParkedProjection
    {
        public Guid OutboundCommandId { get; init; }
        public required string DocumentKind { get; init; }
        public required string DocumentReference { get; init; }
        public required string Payload { get; init; }
        public required string ParkedReason { get; init; }
        public DateTime ParkedAt { get; init; }
        public bool OwnedByManualTask { get; init; }
    }

    private sealed record ParkedCursor(DateTime ParkedAt, Guid Id);
}
