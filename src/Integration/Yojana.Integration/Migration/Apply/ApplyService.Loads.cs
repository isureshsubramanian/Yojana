using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Apply;

public sealed record LoadReason(string Code, string Message);
public sealed record LoadAction(bool Allowed, IReadOnlyList<LoadReason> Reasons);
public sealed record DeltaLoadAction(bool Allowed, IReadOnlyList<LoadReason> Reasons, Guid? BaseLoadId, string? BaseLabel);
public sealed record LoadReadiness(LoadAction Rehearsal, LoadAction Final, DeltaLoadAction Delta);
public sealed record LoadUndo(bool Allowed, string? Reason);
public sealed record LoadHandOver(Guid ConnectionId, string ConnectionName, DateTime? From, string SyncState, string? Note);
public sealed record MigrationLoadSummary(Guid LoadId, string Label, string Kind, string Status, Guid BatchId,
    string BatchDisplayNumber, Guid? BaseLoadId, string? BaseLabel, string RequestedByName, DateTime RequestedAt,
    DateTime? StartedAt, DateTime? FinishedAt, long Applied, long Refused, long AlreadyInModule, long CoverReplaced,
    long CoverRefused, long OwnerDiffers, long GoneFromSource, long EventsWritten, long EntitlementsWritten,
    long ReplacementLinksWritten, DateTime? RolledBackAt, string? RolledBackByName, LoadUndo Undo,
    LoadHandOver? HandOver, DateTime? BatchStartedAt = null, DateTime? BatchFinishedAt = null);
public sealed record MigrationLoadSummaryPage(IReadOnlyList<MigrationLoadSummary> Items, string? NextCursor);

/// <summary>
/// The console's view of loads (P9-T34, OD-59 to OD-63): which load actions a batch allows and why not, every load of a
/// source with its label (R1, F1, D1 by kind in request order) and whether it can be undone, and the final load's
/// connector hand-over. The enqueue methods and rollback ask the same rules, so the screen never offers what the
/// server refuses.
/// </summary>
public sealed partial class ApplyService
{
    public const string RehearsalKind = "rehearsal";
    public const string FinalKind = "final";

    /// <summary>The kind the console shows: an initial load is a rehearsal or the final load; a delta is a delta.</summary>
    private static string ViewKind(MigrationApplyRun run) =>
        run.Kind == ApplyRunKind.Delta ? ApplyRunKind.Delta : run.Rehearsal ? RehearsalKind : FinalKind;

