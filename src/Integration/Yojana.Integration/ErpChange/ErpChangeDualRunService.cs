using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.ErpChange;

/// <summary>Narrow gateway seam for observing a target version without applying it.</summary>
public interface IErpChangeDivergenceSink
{
    Task ObserveDocumentAsync(Guid targetConnectionId, Guid subjectId, string subjectRef,
        string sourceValue, string targetValue, Guid sourceInboundMessageId,
        Guid targetInboundMessageId, CancellationToken cancellationToken);
}

public sealed class ErpChangeDualRunService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    ErpChangeCleanPeriodSettings cleanPeriod,
    ReconciliationService reconciliation,
    ErpChangeStockService stock,
    IUserDirectory people,
    ErpChangeNotifications notifications) : IErpChangeDivergenceSink
{
    public const int MinimumDays = 30;

    public async Task<StartDualRunResult> StartAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep != ErpChangeStep.DualRun)
            throw new ErpChangeRuleException("erp_change_step_invalid", "The dual run starts in step 4.");
        var source = await SettingsAsync(run.SourceConnectionId, cancellationToken);
        var target = await SettingsAsync(run.TargetConnectionId, cancellationToken);
        if (run.DualRunStartedAt is null)
        {
            run.DualRunStartedAt = clock.UtcNow;
            run.SourceOutboundMode = source.OutboundMode;
        }
        target.ReadOnly = true;
        target.OutboundMode = "none";
        await db.SaveChangesAsync(cancellationToken);
        var required = await cleanPeriod.GetAsync(cancellationToken);
        return new(runId, run.DualRunStartedAt.Value, MinimumDays, required.Days,
            source.ReadOnly, source.OutboundMode, target.ReadOnly, target.OutboundMode);
    }

    public async Task ObserveDocumentAsync(Guid targetConnectionId, Guid subjectId, string subjectRef,
        string sourceValue, string targetValue, Guid sourceInboundMessageId,
        Guid targetInboundMessageId, CancellationToken cancellationToken)
    {
        var run = await db.ErpChangeRuns.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.Status == ErpChangeRunStatus.Active && row.CurrentStep == ErpChangeStep.DualRun
            && row.TargetConnectionId == targetConnectionId && row.DualRunStartedAt != null,
            cancellationToken);
        if (run is null) return;
        await ReportAsync(run, new(ErpChangeDivergenceKind.Document, "document", subjectId,
            subjectRef, sourceValue, targetValue, sourceInboundMessageId, targetInboundMessageId),
            cancellationToken);
    }

    public async Task<ErpChangeDivergenceRow> ReportAsync(Guid runId,
        ReportErpChangeDivergence request, CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        return View(await ReportAsync(run, request, cancellationToken),
            new Dictionary<string, string>());
    }

    private async Task<ErpChangeDivergence> ReportAsync(ErpChangeRun run,
        ReportErpChangeDivergence request, CancellationToken cancellationToken)
    {
        if (run.CurrentStep != ErpChangeStep.DualRun || run.DualRunStartedAt is null)
            throw new ErpChangeRuleException("erp_change_dual_run_not_started", "Start the dual run before recording divergences.");
        if (request.Kind is not (ErpChangeDivergenceKind.Stock or ErpChangeDivergenceKind.Document)
            || string.IsNullOrWhiteSpace(request.SubjectType) || request.SubjectType.Length > 40
            || string.IsNullOrWhiteSpace(request.SubjectRef) || request.SubjectRef.Length > 320
            || request.SourceValue.Length > 64000 || request.TargetValue.Length > 64000
            || request.AgeLimitDays is < 1 or > 90)
            throw new ErpChangeRuleException("erp_change_divergence_invalid", "Supply a valid stock or document divergence and an age limit from 1 to 90 days.");
        var row = await db.ErpChangeDivergences.SingleOrDefaultAsync(value =>
            value.TenantId == tenant.TenantId && value.ErpChangeRunId == run.ErpChangeRunId
            && value.Kind == request.Kind && value.SubjectType == request.SubjectType
            && value.SubjectRef == request.SubjectRef, cancellationToken);
        var created = false;
        if (row is null)
        {
            created = true;
            row = new()
            {
                TenantId = tenant.TenantId,
                ErpChangeDivergenceId = TimeOrderedId.New(clock.UtcNow),
                ErpChangeRunId = run.ErpChangeRunId,
                Kind = request.Kind,
                SubjectType = request.SubjectType.Trim(),
                SubjectId = request.SubjectId,
                SubjectRef = request.SubjectRef.Trim(),
                SourceValue = request.SourceValue,
                TargetValue = request.TargetValue,
                SourceInboundMessageId = request.SourceInboundMessageId,
                TargetInboundMessageId = request.TargetInboundMessageId,
                StockDifferenceId = request.StockDifferenceId,
                FirstSeenAt = clock.UtcNow,
                LastSeenAt = clock.UtcNow,
                ReviewDueAt = clock.UtcNow.AddDays(request.AgeLimitDays),
                Status = ErpChangeDivergenceStatus.Open,
            };
            db.ErpChangeDivergences.Add(row);
        }
        else
        {
            var changed = !StringComparer.Ordinal.Equals(row.SourceValue, request.SourceValue)
                || !StringComparer.Ordinal.Equals(row.TargetValue, request.TargetValue);
            row.SourceValue = request.SourceValue;
            row.TargetValue = request.TargetValue;
            row.SourceInboundMessageId = request.SourceInboundMessageId ?? row.SourceInboundMessageId;
            row.TargetInboundMessageId = request.TargetInboundMessageId ?? row.TargetInboundMessageId;
            row.StockDifferenceId = request.StockDifferenceId ?? row.StockDifferenceId;
            row.LastSeenAt = clock.UtcNow;
            if (row.Status == ErpChangeDivergenceStatus.Resolved && changed)
            {
                row.Status = ErpChangeDivergenceStatus.Open;
                row.Resolution = null;
                row.ResolutionNote = null;
                row.ResolutionCommandId = null;
                row.ResolvedBy = null;
                row.ResolvedAt = null;
                row.FirstSeenAt = clock.UtcNow;
                row.ReviewDueAt = clock.UtcNow.AddDays(request.AgeLimitDays);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        if (created && request.Kind == ErpChangeDivergenceKind.Stock)
            await notifications.FirstStockDivergenceAsync(run.ErpChangeRunId, cancellationToken);
        return row;
    }

    public async Task<ErpChangeDivergencePage> ListAsync(Guid runId,
        DivergenceListRequest request, CancellationToken cancellationToken)
    {
        _ = await RunAsync(runId, cancellationToken);
        var take = Math.Clamp(request.Take, 1, 100);
        var query = db.ErpChangeDivergences.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId);
        if (!string.IsNullOrWhiteSpace(request.Kind)) query = query.Where(row => row.Kind == request.Kind);
        if (!string.IsNullOrWhiteSpace(request.Status)) query = query.Where(row => row.Status == request.Status);
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var text = request.Query.Trim();
            query = query.Where(row => row.SubjectRef.Contains(text) || row.SourceValue.Contains(text)
                || row.TargetValue.Contains(text));
        }
        if (Decode(request.Cursor) is { } cursor)
            query = query.Where(row => row.FirstSeenAt < cursor.At
                || (row.FirstSeenAt == cursor.At && row.ErpChangeDivergenceId.CompareTo(cursor.Id) > 0));
        var rows = await query.OrderByDescending(row => row.FirstSeenAt)
            .ThenBy(row => row.ErpChangeDivergenceId).Take(take + 1).ToListAsync(cancellationToken);
        var more = rows.Count > take;
        var pageRows = rows.Take(take).ToList();
        var names = await NamesAsync(pageRows.Select(row => row.ResolvedBy), cancellationToken);
        var all = db.ErpChangeDivergences.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId);
        return new(pageRows.Select(row => View(row, names)).ToList(),
            more && pageRows.Count != 0 ? Encode(new(pageRows[^1].FirstSeenAt,
                pageRows[^1].ErpChangeDivergenceId)) : null,
            await all.CountAsync(row => row.Status == ErpChangeDivergenceStatus.Open, cancellationToken),
            await all.CountAsync(row => row.Status == ErpChangeDivergenceStatus.Open
                && row.ReviewDueAt < clock.UtcNow, cancellationToken),
            await all.CountAsync(row => row.Status == ErpChangeDivergenceStatus.Resolved, cancellationToken));
    }

    public async Task<ErpChangeDivergenceRow> AcceptSourceAsync(Guid runId, Guid divergenceId,
        string expectedKind, AcceptDivergenceSourceRequest request, string actor,
        CancellationToken cancellationToken)
    {
        if (request.CommandId == Guid.Empty)
            throw new CommandRefusedException("erp_change_command_id_required", "A command id is required.");
        await RequireKindAsync(runId, divergenceId, expectedKind, cancellationToken);
        return await ResolveAsync(runId, divergenceId, ErpChangeDivergenceResolution.AcceptedSource,
            null, request.CommandId, actor, cancellationToken);
    }

    public async Task<ErpChangeDivergenceRow> ExplainAsync(Guid runId, Guid divergenceId,
        string expectedKind, ExplainDivergenceRequest request, string actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Note) || request.Note.Length > 2000)
            throw new ErpChangeRuleException("erp_change_note_required", "An explanation of at most 2,000 characters is required.");
        await RequireKindAsync(runId, divergenceId, expectedKind, cancellationToken);
        return await ResolveAsync(runId, divergenceId, ErpChangeDivergenceResolution.Explained,
            request.Note.Trim(), null, actor, cancellationToken);
    }

    public async Task<ErpChangeDivergenceRow> RaiseCorrectionAsync(Guid runId, Guid divergenceId,
        RaiseDivergenceCorrectionRequest request, string actor, CancellationToken cancellationToken)
    {
        var row = await OpenAsync(runId, divergenceId, cancellationToken);
        if (row.Kind != ErpChangeDivergenceKind.Stock || row.StockDifferenceId is not { } differenceId)
            throw new ErpChangeRuleException("erp_change_stock_correction_unavailable",
                "Only a stock divergence linked to a stock difference can raise an ordinary correction.");
        await stock.ResolveAsync(runId, differenceId, request.Correction, actor, cancellationToken);
        return await ResolveAsync(runId, divergenceId, ErpChangeDivergenceResolution.CorrectionRaised,
            request.Correction.Note, request.Correction.CommandId, actor, cancellationToken);
    }

    public async Task<ErpChangeDailyCheckView> RunDailyAsync(Guid runId, string actor,
        CancellationToken cancellationToken)
    {
        var run = await ActiveRunAsync(runId, cancellationToken);
        if (run.CurrentStep != ErpChangeStep.DualRun || run.DualRunStartedAt is null)
            throw new ErpChangeRuleException("erp_change_dual_run_not_started", "Start the dual run before the daily check.");
        var day = DateOnly.FromDateTime(clock.UtcNow);
        var sourceRun = await reconciliation.RunAsync(run.SourceConnectionId, day, day,
            ReconciliationTrigger.OnDemand, includeEntityDays: true, actor, cancellationToken);
        var targetRun = await reconciliation.RunAsync(run.TargetConnectionId, day, day,
            ReconciliationTrigger.OnDemand, includeEntityDays: true, actor, cancellationToken);
        var start = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var end = start.AddDays(1);
        var newCount = await db.ErpChangeDivergences.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.FirstSeenAt >= start && row.FirstSeenAt < end,
            cancellationToken);
        var aged = await db.ErpChangeDivergences.CountAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.Status == ErpChangeDivergenceStatus.Open
            && row.ReviewDueAt < clock.UtcNow, cancellationToken);
        var row = await db.ErpChangeDailyChecks.SingleOrDefaultAsync(value => value.TenantId == tenant.TenantId
            && value.ErpChangeRunId == runId && value.BusinessDate == day, cancellationToken);
        if (row is null)
        {
            row = new()
            {
                TenantId = tenant.TenantId,
                ErpChangeDailyCheckId = TimeOrderedId.New(clock.UtcNow),
                ErpChangeRunId = runId,
                BusinessDate = day,
                CheckedBy = actor,
            };
            db.ErpChangeDailyChecks.Add(row);
        }
        row.SourceReconciliationRunId = sourceRun;
        row.TargetReconciliationRunId = targetRun;
        row.NewDivergences = newCount;
        row.AgedOpenDivergences = aged;
        row.Clean = newCount == 0 && aged == 0;
        row.CheckedBy = actor;
        row.CheckedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return new(day, newCount, aged, row.Clean, await ConsecutiveCleanDaysAsync(runId, cancellationToken),
            row.CheckedAt, actor);
    }

    public async Task<DualRunSummaryView> SummaryAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await RunAsync(runId, cancellationToken);
        var required = await cleanPeriod.GetAsync(cancellationToken);
        var elapsed = run.DualRunStartedAt is null ? 0
            : Math.Max(0, DateOnly.FromDateTime(clock.UtcNow).DayNumber
                - DateOnly.FromDateTime(run.DualRunStartedAt.Value).DayNumber);
        var last = await db.ErpChangeDailyChecks.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId).OrderByDescending(row => row.BusinessDate)
            .Select(row => (DateOnly?)row.BusinessDate).FirstOrDefaultAsync(cancellationToken);
        return new(run.DualRunStartedAt, MinimumDays, elapsed, required.Days,
            await ConsecutiveCleanDaysAsync(runId, cancellationToken),
            await db.ErpChangeDivergences.CountAsync(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId && row.Status == ErpChangeDivergenceStatus.Open, cancellationToken),
            await db.ErpChangeDivergences.CountAsync(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId && row.Status == ErpChangeDivergenceStatus.Open
                && row.ReviewDueAt < clock.UtcNow, cancellationToken), last);
    }

    public async Task<int> ConsecutiveCleanDaysAsync(Guid runId, CancellationToken cancellationToken)
    {
        var rows = await db.ErpChangeDailyChecks.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId).OrderByDescending(row => row.BusinessDate)
            .Select(row => new DailyProjection { Date = row.BusinessDate, Clean = row.Clean })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0) return 0;
        var expected = DateOnly.FromDateTime(clock.UtcNow);
        var count = 0;
        foreach (var row in rows)
        {
            if (row.Date != expected || !row.Clean) break;
            count++;
            expected = expected.AddDays(-1);
        }
        return count;
    }

    private async Task<ErpChangeDivergenceRow> ResolveAsync(Guid runId, Guid divergenceId,
        string resolution, string? note, Guid? commandId, string actor, CancellationToken cancellationToken)
    {
        var row = await OpenAsync(runId, divergenceId, cancellationToken);
        row.Status = ErpChangeDivergenceStatus.Resolved;
        row.Resolution = resolution;
        row.ResolutionNote = note;
        row.ResolutionCommandId = commandId;
        row.ResolvedBy = actor;
        row.ResolvedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return View(row, await NamesAsync([actor], cancellationToken));
    }

    private async Task<ErpChangeDivergence> OpenAsync(Guid runId, Guid divergenceId,
        CancellationToken cancellationToken)
    {
        _ = await ActiveRunAsync(runId, cancellationToken);
        var row = await db.ErpChangeDivergences.SingleOrDefaultAsync(value => value.TenantId == tenant.TenantId
            && value.ErpChangeRunId == runId && value.ErpChangeDivergenceId == divergenceId,
            cancellationToken) ?? throw new ErpChangeRuleException("erp_change_divergence_not_found",
            "The divergence was not found.");
        if (row.Status != ErpChangeDivergenceStatus.Open)
            throw new ErpChangeRuleException("erp_change_divergence_resolved", "The divergence is already resolved.");
        return row;
    }

    private async Task RequireKindAsync(Guid runId, Guid divergenceId, string expectedKind,
        CancellationToken cancellationToken)
    {
        var kind = await db.ErpChangeDivergences.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ErpChangeRunId == runId && row.ErpChangeDivergenceId == divergenceId)
            .Select(row => row.Kind).SingleOrDefaultAsync(cancellationToken)
            ?? throw new ErpChangeRuleException("erp_change_divergence_not_found", "The divergence was not found.");
        if (kind != expectedKind)
            throw new ErpChangeRuleException("erp_change_divergence_role_mismatch",
                expectedKind == ErpChangeDivergenceKind.Stock
                    ? "This action is only for stock divergences."
                    : "Stock divergences must be decided by the inventory controller.");
    }

    private async Task<ErpChangeRun> ActiveRunAsync(Guid runId, CancellationToken cancellationToken) =>
        await db.ErpChangeRuns.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId && row.Status == ErpChangeRunStatus.Active, cancellationToken)
        ?? throw new ErpChangeRuleException("erp_change_run_not_found", "The active ERP change was not found.");

    private async Task<ErpChangeRun> RunAsync(Guid runId, CancellationToken cancellationToken) =>
        await db.ErpChangeRuns.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.ErpChangeRunId == runId, cancellationToken)
        ?? throw new ErpChangeRuleException("erp_change_run_not_found", "The ERP change was not found.");

    private async Task<ConnectionGatewaySetting> SettingsAsync(Guid connectionId,
        CancellationToken cancellationToken) => await db.ConnectionGatewaySettings.SingleAsync(row =>
        row.TenantId == tenant.TenantId && row.ConnectionId == connectionId, cancellationToken);

    private async Task<Dictionary<string, string>> NamesAsync(IEnumerable<string?> actors,
        CancellationToken cancellationToken)
    {
        var refs = actors.OfType<string>().Distinct().ToList();
        return refs.Count == 0 ? [] : new(await people.DisplayNamesAsync(refs, cancellationToken));
    }

    private static ErpChangeDivergenceRow View(ErpChangeDivergence row,
        IReadOnlyDictionary<string, string> names) => new(row.ErpChangeDivergenceId, row.Kind,
        row.SubjectType, row.SubjectId, row.SubjectRef, row.SourceValue, row.TargetValue,
        row.FirstSeenAt, row.LastSeenAt, row.ReviewDueAt, row.Status, row.Resolution,
        row.ResolutionNote, row.ResolutionCommandId,
        row.ResolvedBy is null ? null : names.GetValueOrDefault(row.ResolvedBy) ?? "Former or system user",
        row.ResolvedAt);

    private static string Encode(Cursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(cursor)));
    private static Cursor? Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String(value))); }
        catch (Exception exception) when (exception is FormatException or JsonException)
        { throw new ErpChangeRuleException("erp_change_cursor_invalid", "The page cursor is invalid."); }
    }

    private sealed record Cursor(DateTime At, Guid Id);
    private sealed class DailyProjection
    {
        public DateOnly Date { get; init; }
        public bool Clean { get; init; }
    }
}
