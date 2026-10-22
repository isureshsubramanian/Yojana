using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Yojana.Integration.Connectors;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.LinkOverrides;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Apply;

public sealed record StartApplyRequest(bool Rehearsal);
public sealed record StartApplyResponse(Guid LoadId, Guid BatchId, Guid MapRunId, bool Rehearsal, string Status);
public sealed record MigrationApplyView(Guid LoadId, Guid BatchId, Guid MapRunId, bool Rehearsal, string Status,
    string RequestedBy, DateTime RequestedAt, DateTime? StartedAt, DateTime? FinishedAt, string? Failure,
    long UnitsInPlan, long Applied, long AlreadyApplied, long Refused, long EventsWritten,
    long EntitlementsWritten, long ReplacementLinksWritten, long CertificatesRecorded,
    string? RolledBackBy, DateTime? RolledBackAt, Guid? GapReportRunId = null, int? CoverGaps = null,
    long ReplacementLinksUsingOverride = 0, IReadOnlyList<MigrationGapCount>? GapsByKind = null,
    string Kind = ApplyRunKind.Initial, Guid? BaseLoadId = null, DateTime? WatermarkHandedOverFrom = null,
    string? WatermarkNote = null, long CoverReplaced = 0, long CoverRefused = 0, long AlreadyInModule = 0,
    long OwnerDiffers = 0, long GoneFromSource = 0);
public sealed record MigrationApplyRefusalView(Guid RefusalId, Guid PlanUnitId, string ItemCode, string? Serial,
    IReadOnlyList<string> Reasons);
public sealed record MigrationApplyRefusalPage(IReadOnlyList<MigrationApplyRefusalView> Items, string? NextCursor);
public sealed record RollBackLoadResponse(Guid LoadId, string Outcome, int UnitsRemoved, int EventsRemoved,
    IReadOnlyList<Guid> TouchedUnitIds, int EntitlementsRemoved = 0, int ReplacementLinksRemoved = 0,
    int CertificatesRemoved = 0, int CoverReplacementsReversed = 0);

public sealed class ApplyRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}