    public async Task<LoadReadiness> ReadinessAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.MigrationBatches.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batchId, cancellationToken)
            ?? throw new ApplyRuleException("migration_batch_not_found", "The batch does not exist.");
        var map = await LatestMapAsync(batchId, cancellationToken);
        var runs = await db.Set<MigrationApplyRun>().AsNoTracking().Where(row => row.TenantId == tenant.TenantId)
            .Select(row => new { row.RunId, row.BatchId, row.Status, row.Kind, row.Rehearsal, row.RequestedAt })
            .ToListAsync(cancellationToken);
        var labels = await LabelsAsync(batch.ConnectionId, cancellationToken);
        var mapReason = map is null
            ? new LoadReason("migration_apply_map_not_found", "No completed load plan exists for this batch. Run mapping first.")
            : null;
        var busy = runs.FirstOrDefault(row => row.Status is ApplyRunStatus.Queued or ApplyRunStatus.Running);
        var busyReason = busy is null ? null : new LoadReason("migration_apply_load_open",
            $"Load {labels.GetValueOrDefault(busy.RunId, "")} is queued or running.".Replace("Load  is", "A load is"));
        // A failed initial load of this batch resumes under its own id (EnqueueAsync), so it does not block itself.
        var resumable = runs.Any(row => row.Status == ApplyRunStatus.Failed && row.Kind == ApplyRunKind.Initial &&
            row.BatchId == batchId);
        var standing = runs.Where(row => row.Status is ApplyRunStatus.Completed or ApplyRunStatus.Failed &&
            !(resumable && row.BatchId == batchId && row.Kind == ApplyRunKind.Initial)).ToList();
        var finalIn = standing.FirstOrDefault(row => row.Kind == ApplyRunKind.Initial && !row.Rehearsal);
        var rehearsalIn = standing.FirstOrDefault(row => row.Kind == ApplyRunKind.Initial && row.Rehearsal);

        var rehearsal = new List<LoadReason>();
        var final = new List<LoadReason>();
        if (mapReason is not null) { rehearsal.Add(mapReason); final.Add(mapReason); }
        if (busyReason is not null) { rehearsal.Add(busyReason); final.Add(busyReason); }
        else if (finalIn is not null)
        {
            var label = labels.GetValueOrDefault(finalIn.RunId, "The final load");
            rehearsal.Add(new("migration_apply_rehearsal_closed",
                $"{label} is in the module; rehearsals are closed. Undo it first to rehearse again."));
            final.Add(new("migration_apply_final_done",
                $"{label} is in the module. Later changes in the source go in as delta loads."));
        }
        else if (standing.Count > 0)
        {
            var label = rehearsalIn is null ? "An earlier load" : labels.GetValueOrDefault(rehearsalIn.RunId, "An earlier load");
            var reason = new LoadReason("migration_apply_load_open",
                $"{label} is still in the module. Roll it back before loading again.");
            rehearsal.Add(reason);
            final.Add(reason);
        }
        if (finalIn is null && map is not null && !await signoff.HasFinalSignoffAsync(map.RunId, cancellationToken))
            final.Add(new("migration_apply_signoff_required",
                "A final load needs the validation sign-off record of this load plan. Run a rehearsal load, or sign off validation first."));
        if (finalIn is null && await SourceConnectionRunningAsync(batchId, cancellationToken))
            final.Add(new("migration_apply_connection_running",
                $"Stop the sync on {await ConnectionNameAsync(batch.ConnectionId, cancellationToken)} first. The final load hands the connector the extraction time, and you resume the sync after it."));

        var delta = new List<LoadReason>();
        if (mapReason is not null) delta.Add(mapReason);
        else if (await db.MigrationPlanUnits.AnyAsync(row => row.TenantId == tenant.TenantId &&
                     row.MapRunId == map!.RunId && row.SourceKeyHash == null, cancellationToken))
            delta.Add(new("migration_delta_plan_without_source_keys",
                "This load plan was mapped before delta loads existed. Map the batch again."));
        if (busyReason is not null) delta.Add(busyReason);
        var deltaResumable = runs.Any(row => row.Status == ApplyRunStatus.Failed && row.Kind == ApplyRunKind.Delta &&
            row.BatchId == batchId);
        var baseLoad = deltaResumable ? null : await DeltaBaseAsync(batch.ConnectionId, cancellationToken);
        if (!deltaResumable)
        {
            if (baseLoad is null)
                delta.Add(new("migration_delta_base_missing", "A delta load needs a completed final load from this source."));
            else if (baseLoad.BatchId == batchId)
                delta.Add(new("migration_delta_same_batch",
                    "This batch is the one already loaded. Extract the source again for the delta."));
            else if (batch.RequestedAt <= baseLoad.BatchRequestedAt)
                delta.Add(new("migration_delta_batch_older",
                    "This batch is older than the one already loaded. Extract the source again for the delta."));
        }
        return new(new(rehearsal.Count == 0, rehearsal), new(final.Count == 0, final),
            new(delta.Count == 0, delta, baseLoad?.RunId, baseLoad is null ? null : labels.GetValueOrDefault(baseLoad.RunId)));
    }

    public async Task<MigrationLoadSummaryPage> LoadsAsync(Guid sourceId, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 100);
        // A source has a handful of loads (rehearsals, one final, a few deltas), so labels and undo state are worked
        // out over all of them and the page is cut afterwards; the keyset cursor is the last load id shown.
        var runs = await RunsOfSourceAsync(sourceId, cancellationToken);
        var ordered = runs.OrderByDescending(row => row.RequestedAt).ThenByDescending(row => row.RunId).ToList();
        var start = Guid.TryParse(cursor, out var after) ? ordered.FindIndex(row => row.RunId == after) + 1 : 0;
        var page = ordered.Skip(start).Take(take).ToList();
        var items = await SummariesAsync(sourceId, runs, page, cancellationToken);
        return new(items, start + take < ordered.Count ? page[^1].RunId.ToString("D") : null);
    }

    public async Task<MigrationLoadSummary?> LoadAsync(Guid loadId, CancellationToken cancellationToken)
    {
        var found = await (from run in db.Set<MigrationApplyRun>().AsNoTracking()
                           join batch in db.MigrationBatches.AsNoTracking()
                               on new { run.TenantId, run.BatchId } equals new { batch.TenantId, batch.BatchId }
                           where run.TenantId == tenant.TenantId && run.RunId == loadId
                           select new { batch.ConnectionId, batch.StartedAt, batch.FinishedAt })
            .SingleOrDefaultAsync(cancellationToken);
        if (found is null) return null;
        var runs = await RunsOfSourceAsync(found.ConnectionId, cancellationToken);
        var summary = (await SummariesAsync(found.ConnectionId, runs, runs.Where(row => row.RunId == loadId).ToList(),
            cancellationToken)).Single();
        return summary with { BatchStartedAt = found.StartedAt, BatchFinishedAt = found.FinishedAt };
    }

    /// <summary>
    /// Why a load cannot be undone, or null when it can. Rollback (<see cref="RollBackAsync"/>) and every view ask this,
    /// so the Undo button and the server agree. The touched rule (OD-53) is the module's and is only known on the attempt.
    /// </summary>
    private async Task<(string Code, string Message)?> UndoBlockedAsync(MigrationApplyRun run,
        CancellationToken cancellationToken)
    {
        if (run.Status == ApplyRunStatus.RolledBack)
            return ("migration_apply_not_rollbackable", "Already undone.");
        if (run.Status is not (ApplyRunStatus.Completed or ApplyRunStatus.Failed))
            return ("migration_apply_not_rollbackable", $"A load that is {run.Status} cannot be undone.");
        // Deltas stack: each one replaced what the one before it left, so the latest goes first (OD-62); a final load
        // is undone only once no delta built on it stands (OD-63).
        var later = await db.Set<MigrationApplyRun>().AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.Kind == ApplyRunKind.Delta && row.BaseLoadId == run.RunId && row.Status != ApplyRunStatus.RolledBack)
            .Select(row => new { row.RunId }).FirstOrDefaultAsync(cancellationToken);
        if (later is not null)
        {
            var labels = await LabelsOfRunAsync(run, cancellationToken);
            var label = labels.GetValueOrDefault(later.RunId, "a later delta load");
            return run.Kind == ApplyRunKind.Delta
                ? ("migration_delta_not_latest", $"A later delta load ({label}) was built on this one. Undo {label} first.")
                : ("migration_final_later_delta", $"A later delta load ({label}) stands. Undo it first.");
        }
        if (ViewKind(run) == FinalKind && await SourceConnectionRunningAsync(run.BatchId, cancellationToken))
        {
            var connectionId = await db.MigrationBatches.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.BatchId == run.BatchId).Select(row => row.ConnectionId).SingleAsync(cancellationToken);
            return ("migration_apply_connection_running",
                $"Stop the sync on {await ConnectionNameAsync(connectionId, cancellationToken)} first.");
        }
        return null;
    }

    private async Task<MigrationMapRun?> LatestMapAsync(Guid batchId, CancellationToken cancellationToken) =>
        await db.MigrationMapRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId && row.Status == MappingRunStatus.Completed)
            .OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);

    private sealed record DeltaBase(Guid RunId, Guid BatchId, DateTime BatchRequestedAt);

    /// <summary>The load a new delta builds on: the latest completed non-rehearsal load (final or delta) of the source.</summary>
    private async Task<DeltaBase?> DeltaBaseAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await (from run in db.Set<MigrationApplyRun>().AsNoTracking()
               join earlier in db.MigrationBatches.AsNoTracking()
                   on new { run.TenantId, run.BatchId } equals new { earlier.TenantId, earlier.BatchId }
               where run.TenantId == tenant.TenantId && earlier.ConnectionId == connectionId && !run.Rehearsal &&
                     run.Status == ApplyRunStatus.Completed
               orderby run.FinishedAt descending, run.RunId descending
               select new DeltaBase(run.RunId, run.BatchId, earlier.RequestedAt))
            .FirstOrDefaultAsync(cancellationToken);

    private async Task<string> ConnectionNameAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
            row.ConnectionId == connectionId).Select(row => row.DisplayLabel).SingleOrDefaultAsync(cancellationToken)
        ?? "the source connection";

    private async Task<List<MigrationApplyRun>> RunsOfSourceAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await (from run in db.Set<MigrationApplyRun>().AsNoTracking()
               join batch in db.MigrationBatches.AsNoTracking()
                   on new { run.TenantId, run.BatchId } equals new { batch.TenantId, batch.BatchId }
               where run.TenantId == tenant.TenantId && batch.ConnectionId == connectionId
               select run).ToListAsync(cancellationToken);

    private static Dictionary<Guid, string> Labels(IEnumerable<MigrationApplyRun> runs) =>
        runs.GroupBy(ViewKind).SelectMany(group => group.OrderBy(row => row.RequestedAt).ThenBy(row => row.RunId)
                .Select((row, index) => (row.RunId, Label: $"{group.Key switch
                {
                    RehearsalKind => "R",
                    FinalKind => "F",
                    _ => "D",
                }}{index + 1}")))
            .ToDictionary(row => row.RunId, row => row.Label);

    private async Task<Dictionary<Guid, string>> LabelsAsync(Guid connectionId, CancellationToken cancellationToken) =>
        Labels(await RunsOfSourceAsync(connectionId, cancellationToken));

    private async Task<Dictionary<Guid, string>> LabelsOfRunAsync(MigrationApplyRun run, CancellationToken cancellationToken)
    {
        var connectionId = await db.MigrationBatches.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
            row.BatchId == run.BatchId).Select(row => row.ConnectionId).SingleAsync(cancellationToken);
        return await LabelsAsync(connectionId, cancellationToken);
    }

    private async Task<List<MigrationLoadSummary>> SummariesAsync(Guid connectionId, List<MigrationApplyRun> all,
        List<MigrationApplyRun> page, CancellationToken cancellationToken)
    {
        var labels = Labels(all);
        var batchIds = page.Select(row => row.BatchId).Distinct().ToList();
        var batches = await db.MigrationBatches.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                batchIds.Contains(row.BatchId)).Select(row => new { row.BatchId, row.DisplayNumber })
            .ToDictionaryAsync(row => row.BatchId, row => $"MB-{row.DisplayNumber:0000}", cancellationToken);
        var actors = page.Select(row => row.RequestedBy).Concat(page.Where(row => row.RolledBackBy != null)
            .Select(row => row.RolledBackBy!)).Distinct().ToList();
        var names = await people.DisplayNamesAsync(actors, cancellationToken);
        var items = new List<MigrationLoadSummary>(page.Count);
        foreach (var run in page)
        {
            var blocked = await UndoBlockedAsync(run, cancellationToken);
            items.Add(new(run.RunId, labels[run.RunId], ViewKind(run), run.Status, run.BatchId,
                batches.GetValueOrDefault(run.BatchId) ?? "", run.BaseLoadId,
                run.BaseLoadId is { } baseId ? labels.GetValueOrDefault(baseId) : null,
                names.GetValueOrDefault(run.RequestedBy) ?? "Unknown user", run.RequestedAt, run.StartedAt,
                run.FinishedAt, run.Applied, run.Refused, run.AlreadyInModule, run.CoverReplaced, run.CoverRefused,
                run.OwnerDiffers, run.GoneFromSource, run.EventsWritten, run.EntitlementsWritten,
                run.ReplacementLinksWritten, run.RolledBackAt,
                run.RolledBackBy is null ? null : names.GetValueOrDefault(run.RolledBackBy) ?? "Unknown user",
                new(blocked is null, blocked?.Message), await HandOverAsync(run, connectionId, cancellationToken)));
        }
        return items;
    }

    /// <summary>The final load's connector hand-over (OD-60) with the connection's state now; null for other loads.</summary>
    private async Task<LoadHandOver?> HandOverAsync(MigrationApplyRun run, Guid connectionId,
        CancellationToken cancellationToken)
    {
        if (ViewKind(run) != FinalKind || run.Status != ApplyRunStatus.Completed) return null;
        var connection = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.ConnectionId == connectionId).Select(row => new { row.DisplayLabel, row.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (connection is null) return null;
        var state = connection.Status == ConnectionStatus.Active ? "running" : "stopped";
        return new(connectionId, connection.DisplayLabel, run.WatermarkHandedOverFrom, state, run.WatermarkNote);
    }

    /// <summary>
    /// Every poll watermark of the source connection as it stood before the final load moved it (OD-63): undoing the
    /// final load puts them back, so the connector does not keep a starting point for units that are no longer there.
    /// </summary>
    private sealed record SavedWatermark(string EntityType, string? Watermark, DateTime NextPollAt);

    private static string SaveWatermarks(IEnumerable<ConnectionPollWatermark> states) => JsonSerializer.Serialize(
        states.Select(row => new SavedWatermark(row.EntityType, row.Watermark, row.NextPollAt)).ToList());

    private async Task RestoreWatermarksAsync(MigrationApplyRun run, Guid connectionId,
        CancellationToken cancellationToken)
    {
        if (run.WatermarksBefore is null) return;
        var saved = JsonSerializer.Deserialize<List<SavedWatermark>>(run.WatermarksBefore) ?? [];
        var states = await db.ConnectionPollWatermarks.Where(row => row.TenantId == tenant.TenantId &&
            row.ConnectionId == connectionId).ToListAsync(cancellationToken);
        foreach (var state in states)
        {
            var before = saved.FirstOrDefault(row => row.EntityType == state.EntityType);
            if (before is null) continue;
            state.Watermark = before.Watermark;
            state.NextPollAt = before.NextPollAt;
            state.Revision++;
        }
    }
}
