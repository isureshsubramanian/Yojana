using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Serialisation.Contracts;
using Yojana.Platform.Contracts.Paging;

namespace Yojana.Integration.LegacyMigration.Apply;

public sealed record MigrationDeltaKindCount(string Kind, string Label, long Count);
public sealed record MigrationDeltaItemView(Guid ItemId, string Kind, string KindLabel, string? ItemCode, string? Serial,
    string? Detail, Guid? UnitId, Guid? ConflictId = null);
public sealed record MigrationDeltaItemPage(IReadOnlyList<MigrationDeltaKindCount> Counts, long Total,
    IReadOnlyList<MigrationDeltaItemView> Items, string? NextCursor, long LoadTotal = 0);

/// <summary>
/// The delta load at cutover (P9-T10b, OD-59 to OD-62). After the final load, movements reach the module only through
/// the connector, which the final load started at the extraction time; the delta never replays them. A tenant
/// administrator starts it by hand on a newer extraction of the same source (OD-59). It carries what the connector
/// cannot: serials new in the source are loaded as migrated units; units whose reviewed cover changed get their migrated
/// cover replaced (refused and listed when the module has facts of its own on the unit); an owner change with no
/// delivery behind it is listed and raised as a coexistence conflict, never written; serials gone from the source are
/// listed, never removed. A delta is undone on its own (OD-62), latest first.
/// </summary>
public sealed partial class ApplyService
{
    public const string OwnerFieldName = "owner";
    public const string MigratedUnitRecordType = "migrated_unit";
    public async Task<StartApplyResponse> EnqueueDeltaAsync(Guid batchId, string actor,
        CancellationToken cancellationToken)
    {
        var readiness = await ReadinessAsync(batchId, cancellationToken);
        if (!readiness.Delta.Allowed)
            throw new ApplyRuleException(readiness.Delta.Reasons[0].Code, readiness.Delta.Reasons[0].Message);
        var batch = await db.MigrationBatches.AsNoTracking().SingleOrDefaultAsync(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batchId, cancellationToken)
            ?? throw new ApplyRuleException("migration_batch_not_found", "The batch does not exist.");
        var map = await db.MigrationMapRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId && row.Status == MappingRunStatus.Completed)
            .OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ApplyRuleException("migration_apply_map_not_found",
                "No completed load plan exists for this batch. Run mapping first.");
        if (await db.MigrationPlanUnits.AnyAsync(row => row.TenantId == tenant.TenantId && row.MapRunId == map.RunId &&
                row.SourceKeyHash == null, cancellationToken))
            throw new ApplyRuleException("migration_delta_plan_without_source_keys",
                "This load plan was mapped before delta loads existed. Map the batch again.");
        var runs = db.Set<MigrationApplyRun>().AsNoTracking().Where(row => row.TenantId == tenant.TenantId);
        if (await runs.AnyAsync(row => row.Status == ApplyRunStatus.Queued || row.Status == ApplyRunStatus.Running,
                cancellationToken))
            throw new ApplyRuleException("migration_apply_load_open", "A load is already queued or running.");
        var failed = await db.Set<MigrationApplyRun>().Where(row => row.TenantId == tenant.TenantId &&
                row.Kind == ApplyRunKind.Delta && row.BatchId == batchId && row.Status == ApplyRunStatus.Failed)
            .FirstOrDefaultAsync(cancellationToken);
        if (failed is not null)
        {
            // A failed delta resumes from its cursor under the same load id, like a failed load.
            failed.Status = ApplyRunStatus.Queued;
            failed.FailureCode = null;
            failed.FinishedAt = null;
            await db.SaveChangesAsync(cancellationToken);
            return new(failed.RunId, batchId, failed.MapRunId, false, failed.Status);
        }
        var baseLoad = await (from run in runs
                              join earlier in db.MigrationBatches.AsNoTracking()
                                  on new { run.TenantId, run.BatchId } equals new { earlier.TenantId, earlier.BatchId }
                              where earlier.ConnectionId == batch.ConnectionId && !run.Rehearsal &&
                                    run.Status == ApplyRunStatus.Completed
                              orderby run.FinishedAt descending, run.RunId descending
                              select new { run.RunId, run.BatchId, BatchRequestedAt = earlier.RequestedAt })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ApplyRuleException("migration_delta_base_missing",
                "A delta load needs a completed final load from this source.");
        if (baseLoad.BatchId == batchId)
            throw new ApplyRuleException("migration_delta_same_batch",
                "This batch is the one already loaded. Extract the source again for the delta.");
        if (batch.RequestedAt <= baseLoad.BatchRequestedAt)
            throw new ApplyRuleException("migration_delta_batch_older",
                "This batch is older than the one already loaded. Extract the source again for the delta.");
        var now = clock.UtcNow;
        var delta = new MigrationApplyRun
        {
            TenantId = tenant.TenantId, RunId = Guid.CreateVersion7(now), MapRunId = map.RunId, BatchId = batchId,
            Rehearsal = false, Kind = ApplyRunKind.Delta, BaseLoadId = baseLoad.RunId, Status = ApplyRunStatus.Queued,
            RequestedBy = actor, RequestedAt = now, UnitsInPlan = map.UnitCount,
        };
        db.Set<MigrationApplyRun>().Add(delta);
        await db.SaveChangesAsync(cancellationToken);
        return new(delta.RunId, batchId, map.RunId, false, delta.Status);
    }

    public async Task<MigrationDeltaItemPage> DeltaItemsAsync(Guid loadId, string? kind, string? itemCode,
        string? serial, string? sort, string? direction, string? cursor, int? take,
        CancellationToken cancellationToken)
    {
        var all = DeltaFindings(loadId);
        var counts = (await all.GroupBy(row => row.Kind).Select(group => new { group.Key, Count = group.LongCount() })
                .ToListAsync(cancellationToken))
            .OrderBy(row => Array.IndexOf(DeltaItemKind.All.ToArray(), row.Key))
            .Select(row => new MigrationDeltaKindCount(row.Key, DeltaItemKind.Label(row.Key), row.Count)).ToList();
        var filtered = FilterDeltaFindings(all, kind, itemCode, serial);
        var total = await filtered.LongCountAsync(cancellationToken);
        var paging = MigrationEndpoints.DeltaPages.Parse(sort, direction, take, cursor, defaultTake: 50);
        var rows = await paging.Apply(filtered).Take(paging.Take + 1).ToListAsync(cancellationToken);
        var pageRows = rows.Take(paging.Take).ToList();
        var items = pageRows.Select(DeltaView).ToList();
        return new(counts, total, items, rows.Count > paging.Take ? paging.CursorFor(pageRows[^1]) : null,
            counts.Sum(row => row.Count));
    }

    public Task<MigrationDeltaItemPage> DeltaItemsAsync(Guid loadId, string? kind, string? itemCode,
        string? serial, string? cursor, int take, CancellationToken cancellationToken) =>
        DeltaItemsAsync(loadId, kind, itemCode, serial, null, null, cursor, take, cancellationToken);

    /// <summary>The filtered findings as a table for the CSV and Excel exports (OD-63), in the order the page shows.</summary>
    public TabularExport ExportDeltaItems(Guid loadId, string? kind, string? itemCode, string? serial,
        string? sort, string? direction)
    {
        var filtered = FilterDeltaFindings(DeltaFindings(loadId), kind, itemCode, serial);
        return new(["Serial", "Item", "Kind", "What happened"], Rows());

        async IAsyncEnumerable<IReadOnlyList<string?>> Rows()
        {
            string? cursor = null;
            while (true)
            {
                var paging = MigrationEndpoints.DeltaPages.Parse(sort, direction,
                    SortedPage<MigrationDeltaItem, Guid>.MaximumTake, cursor);
                var page = await paging.Apply(filtered).Take(paging.Take).ToListAsync();
                foreach (var row in page) yield return [row.Serial, row.ItemCode, DeltaItemKind.Label(row.Kind), row.Detail];
                if (page.Count < paging.Take) yield break;
                cursor = paging.CursorFor(page[^1]);
            }
        }
    }

    public TabularExport ExportDeltaItems(Guid loadId, string? kind, string? itemCode, string? serial) =>
        ExportDeltaItems(loadId, kind, itemCode, serial, null, null);

    private IQueryable<MigrationDeltaItem> DeltaFindings(Guid loadId) => db.Set<MigrationDeltaItem>().AsNoTracking()
        .Where(row => row.TenantId == tenant.TenantId && row.RunId == loadId);

    private static IQueryable<MigrationDeltaItem> FilterDeltaFindings(IQueryable<MigrationDeltaItem> rows, string? kind,
        string? itemCode, string? serial)
    {
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!DeltaItemKind.All.Contains(kind))
                throw new ApplyRuleException("migration_delta_kind_unknown", "Unknown delta finding kind.");
            rows = rows.Where(row => row.Kind == kind);
        }
        // Item code is an exact match, as on the refused-units view; the serial is a contains search.
        if (!string.IsNullOrWhiteSpace(itemCode)) rows = rows.Where(row => row.ItemCode == itemCode.Trim());
        if (!string.IsNullOrWhiteSpace(serial)) rows = rows.Where(row => row.Serial != null && row.Serial.Contains(serial.Trim()));
        return rows;
    }

    private static MigrationDeltaItemView DeltaView(MigrationDeltaItem row) => new(row.ItemId, row.Kind,
        DeltaItemKind.Label(row.Kind), row.ItemCode, row.Serial, row.Detail, row.UnitId, row.ConflictId);

    private async Task ApplyDeltaAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await Run(runId, cancellationToken);
        var batch = await db.MigrationBatches.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.BatchId == run.BatchId, cancellationToken);
        var map = await db.MigrationMapRuns.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == run.MapRunId, cancellationToken);
        var baseRun = await Run(run.BaseLoadId!.Value, cancellationToken);
        var baseBatch = await db.MigrationBatches.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.BatchId == baseRun.BatchId, cancellationToken);
        // Shipments from this day on came through the connector, which the final load started at its extraction.
        var connectorFrom = DateOnly.FromDateTime(baseBatch.SourceClockAtStart ?? baseBatch.StartedAt ?? baseBatch.RequestedAt);
        var legalEntity = await db.ConnectionGatewaySettings.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ConnectionId == batch.ConnectionId)
            .Select(row => (Guid?)row.LegalEntityId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new ApplyRuleException("migration_apply_connection_unconfigured",
                "The source connection has no gateway settings (legal entity).");
        var cutover = DateTime.SpecifyKind(map.CutoverDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        var source = $"migration:{batch.SourceSystem}:{batch.BatchId:N}";
        while (!cancellationToken.IsCancellationRequested)
        {
            var cursor = run.CursorPlanUnitId;
            var plans = await db.MigrationPlanUnits.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                    row.MapRunId == run.MapRunId && (cursor == null || row.PlanUnitId.CompareTo(cursor.Value) > 0))
                .OrderBy(row => row.PlanUnitId).Take(ChunkSize).ToListAsync(cancellationToken);
            if (plans.Count == 0) break;
            var hashes = plans.Select(row => row.SourceKeyHash!).ToList();
            var loaded = await db.Set<MigrationLoadedUnit>().Where(row => row.TenantId == tenant.TenantId &&
                    row.ConnectionId == batch.ConnectionId && hashes.Contains(row.SourceKeyHash))
                .ToDictionaryAsync(row => row.SourceKeyHash, StringComparer.Ordinal, cancellationToken);
            var (units, missing, overridesUsed) = await BuildChunkAsync(plans, batch.ConnectionId, cutover, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            var plansById = plans.ToDictionary(row => row.PlanUnitId);
            var serials = await SerialsAsync(plans.Select(row => row.PlanUnitId).ToList(), cancellationToken);
            var items = new List<MigrationDeltaItem>();
            MigrationDeltaItem Item(string kind, MigrationPlanUnit plan, Guid? unitId, string? detail) => new()
            {
                TenantId = tenant.TenantId, ItemId = Guid.CreateVersion7(clock.UtcNow), RunId = runId, Kind = kind,
                SourceKey = plan.SourceKey!, PlanUnitId = plan.PlanUnitId, UnitId = unitId, ItemCode = plan.ItemCode,
                Serial = serials.GetValueOrDefault(plan.PlanUnitId), Detail = detail,
            };

            // (a) Serials new in the source: written as migrated units, exactly as a load writes them.
            var fresh = units.Where(unit => !loaded.ContainsKey(plansById[unit.PlanUnitId].SourceKeyHash!)).ToList();
            var freshPlanIds = fresh.Select(unit => unit.PlanUnitId).ToHashSet();
            var applied = fresh.Count == 0
                ? new ApplyMigratedUnitsResult([], 0, 0, 0, 0)
                : await bus.InvokeAsync<ApplyMigratedUnitsResult>(
                    new ApplyMigratedUnits(runId, legalEntity, run.RequestedBy, source, fresh, map.CutoverDate),
                    Options(), cancellationToken);
            run = await db.Set<MigrationApplyRun>().SingleAsync(row => row.TenantId == tenant.TenantId &&
                row.RunId == runId, cancellationToken);
            await RememberLoadedUnitsAsync(batch.ConnectionId, runId, plans, fresh, applied.Units, cancellationToken);
            var refusals = missing.Where(row => !loaded.ContainsKey(plansById[row.PlanUnitId].SourceKeyHash!)).ToList();
            foreach (var outcome in applied.Units)
            {
                var plan = plansById[outcome.PlanUnitId];
                if (outcome.Outcome is MigratedUnitOutcome.Applied or MigratedUnitOutcome.AlreadyApplied)
                    items.Add(Item(DeltaItemKind.NewUnit, plan, outcome.UnitId, null));
                else if (outcome.Reasons.Count > 0 && outcome.Reasons.All(IsOpenOnAnotherUnit))
                {
                    // The connector met this serial on a document after the final load and the module holds it already.
                    items.Add(Item(DeltaItemKind.AlreadyInModule, plan, null, string.Join(' ', outcome.Reasons)));
                    run.AlreadyInModule++;
                }
                else refusals.Add((outcome.PlanUnitId, outcome.Reasons));
            }
            await RecordRefusalsAsync(run, refusals, cancellationToken);

            // (b) Cover changed in the source on a unit already loaded: replace the migrated cover (OD-61 b, e).
            var changes = new List<(MigratedUnit Unit, MigrationLoadedUnit Loaded, string Hash)>();
            foreach (var unit in units.Where(unit => !freshPlanIds.Contains(unit.PlanUnitId)))
            {
                var row = loaded[plansById[unit.PlanUnitId].SourceKeyHash!];
                var hash = MigrationCoverFingerprint.Of(unit.Entitlements);
                if (!string.Equals(hash, row.CoverHash, StringComparison.Ordinal)) changes.Add((unit, row, hash));
            }
            if (changes.Count > 0)
            {
                var replaced = await bus.InvokeAsync<ReplaceMigratedCoverResult>(new ReplaceMigratedCover(runId,
                    legalEntity, run.RequestedBy, changes.Select(change => new MigratedCoverChange(change.Loaded.UnitId,
                        change.Unit.PlanUnitId, change.Unit.Entitlements ?? [])).ToList(), map.CutoverDate),
                    Options(), cancellationToken);
                var byUnit = replaced.Units.ToDictionary(row => row.UnitId);
                foreach (var (unit, row, hash) in changes)
                {
                    var plan = plansById[unit.PlanUnitId];
                    var outcome = byUnit[row.UnitId];
                    if (outcome.Outcome is MigratedCoverOutcome.Replaced or MigratedCoverOutcome.AlreadyReplaced)
                    {
                        var item = Item(DeltaItemKind.CoverReplaced, plan, row.UnitId, null);
                        item.PreviousCoverHash = row.CoverHash;
                        item.PreviousCoverLoadId = row.CoverLoadId;
                        items.Add(item);
                        row.CoverHash = hash;
                        row.CoverLoadId = runId;
                        run.CoverReplaced++;
                    }
                    else
                    {
                        // A touched unit comes back with its outcome alone; the finding names it so the list reads on its own.
                        items.Add(Item(DeltaItemKind.CoverRefused, plan, row.UnitId,
                            string.Join(' ', outcome.Reasons.Count == 0 ? [outcome.Outcome] : outcome.Reasons)));
                        run.CoverRefused++;
                    }
                }
                run.EventsWritten += replaced.EventsWritten;
                run.EntitlementsWritten += replaced.EntitlementsWritten;
            }

            // (c) Owner differs with no delivery since the final load's extraction: listed for a person, never written.
            var known = plans.Where(plan => loaded.ContainsKey(plan.SourceKeyHash!)).Select(plan => plan.PlanUnitId).ToList();
            if (known.Count > 0)
            {
                var owners = await CurrentOwnerCodesAsync(known, cancellationToken);
                var shippedSince = (await db.MigrationPlanEvents.AsNoTracking().Where(row =>
                        row.TenantId == tenant.TenantId && known.Contains(row.PlanUnitId) &&
                        row.MovementKind == HistoryMovementKind.Shipped)
                    .Select(row => new { row.PlanUnitId, row.EffectiveDate }).ToListAsync(cancellationToken))
                    .Where(row => DateOnly.FromDateTime(row.EffectiveDate) >= connectorFrom)
                    .Select(row => row.PlanUnitId).ToHashSet();
                foreach (var planUnitId in known)
                {
                    var plan = plansById[planUnitId];
                    var row = loaded[plan.SourceKeyHash!];
                    var owner = owners.GetValueOrDefault(planUnitId);
                    if (string.Equals(owner, row.OwnerPartyCode, StringComparison.Ordinal) || shippedSince.Contains(planUnitId))
                        continue;
                    var finding = Item(DeltaItemKind.OwnerDiffers, plan, row.UnitId,
                        $"Loaded owner {row.OwnerPartyCode ?? "(none)"}; the source now names {owner ?? "(none)"}.");
                    finding.ConflictId = await RaiseOwnerConflictAsync(batch.ConnectionId, plan,
                        serials.GetValueOrDefault(planUnitId), row.OwnerPartyCode, owner, cancellationToken);
                    items.Add(finding);
                    run.OwnerDiffers++;
                }
            }

            db.Set<MigrationDeltaItem>().AddRange(items);
            run.Applied += applied.Applied;
            run.AlreadyApplied += applied.AlreadyApplied;
            run.EventsWritten += applied.EventsWritten;
            run.EntitlementsWritten += applied.EntitlementsWritten;
            run.ReplacementLinksWritten += applied.ReplacementLinksWritten;
            run.ReplacementLinksUsingOverride += overridesUsed;
            run.CertificatesRecorded += applied.CertificatesRecorded;
            run.CursorPlanUnitId = plans[^1].PlanUnitId;
            // The register, the findings and the cursor commit together: a chunk resent after a failure finds the
            // module side already written (already applied / already replaced) and records it once.
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            run = await Run(runId, cancellationToken);
        }

        // (d) Serials an earlier load wrote that this extraction no longer has: listed, never removed (OD-61 d).
        run = await db.Set<MigrationApplyRun>().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId, cancellationToken);
        await db.Set<MigrationDeltaItem>().Where(row => row.TenantId == tenant.TenantId && row.RunId == runId &&
            row.Kind == DeltaItemKind.GoneFromSource).ExecuteDeleteAsync(cancellationToken);
        run.GoneFromSource = 0;
        string? after = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var page = await db.Set<MigrationLoadedUnit>().AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                    row.ConnectionId == batch.ConnectionId && row.LoadId != runId &&
                    (after == null || row.SourceKeyHash.CompareTo(after) > 0) &&
                    !db.MigrationPlanUnits.Any(plan => plan.TenantId == row.TenantId && plan.MapRunId == run.MapRunId &&
                        plan.SourceKeyHash == row.SourceKeyHash))
                .OrderBy(row => row.SourceKeyHash).Take(ChunkSize).ToListAsync(cancellationToken);
            if (page.Count == 0) break;
            db.Set<MigrationDeltaItem>().AddRange(page.Select(row => new MigrationDeltaItem
            {
                TenantId = tenant.TenantId, ItemId = Guid.CreateVersion7(clock.UtcNow), RunId = runId,
                Kind = DeltaItemKind.GoneFromSource, SourceKey = row.SourceKey, UnitId = row.UnitId,
                Detail = "Not in this extraction; the unit stays in the module.",
            }));
            run.GoneFromSource += page.Count;
            after = page[^1].SourceKeyHash;
        }
        await db.SaveChangesAsync(cancellationToken);
        await FinishAsync(runId, cancellationToken);
    }

    private async Task RecordRefusalsAsync(MigrationApplyRun run,
        List<(Guid PlanUnitId, IReadOnlyList<string> Reasons)> refusals, CancellationToken cancellationToken)
    {
        var known = refusals.Select(r => r.PlanUnitId).ToList();
        var already = await db.Set<MigrationApplyRefusal>().Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == run.RunId && known.Contains(row.PlanUnitId)).Select(row => row.PlanUnitId)
            .ToListAsync(cancellationToken);
        foreach (var (planUnitId, reasons) in refusals.Where(r => !already.Contains(r.PlanUnitId)))
        {
            db.Set<MigrationApplyRefusal>().Add(new MigrationApplyRefusal
            {
                TenantId = tenant.TenantId, RefusalId = Guid.CreateVersion7(clock.UtcNow), RunId = run.RunId,
                PlanUnitId = planUnitId, Reasons = string.Join(' ', reasons),
            });
        }
        run.Refused += refusals.Count - already.Count;
    }

    private static bool IsOpenOnAnotherUnit(string reason) =>
        reason.EndsWith(":already_open_on_another_unit", StringComparison.Ordinal);

    /// <summary>
    /// Every load records the module unit each serial-master source key became, with the cover fingerprint and owner it
    /// wrote, so a later delta can find and compare them. Applied and already-applied outcomes both count: a resent chunk
    /// records what the first attempt wrote.
    /// </summary>
    private async Task RememberLoadedUnitsAsync(Guid connectionId, Guid loadId, List<MigrationPlanUnit> plans,
        List<MigratedUnit> sent, IReadOnlyList<MigratedUnitOutcome> outcomes, CancellationToken cancellationToken)
    {
        var written = outcomes.Where(row => row.UnitId is not null &&
            row.Outcome is MigratedUnitOutcome.Applied or MigratedUnitOutcome.AlreadyApplied).ToList();
        if (written.Count == 0) return;
        var plansById = plans.ToDictionary(row => row.PlanUnitId);
        var sentById = sent.ToDictionary(row => row.PlanUnitId);
        var owners = await CurrentOwnerCodesAsync(written.Select(row => row.PlanUnitId).ToList(), cancellationToken);
        var hashes = written.Select(row => plansById[row.PlanUnitId].SourceKeyHash).OfType<string>().ToList();
        var existing = await db.Set<MigrationLoadedUnit>().Where(row => row.TenantId == tenant.TenantId &&
                row.ConnectionId == connectionId && hashes.Contains(row.SourceKeyHash))
            .Select(row => row.SourceKeyHash).ToListAsync(cancellationToken);
        foreach (var outcome in written)
        {
            var plan = plansById[outcome.PlanUnitId];
            // Plans mapped before P9-T10b carry no source key; their units cannot take part in a delta.
            if (plan.SourceKeyHash is null || plan.SourceKey is null || existing.Contains(plan.SourceKeyHash)) continue;
            db.Set<MigrationLoadedUnit>().Add(new MigrationLoadedUnit
            {
                TenantId = tenant.TenantId, ConnectionId = connectionId, SourceKeyHash = plan.SourceKeyHash,
                SourceKey = plan.SourceKey, UnitId = outcome.UnitId!.Value, LoadId = loadId, PlanUnitId = plan.PlanUnitId,
                CoverHash = MigrationCoverFingerprint.Of(sentById[plan.PlanUnitId].Entitlements), CoverLoadId = loadId,
                OwnerPartyCode = owners.GetValueOrDefault(plan.PlanUnitId), LoadedAt = clock.UtcNow,
            });
        }
    }

    /// <summary>After a rolled-back load: its units leave the register, and covers a delta replaced point back to what they were.</summary>
    private async Task ForgetLoadedUnitsAsync(Guid loadId, CancellationToken cancellationToken)
    {
        var replaced = await db.Set<MigrationDeltaItem>().AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.RunId == loadId && row.Kind == DeltaItemKind.CoverReplaced && row.PreviousCoverHash != null)
            .Select(row => new { row.SourceKey, row.PreviousCoverHash, row.PreviousCoverLoadId })
            .ToListAsync(cancellationToken);
        foreach (var chunk in replaced.Chunk(ChunkSize))
        {
            var byHash = chunk.ToDictionary(row => MigrationSourceKey.Hash(row.SourceKey), StringComparer.Ordinal);
            var keys = byHash.Keys.ToList();
            var rows = await db.Set<MigrationLoadedUnit>().Where(row => row.TenantId == tenant.TenantId &&
                row.CoverLoadId == loadId && keys.Contains(row.SourceKeyHash)).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                row.CoverHash = byHash[row.SourceKeyHash].PreviousCoverHash!;
                row.CoverLoadId = byHash[row.SourceKeyHash].PreviousCoverLoadId ?? row.LoadId;
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        await db.Set<MigrationLoadedUnit>().Where(row => row.TenantId == tenant.TenantId && row.LoadId == loadId)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<Dictionary<Guid, string?>> CurrentOwnerCodesAsync(List<Guid> planUnitIds,
        CancellationToken cancellationToken) =>
        (await db.MigrationPlanOwnership.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                planUnitIds.Contains(row.PlanUnitId) && row.IsCurrent)
            .Select(row => new { row.PlanUnitId, row.Pseudonymised, row.PartyCode, row.PartyReference })
            .ToListAsync(cancellationToken))
        .GroupBy(row => row.PlanUnitId)
        .ToDictionary(group => group.Key, group => (string?)group.Select(row => row.Pseudonymised || row.PartyCode is null
            ? row.PartyReference : row.PartyCode).First());

    private async Task<Dictionary<Guid, string>> SerialsAsync(List<Guid> planUnitIds, CancellationToken cancellationToken) =>
        (await db.MigrationPlanIdentifiers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                planUnitIds.Contains(row.PlanUnitId) && row.IdentifierType == IdentifierType.InternalSerial)
            .Select(row => new { row.PlanUnitId, row.RawValue }).ToListAsync(cancellationToken))
        .GroupBy(row => row.PlanUnitId).ToDictionary(group => group.Key, group => group.First().RawValue);

    /// <summary>
    /// An owner change the delta cannot explain goes to the coexistence conflict queue (OD-61 c). The record key is the
    /// source-key hash, so a later delta that sees the same difference refreshes the open conflict instead of adding one.
    /// </summary>
    private async Task<Guid> RaiseOwnerConflictAsync(Guid connectionId, MigrationPlanUnit plan, string? serial,
        string? loadedOwner, string? sourceOwner, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var open = await db.CoexistenceConflicts.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId &&
            row.ConnectionId == connectionId && row.RecordType == MigratedUnitRecordType &&
            row.RecordKey == plan.SourceKeyHash && row.FieldName == OwnerFieldName &&
            row.Status != CoexistenceConflictStatus.Closed, cancellationToken);
        if (open is not null)
        {
            open.RemoteValue = sourceOwner;
            open.LastSeenAt = now;
            return open.ConflictId;
        }
        var label = serial is null ? plan.ItemCode : $"{plan.ItemCode} {serial}";
        var conflictId = Guid.CreateVersion7(now);
        db.CoexistenceConflicts.Add(new CoexistenceConflict
        {
            TenantId = tenant.TenantId, ConflictId = conflictId, ConnectionId = connectionId,
            RecordType = MigratedUnitRecordType, RecordKey = plan.SourceKeyHash!,
            RecordLabel = label.Length <= 300 ? label : label[..300],
            Kind = CoexistenceConflictKind.MigrationOwnerDiffers, FieldName = OwnerFieldName,
            BaselineValue = loadedOwner, RemoteValue = sourceOwner, Status = CoexistenceConflictStatus.Open,
            DetectedAt = now, LastSeenAt = now,
        });
        return conflictId;
    }

    private async Task<bool> SourceConnectionRunningAsync(Guid batchId, CancellationToken cancellationToken) =>
        await (from batch in db.MigrationBatches.AsNoTracking()
               join connection in db.Connections.AsNoTracking()
                   on new { batch.TenantId, batch.ConnectionId } equals new { connection.TenantId, connection.ConnectionId }
               where batch.TenantId == tenant.TenantId && batch.BatchId == batchId &&
                     connection.Status == ConnectionStatus.Active
               select batch.BatchId).AnyAsync(cancellationToken);

    /// <summary>
    /// OD-60 hand-over at the end of a final load: every poll watermark of the source connection is set to start at the
    /// extraction's source-clock time, so the first poll after a person resumes the sync brings exactly the documents
    /// posted since the extraction. The connection stays stopped; resuming it is a person's step.
    /// </summary>
    private async Task HandOverWatermarksAsync(MigrationApplyRun run, MigrationBatch batch,
        CancellationToken cancellationToken)
    {
        if (batch.SourceClockAtStart is not { } from)
        {
            run.WatermarkNote = "The batch was extracted before the hand-over existed; the watermarks were left as they were.";
            return;
        }
        var connectorCode = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
            row.ConnectionId == batch.ConnectionId).Select(row => row.ConnectorCode).SingleOrDefaultAsync(cancellationToken);
        if (connectorCode is null)
        {
            run.WatermarkNote = "The source has no registered connection; nothing to hand over.";
            return;
        }
        var watermark = await connectors.Poller(connectorCode)
            .StartingWatermarkAsync(batch.ConnectionId, from, cancellationToken);
        if (watermark is null)
        {
            run.WatermarkNote = "The connector cannot start from a time; the watermarks were left as they were.";
            return;
        }
        var now = clock.UtcNow;
        var states = await db.ConnectionPollWatermarks.Where(row => row.TenantId == tenant.TenantId &&
            row.ConnectionId == batch.ConnectionId).ToListAsync(cancellationToken);
        run.WatermarksBefore = SaveWatermarks(states);
        foreach (var state in states)
        {
            state.Watermark = watermark;
            state.NextPollAt = now;
            state.LastError = null;
            state.Revision++;
        }
        run.WatermarkHandedOverFrom = from;
        run.WatermarkNote = states.Count == 0 ? "The connection has no polled record types yet." : null;
    }
}

/// <summary>
/// A stable fingerprint of a unit's reviewed cover: the facts the module stores per migrated entitlement, sorted, so a
/// delta load can tell whether the source changed it. Evidence rows that were not selected do not change the cover.
/// </summary>
public static class MigrationCoverFingerprint
{
    public static string Of(IReadOnlyList<MigratedEntitlement>? cover)
    {
        var lines = (cover ?? []).Select(row => string.Join('|', row.CoverageFamily, row.PolicyVersionId.ToString("N"),
                row.PolicyVersion.ToString(CultureInfo.InvariantCulture), Day(row.StartDate), Day(row.EndDate),
                Day(row.TerminationDate), row.Direction, row.DateSource, row.SourceKey))
            .Order(StringComparer.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
    }

    private static string Day(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";
}
