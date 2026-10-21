using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.LegacyMigration.Reconstruction;

public sealed class ReconstructionService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IMigrationProfileStatus profileStatus,
    IMigrationReviewCarryOver reviewQueue,
    SourceHistoryReader sourceHistory)
{
    private const int CandidateBatchSize = 1000;

    public async Task<StartReconstructionResponse> EnqueueAsync(Guid batchId,
        CancellationToken cancellationToken)
    {
        var canonical = await ReadyCanonicalisationAsync(batchId, cancellationToken);
        if (await db.MigrationReconstructionRuns.AnyAsync(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId && (row.Status == ReconstructionRunStatus.Queued ||
                row.Status == ReconstructionRunStatus.Running), cancellationToken))
            throw new MigrationRuleException("migration_reconstruction_already_running",
                "History reconstruction is already queued or running for this batch.");
        var run = NewRun(canonical);
        db.MigrationReconstructionRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return new(run.RunId, run.BatchId, run.Status);
    }

    public async Task<int> EnqueueEligibleAsync(CancellationToken cancellationToken)
    {
        var eligible = await db.MigrationCanonRuns.AsNoTracking().Where(canonical =>
                canonical.TenantId == tenant.TenantId && canonical.Status == CanonicalisationRunStatus.Completed &&
                !db.MigrationReconstructionRuns.Any(run => run.TenantId == canonical.TenantId &&
                    run.CanonicalisationRunId == canonical.RunId))
            .OrderBy(canonical => canonical.FinishedAt).ThenBy(canonical => canonical.RunId)
            .ToListAsync(cancellationToken);
        foreach (var canonical in eligible) db.MigrationReconstructionRuns.Add(NewRun(canonical));
        if (eligible.Count != 0) await db.SaveChangesAsync(cancellationToken);
        return eligible.Count;
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
                await ReconstructAsync(run, cancellationToken);
                db.ChangeTracker.Clear();
                run = await db.MigrationReconstructionRuns.SingleAsync(row => row.TenantId == tenant.TenantId &&
                    row.RunId == run.RunId, cancellationToken);
                await reviewQueue.ApplyCarryOversAsync(run.CanonicalisationRunId, cancellationToken);
                run.Status = ReconstructionRunStatus.Completed;
                run.FinishedAt = clock.UtcNow;
                run.CandidatesQueuedForReview = await db.MigrationReviewItems.LongCountAsync(row =>
                    row.TenantId == tenant.TenantId && row.RunId == run.CanonicalisationRunId &&
                    row.Kind == MigrationReviewKind.HistoryNeedsReview, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await MarkFailedAsync(run.RunId, "migration_reconstruction_cancelled", CancellationToken.None);
                throw;
            }
            catch
            {
                await MarkFailedAsync(run.RunId, "migration_reconstruction_failed", CancellationToken.None);
            }
        }
        return completed;
    }

    public async Task<ReconstructionView?> GetAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var run = await db.MigrationReconstructionRuns.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batchId)
            .OrderByDescending(row => row.RequestedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null) return null;
        var eventCounts = await db.MigrationHistoryEvents.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ReconstructionRunId == run.RunId)
            .GroupBy(row => row.MovementKind).Select(group => new { Key = group.Key, Count = group.LongCount() })
            .OrderBy(row => row.Key).ToListAsync(cancellationToken);
        var depthCounts = await db.MigrationUnitCandidates.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == run.CanonicalisationRunId &&
                row.HistoryDepth != null)
            .GroupBy(row => row.HistoryDepth!).Select(group => new { Key = group.Key, Count = group.LongCount() })
            .OrderBy(row => row.Key).ToListAsync(cancellationToken);
        var reviewReasons = await db.MigrationReconstructionLogs.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ReconstructionRunId == run.RunId &&
                (row.ReasonCode == ReconstructionLogReason.IssuedOut ||
                 row.ReasonCode == ReconstructionLogReason.UnknownMovement ||
                 row.ReasonCode == ReconstructionLogReason.ShippedTwiceWithoutReturn ||
                 row.ReasonCode == ReconstructionLogReason.QuantitySumNotZeroOrOne))
            .GroupBy(row => row.ReasonCode).Select(group => new
            {
                Key = group.Key,
                Count = group.Select(row => row.CandidateId).Distinct().LongCount(),
            }).OrderBy(row => row.Key).ToListAsync(cancellationToken);
        return new(run.RunId, run.BatchId, run.CanonicalisationRunId, run.StartedAt, run.FinishedAt,
            run.Status, run.FailureCode, run.CandidatesReconstructed, run.CandidatesSkippedByDisposition,
            eventCounts.Select(row => new ReconstructionCount(row.Key, row.Count)).ToList(),
            depthCounts.Select(row => new ReconstructionCount(row.Key, row.Count)).ToList(),
            run.CancellationPairsRemoved,
            reviewReasons.Select(row => new ReconstructionCount(row.Key, row.Count)).ToList());
    }

    public async Task<MigrationCandidateHistoryView?> HistoryAsync(Guid candidateId, string? cursor, int take,
        CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var candidate = await db.MigrationUnitCandidates.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.CandidateId == candidateId, cancellationToken);
        if (candidate is null) return null;
        var run = await db.MigrationReconstructionRuns.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.CanonicalisationRunId == candidate.RunId &&
                row.Status == ReconstructionRunStatus.Completed)
            .OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null) return null;
        var after = DecodeCursor(cursor);
        var events = await db.MigrationHistoryEvents.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ReconstructionRunId == run.RunId &&
                row.CandidateId == candidateId && row.Sequence > after)
            .OrderBy(row => row.Sequence).Take(take + 1).ToListAsync(cancellationToken);
        var page = events.Take(take).ToList();
        var episodes = await db.MigrationOwnershipEpisodes.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ReconstructionRunId == run.RunId &&
                row.CandidateId == candidateId)
            .OrderBy(row => row.FromHistoryEventId).ToListAsync(cancellationToken);
        var logs = await db.MigrationReconstructionLogs.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.ReconstructionRunId == run.RunId &&
                row.CandidateId == candidateId)
            .OrderBy(row => row.LogId).ToListAsync(cancellationToken);
        var partyCodes = page.Select(row => row.PartyCode).Concat(episodes.Select(row => row.PartyCode))
            .Where(value => value is not null).Select(value => value!).Distinct(StringComparer.Ordinal).ToList();
        var partyRows = await db.ReferenceProjections.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.EntityType == "party" && row.Code != null &&
                partyCodes.Contains(row.Code))
            .Select(row => new { Code = row.Code!, row.DisplayName }).ToListAsync(cancellationToken);
        var partyNames = partyRows.ToDictionary(row => row.Code, row => row.DisplayName, StringComparer.Ordinal);
        string? Party(string? code) => code is null ? null : partyNames.GetValueOrDefault(code) ?? code;
        return new(candidate.CandidateId, candidate.ItemCode, candidate.ItemName,
            candidate.StoredSerial ?? candidate.CanonicalSerial,
            candidate.HistoryDepth ?? Reconstruction.HistoryDepth.OpeningBalanceOnly,
            page.Select(row => new MigrationHistoryEventView(row.HistoryEventId, row.Sequence, row.MovementKind,
                MovementLabel(row.MovementKind), row.EffectiveDate, row.SourceDocumentTypeLabel,
                row.DocumentNumber, Party(row.PartyCode), row.Warehouse, row.Quantity, row.Provenance)).ToList(),
            events.Count > take ? page[^1].Sequence.ToString(CultureInfo.InvariantCulture) : null,
            episodes.Select(row => new MigrationOwnershipEpisodeView(row.OwnershipEpisodeId,
                Party(row.PartyCode)!, row.FromHistoryEventId, row.ToHistoryEventId, row.OpenReason,
                row.CloseReason)).ToList(),
            logs.Select(row => new MigrationReconstructionLogView(row.LogId, row.SourceKey,
                row.RelatedSourceKey, row.ReasonCode, row.Note)).ToList());
    }

    private MigrationReconstructionRun NewRun(MigrationCanonRun canonical) => new()
    {
        TenantId = tenant.TenantId,
        RunId = Guid.CreateVersion7(clock.UtcNow),
        CanonicalisationRunId = canonical.RunId,
        BatchId = canonical.BatchId,
        RequestedAt = clock.UtcNow,
        Status = ReconstructionRunStatus.Queued,
    };

    private async Task<MigrationCanonRun> ReadyCanonicalisationAsync(Guid batchId,
        CancellationToken cancellationToken)
    {
        if (!await db.MigrationBatches.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId, cancellationToken))
            throw new MigrationRuleException("migration_batch_not_found", "The migration batch was not found.");
        var canonical = await db.MigrationCanonRuns.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batchId)
            .OrderByDescending(row => row.RequestedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        if (canonical is null)
            throw new MigrationRuleException("migration_reconstruction_canonicalisation_missing",
                "Complete canonicalisation for this batch before reconstructing history.");
        if (canonical.Status != CanonicalisationRunStatus.Completed)
            throw new MigrationRuleException("migration_reconstruction_canonicalisation_not_completed",
                "The latest canonicalisation run has not completed successfully.");
        if (await profileStatus.IsLoadBlocked(batchId, cancellationToken))
            throw new MigrationRuleException("migration_reconstruction_profile_blocked",
                "History reconstruction cannot start because profiling found a load-blocking anomaly.");
        return canonical;
    }

    private async Task<MigrationReconstructionRun?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var run = await db.MigrationReconstructionRuns.Where(row => row.TenantId == tenant.TenantId &&
                row.Status == ReconstructionRunStatus.Queued)
            .OrderBy(row => row.RequestedAt).ThenBy(row => row.RunId).FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        run.Status = ReconstructionRunStatus.Running;
        run.StartedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    private async Task ReconstructAsync(MigrationReconstructionRun run, CancellationToken cancellationToken)
    {
        run.CandidatesSkippedByDisposition = await db.MigrationUnitCandidates.LongCountAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == run.CanonicalisationRunId &&
            row.Disposition != CandidateDisposition.Active, cancellationToken);
        var batch = await db.MigrationBatches.AsNoTracking().SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.BatchId == run.BatchId, cancellationToken);
        Guid? lastCandidateId = null;
        while (true)
        {
            var query = db.MigrationUnitCandidates.Where(row => row.TenantId == tenant.TenantId &&
                row.RunId == run.CanonicalisationRunId && row.Disposition == CandidateDisposition.Active);
            if (lastCandidateId is not null)
                query = query.Where(row => row.CandidateId.CompareTo(lastCandidateId.Value) > 0);
            var candidates = await query.OrderBy(row => row.CandidateId).Take(CandidateBatchSize)
                .ToListAsync(cancellationToken);
            if (candidates.Count == 0) break;
            var ids = candidates.Select(row => row.CandidateId).ToList();
            var sourceRows = await sourceHistory.ReadAsync(run.CanonicalisationRunId, ids, cancellationToken);
            var sourceMasters = await db.Set<SerialMasterStagingRow>().AsNoTracking().Where(row =>
                    row.TenantId == tenant.TenantId && row.BatchId == run.BatchId &&
                    row.SourceSystem == batch.SourceSystem && row.AbsentSinceBatchId == null &&
                    candidates.Select(candidate => candidate.PrimarySourceKey).Contains(row.SourcePk))
                .Select(row => new { row.SourcePk, row.InboundDate, row.ExtractedAt })
                .ToListAsync(cancellationToken);
            var masters = sourceMasters.ToDictionary(row => row.SourcePk, StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                var rows = sourceRows.Where(row => row.CandidateId == candidate.CandidateId)
                    .OrderBy(row => row.DocumentDate ?? DateTime.MaxValue)
                    .ThenBy(row => row.CreatedDate ?? DateTime.MaxValue).ThenBy(row => row.CreatedTime ?? int.MaxValue)
                    .ThenBy(row => row.LogEntry ?? int.MaxValue).ThenBy(row => row.TransactionSourceKey,
                        StringComparer.Ordinal).ToList();
                var master = masters.GetValueOrDefault(candidate.PrimarySourceKey);
                ProcessCandidate(run, batch.SourceSystem, candidate, rows, master?.InboundDate,
                    master?.ExtractedAt ?? batch.FinishedAt ?? clock.UtcNow);
            }
            run.CandidatesReconstructed += candidates.Count;
            lastCandidateId = candidates[^1].CandidateId;
            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            run = await db.MigrationReconstructionRuns.SingleAsync(row => row.TenantId == tenant.TenantId &&
                row.RunId == run.RunId, cancellationToken);
        }
    }

    private void ProcessCandidate(MigrationReconstructionRun run, string sourceSystem,
        MigrationUnitCandidate candidate, List<SourceHistoryMovementRow> rows, DateTime? inboundDate,
        DateTime extractionDate)
    {
        var removed = new HashSet<int>();
        var byDocument = rows.Select((row, index) => (row, index)).Where(item => item.row.DocumentEntry is not null)
            .GroupBy(item => new { item.row.SerialMasterSourceKey, item.row.CancellationFamily,
                item.row.DocumentEntry })
            .ToDictionary(group => group.Key, group => group.ToList());
        for (var index = 0; index < rows.Count; index++)
        {
            var cancellation = rows[index];
            if (cancellation.BaseEntry is null || cancellation.BaseCancellationFamily is null ||
                cancellation.BaseCancellationFamily != cancellation.CancellationFamily) continue;
            var key = new { cancellation.SerialMasterSourceKey, cancellation.CancellationFamily,
                DocumentEntry = cancellation.BaseEntry };
            if (!byDocument.TryGetValue(key, out var originals)) continue;
            var original = originals.FirstOrDefault(item => item.index != index &&
                Math.Sign(item.row.Quantity) == -Math.Sign(cancellation.Quantity));
            if (original.row is null) continue;
            if (!removed.Add(original.index) | !removed.Add(index)) continue;
            run.CancellationPairsRemoved++;
            AddLog(run, candidate, original.row.TransactionSourceKey, cancellation.TransactionSourceKey,
                ReconstructionLogReason.CancelledInSource,
                "Cancelled in the source. The original and reversal produce no history event.");
            AddLog(run, candidate, cancellation.TransactionSourceKey, original.row.TransactionSourceKey,
                ReconstructionLogReason.CancelledInSource,
                "Cancelled in the source. Delivery cancellations are verified; other document types are expected to follow the same source pattern and must be checked before cutover.");
        }

        for (var index = 0; index < rows.Count; index++)
        {
            if (removed.Contains(index) || !rows[index].SuppressWhenBasedOnCustomerReturn ||
                rows[index].BaseEntry is null) continue;
            var duplicate = rows.Any(other => other.SerialMasterSourceKey == rows[index].SerialMasterSourceKey &&
                other.DocumentEntry == rows[index].BaseEntry &&
                other.Kind == SourceMovementKind.CustomerReturned);
            if (!duplicate) continue;
            removed.Add(index);
            AddLog(run, candidate, rows[index].TransactionSourceKey, null,
                ReconstructionLogReason.DuplicateCustomerReturn,
                "The credit memo is based on the recorded customer return, so it does not create a second return event.");
        }

        var standing = candidate.StoredSerial is null
            ? []
            : rows.Select((row, index) => (row, index)).Where(item => !removed.Contains(item.index) &&
                item.row.Kind != SourceMovementKind.Ignored).ToList();
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);
        var history = new List<MigrationHistoryEvent>();
        var consumedTransfers = new HashSet<int>();
        foreach (var outgoing in standing.Where(item => item.row.Kind == SourceMovementKind.TransferredOut))
        {
            var incoming = standing.FirstOrDefault(item => !consumedTransfers.Contains(item.index) &&
                item.row.Kind == SourceMovementKind.TransferredIn && item.row.CancellationFamily ==
                outgoing.row.CancellationFamily && item.row.DocumentEntry == outgoing.row.DocumentEntry);
            if (incoming.row is null) continue;
            consumedTransfers.Add(outgoing.index);
            consumedTransfers.Add(incoming.index);
            history.Add(NewEvent(run, candidate, incoming.row, HistoryMovementKind.Transferred,
                $"{outgoing.row.WarehouseCode ?? "?"} → {incoming.row.WarehouseCode ?? "?"}",
                Math.Abs(incoming.row.Quantity)));
        }
        foreach (var item in standing)
        {
            if (consumedTransfers.Contains(item.index)) continue;
            var kind = item.row.Kind switch
            {
                SourceMovementKind.Received => HistoryMovementKind.Received,
                SourceMovementKind.Produced => HistoryMovementKind.Produced,
                SourceMovementKind.Consumed => HistoryMovementKind.Consumed,
                SourceMovementKind.Shipped => HistoryMovementKind.Shipped,
                SourceMovementKind.CustomerReturned => HistoryMovementKind.CustomerReturned,
                SourceMovementKind.VendorReturned => HistoryMovementKind.VendorReturned,
                SourceMovementKind.IssuedOut => HistoryMovementKind.IssuedOut,
                _ => HistoryMovementKind.Unknown,
            };
            if (item.row.Kind is SourceMovementKind.TransferredIn or SourceMovementKind.TransferredOut)
                kind = HistoryMovementKind.Unknown;
            history.Add(NewEvent(run, candidate, item.row, kind, item.row.WarehouseCode, item.row.Quantity));
            if (kind == HistoryMovementKind.IssuedOut)
            {
                reasons.TryAdd(ReconstructionLogReason.IssuedOut,
                    "Inventory was issued out without a shipment and needs review.");
                AddLog(run, candidate, item.row.TransactionSourceKey, null, ReconstructionLogReason.IssuedOut,
                    "Inventory issue recorded as issued out; it is never treated as a shipment.");
            }
            if (kind == HistoryMovementKind.Unknown)
            {
                reasons.TryAdd(ReconstructionLogReason.UnknownMovement,
                    "A source movement type is unknown or a transfer side is unpaired.");
                AddLog(run, candidate, item.row.TransactionSourceKey, null,
                    ReconstructionLogReason.UnknownMovement,
                    $"{item.row.DocumentLabel} was not guessed and needs review.");
            }
        }

        var signedQuantity = standing.Sum(item => item.row.Quantity);
        if (signedQuantity is not (0m or 1m))
        {
            reasons.TryAdd(ReconstructionLogReason.QuantitySumNotZeroOrOne,
                $"The standing signed quantity is {signedQuantity.ToString(CultureInfo.InvariantCulture)}, not 0 or 1.");
            AddLog(run, candidate, null, null, ReconstructionLogReason.QuantitySumNotZeroOrOne,
                reasons[ReconstructionLogReason.QuantitySumNotZeroOrOne]);
        }

        history = history.OrderBy(row => row.EffectiveDate).ThenBy(row =>
            rows.FindIndex(source => source.EventCandidateId == row.EventCandidateId)).ToList();
        if (history.Count == 0)
        {
            var earliest = rows.Select(row => row.DocumentDate ?? row.CreatedDate).Where(value => value is not null)
                .Min() ?? extractionDate;
            history.Add(new MigrationHistoryEvent
            {
                TenantId = tenant.TenantId,
                HistoryEventId = Guid.CreateVersion7(clock.UtcNow),
                ReconstructionRunId = run.RunId,
                CandidateId = candidate.CandidateId,
                MovementKind = HistoryMovementKind.OpeningBalance,
                EffectiveDate = inboundDate ?? earliest,
                SourceDocumentTypeLabel = "Opening balance",
                Quantity = 1m,
                Provenance = HistoryProvenance.Reconstructed,
            });
            candidate.HistoryDepth = HistoryDepth.OpeningBalanceOnly;
        }
        else
            candidate.HistoryDepth = history[0].MovementKind is HistoryMovementKind.Received or HistoryMovementKind.Produced
                ? HistoryDepth.Full : HistoryDepth.Partial;

        for (var index = 0; index < history.Count; index++) history[index].Sequence = index + 1;
        db.MigrationHistoryEvents.AddRange(history);
        MigrationOwnershipEpisode? open = null;
        foreach (var historyEvent in history)
        {
            if (historyEvent.MovementKind == HistoryMovementKind.Shipped && historyEvent.PartyCode is not null)
            {
                if (open is not null)
                {
                    open.ToHistoryEventId = historyEvent.HistoryEventId;
                    open.CloseReason = "shipment_without_return";
                    reasons.TryAdd(ReconstructionLogReason.ShippedTwiceWithoutReturn,
                        "Shipped twice with no return between.");
                    AddLog(run, candidate, EventSourceKey(historyEvent, rows), null,
                        ReconstructionLogReason.ShippedTwiceWithoutReturn,
                        "Shipped twice with no return between.");
                }
                open = new MigrationOwnershipEpisode
                {
                    TenantId = tenant.TenantId,
                    OwnershipEpisodeId = Guid.CreateVersion7(clock.UtcNow),
                    ReconstructionRunId = run.RunId,
                    CandidateId = candidate.CandidateId,
                    PartyCode = historyEvent.PartyCode,
                    FromHistoryEventId = historyEvent.HistoryEventId,
                    OpenReason = "shipment",
                };
                db.MigrationOwnershipEpisodes.Add(open);
            }
            else if (historyEvent.MovementKind == HistoryMovementKind.CustomerReturned && open is not null)
            {
                open.ToHistoryEventId = historyEvent.HistoryEventId;
                open.CloseReason = "customer_return";
                open = null;
            }
        }
        if (reasons.Count != 0) AddReviewItem(run, sourceSystem, candidate, rows, reasons);
    }

    private void AddReviewItem(MigrationReconstructionRun run, string sourceSystem,
        MigrationUnitCandidate candidate, IReadOnlyList<SourceHistoryMovementRow> rows,
        IReadOnlyDictionary<string, string> reasons)
    {
        var reasonKeys = reasons.Keys.Order(StringComparer.Ordinal).ToList();
        var sourceKeys = rows.Select(row => row.TransactionSourceKey).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        var fingerprintKeys = new List<string> { candidate.PrimarySourceKey };
        fingerprintKeys.AddRange(reasonKeys);
        fingerprintKeys.AddRange(sourceKeys);
        var itemCodes = new List<string> { candidate.ItemCode };
        db.MigrationReviewItems.Add(new MigrationReviewItem
        {
            TenantId = tenant.TenantId,
            ReviewItemId = Guid.CreateVersion7(clock.UtcNow),
            RunId = run.CanonicalisationRunId,
            BatchId = run.BatchId,
            SourceSystem = sourceSystem,
            Kind = MigrationReviewKind.HistoryNeedsReview,
            Status = MigrationReviewStatus.Waiting,
            Fingerprint = MigrationReviewIdentity.Fingerprint(sourceSystem,
                MigrationReviewKind.HistoryNeedsReview, candidate.CanonicalSerial, itemCodes, fingerprintKeys),
            CorrelationKey = MigrationReviewIdentity.Correlation(sourceSystem,
                MigrationReviewKind.HistoryNeedsReview, candidate.CanonicalSerial, itemCodes),
            CanonicalKey = candidate.CanonicalSerial,
            ItemCode = candidate.ItemCode,
            ItemName = candidate.ItemName,
            Family = candidate.Family,
            StoredSerial = candidate.StoredSerial,
            RuleTried = "history_reconstruction",
            SourceKeysJson = JsonSerializer.Serialize(sourceKeys),
            CandidateSourceKeysJson = JsonSerializer.Serialize(new[] { candidate.PrimarySourceKey }),
            CandidateSummary = string.Join("; ", reasonKeys.Select(key => reasons[key])),
            Suggestion = MigrationReviewDecisionKind.AcceptAsRecorded,
            SuggestionReason = "Accept only after the source history has been checked.",
            Hint = "Choose accept as recorded, exclude from history, or fix in the source application.",
            CreatedAt = clock.UtcNow,
        });
    }

    private MigrationHistoryEvent NewEvent(MigrationReconstructionRun run, MigrationUnitCandidate candidate,
        SourceHistoryMovementRow row, string kind, string? warehouse, decimal quantity) => new()
    {
        TenantId = tenant.TenantId,
        HistoryEventId = Guid.CreateVersion7(clock.UtcNow),
        ReconstructionRunId = run.RunId,
        CandidateId = candidate.CandidateId,
        MovementKind = kind,
        EffectiveDate = row.DocumentDate ?? row.CreatedDate ?? run.StartedAt ?? clock.UtcNow,
        SourceDocumentTypeLabel = row.DocumentLabel,
        DocumentNumber = row.DocumentNumber,
        PartyCode = row.PartyCode,
        Warehouse = warehouse,
        Quantity = quantity,
        Provenance = HistoryProvenance.Source,
        EventCandidateId = row.EventCandidateId,
    };

    private void AddLog(MigrationReconstructionRun run, MigrationUnitCandidate candidate, string? sourceKey,
        string? relatedSourceKey, string reason, string note) => db.MigrationReconstructionLogs.Add(new()
    {
        TenantId = tenant.TenantId,
        LogId = Guid.CreateVersion7(clock.UtcNow),
        ReconstructionRunId = run.RunId,
        CandidateId = candidate.CandidateId,
        SourceKey = sourceKey,
        RelatedSourceKey = relatedSourceKey,
        ReasonCode = reason,
        Note = note,
    });

    private static string? EventSourceKey(MigrationHistoryEvent historyEvent,
        IReadOnlyList<SourceHistoryMovementRow> rows) =>
        rows.FirstOrDefault(row => row.EventCandidateId == historyEvent.EventCandidateId)?.TransactionSourceKey;

    private async Task MarkFailedAsync(Guid runId, string code, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var failed = await db.MigrationReconstructionRuns.SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId, cancellationToken);
        failed.Status = ReconstructionRunStatus.Failed;
        failed.FailureCode = code;
        failed.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        if (int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) && sequence >= 0)
            return sequence;
        throw new MigrationRuleException("migration_cursor_invalid", "The page cursor is invalid.");
    }

    private static string MovementLabel(string kind) => kind switch
    {
        HistoryMovementKind.OpeningBalance => "Opening balance",
        HistoryMovementKind.Received => "Received",
        HistoryMovementKind.Produced => "Produced",
        HistoryMovementKind.Consumed => "Consumed",
        HistoryMovementKind.Shipped => "Shipped",
        HistoryMovementKind.CustomerReturned => "Customer returned",
        HistoryMovementKind.VendorReturned => "Returned to supplier",
        HistoryMovementKind.Transferred => "Transferred",
        HistoryMovementKind.IssuedOut => "Issued out",
        _ => "Unknown movement",
    };

}