/// <summary>
/// The apply step (P9-T10, OD-52, OD-53): writes one completed map run into the module through the
/// <see cref="ApplyMigratedUnits"/> contract, chunk by chunk, resolving the plan's source codes to module ids first
/// (items must already be synced from the connection; warehouses and parties resolve as the gateway does). Refused
/// units are listed with their rules. One summary <see cref="MigrationLoadApplied"/> is published when the load
/// completes; nothing is published per unit. A rehearsal load can be rolled back while no unit in it was touched.
/// </summary>
public sealed partial class ApplyService(IntegrationDbContext db, ITenantContext tenant, IClock clock, IMessageBus bus,
    GatewayReferenceStore references, IMigrationSignoffStatus signoff, MappingSettings mappingSettings,
    ReplacementLinkValueValidator linkValidator, IConnectorAdapterResolver connectors)
{
    public const int ChunkSize = 500;

    public async Task<StartApplyResponse> EnqueueAsync(Guid batchId, StartApplyRequest request, string actor,
        CancellationToken cancellationToken)
    {
        var map = await db.MigrationMapRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId && row.Status == MappingRunStatus.Completed)
            .OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new ApplyRuleException("migration_apply_map_not_found",
                "No completed load plan exists for this batch. Run mapping first.");
        // A final load needs the non-rehearsal sign-off record (P9-T08, OD-54) of exactly this load plan.
        if (!request.Rehearsal && !await signoff.HasFinalSignoffAsync(map.RunId, cancellationToken))
            throw new ApplyRuleException("migration_apply_signoff_required",
                "A final load needs the validation sign-off record of this load plan. Run a rehearsal load, or sign off validation first.");
        // OD-60: from the final load on, movements come only through the connector, starting at the extraction. A
        // connection already polling would bring documents in before (or beside) the units they move, and a delivery
        // of a serial not yet loaded opens a provisional unit that then blocks the migrated one.
        if (!request.Rehearsal && await SourceConnectionRunningAsync(batchId, cancellationToken))
            throw new ApplyRuleException("migration_apply_connection_running",
                "Stop the sync of the source connection before a final load. The load hands the connector the extraction time, and you resume the sync after it.");
        var open = await db.Set<MigrationApplyRun>().AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                (row.Status == ApplyRunStatus.Queued || row.Status == ApplyRunStatus.Running ||
                 row.Status == ApplyRunStatus.Completed || row.Status == ApplyRunStatus.Failed))
            .OrderBy(row => row.Status == ApplyRunStatus.Failed ? 0 : 1)
            .Select(row => new { row.RunId, row.BatchId, row.Status, row.Kind }).FirstOrDefaultAsync(cancellationToken);
        if (open is { Status: ApplyRunStatus.Failed, Kind: ApplyRunKind.Initial } && open.BatchId == batchId)
        {
            // A failed load resumes from its cursor under the same load id; the module skips units it already holds.
            var failed = await db.Set<MigrationApplyRun>().SingleAsync(row => row.TenantId == tenant.TenantId &&
                row.RunId == open.RunId, cancellationToken);
            failed.Status = ApplyRunStatus.Queued;
            failed.FailureCode = null;
            failed.FinishedAt = null;
            await db.SaveChangesAsync(cancellationToken);
            return new(failed.RunId, batchId, failed.MapRunId, failed.Rehearsal, failed.Status);
        }
        if (open is not null)
            throw new ApplyRuleException("migration_apply_load_open",
                open.Status is ApplyRunStatus.Completed or ApplyRunStatus.Failed
                    ? "An earlier load is still in the module. Roll it back before loading again."
                    : "A load is already queued or running.");
        var now = clock.UtcNow;
        var run = new MigrationApplyRun
        {
            TenantId = tenant.TenantId, RunId = Guid.CreateVersion7(now), MapRunId = map.RunId, BatchId = batchId,
            Rehearsal = request.Rehearsal, Status = ApplyRunStatus.Queued, RequestedBy = actor, RequestedAt = now,
            UnitsInPlan = map.UnitCount,
        };
        db.Set<MigrationApplyRun>().Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return new(run.RunId, batchId, map.RunId, run.Rehearsal, run.Status);
    }

    public async Task<int> RunPendingAsync(CancellationToken cancellationToken)
    {
        var completed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var run = await ClaimAsync(cancellationToken);
            if (run is null) return completed;
            try
            {
                if (run.Kind == ApplyRunKind.Delta) await ApplyDeltaAsync(run.RunId, cancellationToken);
                else await ApplyAsync(run.RunId, cancellationToken);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await MarkFailedAsync(run.RunId, "migration_apply_cancelled", CancellationToken.None);
                throw;
            }
            catch
            {
                await MarkFailedAsync(run.RunId, "migration_apply_failed", CancellationToken.None);
            }
        }
        return completed;
    }

    public async Task<MigrationApplyView?> GetAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var run = await db.Set<MigrationApplyRun>().AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.BatchId == batchId)
            .OrderByDescending(row => row.RequestedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        return run is null ? null : await ViewAsync(run, cancellationToken);
    }

    public async Task<MigrationApplyRefusalPage> RefusalsAsync(Guid loadId, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        Guid? after = Guid.TryParse(cursor, out var parsed) ? parsed : null;
        var rows = await (from refusal in db.Set<MigrationApplyRefusal>().AsNoTracking()
                          join unit in db.MigrationPlanUnits.AsNoTracking()
                              on new { refusal.TenantId, refusal.PlanUnitId } equals new { unit.TenantId, unit.PlanUnitId }
                          where refusal.TenantId == tenant.TenantId && refusal.RunId == loadId &&
                                (after == null || refusal.RefusalId.CompareTo(after.Value) > 0)
                          orderby refusal.RefusalId
                          select new
                          {
                              refusal.RefusalId, refusal.PlanUnitId, unit.ItemCode, refusal.Reasons,
                              Serial = db.MigrationPlanIdentifiers.Where(id => id.TenantId == unit.TenantId &&
                                      id.PlanUnitId == unit.PlanUnitId && id.IdentifierType == IdentifierType.InternalSerial)
                                  .Select(id => id.RawValue).FirstOrDefault(),
                          }).Take(take + 1).ToListAsync(cancellationToken);
        var items = rows.Take(take).Select(row => new MigrationApplyRefusalView(row.RefusalId, row.PlanUnitId,
            row.ItemCode, row.Serial, row.Reasons.Split(' ', StringSplitOptions.RemoveEmptyEntries))).ToList();
        return new(items, rows.Count > take ? items[^1].RefusalId.ToString("D") : null);
    }

    public async Task<RollBackLoadResponse> RollBackAsync(Guid loadId, string actor, CancellationToken cancellationToken)
    {
        var run = await db.Set<MigrationApplyRun>().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId &&
                row.RunId == loadId, cancellationToken)
            ?? throw new ApplyRuleException("migration_apply_load_not_found", "The load does not exist.");
        if (!run.Rehearsal && run.Kind != ApplyRunKind.Delta)
            throw new ApplyRuleException("migration_apply_final_load",
                "A final load is not rolled back here; follow the cutover rollback runbook.");
        // Deltas stack: each one replaced what the one before it left, so only the latest can be undone (OD-62).
        if (run.Kind == ApplyRunKind.Delta && await db.Set<MigrationApplyRun>().AnyAsync(row =>
                row.TenantId == tenant.TenantId && row.Kind == ApplyRunKind.Delta && row.BaseLoadId == run.RunId &&
                row.Status != ApplyRunStatus.RolledBack, cancellationToken))
            throw new ApplyRuleException("migration_delta_not_latest",
                "A later delta load was built on this one. Undo the later delta first.");
        if (run.Status is not (ApplyRunStatus.Completed or ApplyRunStatus.Failed))
            throw new ApplyRuleException("migration_apply_not_rollbackable",
                $"A load that is {run.Status} cannot be rolled back.");
        var result = await bus.InvokeAsync<RollBackMigrationLoadResult>(new RollBackMigrationLoad(loadId, actor),
            Options(), cancellationToken);
        if (result.Outcome != RollBackMigrationLoadResult.RefusedTouched)
        {
            await ForgetLoadedUnitsAsync(run.RunId, cancellationToken);
            run.Status = ApplyRunStatus.RolledBack;
            run.RolledBackBy = actor;
            run.RolledBackAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return new(loadId, result.Outcome, result.UnitsRemoved, result.EventsRemoved, result.TouchedUnitIds,
            result.EntitlementsRemoved, result.ReplacementLinksRemoved, result.CertificatesRemoved,
            result.CoverReplacementsReversed);
    }

    private async Task ApplyAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await Run(runId, cancellationToken);
        var batch = await db.MigrationBatches.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.BatchId == run.BatchId, cancellationToken);
        var map = await db.MigrationMapRuns.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == run.MapRunId, cancellationToken);
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
            var (units, missing, overridesUsed) = await BuildChunkAsync(plans, batch.ConnectionId, cutover, cancellationToken);
            // Warehouse and party references created for this chunk commit BEFORE the module sees their ids, so a
            // resumed load resolves the same ids again.
            await db.SaveChangesAsync(cancellationToken);
            var result = units.Count == 0
                ? new ApplyMigratedUnitsResult([], 0, 0, 0, 0)
                : await bus.InvokeAsync<ApplyMigratedUnitsResult>(
                    new ApplyMigratedUnits(run.RunId, legalEntity, run.RequestedBy, source, units, map.CutoverDate), Options(), cancellationToken);
            run = await db.Set<MigrationApplyRun>().SingleAsync(row => row.TenantId == tenant.TenantId &&
                row.RunId == runId, cancellationToken);
            await RememberLoadedUnitsAsync(batch.ConnectionId, run.RunId, plans, units, result.Units, cancellationToken);
            var refusals = missing.Concat(result.Units.Where(u => u.Outcome == MigratedUnitOutcome.Refused)
                .Select(u => (u.PlanUnitId, u.Reasons))).ToList();
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
            run.Applied += result.Applied;
            run.AlreadyApplied += result.AlreadyApplied;
            run.Refused += refusals.Count - already.Count;
            run.EventsWritten += result.EventsWritten;
            run.EntitlementsWritten += result.EntitlementsWritten;
            run.ReplacementLinksWritten += result.ReplacementLinksWritten;
            run.ReplacementLinksUsingOverride += overridesUsed;
            run.CertificatesRecorded += result.CertificatesRecorded;
            run.CursorPlanUnitId = plans[^1].PlanUnitId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            run = await Run(runId, cancellationToken);
        }

        await FinishAsync(runId, cancellationToken, batch);
    }

    /// <summary>The last steps of every load, initial or delta: the gap report, the hand-over of a final load, the summary event.</summary>
    private async Task FinishAsync(Guid runId, CancellationToken cancellationToken, MigrationBatch? batch = null)
    {
        // Last step of every load (P9-T09, ADR "Entitlement gap report and backfill"): the standing gap report over
        // migrated owners, run once per load (its id derives from the load). Backfill itself stays a service-manager
        // approved selection set on the gap report screen; it is never committed here on nobody's authority.
        var gaps = await bus.InvokeAsync<EntitlementGapRunResult>(new RunEntitlementGapReport(
            DeterministicId.Create(runId, "entitlement-gap-report"), EntitlementGapTrigger.MigrationLoad,
            new EntitlementGapFilters(Channels: [EpisodeSource.Migration])), Options(), cancellationToken);
        var run = await db.Set<MigrationApplyRun>().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId, cancellationToken);
        run.GapReportRunId = gaps.RunId;
        run.CoverGaps = gaps.Total;
        if (!run.Rehearsal && run.Kind == ApplyRunKind.Initial && batch is not null)
            await HandOverWatermarksAsync(run, batch, cancellationToken);
        run.Status = ApplyRunStatus.Completed;
        run.FinishedAt = clock.UtcNow;
        await bus.PublishAsync(new MigrationLoadApplied(run.RunId, run.BatchId, run.Rehearsal,
            (int)(run.Applied + run.AlreadyApplied), (int)run.EventsWritten, run.FinishedAt.Value,
            (int)run.EntitlementsWritten, (int)run.ReplacementLinksWritten,
            (int)run.CertificatesRecorded), Options());
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<(List<MigratedUnit> Units, List<(Guid PlanUnitId, IReadOnlyList<string> Reasons)> Missing,
        int OverridesUsed)>
        BuildChunkAsync(List<MigrationPlanUnit> plans, Guid connectionId, DateTime cutover,
            CancellationToken cancellationToken)
    {
        var ids = plans.Select(row => row.PlanUnitId).ToList();
        var identifiers = (await db.MigrationPlanIdentifiers.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.PlanUnitId)).ToListAsync(cancellationToken))
            .ToLookup(row => row.PlanUnitId);
        var events = (await db.MigrationPlanEvents.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.PlanUnitId)).ToListAsync(cancellationToken))
            .ToLookup(row => row.PlanUnitId);
        var owners = (await db.MigrationPlanOwnership.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.PlanUnitId)).ToListAsync(cancellationToken))
            .ToLookup(row => row.PlanUnitId);
        var planEntitlements = (await db.MigrationPlanEntitlements.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.PlanUnitId)).ToListAsync(cancellationToken))
            .ToLookup(row => row.PlanUnitId);
        var warrantyEvidence = (await db.MigrationPlanWarrantyEvidence.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.PlanUnitId)).ToListAsync(cancellationToken))
            .ToLookup(row => row.PlanUnitId);
        var planCertificates = await db.MigrationPlanCertificates.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.PlanUnitId))
            .ToDictionaryAsync(row => row.PlanUnitId, cancellationToken);
        var linkRows = await db.MigrationPlanReplacementLinks.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.MapRunId == plans[0].MapRunId
                && (ids.Contains(row.ReplacedPlanUnitId) || ids.Contains(row.ReplacementPlanUnitId)))
            .ToListAsync(cancellationToken);
        var links = linkRows.Where(row =>
                ids.Contains(row.ReplacedPlanUnitId.CompareTo(row.ReplacementPlanUnitId) > 0
                    ? row.ReplacedPlanUnitId : row.ReplacementPlanUnitId))
            .ToLookup(row => row.ReplacedPlanUnitId.CompareTo(row.ReplacementPlanUnitId) > 0
                ? row.ReplacedPlanUnitId : row.ReplacementPlanUnitId);
        var sourceKeys = linkRows.Select(row => row.SourceKey).Distinct().ToList();
        var linkOverrides = sourceKeys.Count == 0
            ? new Dictionary<string, MigrationLinkOverride>(StringComparer.Ordinal)
            : await db.Set<MigrationLinkOverride>().AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                    sourceKeys.Contains(row.SourceKey))
                .ToDictionaryAsync(row => row.SourceKey, StringComparer.Ordinal, cancellationToken);
        var linkDefaults = (await mappingSettings.GetAsync(cancellationToken)).LinkDefaults;
        var itemCodes = plans.Select(row => row.ItemCode).Distinct().ToList();
        var items = await db.ExternalIdMaps.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.ConnectionId == connectionId && row.EntityType == "item" && row.ValidTo == null &&
                itemCodes.Contains(row.ExternalId))
            .ToDictionaryAsync(row => row.ExternalId, row => row.ModuleId, StringComparer.Ordinal, cancellationToken);

        var units = new List<MigratedUnit>();
        var missing = new List<(Guid, IReadOnlyList<string>)>();
        foreach (var plan in plans)
        {
            if (!items.TryGetValue(plan.ItemCode, out var itemId))
            {
                missing.Add((plan.PlanUnitId, ["item_not_synced"]));
                continue;
            }
            // Cover whose dates come from the equipment card or serial master names no policy, and the load never
            // infers one: the unit loads without it, and the gap report and backfill (P9-T09) close that cover.
            var plannedCover = planEntitlements[plan.PlanUnitId]
                .Where(row => row.PolicyId is not null && row.PolicyVersion is not null).ToList();
            var history = events[plan.PlanUnitId].OrderBy(row => row.Sequence).ToList();
            var created = history.Count == 0 ? cutover : Utc(history[0].EffectiveDate);
            MigratedCustody? custody = null;
            if (plan.LocationState == PlanLocationState.InStock && plan.WarehouseCode is { } warehouse)
            {
                var arrived = history.LastOrDefault(row => row.Quantity > 0 && row.WarehouseCode == warehouse);
                custody = new MigratedCustody(
                    await references.ResolveAsync(connectionId, "location", warehouse, true, cancellationToken),
                    plan.CustodyLocationKind ?? LocationKind.Warehouse, arrived is null ? created : Utc(arrived.EffectiveDate));
            }
            MigratedOwnership? ownership = null;
            var unitOwners = owners[plan.PlanUnitId].ToList();
            if (plan.LocationState == PlanLocationState.Shipped && unitOwners.FirstOrDefault(row => row.IsCurrent) is { } current)
            {
                var lastShipment = history.LastOrDefault(row => row.MovementKind == HistoryMovementKind.Shipped);
                var from = current.EvidenceDate is { } ownershipEvidence ? Utc(ownershipEvidence)
                    : lastShipment is null ? created : Utc(lastShipment.EffectiveDate);
                var direct = unitOwners.FirstOrDefault(row => row.Tier == PlanOwnershipTier.DirectCustomer);
                // The last delivery is the start evidence a later backfill uses for cover the plan could not name.
                var delivery = lastShipment is null ? null : new MigratedDelivery(Utc(lastShipment.EffectiveDate),
                    lastShipment.DocumentTypeLabel, lastShipment.DocumentNumber?.ToString(CultureInfo.InvariantCulture));
                ownership = new MigratedOwnership(await PartyAsync(connectionId, current, cancellationToken),
                    PartyType.Customer, from, current.Pseudonymised,
                    direct is null ? null : await PartyAsync(connectionId, direct, cancellationToken), delivery);
            }
            var evidence = warrantyEvidence[plan.PlanUnitId].Select(row => new MigratedWarrantyEvidence(
                row.SourceKind, row.SourceKey, row.StartDate, row.EndDate, row.TerminationDate,
                row.Direction, row.SelectedForEntitlement)).ToList();
            var entitlements = plannedCover.Select(row => new MigratedEntitlement(row.PlanEntitlementId,
                row.CoverageFamily, row.PolicyId!.Value, row.PolicyVersion!.Value, row.StartDate, row.EndDate,
                row.TerminationDate, row.Direction, row.DateSource, row.SourceKey, evidence)).ToList();
            var replacements = links[plan.PlanUnitId].Select(row =>
            {
                var values = linkOverrides.TryGetValue(row.SourceKey, out var itemOverride)
                    ? itemOverride.Effective(linkDefaults) : linkDefaults;
                values = linkValidator.Validate(values);
                return new MigratedReplacementLink(row.PlanReplacementLinkId, row.ReplacedPlanUnitId,
                    row.ReplacementPlanUnitId, row.PolicyApplied, row.SourceKey, values.Direction,
                    values.ReplacementReason, values.State, values.ReplacementConditionAllowed,
                    values.PricePolicy, values.DifferenceAmount, values.Currency);
            }).ToList();
            MigratedCertificate? certificate = null;
            if (planCertificates.TryGetValue(plan.PlanUnitId, out var plannedCertificate))
            {
                var unknowns = new List<string> { "The source plan did not contain an as-built production record." };
                if (plan.HistoryDepth != Yojana.Serialisation.Contracts.HistoryDepth.Full)
                    unknowns.Add("History before the earliest retained source fact is unknown.");
                if (unitOwners.Any(row => row.Pseudonymised))
                    unknowns.Add("The source removed at least one customer identity.");
                var certificateEvidence = evidence.Select(row => $"{row.SourceKind}:{row.SourceKey}")
                    .Concat(identifiers[plan.PlanUnitId].Select(row => $"identifier:{row.IdentifierType}:{row.Provenance}"))
                    .Concat(history.Select(row => $"history:{row.DocumentTypeLabel}:{row.Provenance}"))
                    .Distinct(StringComparer.Ordinal).ToList();
                certificate = new MigratedCertificate(plannedCertificate.PlanCertificateId,
                    plannedCertificate.Version, plannedCertificate.Provenance, unknowns, certificateEvidence);
            }
            units.Add(new MigratedUnit(plan.PlanUnitId, itemId, plan.HistoryDepth, created,
                identifiers[plan.PlanUnitId].Select(row => new IdentifierInput(row.IdentifierType, row.RawValue)).ToList(),
                history.Select(row => new MigratedHistoryEvent(Utc(row.EffectiveDate), row.MovementKind, row.Provenance,
                    row.DocumentTypeLabel, row.DocumentNumber, row.PartyCode, row.WarehouseCode, row.Quantity)).ToList(),
                custody, ownership, plan.ConditionGrade, plan.ConditionProvenance, entitlements, replacements,
                certificate));
        }
        return (units, missing, linkRows.Count(row =>
            ids.Contains(row.ReplacedPlanUnitId.CompareTo(row.ReplacementPlanUnitId) > 0
                ? row.ReplacedPlanUnitId : row.ReplacementPlanUnitId) && linkOverrides.ContainsKey(row.SourceKey)));
    }

    /// <summary>A named party resolves as the gateway resolves it; a pseudonymised one gets a stable id with no name behind it.</summary>
    private async Task<Guid> PartyAsync(Guid connectionId, MigrationPlanOwnership owner, CancellationToken cancellationToken) =>
        owner.Pseudonymised || owner.PartyCode is null
            ? DeterministicId.Create(connectionId, owner.PartyReference)
            : await references.ResolveAsync(connectionId, "party", owner.PartyCode, true, cancellationToken);

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private async Task<MigrationApplyRun> Run(Guid runId, CancellationToken cancellationToken) =>
        await db.Set<MigrationApplyRun>().AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId, cancellationToken);

    private async Task<MigrationApplyRun?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var run = await db.Set<MigrationApplyRun>().Where(row => row.TenantId == tenant.TenantId &&
                row.Status == ApplyRunStatus.Queued).OrderBy(row => row.RequestedAt).ThenBy(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        run.Status = ApplyRunStatus.Running;
        run.StartedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        db.ChangeTracker.Clear();
        return run;
    }

    private async Task MarkFailedAsync(Guid runId, string code, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var failed = await db.Set<MigrationApplyRun>().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId, cancellationToken);
        failed.Status = ApplyRunStatus.Failed;
        failed.FailureCode = code;
        failed.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private DeliveryOptions Options() => new() { TenantId = tenant.TenantId.ToString() };

    private async Task<MigrationApplyView> ViewAsync(MigrationApplyRun run, CancellationToken cancellationToken)
    {
        var cursor = run.CursorPlanUnitId;
        var rows = await (from gap in db.MigrationPlanGaps.AsNoTracking()
                          where gap.TenantId == tenant.TenantId && gap.MapRunId == run.MapRunId &&
                                cursor != null && gap.PlanUnitId != null &&
                                gap.PlanUnitId.Value.CompareTo(cursor.Value) <= 0 &&
                                !db.Set<MigrationApplyRefusal>().Any(refusal =>
                                    refusal.TenantId == gap.TenantId && refusal.RunId == run.RunId &&
                                    refusal.PlanUnitId == gap.PlanUnitId)
                          select new { gap.Kind, PlanUnitId = gap.PlanUnitId!.Value })
            .Distinct().ToListAsync(cancellationToken);
        var gaps = rows.GroupBy(row => row.Kind).OrderBy(group => group.Key)
            .Select(group => new MigrationGapCount(group.Key, PlanGapKind.Label(group.Key), group.LongCount()))
            .ToList();
        return new(run.RunId, run.BatchId, run.MapRunId, run.Rehearsal, run.Status, run.RequestedBy,
            run.RequestedAt, run.StartedAt, run.FinishedAt, run.FailureCode, run.UnitsInPlan, run.Applied,
            run.AlreadyApplied, run.Refused, run.EventsWritten, run.EntitlementsWritten,
            run.ReplacementLinksWritten, run.CertificatesRecorded, run.RolledBackBy, run.RolledBackAt,
            run.GapReportRunId, run.CoverGaps, run.ReplacementLinksUsingOverride, gaps, run.Kind, run.BaseLoadId,
            run.WatermarkHandedOverFrom, run.WatermarkNote, run.CoverReplaced, run.CoverRefused, run.AlreadyInModule,
            run.OwnerDiffers, run.GoneFromSource);
    }
}
