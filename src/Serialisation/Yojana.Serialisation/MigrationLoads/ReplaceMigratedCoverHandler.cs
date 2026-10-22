using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.MigrationLoads;

/// <summary>
/// Replaces migrated cover at cutover without replaying a unit or publishing per-unit history (OD-52, OD-61). Each
/// accepted chunk is one transaction and its register snapshot is sufficient for an exact OD-62 undo.
/// </summary>
public static class ReplaceMigratedCoverHandler
{
    public static async Task<ReplaceMigratedCoverResult> Handle(ReplaceMigratedCover command,
        SerialisationDbContext db, BulkIngestion bulk, MigrationCoverBulkWriter coverWriter,
        LedgerPublication publication, ITenantContext tenant, IClock clock,
        CancellationToken cancellationToken)
    {
        if (command.Units.Count > ApplyMigratedUnitsHandler.MaxUnitsPerChunk)
        {
            throw new CommandRefusedException("chunk_too_large",
                $"A chunk carries at most {ApplyMigratedUnitsHandler.MaxUnitsPerChunk} units; this one has {command.Units.Count}.",
                "send smaller chunks");
        }

        // TIGHT COUPLING (OD-52, migrated delta history publishes nothing per unit)
        // WHAT: entitlement.created and entitlement.closed are normal live fact types, so append observers and the
        //        mapper would publish one event per change. The scoped LedgerPublication switch is the seam they honour.
        // WHY:   cutover cover is source history already represented by the migration summary, not new customer work.
        // WHAT BREAKS IF SEPARATED: downstream systems receive a burst of historical cover changes and may act on them.
        // Keep this matching the suppression contract in ApplyMigratedUnitsHandler.
        publication.Suppressed = true;

        var tenantId = tenant.TenantId;
        var now = clock.UtcNow;
        var cutover = command.CutoverDate ?? DateOnly.FromDateTime(now);
        var requested = command.Units.DistinctBy(row => row.UnitId).ToList();
        var unitIds = requested.Select(row => row.UnitId).ToList();
        var already = unitIds.Count == 0
            ? new HashSet<Guid>()
            : (await db.MigrationCoverReplacements.AsNoTracking()
                .Where(row => row.TenantId == tenantId && row.LoadId == command.LoadId
                    && unitIds.Contains(row.UnitId))
                .Select(row => row.UnitId).ToListAsync(cancellationToken)).ToHashSet();
        var migrated = unitIds.Count == 0
            ? new HashSet<Guid>()
            : (await db.MigrationLoadUnits.AsNoTracking()
                .Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId))
                .Select(row => row.UnitId).Distinct().ToListAsync(cancellationToken)).ToHashSet();

        var touched = new HashSet<Guid>();
        if (unitIds.Count > 0)
        {
            var counts = await db.Units.AsNoTracking().Where(row => row.TenantId == tenantId && unitIds.Contains(row.UnitId))
                .Select(row => new
                {
                    row.UnitId,
                    Live = db.Events.Count(item => item.TenantId == tenantId && item.UnitId == row.UnitId),
                    Archived = db.ArchivedEvents.Count(item => item.TenantId == tenantId && item.UnitId == row.UnitId),
                    Loaded = db.MigrationLoadUnits.Where(item => item.TenantId == tenantId && item.UnitId == row.UnitId)
                        .Sum(item => (int?)item.EventCount) ?? 0,
                    Replaced = db.MigrationCoverReplacements.Where(item => item.TenantId == tenantId && item.UnitId == row.UnitId)
                        .Sum(item => (int?)item.EventsWritten) ?? 0,
                }).ToListAsync(cancellationToken);
            touched = counts.Where(row => row.Live + row.Archived > row.Loaded + row.Replaced)
                .Select(row => row.UnitId).ToHashSet();
        }

        var policyIds = requested.SelectMany(row => row.Entitlements).Select(row => row.PolicyVersionId)
            .Distinct().ToList();
        var policies = policyIds.Count == 0
            ? new Dictionary<Guid, WarrantyPolicyVersion>()
            : await db.PolicyVersions.AsNoTracking().Where(row => row.TenantId == tenantId
                    && policyIds.Contains(row.PolicyVersionId))
                .ToDictionaryAsync(row => row.PolicyVersionId, cancellationToken);
        var familyCodes = requested.SelectMany(row => row.Entitlements).Select(row => row.CoverageFamily)
            .Distinct().ToList();
        var families = familyCodes.Count == 0
            ? new Dictionary<string, CoverageFamily>(StringComparer.Ordinal)
            : await db.CoverageFamilies.AsNoTracking().Where(row => row.TenantId == tenantId
                    && familyCodes.Contains(row.FamilyCode))
                .ToDictionaryAsync(row => row.FamilyCode, StringComparer.Ordinal, cancellationToken);

        var outcomes = new List<MigratedCoverOutcome>();
        var preparedChanges = new List<MigratedCoverChange>();
        foreach (var change in requested)
        {
            if (already.Contains(change.UnitId))
            {
                outcomes.Add(new(change.UnitId, MigratedCoverOutcome.AlreadyReplaced, []));
                continue;
            }
            if (!migrated.Contains(change.UnitId))
            {
                outcomes.Add(new(change.UnitId, MigratedCoverOutcome.Refused, ["unit_not_migrated"]));
                continue;
            }
            if (touched.Contains(change.UnitId))
            {
                outcomes.Add(new(change.UnitId, MigratedCoverOutcome.RefusedTouched, []));
                continue;
            }
            var reasons = MigratedCoverRules.Refusals(change.Entitlements, cutover, policies, families);
            if (reasons.Count > 0)
            {
                outcomes.Add(new(change.UnitId, MigratedCoverOutcome.Refused, reasons));
                continue;
            }
            preparedChanges.Add(change);
        }

        if (preparedChanges.Count == 0)
            return Result(outcomes, 0, 0, 0);

        var preparedIds = preparedChanges.Select(row => row.UnitId).ToList();
        var unitFacts = await (from unit in db.Units.AsNoTracking()
                               join current in db.Current.AsNoTracking()
                                   on new { unit.TenantId, unit.UnitId } equals new { current.TenantId, current.UnitId }
                               where unit.TenantId == tenantId && preparedIds.Contains(unit.UnitId)
                               select new { unit.UnitId, unit.CreatedEventTime, current.ConditionGrade,
                                   current.OwnerPartyRef }).ToDictionaryAsync(row => row.UnitId, cancellationToken);
        var ownerTypes = await db.Episodes.AsNoTracking().Where(row => row.TenantId == tenantId
                && preparedIds.Contains(row.UnitId) && row.Kind == EpisodeKind.Ownership && row.ValidTo == null)
            .ToDictionaryAsync(row => row.UnitId, row => row.PartyType, cancellationToken);
        var existingCover = await db.Entitlements.AsNoTracking().Where(row => row.TenantId == tenantId
                && preparedIds.Contains(row.UnitId)).ToListAsync(cancellationToken);
        var liveMigrated = existingCover.Where(row => row.Status != EntitlementStatus.Closed
                && row.Provenance == EntitlementProvenance.Migrated)
            .GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group => group.ToList());

        var entitlementRows = new List<Entitlement>();
        var evidenceRows = new List<EntitlementEvidence>();
        var usageRows = new List<EntitlementUsageLimit>();
        var eventRows = new List<UnitEventDraft>();
        var registerRows = new List<MigrationCoverReplacement>();
        var closeIds = new List<Guid>();
        var entitlementsClosed = 0;
        foreach (var change in preparedChanges)
        {
            var fact = unitFacts[change.UnitId];
            var written = 0;
            string Key(string type, DateTime at) => IdempotencyKey.Derive(
                IdempotencyKey.ForCommand(ApplyMigratedUnitsHandler.Source, command.LoadId, change.UnitId, type, at),
                (written++).ToString(System.Globalization.CultureInfo.InvariantCulture));
            var close = liveMigrated.GetValueOrDefault(change.UnitId) ?? [];
            var snapshots = new List<MigrationClosedEntitlementSnapshot>();
            foreach (var entitlement in close)
            {
                var previous = new MigrationClosedEntitlementSnapshot(entitlement.EntitlementId,
                    TimeOrderedId.New(now), entitlement.Status, entitlement.ClosedReason,
                    entitlement.ClosedEffective, entitlement.ClosedAt);
                snapshots.Add(previous);
                closeIds.Add(entitlement.EntitlementId);
                EntitlementLedger.Transition(entitlement, EntitlementEventType.Closed);
                eventRows.Add(new UnitEventDraft
                {
                    UnitId = change.UnitId, EventTime = now, EventType = EntitlementEventType.Closed,
                    PresetEventId = previous.ClosedEventId, LegalEntityId = command.LegalEntityId,
                    ActorRef = command.ActorRef, DocumentType = "migration_cover", DocumentId = change.PlanUnitId,
                    IdempotencyKey = Key(EntitlementEventType.Closed, now),
                    Attributes = JsonObjectText.Of(new
                    {
                        load_id = command.LoadId, entitlement_id = entitlement.EntitlementId,
                        coverage_family = entitlement.CoverageFamily,
                        closed_reason = EntitlementClosedReason.MigrationDeltaReplaced,
                        closed_effective = cutover,
                    }),
                });
            }

            var cover = MigratedCoverRules.Prepare(tenantId, command.LoadId, change.UnitId,
                command.LegalEntityId, fact.OwnerPartyRef, ownerTypes.GetValueOrDefault(change.UnitId),
                fact.ConditionGrade, fact.CreatedEventTime, cutover, now, command.ActorRef,
                change.Entitlements, policies, Key);
            entitlementRows.AddRange(cover.Entitlements);
            evidenceRows.AddRange(cover.Evidence);
            usageRows.AddRange(cover.UsageLimits);
            eventRows.AddRange(cover.Events);

            var closingIds = close.Select(row => row.EntitlementId).ToHashSet();
            var remaining = existingCover.Where(row => row.UnitId == change.UnitId
                    && !closingIds.Contains(row.EntitlementId))
                .Select(row => new EntitlementCoverageEntry(row.EntitlementId, row.Status, row.EndDate,
                    row.Direction));
            var coverage = EntitlementCoverage.From(remaining.Concat(cover.Entitlements.Select(row =>
                new EntitlementCoverageEntry(row.EntitlementId, row.Status, row.EndDate, row.Direction))));
            for (var index = eventRows.Count - close.Count - cover.Events.Count; index < eventRows.Count; index++)
            {
                eventRows[index] = eventRows[index] with
                {
                    Delta = new CurrentStateDelta
                    {
                        LiveEntitlementIds = coverage.LiveEntitlementIds,
                        WarrantyEndMax = new Optional<DateOnly?>(coverage.WarrantyEndMax),
                    },
                };
            }

            // HIDDEN COUPLING (OD-53/OD-61 touched rule): EventsWritten is summed with
            // MigrationLoadUnit.EventCount by replacement and rollback. The counters are the only way to distinguish
            // migration facts from operational facts across live and archived ledger partitions. If this count omits
            // either a close or create fact, later delta loads are refused and rollback safety is misclassified. Keep
            // the loader-register and rollback comments/counting query in step.
            registerRows.Add(new MigrationCoverReplacement
            {
                TenantId = tenantId, LoadId = command.LoadId, UnitId = change.UnitId,
                PlanUnitId = change.PlanUnitId,
                ClosedEntitlements = JsonSerializer.Serialize(snapshots, LedgerJson.Options),
                EventsWritten = close.Count + cover.Events.Count, ReplacedAt = now,
                ActorRef = command.ActorRef,
            });
            entitlementsClosed += close.Count;
            outcomes.Add(new(change.UnitId, MigratedCoverOutcome.Replaced, []));
        }

        await using var transaction = db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        if (closeIds.Count > 0)
        {
            await db.Entitlements.Where(row => row.TenantId == tenantId
                    && closeIds.Contains(row.EntitlementId) && row.Status != EntitlementStatus.Closed)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Status, EntitlementStatus.Closed)
                    .SetProperty(row => row.ClosedReason, EntitlementClosedReason.MigrationDeltaReplaced)
                    .SetProperty(row => row.ClosedEffective, cutover)
                    .SetProperty(row => row.ClosedAt, now), cancellationToken);
        }
        await coverWriter.WriteAsync(new MigrationCoverBulkWriter.Batch(entitlementRows,
            evidenceRows, usageRows, []), cancellationToken);
        var write = await bulk.IngestAsync(new BulkIngestion.Batch(eventRows, [], [], [], []),
            cancellationToken);
        db.MigrationCoverReplacements.AddRange(registerRows);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken);

        return Result(outcomes, entitlementsClosed, entitlementRows.Count, write.EventsInserted);
    }

    private static ReplaceMigratedCoverResult Result(IReadOnlyList<MigratedCoverOutcome> outcomes,
        int entitlementsClosed, int entitlementsWritten, int eventsWritten) => new(outcomes,
        outcomes.Count(row => row.Outcome == MigratedCoverOutcome.Replaced),
        outcomes.Count(row => row.Outcome == MigratedCoverOutcome.AlreadyReplaced),
        outcomes.Count(row => row.Outcome is MigratedCoverOutcome.Refused or MigratedCoverOutcome.RefusedTouched),
        entitlementsClosed, entitlementsWritten, eventsWritten);
}
