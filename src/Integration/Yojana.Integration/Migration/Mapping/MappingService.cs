using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Mapping;

public sealed class MappingService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IMigrationProfileStatus profileStatus,
    MappingSettings settings,
    IPolicyCatalogueQuery policyCatalogue)
{
    private const int CandidateBatchSize = 1000;

    public async Task<StartMappingResponse> EnqueueAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var reconstruction = await ReadyReconstructionAsync(batchId, cancellationToken);
        var configured = await settings.GetAsync(cancellationToken);
        var waiting = await WaitingReviewsAsync(reconstruction, cancellationToken);
        if (waiting.Count != 0 && !configured.AllowWaitingReviews)
            throw new MappingRuleException("migration_mapping_waiting_reviews",
                "Mapping cannot start while review items are waiting. Decide them, or enable waiting reviews for a rehearsal.",
                waiting);
        if (await db.MigrationMapRuns.AnyAsync(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId && (row.Status == MappingRunStatus.Queued ||
                row.Status == MappingRunStatus.Running), cancellationToken))
            throw new MappingRuleException("migration_mapping_already_running",
                "Mapping is already queued or running for this batch.");
        var batch = await db.MigrationBatches.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.BatchId == batchId, cancellationToken);
        var cutover = configured.CutoverDate ?? DateOnly.FromDateTime(batch.FinishedAt ?? batch.StartedAt ??
            batch.RequestedAt);
        var run = NewRun(reconstruction, cutover, configured.AllowWaitingReviews);
        db.MigrationMapRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return new(run.RunId, run.BatchId, run.Status);
    }

    public async Task<int> EnqueueEligibleAsync(CancellationToken cancellationToken)
    {
        var eligible = await db.MigrationReconstructionRuns.AsNoTracking().Where(reconstruction =>
                reconstruction.TenantId == tenant.TenantId && reconstruction.Status == ReconstructionRunStatus.Completed &&
                !db.MigrationMapRuns.Any(run => run.TenantId == reconstruction.TenantId &&
                    run.ReconstructionRunId == reconstruction.RunId) &&
                !db.MigrationReviewItems.Any(review => review.TenantId == reconstruction.TenantId &&
                    review.RunId == reconstruction.CanonicalisationRunId &&
                    review.Status == MigrationReviewStatus.Waiting))
            .OrderBy(reconstruction => reconstruction.FinishedAt).ThenBy(reconstruction => reconstruction.RunId)
            .ToListAsync(cancellationToken);
        var enqueued = 0;
        foreach (var reconstruction in eligible)
        {
            if (await profileStatus.IsLoadBlocked(reconstruction.BatchId, cancellationToken)) continue;
            var batch = await db.MigrationBatches.AsNoTracking().SingleAsync(row =>
                row.TenantId == tenant.TenantId && row.BatchId == reconstruction.BatchId, cancellationToken);
            var configured = await settings.GetAsync(cancellationToken);
            var cutover = configured.CutoverDate ?? DateOnly.FromDateTime(batch.FinishedAt ?? batch.StartedAt ??
                batch.RequestedAt);
            db.MigrationMapRuns.Add(NewRun(reconstruction, cutover, false));
            enqueued++;
        }
        if (enqueued != 0) await db.SaveChangesAsync(cancellationToken);
        return enqueued;
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
                await MapAsync(run, cancellationToken);
                db.ChangeTracker.Clear();
                run = await db.MigrationMapRuns.SingleAsync(row => row.TenantId == tenant.TenantId &&
                    row.RunId == run.RunId, cancellationToken);
                run.Status = MappingRunStatus.Completed;
                run.FinishedAt = clock.UtcNow;
                run.UnitCount = await db.MigrationPlanUnits.LongCountAsync(row => row.TenantId == tenant.TenantId &&
                    row.MapRunId == run.RunId, cancellationToken);
                run.EventCount = await db.MigrationPlanEvents.LongCountAsync(row => row.TenantId == tenant.TenantId &&
                    row.MapRunId == run.RunId, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await MarkFailedAsync(run.RunId, "migration_mapping_cancelled", CancellationToken.None);
                throw;
            }
            catch
            {
                await MarkFailedAsync(run.RunId, "migration_mapping_failed", CancellationToken.None);
            }
        }
        return completed;
    }

    public async Task<MigrationMapView?> GetAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var run = await db.MigrationMapRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId).OrderByDescending(row => row.RequestedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null) return null;
        var unitsByOrigin = await CountsAsync(db.MigrationPlanUnits.Where(row => row.TenantId == tenant.TenantId &&
            row.MapRunId == run.RunId), row => row.Origin, cancellationToken);
        var unitsByLocation = await CountsAsync(db.MigrationPlanUnits.Where(row => row.TenantId == tenant.TenantId &&
            row.MapRunId == run.RunId), row => row.LocationState, cancellationToken);
        var identifiers = await CountsAsync(db.MigrationPlanIdentifiers.Where(row => row.TenantId == tenant.TenantId &&
            row.MapRunId == run.RunId), row => row.IssuerKind, cancellationToken);
        var events = await CountsAsync(db.MigrationPlanEvents.Where(row => row.TenantId == tenant.TenantId &&
            row.MapRunId == run.RunId), row => row.Provenance, cancellationToken);
        var entitlementsByFamily = await CountsAsync(db.MigrationPlanEntitlements.Where(row =>
            row.TenantId == tenant.TenantId && row.MapRunId == run.RunId), row => row.CoverageFamily,
            cancellationToken);
        var entitlementsByDateSource = await CountsAsync(db.MigrationPlanEntitlements.Where(row =>
            row.TenantId == tenant.TenantId && row.MapRunId == run.RunId), row => row.DateSource,
            cancellationToken);
        var gaps = await CountsAsync(db.MigrationPlanGaps.Where(row => row.TenantId == tenant.TenantId &&
            row.MapRunId == run.RunId), row => row.Kind, cancellationToken);
        var ownershipRows = await db.MigrationPlanOwnership.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.MapRunId == run.RunId)
            .Select(row => new { row.PlanUnitId, row.Tier, row.Pseudonymised }).ToListAsync(cancellationToken);
        var ownership = ownershipRows.GroupBy(row => row.PlanUnitId).SelectMany(group => new[]
        {
            new MappingCount(group.Any(row => row.Tier == PlanOwnershipTier.DirectCustomer) &&
                group.Any(row => row.Tier == PlanOwnershipTier.EndCustomer) ? "two_tier" : "one_tier", 1),
            new MappingCount("pseudonymised", group.Any(row => row.Pseudonymised) ? 1 : 0),
        }).GroupBy(row => row.Key).Select(group => new MappingCount(group.Key, group.Sum(row => row.Count)))
            .Where(row => row.Count != 0).OrderBy(row => row.Key).ToList();
        return new(run.RunId, run.BatchId, run.ReconstructionRunId, run.CutoverDate, run.StartedAt,
            run.FinishedAt, run.Status, run.FailureCode, unitsByOrigin, unitsByLocation, identifiers, events,
            ownership, await db.MigrationPlanReplacementLinks.LongCountAsync(row =>
                row.TenantId == tenant.TenantId && row.MapRunId == run.RunId, cancellationToken),
            entitlementsByFamily, entitlementsByDateSource,
            await db.MigrationPlanEntitlements.LongCountAsync(row => row.TenantId == tenant.TenantId &&
                row.MapRunId == run.RunId && row.Expired, cancellationToken), gaps,
            run.ExcludedCandidateCount, run.WaitingCandidateCount);
    }

    public async Task<MigrationPlanGapPage> GapsAsync(Guid batchId, string? kind, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var run = await LatestCompletedRunAsync(batchId, cancellationToken) ??
            throw new MappingRuleException("migration_mapping_not_found", "A completed mapping plan was not found.");
        var after = DecodeCursor(cursor);
        var query = db.MigrationPlanGaps.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
            row.MapRunId == run.RunId && row.PlanGapId.CompareTo(after) > 0);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(row => row.Kind == kind);
        var rows = await query.OrderBy(row => row.PlanGapId).Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).ToList();
        return new(page.Select(GapView).ToList(), rows.Count > take ? EncodeCursor(page[^1].PlanGapId) : null);
    }

    public async Task<MigrationPlanUnitView?> UnitAsync(Guid planUnitId, CancellationToken cancellationToken)
    {
        var unit = await db.MigrationPlanUnits.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.PlanUnitId == planUnitId, cancellationToken);
        if (unit is null) return null;
        var identifiers = await db.MigrationPlanIdentifiers.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.PlanUnitId == planUnitId).OrderBy(row => row.PlanIdentifierId)
            .ToListAsync(cancellationToken);
        var events = await db.MigrationPlanEvents.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
            row.PlanUnitId == planUnitId).OrderBy(row => row.Sequence).ToListAsync(cancellationToken);
        var ownership = await db.MigrationPlanOwnership.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.PlanUnitId == planUnitId).OrderBy(row => row.PlanOwnershipId)
            .ToListAsync(cancellationToken);
        var entitlements = await db.MigrationPlanEntitlements.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.PlanUnitId == planUnitId).OrderBy(row => row.CoverageFamily)
            .ThenBy(row => row.PlanEntitlementId).ToListAsync(cancellationToken);
        var evidence = await db.MigrationPlanWarrantyEvidence.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.PlanUnitId == planUnitId).OrderBy(row => row.SourceKind)
            .ThenBy(row => row.PlanWarrantyEvidenceId).ToListAsync(cancellationToken);
        var links = await db.MigrationPlanReplacementLinks.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && (row.ReplacedPlanUnitId == planUnitId ||
            row.ReplacementPlanUnitId == planUnitId)).OrderBy(row => row.PlanReplacementLinkId)
            .ToListAsync(cancellationToken);
        var attachments = await db.MigrationPlanAttachments.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.PlanUnitId == planUnitId).OrderBy(row => row.PlanAttachmentId)
            .ToListAsync(cancellationToken);
        var gaps = await db.MigrationPlanGaps.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
            row.PlanUnitId == planUnitId).OrderBy(row => row.PlanGapId).ToListAsync(cancellationToken);
        var logs = await db.MigrationPlanLogs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
            row.PlanUnitId == planUnitId).OrderBy(row => row.PlanLogId).ToListAsync(cancellationToken);
        var certificate = await db.MigrationPlanCertificates.AsNoTracking().SingleAsync(row =>
            row.TenantId == tenant.TenantId && row.PlanUnitId == planUnitId, cancellationToken);
        return new(unit.PlanUnitId, unit.ItemCode, unit.ItemName, unit.Origin, unit.HistoryDepth,
            unit.LocationState, unit.WarehouseCode, unit.CustodyLocationKind, unit.ConditionGrade,
            unit.ConditionProvenance, unit.Note,
            identifiers.Select(row => new MigrationPlanIdentifierView(row.IdentifierType, row.RawValue,
                row.IssuerKind, row.SourceDataValue, row.Provenance)).ToList(),
            events.Select(row => new MigrationPlanEventView(row.Sequence, row.MovementKind, row.EffectiveDate,
                row.DocumentTypeLabel, row.DocumentNumber, row.PartyCode, row.WarehouseCode, row.Quantity,
                row.Provenance)).ToList(),
            ownership.Select(row => new MigrationPlanOwnershipView(row.Tier, row.PartyReference,
                row.PartyName, row.Pseudonymised, row.IsCurrent, row.EvidenceSource, row.EvidenceDate)).ToList(),
            entitlements.Select(row => new MigrationPlanEntitlementView(row.CoverageFamily, row.PolicyId,
                row.PolicyVersion, row.StartDate, row.EndDate, row.TerminationDate, row.RemainingDays,
                row.Expired, row.Direction, row.Provenance, row.DateSource, row.SourceKey,
                evidence.Select(EvidenceView).ToList())).ToList(),
            links.Select(row => new MigrationPlanReplacementView(row.PlanReplacementLinkId,
                row.ReplacedPlanUnitId, row.ReplacementPlanUnitId, row.PolicyApplied, row.SourceKey)).ToList(),
            attachments.Select(row => new MigrationPlanAttachmentView(row.PlanAttachmentId, row.Path,
                row.FileName, row.FileSize, row.ContentHash, row.SourceKey)).ToList(),
            new(certificate.Version, certificate.Provenance), gaps.Select(GapView).ToList(),
            logs.Select(row => new MigrationPlanLogView(row.PlanLogId, row.Kind, row.SourceKey,
                row.RelatedSourceKey, row.Note)).ToList());
    }

    private MigrationMapRun NewRun(MigrationReconstructionRun reconstruction, DateOnly cutover,
        bool allowedWaiting) => new()
    {
        TenantId = tenant.TenantId,
        RunId = Guid.CreateVersion7(clock.UtcNow),
        ReconstructionRunId = reconstruction.RunId,
        BatchId = reconstruction.BatchId,
        CutoverDate = cutover,
        AllowedWaitingReviews = allowedWaiting,
        RequestedAt = clock.UtcNow,
        Status = MappingRunStatus.Queued,
    };

    private async Task<MigrationReconstructionRun> ReadyReconstructionAsync(Guid batchId,
        CancellationToken cancellationToken)
    {
        if (!await db.MigrationBatches.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId, cancellationToken))
            throw new MappingRuleException("migration_batch_not_found", "The migration batch was not found.");
        var reconstruction = await db.MigrationReconstructionRuns.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batchId)
            .OrderByDescending(row => row.RequestedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        if (reconstruction is null)
            throw new MappingRuleException("migration_mapping_reconstruction_missing",
                "Complete history reconstruction for this batch before mapping it.");
        if (reconstruction.Status != ReconstructionRunStatus.Completed)
            throw new MappingRuleException("migration_mapping_reconstruction_not_completed",
                "The latest history reconstruction run has not completed successfully.");
        if (await profileStatus.IsLoadBlocked(batchId, cancellationToken))
            throw new MappingRuleException("migration_mapping_profile_blocked",
                "Mapping cannot start because profiling found a load-blocking anomaly.");
        return reconstruction;
    }

    private async Task<IReadOnlyList<MappingWaitingReviewCount>> WaitingReviewsAsync(
        MigrationReconstructionRun reconstruction, CancellationToken cancellationToken)
    {
        var rows = await db.MigrationReviewItems.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.RunId == reconstruction.CanonicalisationRunId && row.Status == MigrationReviewStatus.Waiting)
            .GroupBy(row => row.Kind).Select(group => new { Kind = group.Key, Count = group.LongCount() })
            .OrderBy(row => row.Kind).ToListAsync(cancellationToken);
        return rows.Select(row => new MappingWaitingReviewCount(row.Kind, row.Count)).ToList();
    }

    private async Task<MigrationMapRun?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var run = await db.MigrationMapRuns.Where(row => row.TenantId == tenant.TenantId &&
                row.Status == MappingRunStatus.Queued).OrderBy(row => row.RequestedAt).ThenBy(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        run.Status = MappingRunStatus.Running;
        run.StartedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    private async Task MapAsync(MigrationMapRun run, CancellationToken cancellationToken)
    {
        var reconstruction = await db.MigrationReconstructionRuns.AsNoTracking().SingleAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == run.ReconstructionRunId, cancellationToken);
        var batch = await db.MigrationBatches.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.BatchId == run.BatchId, cancellationToken);
        var waitingCandidateKeys = await WaitingCandidateKeysAsync(reconstruction, cancellationToken);
        run.WaitingCandidateCount = await db.MigrationUnitCandidates.LongCountAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == reconstruction.CanonicalisationRunId &&
            waitingCandidateKeys.Contains(row.PrimarySourceKey), cancellationToken);
        run.ExcludedCandidateCount = await db.MigrationUnitCandidates.LongCountAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == reconstruction.CanonicalisationRunId &&
            row.Disposition != CandidateDisposition.Active, cancellationToken);
        var excluded = await db.MigrationUnitCandidates.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == reconstruction.CanonicalisationRunId &&
                row.Disposition != CandidateDisposition.Active)
            .Select(row => new { row.PrimarySourceKey, row.Disposition }).ToListAsync(cancellationToken);
        foreach (var row in excluded) AddLog(run, null, PlanLogKind.CandidateExcluded, row.PrimarySourceKey, null,
            $"Candidate left out because its disposition is {row.Disposition}.");
        foreach (var sourceKey in waitingCandidateKeys) AddGap(run, null, PlanGapKind.WaitingReview,
            "Candidate left out of this rehearsal because its review is still waiting.", sourceKey);
        await db.SaveChangesAsync(cancellationToken);

        var catalogue = await policyCatalogue.ReadAsync(cancellationToken);
        var excludedEventKeys = await ExcludedEventKeysAsync(reconstruction, cancellationToken);
        Guid? after = null;
        while (true)
        {
            var query = db.MigrationUnitCandidates.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.RunId == reconstruction.CanonicalisationRunId && row.Disposition == CandidateDisposition.Active &&
                !waitingCandidateKeys.Contains(row.PrimarySourceKey));
            if (after is not null) query = query.Where(row => row.CandidateId.CompareTo(after.Value) > 0);
            var candidates = await query.OrderBy(row => row.CandidateId).Take(CandidateBatchSize)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0) break;
            await MapCandidatesAsync(run, reconstruction, batch, candidates, excludedEventKeys, catalogue,
                cancellationToken);
            after = candidates[^1].CandidateId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            run = await db.MigrationMapRuns.SingleAsync(row => row.TenantId == tenant.TenantId &&
                row.RunId == run.RunId, cancellationToken);
        }
        await MapReplacementLinksAsync(run, reconstruction, batch, cancellationToken);
        await MapAttachmentsAsync(run, batch, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task MapCandidatesAsync(MigrationMapRun run, MigrationReconstructionRun reconstruction,
        MigrationBatch batch, IReadOnlyList<MigrationUnitCandidate> candidates,
        HashSet<string> excludedEventKeys, PolicyCatalogue catalogue, CancellationToken cancellationToken)
    {
        var candidateIds = candidates.Select(row => row.CandidateId).ToList();
        var primaryKeys = candidates.Select(row => row.PrimarySourceKey).ToList();
        var masters = await db.Set<SerialMasterStagingRow>().AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batch.BatchId &&
                row.SourceSystem == batch.SourceSystem && primaryKeys.Contains(row.SourcePk))
            .ToDictionaryAsync(row => row.SourcePk, StringComparer.Ordinal, cancellationToken);
        var sources = await db.MigrationUnitCandidateSources.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == reconstruction.CanonicalisationRunId &&
                candidateIds.Contains(row.CandidateId)).ToListAsync(cancellationToken);
        var cardKeys = sources.Where(row => row.SourceKind == CandidateSourceKind.EquipmentCard)
            .Select(row => row.SourceKey).Distinct(StringComparer.Ordinal).ToList();
        var cards = await db.Set<EquipmentCardStagingRow>().AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batch.BatchId && cardKeys.Contains(row.SourcePk))
            .ToDictionaryAsync(row => row.SourcePk, StringComparer.Ordinal, cancellationToken);
        var cardIds = cards.Values.Where(row => row.CardId is not null).Select(row => row.CardId!.Value).ToList();
        var cardHistory = await db.Set<CardHistoryStagingRow>().AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batch.BatchId && row.CardId != null &&
                cardIds.Contains(row.CardId.Value)).ToListAsync(cancellationToken);
        var quantities = await (from quantity in db.Set<SerialQuantityStagingRow>().AsNoTracking()
            join master in db.Set<SerialMasterStagingRow>().AsNoTracking()
                on new { quantity.TenantId, quantity.BatchId, quantity.SourceSystem, quantity.ItemCode,
                    quantity.SystemNumber }
                equals new { master.TenantId, master.BatchId, master.SourceSystem, master.ItemCode,
                    master.SystemNumber }
            join candidate in db.MigrationUnitCandidates.AsNoTracking()
                on new { master.TenantId, PrimarySourceKey = master.SourcePk }
                equals new { candidate.TenantId, candidate.PrimarySourceKey }
            where quantity.TenantId == tenant.TenantId && quantity.BatchId == batch.BatchId &&
                  quantity.SourceSystem == batch.SourceSystem && candidateIds.Contains(candidate.CandidateId)
            select quantity).ToListAsync(cancellationToken);
        var history = await db.MigrationHistoryEvents.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ReconstructionRunId == reconstruction.RunId &&
                candidateIds.Contains(row.CandidateId)).OrderBy(row => row.CandidateId).ThenBy(row => row.Sequence)
            .ToListAsync(cancellationToken);
        var eventCandidateIds = history.Where(row => row.EventCandidateId is not null)
            .Select(row => row.EventCandidateId!.Value).Distinct().ToList();
        var eventSources = await db.MigrationEventCandidates.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && eventCandidateIds.Contains(row.EventCandidateId))
            .ToDictionaryAsync(row => row.EventCandidateId, row => row.TransactionSourceKey, cancellationToken);
        var episodes = await db.MigrationOwnershipEpisodes.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ReconstructionRunId == reconstruction.RunId &&
                candidateIds.Contains(row.CandidateId)).ToListAsync(cancellationToken);
        var entitlementCandidates = await db.MigrationEntitlementCandidates.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == reconstruction.CanonicalisationRunId &&
                candidateIds.Contains(row.CandidateId)).ToListAsync(cancellationToken);
        var contractLineKeys = entitlementCandidates.Where(row => row.SourceKind == CandidateSourceKind.ContractLine)
            .Select(row => row.SourceKey).Distinct(StringComparer.Ordinal).ToList();
        var contractLines = await db.Set<ContractLineStagingRow>().AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batch.BatchId &&
                contractLineKeys.Contains(row.SourcePk)).ToDictionaryAsync(row => row.SourcePk,
                StringComparer.Ordinal, cancellationToken);
        var contractIds = contractLines.Values.Where(row => row.ContractId is not null)
            .Select(row => row.ContractId!.Value).Distinct().ToList();
        var contracts = await db.Set<ServiceContractStagingRow>().AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batch.BatchId && row.ContractId != null &&
                contractIds.Contains(row.ContractId.Value)).ToListAsync(cancellationToken);
        var mappings = await db.TemplateMappings.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.SourceSystem == batch.SourceSystem).ToListAsync(cancellationToken);
        var approvedMappingIds = mappings.Where(row => row.State == TemplateMappingState.Approved)
            .Select(row => row.TemplateMappingId).ToList();
        var blocks = await db.TemplateMappingBlocks.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                approvedMappingIds.Contains(row.TemplateMappingId)).OrderBy(row => row.Position)
            .ToListAsync(cancellationToken);
        var partyCodes = cards.Values.SelectMany(card => new[] { card.CustomerCode, card.DirectCustomerCode })
            .Concat(episodes.Select(row => row.PartyCode)).Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!).Distinct(StringComparer.Ordinal).ToList();
        var partyNames = await db.ReferenceProjections.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.EntityType == "party" && row.Code != null &&
                partyCodes.Contains(row.Code)).Select(row => new { Code = row.Code!, row.DisplayName })
            .ToDictionaryAsync(row => row.Code, row => row.DisplayName, StringComparer.Ordinal, cancellationToken);
        var sourcesByCandidate = sources.ToLookup(row => row.CandidateId);
        var quantitiesBySerial = quantities.ToLookup(row => (row.ItemCode, row.SystemNumber));
        var historyByCandidate = history.ToLookup(row => row.CandidateId);
        var episodesByCandidate = episodes.ToLookup(row => row.CandidateId);
        var entitlementsByCandidate = entitlementCandidates.ToLookup(row => row.CandidateId);
        var cardHistoryByCard = cardHistory.Where(row => row.CardId is not null)
            .ToLookup(row => row.CardId!.Value);

        foreach (var candidate in candidates)
        {
            var master = masters[candidate.PrimarySourceKey];
            var candidateHistory = historyByCandidate[candidate.CandidateId].ToList();
            var retainedHistory = candidateHistory.Where(row => row.EventCandidateId is null ||
                !eventSources.TryGetValue(row.EventCandidateId.Value, out var sourceKey) ||
                !excludedEventKeys.Contains(sourceKey)).ToList();
            var positive = quantitiesBySerial[(master.ItemCode, master.SystemNumber)].Where(row => row.Quantity > 0)
                .OrderByDescending(row => row.Quantity).ThenBy(row => row.WarehouseCode).FirstOrDefault();
            var planUnitId = Guid.CreateVersion7(clock.UtcNow);
            var cardSource = sourcesByCandidate[candidate.CandidateId].FirstOrDefault(row =>
                row.SourceKind == CandidateSourceKind.EquipmentCard);
            var card = cardSource is null ? null : cards.GetValueOrDefault(cardSource.SourceKey);
            var lastMovement = retainedHistory.LastOrDefault()?.MovementKind;
            var condition = await settings.InferConditionAsync(card?.Status, lastMovement, cancellationToken);
            var plan = new MigrationPlanUnit
            {
                TenantId = tenant.TenantId,
                PlanUnitId = planUnitId,
                MapRunId = run.RunId,
                CandidateId = candidate.CandidateId,
                ItemCode = candidate.ItemCode,
                ItemName = candidate.ItemName,
                Origin = UnitOrigin.Migrated,
                HistoryDepth = MapHistoryDepth(candidate.HistoryDepth),
                LocationState = positive is null ? PlanLocationState.Shipped : PlanLocationState.InStock,
                WarehouseCode = positive?.WarehouseCode,
                CustodyLocationKind = positive is null ? null : LocationKind.Warehouse,
                ConditionGrade = condition,
                ConditionProvenance = "inferred",
                Note = candidate.Note,
            };
            db.MigrationPlanUnits.Add(plan);
            MapIdentifiers(run, plan, candidate, master);
            MapEvents(run, plan, candidateHistory, retainedHistory, eventSources, excludedEventKeys);
            MapOwnership(run, plan, card, card?.CardId is { } cardId ? cardHistoryByCard[cardId].ToList() : [],
                episodesByCandidate[candidate.CandidateId].ToList(), candidateHistory, partyNames);
            MapWarranty(run, plan, master, cardSource, card,
                entitlementsByCandidate[candidate.CandidateId].ToList(), contractLines, contracts, mappings,
                blocks, catalogue);
            db.MigrationPlanCertificates.Add(new MigrationPlanCertificate
            {
                TenantId = tenant.TenantId, PlanCertificateId = Guid.CreateVersion7(clock.UtcNow),
                MapRunId = run.RunId, PlanUnitId = plan.PlanUnitId, Version = 1,
                Provenance = EntitlementProvenance.Migrated,
            });
        }
    }

    private void MapIdentifiers(MigrationMapRun run, MigrationPlanUnit plan, MigrationUnitCandidate candidate,
        SerialMasterStagingRow master)
    {
        var provenance = string.IsNullOrWhiteSpace(master.DataSource) ? "source" : $"source:{master.DataSource}";
        var placeholder = string.IsNullOrWhiteSpace(candidate.StoredSerial);
        var internalValue = placeholder && master.SystemNumber is not null
            ? master.SystemNumber.Value.ToString(CultureInfo.InvariantCulture) : candidate.CanonicalSerial;
        db.MigrationPlanIdentifiers.Add(NewIdentifier(run, plan, IdentifierType.InternalSerial, internalValue,
            PlanIssuerKind.Tenant, master.DataSource, provenance));
        if (!string.IsNullOrWhiteSpace(master.ManufacturerSerial))
            db.MigrationPlanIdentifiers.Add(NewIdentifier(run, plan, IdentifierType.ManufacturerSerial,
                master.ManufacturerSerial.Trim(), PlanIssuerKind.Manufacturer, master.DataSource, provenance));
        if (placeholder)
            plan.Note = string.Join(" ", new[] { plan.Note,
                "The source had a placeholder serial; the source system number is the internal identifier." }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private MigrationPlanIdentifier NewIdentifier(MigrationMapRun run, MigrationPlanUnit plan, string type,
        string value, string issuer, string? sourceValue, string provenance) => new()
    {
        TenantId = tenant.TenantId, PlanIdentifierId = Guid.CreateVersion7(clock.UtcNow), MapRunId = run.RunId,
        PlanUnitId = plan.PlanUnitId, IdentifierType = type, RawValue = value, IssuerKind = issuer,
        SourceDataValue = sourceValue, Provenance = provenance,
    };

    private void MapEvents(MigrationMapRun run, MigrationPlanUnit plan,
        IReadOnlyList<MigrationHistoryEvent> all, IReadOnlyList<MigrationHistoryEvent> retained,
        Dictionary<Guid, string> eventSources, HashSet<string> excludedEventKeys)
    {
        var sequence = 0;
        foreach (var historyEvent in all)
        {
            var sourceKey = historyEvent.EventCandidateId is { } eventCandidateId &&
                eventSources.TryGetValue(eventCandidateId, out var value) ? value : null;
            if (sourceKey is not null && excludedEventKeys.Contains(sourceKey))
            {
                AddLog(run, plan.PlanUnitId, PlanLogKind.EventExcluded, sourceKey, null,
                    $"Reviewer excluded only this {historyEvent.MovementKind} event; the remaining history was retained.");
                continue;
            }
            if (!retained.Contains(historyEvent)) continue;
            db.MigrationPlanEvents.Add(new MigrationPlanEvent
            {
                TenantId = tenant.TenantId, PlanEventId = Guid.CreateVersion7(clock.UtcNow),
                MapRunId = run.RunId, PlanUnitId = plan.PlanUnitId, HistoryEventId = historyEvent.HistoryEventId,
                Sequence = ++sequence, MovementKind = historyEvent.MovementKind,
                EffectiveDate = historyEvent.EffectiveDate, DocumentTypeLabel = historyEvent.SourceDocumentTypeLabel,
                DocumentNumber = historyEvent.DocumentNumber, PartyCode = historyEvent.PartyCode,
                WarehouseCode = historyEvent.Warehouse, Quantity = historyEvent.Quantity,
                Provenance = historyEvent.Provenance,
            });
        }
    }

    private void MapOwnership(MigrationMapRun run, MigrationPlanUnit plan, EquipmentCardStagingRow? card,
        IReadOnlyList<CardHistoryStagingRow> cardHistory, IReadOnlyList<MigrationOwnershipEpisode> episodes,
        IReadOnlyList<MigrationHistoryEvent> history, Dictionary<string, string?> partyNames)
    {
        var standing = episodes.Where(row => row.ToHistoryEventId is null)
            .OrderByDescending(row => history.FirstOrDefault(item => item.HistoryEventId == row.FromHistoryEventId)
                ?.EffectiveDate).FirstOrDefault();
        var shipmentDate = standing is null ? (DateTime?)null : history.FirstOrDefault(row =>
            row.HistoryEventId == standing.FromHistoryEventId)?.EffectiveDate;
        var cardDate = card?.CardId is null ? null : cardHistory.Where(row => row.CardId == card.CardId &&
            row.ChangedAt is not null).Max(row => row.ChangedAt);
        var removed = SourceMappingFacts.PersonalDataRemoved(card?.PersonalDataStatus);

        if (card is not null && !string.IsNullOrWhiteSpace(card.DirectCustomerCode) &&
            !string.IsNullOrWhiteSpace(card.CustomerCode) && standing is not null &&
            card.CustomerCode != standing.PartyCode)
        {
            AddOwnership(run, plan, PlanOwnershipTier.DirectCustomer, card.DirectCustomerCode, removed,
                isCurrent: false, "equipment_card", cardDate, partyNames);
            AddOwnership(run, plan, PlanOwnershipTier.EndCustomer, card.CustomerCode, removed,
                isCurrent: true, "equipment_card", cardDate, partyNames);
            AddLog(run, plan.PlanUnitId, PlanLogKind.OwnershipChoice, card.SourcePk, null,
                "The card differs from the last shipment and names a direct customer, so the plan preserves direct and end customer tiers.");
            return;
        }

        var cardParty = card?.CustomerCode;
        var shipmentParty = standing?.PartyCode;
        string? chosen;
        string evidence;
        DateTime? evidenceDate;
        if (!string.IsNullOrWhiteSpace(cardParty) && !string.IsNullOrWhiteSpace(shipmentParty) &&
            cardParty != shipmentParty)
        {
            var cardIsNewer = cardDate is not null && (shipmentDate is null || cardDate > shipmentDate);
            chosen = cardIsNewer ? cardParty : shipmentParty;
            evidence = cardIsNewer ? "equipment_card" : "last_shipment";
            evidenceDate = cardIsNewer ? cardDate : shipmentDate;
            var timing = cardDate is null ? " The source has no card change time, so the shipment wins."
                : string.Empty;
            AddLog(run, plan.PlanUnitId, PlanLogKind.OwnershipChoice, card?.SourcePk, null,
                $"Card customer and last shipment differ; {evidence.Replace('_', ' ')} is current.{timing}");
            AddOwnership(run, plan, PlanOwnershipTier.CurrentCustomer, chosen, removed, true, evidence,
                evidenceDate, partyNames);
            var otherParty = cardIsNewer ? shipmentParty : cardParty;
            var otherEvidence = cardIsNewer ? "last_shipment" : "equipment_card";
            var otherDate = cardIsNewer ? shipmentDate : cardDate;
            AddOwnership(run, plan, PlanOwnershipTier.EvidenceCustomer, otherParty!, removed, false,
                otherEvidence, otherDate, partyNames);
            return;
        }
        else
        {
            chosen = cardParty ?? shipmentParty;
            evidence = cardParty is not null ? "equipment_card" : "last_shipment";
            evidenceDate = cardParty is not null ? cardDate : shipmentDate;
        }
        if (!string.IsNullOrWhiteSpace(chosen))
            AddOwnership(run, plan, PlanOwnershipTier.CurrentCustomer, chosen, removed, true, evidence,
                evidenceDate, partyNames);
    }

    private void AddOwnership(MigrationMapRun run, MigrationPlanUnit plan, string tier, string partyCode,
        bool pseudonymised, bool isCurrent, string evidence, DateTime? evidenceDate,
        Dictionary<string, string?> partyNames)
    {
        db.MigrationPlanOwnership.Add(new MigrationPlanOwnership
        {
            TenantId = tenant.TenantId, PlanOwnershipId = Guid.CreateVersion7(clock.UtcNow),
            MapRunId = run.RunId, PlanUnitId = plan.PlanUnitId, Tier = tier,
            PartyCode = pseudonymised ? null : partyCode,
            PartyName = pseudonymised ? null : partyNames.GetValueOrDefault(partyCode),
            PartyReference = pseudonymised ? PseudonymousReference(partyCode) : partyCode,
            Pseudonymised = pseudonymised, IsCurrent = isCurrent, EvidenceSource = evidence,
            EvidenceDate = evidenceDate,
        });
    }

    private void MapWarranty(MigrationMapRun run, MigrationPlanUnit plan, SerialMasterStagingRow master,
        MigrationUnitCandidateSource? cardSource, EquipmentCardStagingRow? card,
        IReadOnlyList<MigrationEntitlementCandidate> candidates,
        Dictionary<string, ContractLineStagingRow> contractLines,
        IReadOnlyList<ServiceContractStagingRow> contracts, IReadOnlyList<TemplateMapping> mappings,
        IReadOnlyList<TemplateMappingBlock> blocks, PolicyCatalogue catalogue)
    {
        var evidence = new List<MigrationPlanWarrantyEvidence>();
        foreach (var candidate in candidates.Where(row => row.SourceKind == CandidateSourceKind.ContractLine))
        {
            if (!contractLines.TryGetValue(candidate.SourceKey, out var line)) continue;
            var contract = contracts.FirstOrDefault(row => row.ContractId == line.ContractId &&
                SourceMappingFacts.IsWarrantyContract(row.ServiceType));
            if (contract is null) continue;
            evidence.Add(NewEvidence(run, plan, PlanWarrantyEvidenceSource.ContractLine, candidate.SourceKey,
                line.StartDate ?? contract.StartDate, line.EndDate ?? contract.EndDate,
                Earlier(line.TerminationDate, contract.TerminationDate), EntitlementDirection.Outbound));
        }
        if (card is not null && (card.WarrantyStart is not null || card.WarrantyEnd is not null))
            evidence.Add(NewEvidence(run, plan, PlanWarrantyEvidenceSource.EquipmentCard,
                cardSource?.SourceKey ?? card.SourcePk, card.WarrantyStart, card.WarrantyEnd, null,
                EntitlementDirection.Outbound));
        if (master.WarrantyStart is not null || master.WarrantyEnd is not null)
            evidence.Add(NewEvidence(run, plan, PlanWarrantyEvidenceSource.SerialMaster, master.SourcePk,
                master.WarrantyStart, master.WarrantyEnd, null, EntitlementDirection.Inbound));
        db.MigrationPlanWarrantyEvidence.AddRange(evidence);

        var contractEvidence = evidence.Where(row => row.SourceKind == PlanWarrantyEvidenceSource.ContractLine)
            .OrderByDescending(row => row.StartDate).ThenByDescending(row => row.EndDate)
            .ThenBy(row => row.SourceKey, StringComparer.Ordinal).FirstOrDefault();
        if (contractEvidence is not null)
        {
            contractEvidence.SelectedForEntitlement = true;
            var line = contractLines[contractEvidence.SourceKey];
            var contract = contracts.First(row => row.ContractId == line.ContractId &&
                SourceMappingFacts.IsWarrantyContract(row.ServiceType));
            if (string.IsNullOrWhiteSpace(contract.TemplateName))
            {
                AddGap(run, plan.PlanUnitId, PlanGapKind.NoTemplate,
                    "The warranty contract has no template.", contractEvidence.SourceKey);
                return;
            }
            var mapping = mappings.Where(row => row.TemplateName == contract.TemplateName)
                .OrderByDescending(row => row.ProposedAt).FirstOrDefault();
            if (mapping is null || mapping.State != TemplateMappingState.Approved)
            {
                AddGap(run, plan.PlanUnitId, PlanGapKind.TemplateNotApproved,
                    "The contract template has not been approved for migration.", contractEvidence.SourceKey);
                return;
            }
            var mappingBlocks = blocks.Where(row => row.TemplateMappingId == mapping.TemplateMappingId).ToList();
            var unpublished = mappingBlocks.Where(block => block.PolicyId is null || block.PolicyVersion is null ||
                !catalogue.Policies.Any(policy => policy.PolicyId == block.PolicyId &&
                    policy.Version == block.PolicyVersion && policy.FamilyCode == block.FamilyCode)).ToList();
            if (unpublished.Count != 0)
            {
                AddGap(run, plan.PlanUnitId, PlanGapKind.PolicyNotPublished,
                    "The approved template contains a policy that has not yet been published.",
                    contractEvidence.SourceKey);
                return;
            }
            foreach (var block in mappingBlocks)
                AddEntitlement(run, plan, block.FamilyCode, block.PolicyId, block.PolicyVersion,
                    contractEvidence, EntitlementDirection.Outbound);
            return;
        }
        var cardEvidence = evidence.FirstOrDefault(row =>
            row.SourceKind == PlanWarrantyEvidenceSource.EquipmentCard);
        if (cardEvidence is not null)
        {
            cardEvidence.SelectedForEntitlement = true;
            AddEntitlement(run, plan, CoverageFamilyCode.Base, null, null, cardEvidence,
                EntitlementDirection.Outbound);
            return;
        }
        var masterEvidence = evidence.FirstOrDefault(row => row.SourceKind == PlanWarrantyEvidenceSource.SerialMaster);
        if (masterEvidence is not null)
        {
            masterEvidence.SelectedForEntitlement = true;
            AddEntitlement(run, plan, CoverageFamilyCode.Supplier, null, null, masterEvidence,
                EntitlementDirection.Inbound);
        }
    }

    private MigrationPlanWarrantyEvidence NewEvidence(MigrationMapRun run, MigrationPlanUnit plan,
        string sourceKind, string sourceKey, DateTime? start, DateTime? end, DateTime? termination,
        string direction) => new()
    {
        TenantId = tenant.TenantId, PlanWarrantyEvidenceId = Guid.CreateVersion7(clock.UtcNow),
        MapRunId = run.RunId, PlanUnitId = plan.PlanUnitId, SourceKind = sourceKind, SourceKey = sourceKey,
        StartDate = ToDate(start), EndDate = ToDate(end), TerminationDate = ToDate(termination),
        Direction = direction,
    };

    private void AddEntitlement(MigrationMapRun run, MigrationPlanUnit plan, string family, Guid? policyId,
        int? policyVersion, MigrationPlanWarrantyEvidence evidence, string direction)
    {
        var end = Earlier(evidence.EndDate, evidence.TerminationDate);
        var remaining = end is null ? 0 : Math.Max(0, end.Value.DayNumber - run.CutoverDate.DayNumber);
        db.MigrationPlanEntitlements.Add(new MigrationPlanEntitlement
        {
            TenantId = tenant.TenantId, PlanEntitlementId = Guid.CreateVersion7(clock.UtcNow),
            MapRunId = run.RunId, PlanUnitId = plan.PlanUnitId, CoverageFamily = family,
            PolicyId = policyId, PolicyVersion = policyVersion, StartDate = evidence.StartDate, EndDate = end,
            TerminationDate = evidence.TerminationDate, RemainingDays = remaining,
            Expired = end is not null && end < run.CutoverDate, Direction = direction,
            Provenance = EntitlementProvenance.Migrated, DateSource = evidence.SourceKind,
            SourceKey = evidence.SourceKey,
        });
    }

    private async Task MapReplacementLinksAsync(MigrationMapRun run,
        MigrationReconstructionRun reconstruction, MigrationBatch batch, CancellationToken cancellationToken)
    {
        Guid? after = null;
        var seen = new HashSet<(int Replaced, int Replacement)>();
        while (true)
        {
            var unitQuery = db.MigrationPlanUnits.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.MapRunId == run.RunId);
            if (after is not null) unitQuery = unitQuery.Where(row => row.PlanUnitId.CompareTo(after.Value) > 0);
            var units = await unitQuery.OrderBy(row => row.PlanUnitId).Take(CandidateBatchSize)
                .ToListAsync(cancellationToken);
            if (units.Count == 0) break;
            var candidateIds = units.Select(row => row.CandidateId).ToList();
            var cardSources = await db.MigrationUnitCandidateSources.AsNoTracking().Where(row =>
                    row.TenantId == tenant.TenantId && row.RunId == reconstruction.CanonicalisationRunId &&
                    candidateIds.Contains(row.CandidateId) && row.SourceKind == CandidateSourceKind.EquipmentCard)
                .ToListAsync(cancellationToken);
            var cardKeys = cardSources.Select(row => row.SourceKey).ToList();
            var cards = await db.Set<EquipmentCardStagingRow>().AsNoTracking().Where(row =>
                    row.TenantId == tenant.TenantId && row.BatchId == batch.BatchId &&
                    cardKeys.Contains(row.SourcePk)).ToListAsync(cancellationToken);
            var pairs = new List<(int Replaced, int Replacement)>();
            foreach (var card in cards.Where(row => row.CardId is not null))
            {
                if (card.ReplacesCardId is { } replaces) pairs.Add((replaces, card.CardId!.Value));
                if (card.ReplacedByCardId is { } replacedBy) pairs.Add((card.CardId!.Value, replacedBy));
            }
            pairs = pairs.Where(seen.Add).ToList();
            var pairCardIds = pairs.SelectMany(pair => new[] { pair.Replaced, pair.Replacement }).Distinct()
                .ToList();
            var resolved = await (from card in db.Set<EquipmentCardStagingRow>().AsNoTracking()
                join source in db.MigrationUnitCandidateSources.AsNoTracking()
                    on new { card.TenantId, SourceKey = card.SourcePk }
                    equals new { source.TenantId, source.SourceKey }
                join unit in db.MigrationPlanUnits.AsNoTracking()
                    on new { source.TenantId, source.CandidateId }
                    equals new { unit.TenantId, unit.CandidateId }
                where card.TenantId == tenant.TenantId && card.BatchId == batch.BatchId &&
                      card.CardId != null && pairCardIds.Contains(card.CardId.Value) &&
                      source.RunId == reconstruction.CanonicalisationRunId &&
                      source.SourceKind == CandidateSourceKind.EquipmentCard && unit.MapRunId == run.RunId
                select new { card.CardId, card.SourcePk, unit.PlanUnitId })
                .ToListAsync(cancellationToken);
            var byCardId = resolved.ToDictionary(row => row.CardId.GetValueOrDefault());
            foreach (var pair in pairs)
            {
                if (!byCardId.TryGetValue(pair.Replaced, out var replaced) ||
                    !byCardId.TryGetValue(pair.Replacement, out var replacement))
                {
                    var existing = byCardId.GetValueOrDefault(pair.Replaced) ??
                        byCardId.GetValueOrDefault(pair.Replacement);
                    AddGap(run, existing?.PlanUnitId, PlanGapKind.ReplacementUnitMissing,
                        "The other side of the source replacement pair is not present in this plan.",
                        $"card:{pair.Replaced}->{pair.Replacement}");
                    continue;
                }
                db.MigrationPlanReplacementLinks.Add(new MigrationPlanReplacementLink
                {
                    TenantId = tenant.TenantId, PlanReplacementLinkId = Guid.CreateVersion7(clock.UtcNow),
                    MapRunId = run.RunId, ReplacedPlanUnitId = replaced.PlanUnitId,
                    ReplacementPlanUnitId = replacement.PlanUnitId, PolicyApplied = "unknown",
                    SourceKey = $"{replaced.SourcePk}->{replacement.SourcePk}",
                });
            }
            await db.SaveChangesAsync(cancellationToken);
            after = units[^1].PlanUnitId;
        }
    }

    private async Task MapAttachmentsAsync(MigrationMapRun run, MigrationBatch batch,
        CancellationToken cancellationToken)
    {
        var found = false;
        var after = string.Empty;
        while (true)
        {
            var attachments = await db.Set<AttachmentStagingRow>().AsNoTracking().Where(row =>
                    row.TenantId == tenant.TenantId && row.BatchId == batch.BatchId &&
                    row.SourceSystem == batch.SourceSystem && row.AbsentSinceBatchId == null &&
                    row.SourcePk.CompareTo(after) > 0).OrderBy(row => row.SourcePk)
                .Take(CandidateBatchSize).ToListAsync(cancellationToken);
            if (attachments.Count == 0) break;
            found = true;
            foreach (var source in attachments)
            {
                var path = CombineSourcePath(source);
                var fileName = string.Concat(source.FileName ?? "attachment",
                    string.IsNullOrWhiteSpace(source.FileExtension) ? "" : $".{source.FileExtension.TrimStart('.')}");
                db.MigrationPlanAttachments.Add(new MigrationPlanAttachment
                {
                    TenantId = tenant.TenantId, PlanAttachmentId = Guid.CreateVersion7(clock.UtcNow),
                    MapRunId = run.RunId, SourceKey = source.SourcePk, Path = path, FileName = fileName,
                    FileSize = source.FileSize, ContentHash = null,
                });
                if (path is null)
                    AddGap(run, null, PlanGapKind.AttachmentPathMissing,
                        "The attachment has no usable source or target path.", source.SourcePk);
                AddGap(run, null, PlanGapKind.AttachmentUnitUnknown,
                    "The extracted attachment row has no unit or equipment-card link; it is retained unattached for validation.",
                    source.SourcePk);
            }
            after = attachments[^1].SourcePk;
            await db.SaveChangesAsync(cancellationToken);
        }
        if (found)
            AddLog(run, null, PlanLogKind.SourceLimitation, null, null,
                "Attachment metadata is retained, but this source extraction does not provide a unit link. No files were copied or hashed during mapping.");
    }

    private async Task<HashSet<string>> WaitingCandidateKeysAsync(MigrationReconstructionRun reconstruction,
        CancellationToken cancellationToken)
    {
        if (!await db.MigrationReviewItems.AnyAsync(row => row.TenantId == tenant.TenantId &&
                row.RunId == reconstruction.CanonicalisationRunId && row.Status == MigrationReviewStatus.Waiting,
                cancellationToken)) return [];
        var json = await db.MigrationReviewItems.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.RunId == reconstruction.CanonicalisationRunId && row.Status == MigrationReviewStatus.Waiting)
            .Select(row => row.CandidateSourceKeysJson).ToListAsync(cancellationToken);
        return json.SelectMany(value => JsonSerializer.Deserialize<List<string>>(value) ?? [])
            .ToHashSet(StringComparer.Ordinal);
    }

    // OD-50: "exclude from history" drops only the events reconstruction flagged for review,
    // never the cancellation or duplicate-return rows it already removed for other reasons.
    private static readonly string[] FlaggedEventReasons =
    [
        ReconstructionLogReason.IssuedOut, ReconstructionLogReason.UnknownMovement,
        ReconstructionLogReason.ShippedTwiceWithoutReturn,
    ];

    private async Task<HashSet<string>> ExcludedEventKeysAsync(MigrationReconstructionRun reconstruction,
        CancellationToken cancellationToken)
    {
        var excludedCandidates = await (from item in db.MigrationReviewItems.AsNoTracking()
            join decision in db.MigrationReviewDecisions.AsNoTracking()
                on new { item.TenantId, item.ReviewItemId } equals new { decision.TenantId, decision.ReviewItemId }
            where item.TenantId == tenant.TenantId && item.RunId == reconstruction.CanonicalisationRunId &&
                  item.Kind == MigrationReviewKind.HistoryNeedsReview &&
                  decision.Decision == MigrationReviewDecisionKind.ExcludeFromHistory
            select item.CandidateSourceKeysJson).ToListAsync(cancellationToken);
        var primaryKeys = excludedCandidates.SelectMany(value =>
                JsonSerializer.Deserialize<List<string>>(value) ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (primaryKeys.Count == 0) return [];
        var candidateIds = await db.MigrationUnitCandidates.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == reconstruction.CanonicalisationRunId &&
                primaryKeys.Contains(row.PrimarySourceKey)).Select(row => row.CandidateId)
            .ToListAsync(cancellationToken);
        return await db.MigrationReconstructionLogs.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ReconstructionRunId == reconstruction.RunId &&
                candidateIds.Contains(row.CandidateId) && row.SourceKey != null &&
                FlaggedEventReasons.Contains(row.ReasonCode))
            .Select(row => row.SourceKey!).ToHashSetAsync(cancellationToken);
    }

    private async Task<MigrationMapRun?> LatestCompletedRunAsync(Guid batchId,
        CancellationToken cancellationToken) => await db.MigrationMapRuns.AsNoTracking().Where(row =>
            row.TenantId == tenant.TenantId && row.BatchId == batchId && row.Status == MappingRunStatus.Completed)
        .OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.RunId)
        .FirstOrDefaultAsync(cancellationToken);

    private async Task MarkFailedAsync(Guid runId, string code, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var failed = await db.MigrationMapRuns.SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId, cancellationToken);
        failed.Status = MappingRunStatus.Failed;
        failed.FailureCode = code;
        failed.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private void AddGap(MigrationMapRun run, Guid? planUnitId, string kind, string reason, string? sourceKey) =>
        db.MigrationPlanGaps.Add(new MigrationPlanGap
        {
            TenantId = tenant.TenantId, PlanGapId = Guid.CreateVersion7(clock.UtcNow), MapRunId = run.RunId,
            PlanUnitId = planUnitId, Kind = kind, Reason = reason, SourceKey = sourceKey,
        });

    private void AddLog(MigrationMapRun run, Guid? planUnitId, string kind, string? sourceKey,
        string? relatedSourceKey, string note) => db.MigrationPlanLogs.Add(new MigrationPlanLog
    {
        TenantId = tenant.TenantId, PlanLogId = Guid.CreateVersion7(clock.UtcNow), MapRunId = run.RunId,
        PlanUnitId = planUnitId, Kind = kind, SourceKey = sourceKey, RelatedSourceKey = relatedSourceKey,
        Note = note,
    });

    private static DateTime? Earlier(DateTime? first, DateTime? second) => first is null ? second :
        second is null ? first : first < second ? first : second;
    private static DateOnly? Earlier(DateOnly? first, DateOnly? second) => first is null ? second :
        second is null ? first : first < second ? first : second;
    private static DateOnly? ToDate(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);
    private static string MapHistoryDepth(string? value) => value switch
    {
        Reconstruction.HistoryDepth.Full => Serialisation.Contracts.HistoryDepth.Full,
        Reconstruction.HistoryDepth.Partial => Serialisation.Contracts.HistoryDepth.Partial,
        _ => Serialisation.Contracts.HistoryDepth.OpeningOnly,
    };
    private static string PseudonymousReference(string partyCode) => "pseudonymised:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(partyCode))).ToLowerInvariant()[..24];
    private static string? CombineSourcePath(AttachmentStagingRow source)
    {
        var root = source.SourcePath ?? source.TargetPath;
        if (string.IsNullOrWhiteSpace(root)) return null;
        return string.IsNullOrWhiteSpace(source.SubPath) ? root : Path.Combine(root, source.SubPath);
    }
    private static MigrationPlanGapView GapView(MigrationPlanGap row) => new(row.PlanGapId,
        row.PlanUnitId, row.Kind, row.Reason, row.SourceKey);
    private static MigrationPlanWarrantyEvidenceView EvidenceView(MigrationPlanWarrantyEvidence row) =>
        new(row.SourceKind, row.SourceKey, row.StartDate, row.EndDate, row.TerminationDate, row.Direction,
            row.SelectedForEntitlement);
    private static string EncodeCursor(Guid id) => Convert.ToBase64String(id.ToByteArray());
    private static Guid DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return Guid.Empty;
        try { return new Guid(Convert.FromBase64String(cursor)); }
        catch (FormatException)
        {
            throw new MappingRuleException("migration_mapping_cursor_invalid", "The page cursor is invalid.");
        }
    }

    private static async Task<IReadOnlyList<MappingCount>> CountsAsync<TEntity>(IQueryable<TEntity> query,
        System.Linq.Expressions.Expression<Func<TEntity, string>> key, CancellationToken cancellationToken)
        where TEntity : class
    {
        var rows = await query.GroupBy(key).Select(group => new { Key = group.Key, Count = group.LongCount() })
            .OrderBy(row => row.Key).ToListAsync(cancellationToken);
        return rows.Select(row => new MappingCount(row.Key, row.Count)).ToList();
    }
}



