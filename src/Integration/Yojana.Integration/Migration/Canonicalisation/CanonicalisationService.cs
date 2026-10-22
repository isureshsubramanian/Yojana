using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Canonicalisation;

public interface IMigrationReviewCarryOver
{
    Task ApplyCarryOversAsync(Guid canonicalisationRunId, CancellationToken cancellationToken);
}

public sealed class CanonicalisationService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IMigrationProfileStatus profileStatus,
    CanonicalFamilySettings familySettings,
    CanonicalisationWriter writer,
    IUserDirectory people,
    IItemFamilyNamePort itemFamilies) : IMigrationReviewCarryOver
{
    private const string BinaryCollation = "Latin1_General_100_BIN2";
    public const int MaximumExportRows = 100_000;
    private static readonly string[] MigrationReviewKinds =
    [
        MigrationReviewKind.SameSerialOneItem,
        MigrationReviewKind.SameSerialDifferentItems,
        MigrationReviewKind.OrphanEquipmentCard,
        MigrationReviewKind.SerialInUserField,
        MigrationReviewKind.HistoryNeedsReview,
    ];
    private static readonly string[] MigrationReviewDecisions =
    [
        MigrationReviewDecisionKind.MergeInto,
        MigrationReviewDecisionKind.KeepApart,
        MigrationReviewDecisionKind.FixInIncumbent,
        MigrationReviewDecisionKind.AcceptAsRecorded,
        MigrationReviewDecisionKind.ExcludeFromHistory,
    ];

    public async Task<StartCanonicalisationResponse> EnqueueAsync(Guid batchId,
        CancellationToken cancellationToken)
    {
        var profile = await ReadyProfileAsync(batchId, cancellationToken);
        if (await db.MigrationCanonRuns.AnyAsync(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId && (row.Status == CanonicalisationRunStatus.Queued ||
                row.Status == CanonicalisationRunStatus.Running), cancellationToken))
            throw new MigrationRuleException("migration_canonicalisation_already_running",
                "Canonicalisation is already queued or running for this batch.");
        var run = NewRun(batchId, profile.RunId);
        db.MigrationCanonRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return new(run.RunId, run.BatchId, run.Status);
    }

    public async Task<int> EnqueueEligibleAsync(CancellationToken cancellationToken)
    {
        var eligible = await db.MigrationProfileRuns.AsNoTracking().Where(profile =>
                profile.TenantId == tenant.TenantId && profile.Status == MigrationProfileRunStatus.Completed &&
                !db.MigrationCanonRuns.Any(run => run.TenantId == profile.TenantId &&
                    run.BatchId == profile.BatchId))
            .OrderBy(profile => profile.FinishedAt).ThenBy(profile => profile.RunId)
            .ToListAsync(cancellationToken);
        foreach (var profile in eligible.GroupBy(row => row.BatchId).Select(group =>
                     group.OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.RunId).First()))
            db.MigrationCanonRuns.Add(NewRun(profile.BatchId, profile.RunId));
        if (eligible.Count != 0) await db.SaveChangesAsync(cancellationToken);
        return eligible.Select(row => row.BatchId).Distinct().Count();
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
                var batch = await db.MigrationBatches.AsNoTracking().SingleAsync(row =>
                    row.TenantId == tenant.TenantId && row.BatchId == run.BatchId, cancellationToken);
                var rules = await familySettings.ResolveAsync(cancellationToken);
                await writer.WriteAsync(run, batch.SourceSystem, rules, cancellationToken);
                await BuildReviewQueueAsync(run, batch.SourceSystem, cancellationToken);
                await ApplyCarryOversAsync(run.RunId, cancellationToken);
                run.Status = CanonicalisationRunStatus.Completed;
                run.FinishedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await RefreshCountsAsync(run, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                completed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                run.Status = CanonicalisationRunStatus.Failed;
                run.FailureCode = "migration_canonicalisation_cancelled";
                run.FinishedAt = clock.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                throw;
            }
            catch
            {
                db.ChangeTracker.Clear();
                var failed = await db.MigrationCanonRuns.SingleAsync(row => row.TenantId == tenant.TenantId &&
                    row.RunId == run.RunId, CancellationToken.None);
                failed.Status = CanonicalisationRunStatus.Failed;
                failed.FailureCode = "migration_canonicalisation_failed";
                failed.FinishedAt = clock.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }
        return completed;
    }

    public async Task<CanonicalisationView?> GetAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var run = await db.MigrationCanonRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId)
            .OrderByDescending(row => row.RequestedAt).ThenByDescending(row => row.RunId)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null) return null;
        var unverified = await db.MigrationEntitlementCandidates.AsNoTracking().LongCountAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == run.RunId &&
            row.Provenance == CanonicalProvenance.Unverified, cancellationToken);
        var waitingByKind = await db.MigrationReviewItems.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == run.RunId &&
                row.Status == MigrationReviewStatus.Waiting)
            .GroupBy(row => row.Kind).Select(group => new { Key = group.Key, Count = group.LongCount() })
            .ToListAsync(cancellationToken);
        var decidedByDecision = await (from item in db.MigrationReviewItems.AsNoTracking()
                                       join decision in db.MigrationReviewDecisions.AsNoTracking()
                                           on new { item.TenantId, DecisionId = item.AppliedDecisionId!.Value }
                                           equals new { decision.TenantId, decision.DecisionId }
                                       where item.TenantId == tenant.TenantId && item.RunId == run.RunId &&
                                             item.Status == MigrationReviewStatus.Decided &&
                                             item.AppliedDecisionId != null
                                       group decision by decision.Decision into grouped
                                       select new { Key = grouped.Key, Count = grouped.LongCount() })
            .ToListAsync(cancellationToken);
        var waitingCounts = Counts(MigrationReviewKinds, waitingByKind.Select(row => (row.Key, row.Count)));
        var decisionCounts = Counts(MigrationReviewDecisions,
            decidedByDecision.Select(row => (row.Key, row.Count)));
        return new(run.RunId, run.BatchId, run.StartedAt, run.FinishedAt, run.Status, run.FailureCode,
            run.SourceSerialCount, run.ExactCount, run.TrimmedUpperCount, run.FamilyNormalisedCount,
            run.MergedCount, run.WaitingCount,
            waitingCounts.Single(row => row.Key == MigrationReviewKind.SameSerialOneItem).Count,
            waitingCounts.Single(row => row.Key == MigrationReviewKind.SameSerialDifferentItems).Count,
            waitingCounts, decisionCounts.Sum(row => row.Count), decisionCounts,
            run.CarriedOverCount, unverified,
            [new(MigrationReviewKind.SerialInUserField,
                "Not configured: the staging schema has no user-field serial text, so canonicalisation cannot create queue rows for it.")]);
    }

    public Task<MigrationReviewPage> ListAsync(Guid batchId, string? kind, string? family, string? status,
        string? cursor, int take, CancellationToken cancellationToken) =>
        ListAsync(batchId, kind, family, status, null, cursor, take, cancellationToken);

    public async Task<MigrationReviewPage> ListAsync(Guid batchId, string? kind, string? family, string? status,
        string? find, string? cursor, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var query = ReviewQuery(batchId, kind, family, status, find);
        if (Decode(cursor) is { } after)
            query = query.Where(row => row.CreatedAt > after.At ||
                (row.CreatedAt == after.At && row.ReviewItemId.CompareTo(after.Id) > 0));
        var rows = await query.OrderBy(row => row.CreatedAt).ThenBy(row => row.ReviewItemId)
            .Take(take + 1).ToListAsync(cancellationToken);
        var page = rows.Take(take).Select(Row).ToList();
        var next = rows.Count > take && page.Count != 0
            ? Encode(new(rows[take - 1].CreatedAt, rows[take - 1].ReviewItemId)) : null;
        return new(page, next);
    }

    public async Task<CanonicalFamilyCatalogue> FamilyCatalogueAsync(CancellationToken cancellationToken)
    {
        var recorded = await db.MigrationUnitCandidates.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId)
            .Select(row => row.Family).Distinct().ToListAsync(cancellationToken);
        var names = await itemFamilies.ListAsync(cancellationToken);
        var labels = names.ToDictionary(row => row.Code, row => row.Name, StringComparer.OrdinalIgnoreCase);
        var codes = recorded.Concat(names.Select(row => row.Code)).Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new(codes.Select(code => new CanonicalFamilyCatalogueItem(code,
                labels.GetValueOrDefault(code) ?? FamilyLabel(code)))
            .OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.Code, StringComparer.OrdinalIgnoreCase).ToList());
    }

    public async Task<bool> ExportTooLargeAsync(Guid batchId, string? kind, string? family, string? status,
        string? find, CancellationToken cancellationToken) => await ReviewQuery(batchId, kind, family, status, find)
        .Take(MaximumExportRows + 1).CountAsync(cancellationToken) > MaximumExportRows;

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportAsync(Guid batchId, string? kind, string? family,
        string? status, string? find, [System.Runtime.CompilerServices.EnumeratorCancellation]
        CancellationToken cancellationToken)
    {
        var query = ReviewQuery(batchId, kind, family, status, find)
            .OrderBy(row => row.CreatedAt).ThenBy(row => row.ReviewItemId).Take(MaximumExportRows);
        await foreach (var entity in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var row = Row(entity);
            yield return [row.SerialAsStored, row.ItemCode, row.ItemName, row.Family, row.KindLabel,
                row.RuleTried, row.CandidateSummary, SuggestionLabel(row), row.Status];
        }
    }

    private IQueryable<MigrationReviewItem> ReviewQuery(Guid batchId, string? kind, string? family,
        string? status, string? find)
    {
        var query = db.MigrationReviewItems.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
            row.BatchId == batchId);
        if (!string.IsNullOrWhiteSpace(kind)) query = query.Where(row => row.Kind == kind);
        if (!string.IsNullOrWhiteSpace(family)) query = query.Where(row => row.Family == family);
        if (!string.IsNullOrWhiteSpace(status)) query = query.Where(row => row.Status == status);
        var search = find?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(row => (row.StoredSerial != null && row.StoredSerial.Contains(search)) ||
                (row.ItemCode != null && row.ItemCode.Contains(search)));
        return query;
    }

    public async Task<MigrationReviewDetail?> DetailAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.MigrationReviewItems.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.ReviewItemId == itemId, cancellationToken);
        if (item is null) return null;
        var keys = JsonSerializer.Deserialize<List<string>>(item.CandidateSourceKeysJson) ?? [];
        var candidates = await CandidateLineageAsync(item, keys, cancellationToken);
        var decisions = await db.MigrationReviewDecisions.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.CorrelationKey == item.CorrelationKey)
            .OrderByDescending(row => row.DecidedAt).ThenByDescending(row => row.DecisionId)
            .ToListAsync(cancellationToken);
        var names = await people.DisplayNamesAsync(decisions.Select(row => row.DecidedBy).Distinct().ToList(),
            cancellationToken);
        return new(item.ReviewItemId, item.Kind, KindLabel(item.Kind), item.Status, item.StoredSerial,
            item.ItemCode, item.ItemName,
            item.RuleTried, candidates, item.Suggestion, item.SuggestedKeepSourceKey, item.SuggestionReason,
            item.Hint, decisions.Select(row => new MigrationReviewDecisionView(row.DecisionId, row.Decision,
                row.KeepSourceKey, row.Reason, row.Note, names.GetValueOrDefault(row.DecidedBy) ?? row.DecidedBy,
                row.DecidedAt, row.ReviewItemId == item.ReviewItemId)).ToList());
    }

    public async Task<MigrationReviewDecisionResponse> DecideAsync(Guid itemId,
        MigrationReviewDecisionRequest request, string actor, CancellationToken cancellationToken)
    {
        GuardActor(actor);
        var reason = Required(request.Reason, 2000, "migration_review_reason_required",
            "A reason is required for every review decision.");
        var note = Optional(request.Note, 2000, "migration_review_note_too_long",
            "The decision note cannot exceed 2,000 characters.");
        var item = await db.MigrationReviewItems.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId &&
            row.ReviewItemId == itemId, cancellationToken)
            ?? throw new MigrationRuleException("migration_review_item_not_found", "The review item was not found.");
        ValidateDecision(request.Decision, item.Kind);
        if (request.Decision == MigrationReviewDecisionKind.FixInIncumbent && note is null)
            throw new MigrationRuleException("migration_review_note_required",
                "A note is required when the issue will be fixed in the incumbent system.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await ApplyDecisionAsync(item, request.Decision, request.KeepSourceKey, carriedOver: false,
            cancellationToken);
        var decision = new MigrationReviewDecision
        {
            TenantId = tenant.TenantId,
            DecisionId = Guid.CreateVersion7(clock.UtcNow),
            ReviewItemId = item.ReviewItemId,
            Fingerprint = item.Fingerprint,
            CorrelationKey = item.CorrelationKey,
            Decision = request.Decision,
            KeepSourceKey = request.Decision == MigrationReviewDecisionKind.MergeInto
                ? request.KeepSourceKey : null,
            Reason = reason,
            Note = note,
            DecidedBy = actor,
            DecidedAt = clock.UtcNow,
        };
        db.MigrationReviewDecisions.Add(decision);
        item.AppliedDecisionId = decision.DecisionId;
        item.Status = MigrationReviewStatus.Decided;
        var run = await db.MigrationCanonRuns.SingleAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == item.RunId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await RefreshCountsAsync(run, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(decision.DecisionId, item.ReviewItemId, decision.Decision, item.Status, decision.DecidedAt);
    }

    private MigrationCanonRun NewRun(Guid batchId, Guid profileRunId) => new()
    {
        TenantId = tenant.TenantId,
        RunId = Guid.CreateVersion7(clock.UtcNow),
        BatchId = batchId,
        ProfileRunId = profileRunId,
        RequestedAt = clock.UtcNow,
        Status = CanonicalisationRunStatus.Queued,
    };

    private async Task<MigrationProfileRun> ReadyProfileAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var batch = await db.MigrationBatches.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.BatchId == batchId, cancellationToken)
            ?? throw new MigrationRuleException("migration_batch_not_found", "The migration batch was not found.");
        if (batch.Status != MigrationBatchStatus.Succeeded)
            throw new MigrationRuleException("migration_canonicalisation_batch_not_succeeded",
                "Canonicalisation requires a successful extraction batch.");
        if (await profileStatus.IsLoadBlocked(batchId, cancellationToken))
            throw new MigrationRuleException("migration_canonicalisation_profile_blocked",
                "Canonicalisation cannot start because profiling found a load-blocking anomaly.");
        var profile = await db.MigrationProfileRuns.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.BatchId == batchId)
            .OrderByDescending(row => row.StartedAt == null).ThenByDescending(row => row.StartedAt)
            .ThenByDescending(row => row.RunId).FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
            throw new MigrationRuleException("migration_canonicalisation_profile_missing",
                "Run profiling for this extraction batch before canonicalisation.");
        if (profile.Status is MigrationProfileRunStatus.Queued or MigrationProfileRunStatus.Running)
            throw new MigrationRuleException("migration_canonicalisation_profile_running",
                "Profiling is still running for this extraction batch.");
        if (profile.Status != MigrationProfileRunStatus.Completed)
            throw new MigrationRuleException("migration_canonicalisation_profile_not_completed",
                "The latest profile did not complete successfully; run profiling again.");
        return profile;
    }

    private async Task<MigrationCanonRun?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var run = await db.MigrationCanonRuns.Where(row => row.TenantId == tenant.TenantId &&
                row.Status == CanonicalisationRunStatus.Queued)
            .OrderBy(row => row.RequestedAt).ThenBy(row => row.RunId).FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        run.Status = CanonicalisationRunStatus.Running;
        run.StartedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    private async Task BuildReviewQueueAsync(MigrationCanonRun run, string sourceSystem,
        CancellationToken cancellationToken)
    {
        var duplicates = await db.MigrationUnitCandidates.Where(candidate =>
                candidate.TenantId == tenant.TenantId && candidate.RunId == run.RunId &&
                db.MigrationUnitCandidates.Any(other => other.TenantId == candidate.TenantId &&
                    other.RunId == candidate.RunId && other.ItemCode == candidate.ItemCode &&
                    other.CanonicalSerial == candidate.CanonicalSerial &&
                    other.CandidateId != candidate.CandidateId))
            .OrderBy(candidate => candidate.ItemCode).ThenBy(candidate => candidate.CanonicalSerial)
            .ThenBy(candidate => candidate.PrimarySourceKey).ToListAsync(cancellationToken);
        var facts = await LineageFactsAsync(run, sourceSystem,
            duplicates.Select(row => row.PrimarySourceKey).ToList(), cancellationToken);
        foreach (var group in duplicates.GroupBy(row => new { row.ItemCode, row.CanonicalSerial }))
        {
            var candidates = group.ToList();
            var ranked = candidates.Select(candidate => facts.GetValueOrDefault(candidate.PrimarySourceKey) ??
                    CandidateFacts.Empty(candidate.PrimarySourceKey))
                .OrderByDescending(fact => fact.Transactions).ThenByDescending(fact => fact.HasCard)
                .ThenByDescending(fact => fact.HasContract).ThenBy(fact => fact.FirstReceipt ?? DateTime.MaxValue)
                .ThenBy(fact => fact.SourceKey, StringComparer.Ordinal).ToList();
            var strongest = UniqueStrongest(ranked);
            if (strongest is not null && CanAutoMerge(strongest, ranked))
            {
                await MergeAsync(run.RunId, candidates.Select(row => row.PrimarySourceKey).ToList(),
                    strongest.SourceKey, reviewed: false, cancellationToken);
                continue;
            }
            await SetDispositionAsync(run.RunId, candidates.Select(row => row.PrimarySourceKey).ToList(),
                CandidateDisposition.Review, cancellationToken);
            var suggestion = strongest is null ? null : MigrationReviewDecisionKind.MergeInto;
            AddReviewItem(run, sourceSystem, MigrationReviewKind.SameSerialOneItem,
                candidates[0].MatchRule, group.Key.CanonicalSerial, candidates, suggestion,
                strongest?.SourceKey, strongest is null ? "The strongest-lineage comparison is tied, so no merge is suggested."
                    : StrongestReason(strongest), null, ranked);
        }
        await db.SaveChangesAsync(cancellationToken);

        var cross = await db.MigrationUnitCandidates.Where(candidate => candidate.TenantId == tenant.TenantId &&
                candidate.RunId == run.RunId && candidate.Disposition == CandidateDisposition.Active &&
                db.MigrationUnitCandidates.Any(other => other.TenantId == candidate.TenantId &&
                    other.RunId == candidate.RunId && other.Disposition == CandidateDisposition.Active &&
                    other.ItemCode != candidate.ItemCode &&
                    ((candidate.StoredSerial != null && other.StoredSerial != null &&
                      EF.Functions.Collate(candidate.StoredSerial, BinaryCollation) ==
                      EF.Functions.Collate(other.StoredSerial, BinaryCollation)) ||
                     other.TrimmedSerial == candidate.TrimmedSerial ||
                     other.FamilyNormalisedSerial == candidate.FamilyNormalisedSerial)))
            .OrderBy(candidate => candidate.PrimarySourceKey).ToListAsync(cancellationToken);
        var consumed = new HashSet<Guid>();
        foreach (var match in CrossGroups(cross))
        {
            var candidates = match.Candidates.Where(row => consumed.Add(row.CandidateId)).ToList();
            if (candidates.Select(row => row.ItemCode).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            await SetDispositionAsync(run.RunId, candidates.Select(row => row.PrimarySourceKey).ToList(),
                CandidateDisposition.Review, cancellationToken);
            AddReviewItem(run, sourceSystem, MigrationReviewKind.SameSerialDifferentItems, match.Rule,
                match.Key, candidates, MigrationReviewDecisionKind.KeepApart, null,
                "The candidates belong to different item codes; units are never merged across items.", null);
        }
        await db.SaveChangesAsync(cancellationToken);

        var orphans = await db.Set<EquipmentCardStagingRow>().AsNoTracking().Where(card =>
                card.TenantId == tenant.TenantId && card.BatchId == run.BatchId &&
                card.SourceSystem == sourceSystem && card.AbsentSinceBatchId == null &&
                !db.MigrationUnitCandidateSources.Any(source => source.TenantId == card.TenantId &&
                    source.RunId == run.RunId && source.SourceKind == CandidateSourceKind.EquipmentCard &&
                    source.SourceKey == card.SourcePk))
            .OrderBy(card => card.SourcePk).ToListAsync(cancellationToken);
        foreach (var card in orphans)
        {
            var serial = card.InternalSerial ?? card.ManufacturerSerial;
            var nearest = await NearestAsync(run.RunId, card.ItemCode, serial, cancellationToken);
            AddOrphanReviewItem(run, sourceSystem, card, nearest);
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private void AddReviewItem(MigrationCanonRun run, string sourceSystem, string kind, string rule,
        string canonicalKey, List<MigrationUnitCandidate> candidates, string? suggestion,
        string? keepSourceKey, string? reason, string? hint, IReadOnlyList<CandidateFacts>? lineage = null)
    {
        var sourceKeys = candidates.Select(row => row.PrimarySourceKey).Order(StringComparer.Ordinal).ToList();
        var itemCodes = candidates.Select(row => row.ItemCode).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToList();
        // A merge decided on one lineage must not be replayed on another: when a candidate gains or loses
        // transactions, a card, a contract or its first receipt, the item returns to a person (the earlier
        // decision is still shown through the correlation key).
        var lineageKeys = lineage?.OrderBy(fact => fact.SourceKey, StringComparer.Ordinal).Select(fact =>
            $"{fact.SourceKey}:{fact.Transactions}:{fact.HasCard}:{fact.HasContract}:{fact.FirstReceipt:O}:{fact.ReceiptKey}")
            .ToList() ?? [];
        var fingerprint = MigrationReviewIdentity.Fingerprint(sourceSystem, kind, canonicalKey, itemCodes,
            [.. sourceKeys, .. lineageKeys]);
        db.MigrationReviewItems.Add(new()
        {
            TenantId = tenant.TenantId,
            ReviewItemId = Guid.CreateVersion7(clock.UtcNow),
            RunId = run.RunId,
            BatchId = run.BatchId,
            SourceSystem = sourceSystem,
            Kind = kind,
            Status = MigrationReviewStatus.Waiting,
            Fingerprint = fingerprint,
            CorrelationKey = MigrationReviewIdentity.Correlation(sourceSystem, kind, canonicalKey, itemCodes),
            CanonicalKey = canonicalKey,
            ItemCode = itemCodes.Count == 1 ? itemCodes[0] : null,
            ItemName = candidates.Select(row => row.ItemName).Where(value => value is not null)
                .Distinct(StringComparer.Ordinal).Count() == 1 ? candidates.First(row => row.ItemName is not null).ItemName : null,
            Family = candidates.Select(row => row.Family).Distinct(StringComparer.Ordinal).Count() == 1
                ? candidates[0].Family : null,
            StoredSerial = candidates[0].StoredSerial,
            RuleTried = rule,
            SourceKeysJson = JsonSerializer.Serialize(sourceKeys),
            CandidateSourceKeysJson = JsonSerializer.Serialize(sourceKeys),
            CandidateSummary = $"{candidates.Count} candidates across {itemCodes.Count} item code(s)",
            Suggestion = suggestion,
            SuggestedKeepSourceKey = keepSourceKey,
            SuggestionReason = reason,
            Hint = hint,
            CreatedAt = clock.UtcNow,
        });
    }

    private void AddOrphanReviewItem(MigrationCanonRun run, string sourceSystem, EquipmentCardStagingRow card,
        MigrationUnitCandidate? nearest)
    {
        var serial = card.InternalSerial ?? card.ManufacturerSerial;
        var sourceKeys = new List<string> { card.SourcePk };
        var candidateKeys = nearest is null ? new List<string>() : [nearest.PrimarySourceKey];
        var itemCodes = card.ItemCode is null ? new List<string>() : [card.ItemCode];
        var canonical = serial?.Trim().ToUpperInvariant() ?? card.SourcePk;
        db.MigrationReviewItems.Add(new()
        {
            TenantId = tenant.TenantId,
            ReviewItemId = Guid.CreateVersion7(clock.UtcNow),
            RunId = run.RunId,
            BatchId = run.BatchId,
            SourceSystem = sourceSystem,
            Kind = MigrationReviewKind.OrphanEquipmentCard,
            Status = MigrationReviewStatus.Waiting,
            Fingerprint = MigrationReviewIdentity.Fingerprint(sourceSystem,
                MigrationReviewKind.OrphanEquipmentCard, canonical, itemCodes, sourceKeys),
            CorrelationKey = MigrationReviewIdentity.Correlation(sourceSystem,
                MigrationReviewKind.OrphanEquipmentCard, canonical, itemCodes),
            CanonicalKey = canonical,
            ItemCode = card.ItemCode,
            ItemName = nearest?.ItemName,
            Family = nearest?.Family,
            StoredSerial = serial,
            RuleTried = "no_serial_match",
            SourceKeysJson = JsonSerializer.Serialize(sourceKeys),
            CandidateSourceKeysJson = JsonSerializer.Serialize(candidateKeys),
            CandidateSummary = nearest is null ? "No serial-master candidate on the same item."
                : $"Nearest same-item serial: {nearest.StoredSerial ?? nearest.CanonicalSerial}",
            Hint = nearest is null ? null
                : $"Nearest serial on the same item is {nearest.StoredSerial ?? nearest.CanonicalSerial} (one edit); this is a hint, not a merge suggestion.",
            CreatedAt = clock.UtcNow,
        });
    }

    public async Task ApplyCarryOversAsync(Guid canonicalisationRunId, CancellationToken cancellationToken)
    {
        var items = await db.MigrationReviewItems.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == canonicalisationRunId && row.Status == MigrationReviewStatus.Waiting)
            .ToListAsync(cancellationToken);
        foreach (var item in items)
        {
            var decision = await db.MigrationReviewDecisions.AsNoTracking().Where(row =>
                    row.TenantId == tenant.TenantId && row.Fingerprint == item.Fingerprint &&
                    row.ReviewItemId != item.ReviewItemId)
                .OrderByDescending(row => row.DecidedAt).ThenByDescending(row => row.DecisionId)
                .FirstOrDefaultAsync(cancellationToken);
            if (decision is null) continue;
            try
            {
                await ApplyDecisionAsync(item, decision.Decision, decision.KeepSourceKey, carriedOver: true,
                    cancellationToken);
                item.Status = MigrationReviewStatus.CarriedOver;
                item.AppliedDecisionId = decision.DecisionId;
            }
            catch (MigrationRuleException)
            {
                // The fingerprint matched but the target no longer validates against this run. Leave it waiting;
                // detail still shows the earlier decision through the correlation key.
            }
        }
    }

    private async Task ApplyDecisionAsync(MigrationReviewItem item, string decision, string? keepSourceKey,
        bool carriedOver, CancellationToken cancellationToken)
    {
        ValidateDecision(decision, item.Kind);
        var candidateKeys = JsonSerializer.Deserialize<List<string>>(item.CandidateSourceKeysJson) ?? [];
        if (item.Kind == MigrationReviewKind.HistoryNeedsReview)
        {
            var candidate = await db.MigrationUnitCandidates.SingleAsync(row =>
                row.TenantId == tenant.TenantId && row.RunId == item.RunId &&
                candidateKeys.Contains(row.PrimarySourceKey), cancellationToken);
            if (decision == MigrationReviewDecisionKind.FixInIncumbent)
                candidate.Disposition = CandidateDisposition.Excluded;
            else if (decision == MigrationReviewDecisionKind.AcceptAsRecorded)
                candidate.Disposition = CandidateDisposition.Active;
            // exclude_from_history is an append-only instruction consumed by the mapping phase; source evidence
            // remains visible here so a later decision can be made without destroying reconstructed history.
            if (!carriedOver) item.Status = MigrationReviewStatus.Decided;
            return;
        }
        if (item.Kind != MigrationReviewKind.OrphanEquipmentCard)
            await ResetCandidatesAsync(item.RunId, candidateKeys, cancellationToken);
        if (decision == MigrationReviewDecisionKind.MergeInto)
        {
            if (string.IsNullOrWhiteSpace(keepSourceKey) || !candidateKeys.Contains(keepSourceKey,
                    StringComparer.Ordinal))
                throw new MigrationRuleException("migration_review_keep_candidate_invalid",
                    "Choose one of this review item's candidates to keep.");
            var items = await db.MigrationUnitCandidates.Where(row => row.TenantId == tenant.TenantId &&
                row.RunId == item.RunId && candidateKeys.Contains(row.PrimarySourceKey)).ToListAsync(cancellationToken);
            if (items.Count == 0)
                throw new MigrationRuleException("migration_review_keep_candidate_invalid",
                    "The kept candidate is not available in this canonicalisation run.");
            if (items.Select(row => row.ItemCode).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new MigrationRuleException("migration_cross_item_merge_forbidden",
                    "Candidates from different item codes can never be merged.");
            if (item.Kind == MigrationReviewKind.OrphanEquipmentCard)
                await AttachOrphanAsync(item, items.Single(row => row.PrimarySourceKey == keepSourceKey),
                    cancellationToken);
            else
                await MergeAsync(item.RunId, candidateKeys, keepSourceKey, reviewed: true, cancellationToken);
        }
        else
        {
            var disposition = decision == MigrationReviewDecisionKind.KeepApart
                ? CandidateDisposition.Active : CandidateDisposition.Excluded;
            // An orphan's candidate list is only a one-edit hint. Leaving the card unattached keeps it out of
            // the load; changing that hinted unit would turn a proximity clue into an implicit decision.
            if (item.Kind != MigrationReviewKind.OrphanEquipmentCard)
                await SetDispositionAsync(item.RunId, candidateKeys, disposition, cancellationToken, reviewed: true);
        }
        if (!carriedOver) item.Status = MigrationReviewStatus.Decided;
    }

    private async Task AttachOrphanAsync(MigrationReviewItem item, MigrationUnitCandidate candidate,
        CancellationToken cancellationToken)
    {
        var cardKey = (JsonSerializer.Deserialize<List<string>>(item.SourceKeysJson) ?? []).Single();
        var card = await db.Set<EquipmentCardStagingRow>().AsNoTracking().SingleAsync(row =>
            row.TenantId == tenant.TenantId && row.BatchId == item.BatchId &&
            row.SourceSystem == item.SourceSystem && row.AbsentSinceBatchId == null && row.SourcePk == cardKey,
            cancellationToken);
        var existing = await db.MigrationUnitCandidateSources.SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == item.RunId &&
            row.SourceKind == CandidateSourceKind.EquipmentCard && row.SourceKey == cardKey,
            cancellationToken);
        if (existing is null) db.MigrationUnitCandidateSources.Add(new()
        {
            TenantId = tenant.TenantId,
            CandidateSourceId = Guid.CreateVersion7(clock.UtcNow),
            RunId = item.RunId,
            CandidateId = candidate.CandidateId,
            MatchedSerialSourceKey = candidate.PrimarySourceKey,
            SourceKind = CandidateSourceKind.EquipmentCard,
            SourceKey = cardKey,
            Role = CandidateSourceRole.Merged,
        });
        else
        {
            existing.CandidateId = candidate.CandidateId;
            existing.MatchedSerialSourceKey = candidate.PrimarySourceKey;
            existing.Role = CandidateSourceRole.Merged;
        }
        var existingEntitlement = await db.MigrationEntitlementCandidates.SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == item.RunId &&
            row.SourceKind == CandidateSourceKind.EquipmentCard && row.SourceKey == cardKey,
            cancellationToken);
        if (existingEntitlement is null && (card.WarrantyStart is not null || card.WarrantyEnd is not null))
            db.MigrationEntitlementCandidates.Add(new()
            {
                TenantId = tenant.TenantId,
                EntitlementCandidateId = Guid.CreateVersion7(clock.UtcNow),
                RunId = item.RunId,
                CandidateId = candidate.CandidateId,
                SerialMasterSourceKey = candidate.PrimarySourceKey,
                SourceKind = CandidateSourceKind.EquipmentCard,
                SourceKey = cardKey,
                StartDate = card.WarrantyStart,
                EndDate = card.WarrantyEnd,
                MatchRule = CanonicalMatchRule.Reviewed,
                Provenance = CanonicalProvenance.Source,
            });
        else if (existingEntitlement is not null)
        {
            existingEntitlement.CandidateId = candidate.CandidateId;
            existingEntitlement.SerialMasterSourceKey = candidate.PrimarySourceKey;
            existingEntitlement.MatchRule = CanonicalMatchRule.Reviewed;
        }
        candidate.MatchRule = CanonicalMatchRule.Reviewed;
        candidate.Disposition = CandidateDisposition.Active;
    }

    private async Task ResetCandidatesAsync(Guid runId, List<string> sourceKeys,
        CancellationToken cancellationToken)
    {
        var candidates = await db.MigrationUnitCandidates.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId && sourceKeys.Contains(row.PrimarySourceKey)).ToListAsync(cancellationToken);
        var bySource = candidates.ToDictionary(row => row.PrimarySourceKey, StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            candidate.Disposition = CandidateDisposition.Review;
            candidate.MatchRule = CanonicalMatchRule.Reviewed;
        }
        var links = await db.MigrationUnitCandidateSources.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId && sourceKeys.Contains(row.MatchedSerialSourceKey)).ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            link.CandidateId = bySource[link.MatchedSerialSourceKey].CandidateId;
            link.Role = CandidateSourceRole.Kept;
        }
        var events = await db.MigrationEventCandidates.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId && sourceKeys.Contains(row.SerialMasterSourceKey)).ToListAsync(cancellationToken);
        foreach (var candidateEvent in events)
        {
            candidateEvent.CandidateId = bySource[candidateEvent.SerialMasterSourceKey].CandidateId;
            candidateEvent.MatchRule = CanonicalMatchRule.Reviewed;
        }
        var entitlements = await db.MigrationEntitlementCandidates.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId && sourceKeys.Contains(row.SerialMasterSourceKey)).ToListAsync(cancellationToken);
        foreach (var entitlement in entitlements)
        {
            entitlement.CandidateId = bySource[entitlement.SerialMasterSourceKey].CandidateId;
            entitlement.MatchRule = CanonicalMatchRule.Reviewed;
        }
    }

    private async Task MergeAsync(Guid runId, IReadOnlyList<string> sourceKeys, string keepSourceKey,
        bool reviewed, CancellationToken cancellationToken)
    {
        var candidates = await db.MigrationUnitCandidates.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId && sourceKeys.Contains(row.PrimarySourceKey)).ToListAsync(cancellationToken);
        var keep = candidates.Single(row => row.PrimarySourceKey == keepSourceKey);
        foreach (var candidate in candidates)
        {
            candidate.Disposition = candidate.CandidateId == keep.CandidateId
                ? CandidateDisposition.Active : CandidateDisposition.Merged;
            if (reviewed) candidate.MatchRule = CanonicalMatchRule.Reviewed;
        }
        var links = await db.MigrationUnitCandidateSources.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId && sourceKeys.Contains(row.MatchedSerialSourceKey)).ToListAsync(cancellationToken);
        foreach (var link in links)
        {
            link.CandidateId = keep.CandidateId;
            link.Role = link.MatchedSerialSourceKey == keepSourceKey
                ? CandidateSourceRole.Kept : CandidateSourceRole.Merged;
        }
        var events = await db.MigrationEventCandidates.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId && sourceKeys.Contains(row.SerialMasterSourceKey)).ToListAsync(cancellationToken);
        foreach (var candidateEvent in events)
        {
            candidateEvent.CandidateId = keep.CandidateId;
            if (reviewed) candidateEvent.MatchRule = CanonicalMatchRule.Reviewed;
        }
        var entitlements = await db.MigrationEntitlementCandidates.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId && sourceKeys.Contains(row.SerialMasterSourceKey)).ToListAsync(cancellationToken);
        foreach (var entitlement in entitlements)
        {
            entitlement.CandidateId = keep.CandidateId;
            if (reviewed) entitlement.MatchRule = CanonicalMatchRule.Reviewed;
        }
    }

    private async Task SetDispositionAsync(Guid runId, List<string> sourceKeys, string disposition,
        CancellationToken cancellationToken, bool reviewed = false)
    {
        if (sourceKeys.Count == 0) return;
        var candidates = await db.MigrationUnitCandidates.Where(row => row.TenantId == tenant.TenantId &&
            row.RunId == runId && sourceKeys.Contains(row.PrimarySourceKey)).ToListAsync(cancellationToken);
        foreach (var candidate in candidates)
        {
            candidate.Disposition = disposition;
            if (reviewed) candidate.MatchRule = CanonicalMatchRule.Reviewed;
        }
        if (!reviewed) return;
        await db.MigrationEventCandidates.Where(row => row.TenantId == tenant.TenantId && row.RunId == runId &&
                sourceKeys.Contains(row.SerialMasterSourceKey))
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.MatchRule, CanonicalMatchRule.Reviewed),
                cancellationToken);
        await db.MigrationEntitlementCandidates.Where(row => row.TenantId == tenant.TenantId && row.RunId == runId &&
                sourceKeys.Contains(row.SerialMasterSourceKey))
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.MatchRule, CanonicalMatchRule.Reviewed),
                cancellationToken);
    }

    private async Task<Dictionary<string, CandidateFacts>> LineageFactsAsync(MigrationCanonRun run,
        string sourceSystem, List<string> sourceKeys, CancellationToken cancellationToken)
    {
        if (sourceKeys.Count == 0) return new(StringComparer.Ordinal);
        var events = await db.MigrationEventCandidates.AsNoTracking().Where(row => row.TenantId == tenant.TenantId &&
                row.RunId == run.RunId && sourceKeys.Contains(row.SerialMasterSourceKey))
            .GroupBy(row => row.SerialMasterSourceKey)
            .Select(group => new { SourceKey = group.Key, Transactions = group.LongCount() })
            .ToListAsync(cancellationToken);
        var links = await db.MigrationUnitCandidateSources.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == run.RunId &&
                sourceKeys.Contains(row.MatchedSerialSourceKey))
            .GroupBy(row => row.MatchedSerialSourceKey)
            .Select(group => new
            {
                SourceKey = group.Key,
                HasCard = group.Any(row => row.SourceKind == CandidateSourceKind.EquipmentCard),
                HasContract = group.Any(row => row.SourceKind == CandidateSourceKind.Contract ||
                    row.SourceKind == CandidateSourceKind.ContractLine),
            }).ToListAsync(cancellationToken);
        var receipts = await (from candidateEvent in db.MigrationEventCandidates.AsNoTracking()
                              join line in db.Set<TransactionLogLineStagingRow>().AsNoTracking()
                                  on new
                                  {
                                      candidateEvent.TenantId,
                                      BatchId = run.BatchId,
                                      SourceSystem = sourceSystem,
                                      SourceKey = candidateEvent.TransactionSourceKey,
                                  }
                                  equals new
                                  {
                                      line.TenantId,
                                      line.BatchId,
                                      line.SourceSystem,
                                      SourceKey = line.SourcePk,
                                  }
                              join header in db.Set<TransactionLogHeaderStagingRow>().AsNoTracking()
                                  on new { line.TenantId, line.BatchId, line.SourceSystem, line.LogEntry }
                                  equals new { header.TenantId, header.BatchId, header.SourceSystem, header.LogEntry }
                              where candidateEvent.TenantId == tenant.TenantId && candidateEvent.RunId == run.RunId &&
                                    sourceKeys.Contains(candidateEvent.SerialMasterSourceKey) &&
                                    line.AbsentSinceBatchId == null && header.AbsentSinceBatchId == null &&
                                    (line.Quantity ?? 0) > 0
                              group header by candidateEvent.SerialMasterSourceKey into grouped
                              select new
                              {
                                  SourceKey = grouped.Key,
                                  FirstReceipt = grouped.Min(row => row.DocumentDate ?? row.CreatedDate),
                                  ReceiptKey = grouped.OrderBy(row => row.DocumentDate ?? row.CreatedDate)
                                      .Select(row => (row.DocumentEntry ?? 0) + "|" + (row.DocumentLine ?? 0))
                                      .FirstOrDefault(),
                              }).ToListAsync(cancellationToken);
        var result = sourceKeys.Distinct(StringComparer.Ordinal).ToDictionary(key => key,
            CandidateFacts.Empty, StringComparer.Ordinal);
        foreach (var row in events) result[row.SourceKey] = result[row.SourceKey] with { Transactions = row.Transactions };
        foreach (var row in links) result[row.SourceKey] = result[row.SourceKey] with
            { HasCard = row.HasCard, HasContract = row.HasContract };
        foreach (var row in receipts) result[row.SourceKey] = result[row.SourceKey] with
            { FirstReceipt = row.FirstReceipt, ReceiptKey = row.ReceiptKey };
        return result;
    }

    private async Task<IReadOnlyList<MigrationCandidateLineage>> CandidateLineageAsync(MigrationReviewItem item,
        List<string> sourceKeys, CancellationToken cancellationToken)
    {
        if (sourceKeys.Count == 0) return [];
        var candidates = await db.MigrationUnitCandidates.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == item.RunId &&
                sourceKeys.Contains(row.PrimarySourceKey))
            .OrderBy(row => row.PrimarySourceKey).ToListAsync(cancellationToken);
        var run = await db.MigrationCanonRuns.AsNoTracking().SingleAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == item.RunId, cancellationToken);
        var facts = await LineageFactsAsync(run, item.SourceSystem, sourceKeys, cancellationToken);
        var movements = await (from candidateEvent in db.MigrationEventCandidates.AsNoTracking()
                               join line in db.Set<TransactionLogLineStagingRow>().AsNoTracking()
                                   on new
                                   {
                                       candidateEvent.TenantId,
                                       BatchId = run.BatchId,
                                       SourceSystem = item.SourceSystem,
                                       SourceKey = candidateEvent.TransactionSourceKey,
                                   }
                                   equals new
                                   {
                                       line.TenantId,
                                       line.BatchId,
                                       line.SourceSystem,
                                       SourceKey = line.SourcePk,
                                   }
                               join header in db.Set<TransactionLogHeaderStagingRow>().AsNoTracking()
                                   on new { line.TenantId, line.BatchId, line.SourceSystem, line.LogEntry }
                                   equals new { header.TenantId, header.BatchId, header.SourceSystem, header.LogEntry }
                               where candidateEvent.TenantId == tenant.TenantId &&
                                     candidateEvent.RunId == item.RunId &&
                                     sourceKeys.Contains(candidateEvent.SerialMasterSourceKey) &&
                                     line.AbsentSinceBatchId == null && header.AbsentSinceBatchId == null
                               select new CandidateMovement
                               {
                                   SourceKey = candidateEvent.SerialMasterSourceKey,
                                   Quantity = line.Quantity ?? 0,
                                   At = header.DocumentDate ?? header.CreatedDate,
                                   CreatedDate = header.CreatedDate,
                                   CreatedTime = header.CreatedTime,
                                   LogEntry = header.LogEntry,
                                   PartyCode = header.PartyCode,
                                   DocumentNumber = header.DocumentNumber,
                                   DocumentLine = header.DocumentLine,
                               }).ToListAsync(cancellationToken);
        var links = await db.MigrationUnitCandidateSources.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == item.RunId &&
                sourceKeys.Contains(row.MatchedSerialSourceKey))
            .ToListAsync(cancellationToken);
        var cardKeys = links.Where(row => row.SourceKind == CandidateSourceKind.EquipmentCard)
            .Select(row => row.SourceKey).Distinct(StringComparer.Ordinal).ToList();
        var cards = await db.Set<EquipmentCardStagingRow>().AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == run.BatchId &&
                row.SourceSystem == item.SourceSystem && row.AbsentSinceBatchId == null &&
                cardKeys.Contains(row.SourcePk))
            .Select(row => new { row.SourcePk, row.CardId }).ToListAsync(cancellationToken);
        var cardNumbers = cards.ToDictionary(row => row.SourcePk,
            row => row.CardId?.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var contractKeys = links.Where(row => row.SourceKind == CandidateSourceKind.Contract)
            .Select(row => row.SourceKey).Distinct(StringComparer.Ordinal).ToList();
        var contracts = await db.Set<ServiceContractStagingRow>().AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.BatchId == run.BatchId &&
                row.SourceSystem == item.SourceSystem && row.AbsentSinceBatchId == null &&
                contractKeys.Contains(row.SourcePk))
            .Select(row => new { row.SourcePk, row.DocumentNumber, row.ContractId }).ToListAsync(cancellationToken);
        var contractNumbers = contracts.ToDictionary(row => row.SourcePk,
            row => (row.DocumentNumber ?? row.ContractId)?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            StringComparer.Ordinal);
        var partyCodes = movements.Where(row => row.Quantity < 0 && row.PartyCode is not null)
            .Select(row => row.PartyCode!).Distinct(StringComparer.Ordinal).ToList();
        var partyRows = await db.ReferenceProjections.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.EntityType == "party" && row.Code != null &&
                partyCodes.Contains(row.Code))
            .Select(row => new { Code = row.Code!, row.DisplayName }).ToListAsync(cancellationToken);
        var partyNames = partyRows.ToDictionary(row => row.Code, row => row.DisplayName,
            StringComparer.Ordinal);
        return candidates.Select(candidate =>
        {
            var fact = facts[candidate.PrimarySourceKey];
            var candidateMovements = movements.Where(row => row.SourceKey == candidate.PrimarySourceKey).ToList();
            var receipt = candidateMovements.Where(row => row.Quantity > 0)
                .OrderBy(row => row.At).ThenBy(row => row.CreatedDate).ThenBy(row => row.CreatedTime)
                .ThenBy(row => row.LogEntry).FirstOrDefault();
            var delivery = candidateMovements.Where(row => row.Quantity < 0)
                .OrderByDescending(row => row.At).ThenByDescending(row => row.CreatedDate)
                .ThenByDescending(row => row.CreatedTime).ThenByDescending(row => row.LogEntry).FirstOrDefault();
            var candidateLinks = links.Where(row => row.MatchedSerialSourceKey == candidate.PrimarySourceKey).ToList();
            var cardNumber = candidateLinks.Where(row => row.SourceKind == CandidateSourceKind.EquipmentCard)
                .Select(row => cardNumbers.GetValueOrDefault(row.SourceKey)).FirstOrDefault(value => value is not null);
            var contractNumber = candidateLinks.Where(row => row.SourceKind == CandidateSourceKind.Contract)
                .Select(row => contractNumbers.GetValueOrDefault(row.SourceKey)).FirstOrDefault(value => value is not null);
            var party = delivery?.PartyCode is null ? null
                : partyNames.GetValueOrDefault(delivery.PartyCode) ?? delivery.PartyCode;
            return new MigrationCandidateLineage(candidate.PrimarySourceKey, candidate.StoredSerial,
                candidate.ItemCode, candidate.ItemName, fact.Transactions, fact.FirstReceipt, delivery?.At,
                party, fact.HasCard, cardNumber, fact.HasContract, contractNumber,
                receipt?.DocumentNumber, receipt?.DocumentLine, candidate.Disposition);
        }).ToList();
    }

    private async Task<MigrationUnitCandidate?> NearestAsync(Guid runId, string? itemCode, string? serial,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(itemCode) || string.IsNullOrWhiteSpace(serial)) return null;
        var normal = serial.Trim().ToUpperInvariant();
        var candidates = await db.MigrationUnitCandidates.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == runId && row.ItemCode == itemCode &&
                row.TrimmedSerial.Length >= normal.Length - 1 && row.TrimmedSerial.Length <= normal.Length + 1)
            .OrderBy(row => row.PrimarySourceKey).Take(500).ToListAsync(cancellationToken);
        return candidates.Where(row => OneEditApart(normal, row.TrimmedSerial))
            .OrderBy(row => row.TrimmedSerial, StringComparer.Ordinal).FirstOrDefault();
    }

    private async Task RefreshCountsAsync(MigrationCanonRun run, CancellationToken cancellationToken)
    {
        run.SourceSerialCount = await db.MigrationUnitCandidates.LongCountAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == run.RunId, cancellationToken);
        run.ExactCount = await db.MigrationUnitCandidates.LongCountAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == run.RunId && row.MatchRule == CanonicalMatchRule.Exact, cancellationToken);
        run.TrimmedUpperCount = await db.MigrationUnitCandidates.LongCountAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == run.RunId &&
            row.MatchRule == CanonicalMatchRule.TrimmedUpper, cancellationToken);
        run.FamilyNormalisedCount = await db.MigrationUnitCandidates.LongCountAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == run.RunId &&
            row.MatchRule == CanonicalMatchRule.FamilyNormalised, cancellationToken);
        run.WaitingCount = await db.MigrationReviewItems.LongCountAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == run.RunId && row.Status == MigrationReviewStatus.Waiting, cancellationToken);
        run.MergedCount = await db.MigrationUnitCandidates.LongCountAsync(row => row.TenantId == tenant.TenantId &&
            row.RunId == run.RunId && row.Disposition == CandidateDisposition.Merged, cancellationToken);
        run.KeptApartCount = await LatestDecisionCountAsync(run.RunId,
            MigrationReviewDecisionKind.KeepApart, cancellationToken);
        run.FixInIncumbentCount = await LatestDecisionCountAsync(run.RunId,
            MigrationReviewDecisionKind.FixInIncumbent, cancellationToken);
        run.CarriedOverCount = await db.MigrationReviewItems.LongCountAsync(row =>
            row.TenantId == tenant.TenantId && row.RunId == run.RunId &&
            row.Status == MigrationReviewStatus.CarriedOver, cancellationToken);
    }

    private async Task<long> LatestDecisionCountAsync(Guid runId, string decision,
        CancellationToken cancellationToken) => await db.MigrationReviewItems.LongCountAsync(item =>
        item.TenantId == tenant.TenantId && item.RunId == runId && item.AppliedDecisionId != null &&
        db.MigrationReviewDecisions.Any(row => row.TenantId == item.TenantId &&
            row.DecisionId == item.AppliedDecisionId && row.Decision == decision), cancellationToken);

    private static CandidateFacts? UniqueStrongest(IReadOnlyList<CandidateFacts> ranked)
    {
        if (ranked.Count == 0) return null;
        if (ranked.Count == 1) return ranked[0];
        return SameStrength(ranked[0], ranked[1]) ? null : ranked[0];
    }

    private static bool CanAutoMerge(CandidateFacts strongest, IReadOnlyList<CandidateFacts> all)
    {
        if (strongest.Transactions == 0 && !strongest.HasCard && !strongest.HasContract) return false;
        return all.Where(row => row.SourceKey != strongest.SourceKey).All(row =>
            !row.HasCard && !row.HasContract && (row.Transactions == 0 ||
             (row.Transactions == 1 && row.ReceiptKey is not null && row.ReceiptKey == strongest.ReceiptKey)));
    }

    private static bool SameStrength(CandidateFacts left, CandidateFacts right) =>
        left.Transactions == right.Transactions && left.HasCard == right.HasCard &&
        left.HasContract == right.HasContract && left.FirstReceipt == right.FirstReceipt;

    private static string StrongestReason(CandidateFacts fact)
    {
        var parts = new List<string> { $"{fact.Transactions} transaction-log row(s)" };
        if (fact.HasCard) parts.Add("an equipment card");
        if (fact.HasContract) parts.Add("a contract");
        if (fact.FirstReceipt is not null) parts.Add($"first receipt {fact.FirstReceipt:yyyy-MM-dd}");
        return "This candidate has the strongest lineage: " + string.Join(", ", parts) + ".";
    }

    private static List<CrossGroup> CrossGroups(List<MigrationUnitCandidate> candidates)
    {
        var used = new HashSet<Guid>();
        var result = new List<CrossGroup>();
        Add(candidates.Where(row => row.StoredSerial is not null).GroupBy(row => row.StoredSerial!,
            StringComparer.Ordinal), CanonicalMatchRule.Exact);
        Add(candidates.Where(row => !used.Contains(row.CandidateId)).GroupBy(row => row.TrimmedSerial,
            StringComparer.Ordinal), CanonicalMatchRule.TrimmedUpper);
        Add(candidates.Where(row => !used.Contains(row.CandidateId)).GroupBy(row => row.FamilyNormalisedSerial,
            StringComparer.Ordinal), CanonicalMatchRule.FamilyNormalised);
        return result;

        void Add(IEnumerable<IGrouping<string, MigrationUnitCandidate>> groups, string rule)
        {
            foreach (var group in groups.Where(group => group.Select(row => row.ItemCode)
                         .Distinct(StringComparer.Ordinal).Count() > 1))
            {
                var rows = group.Where(row => !used.Contains(row.CandidateId)).ToList();
                if (rows.Select(row => row.ItemCode).Distinct(StringComparer.Ordinal).Count() < 2) continue;
                foreach (var row in rows) used.Add(row.CandidateId);
                result.Add(new(rule, group.Key, rows));
            }
        }
    }

    private static bool OneEditApart(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1 || left == right) return false;
        if (left.Length == right.Length)
            return left.Zip(right).Count(pair => pair.First != pair.Second) == 1;
        var shorter = left.Length < right.Length ? left : right;
        var longer = left.Length < right.Length ? right : left;
        var shortIndex = 0;
        var longIndex = 0;
        var edits = 0;
        while (shortIndex < shorter.Length && longIndex < longer.Length)
        {
            if (shorter[shortIndex] == longer[longIndex]) { shortIndex++; longIndex++; continue; }
            if (++edits > 1) return false;
            longIndex++;
        }
        return true;
    }

    private static MigrationReviewRow Row(MigrationReviewItem row) => new(row.ReviewItemId, row.StoredSerial,
        row.ItemCode, row.ItemName, row.Family, row.Kind, KindLabel(row.Kind), row.RuleTried,
        row.CandidateSummary, row.Suggestion, row.SuggestedKeepSourceKey, SuggestedKeepLabel(row),
        row.SuggestionReason, row.Hint, row.Status,
        row.Status == MigrationReviewStatus.CarriedOver);

    private static string? SuggestedKeepLabel(MigrationReviewItem row)
    {
        if (row.SuggestedKeepSourceKey is null) return null;
        var keys = JsonSerializer.Deserialize<List<string>>(row.CandidateSourceKeysJson) ?? [];
        var index = keys.FindIndex(key => key == row.SuggestedKeepSourceKey);
        return index < 0 ? null : CandidateLabel(index);
    }

    private static string? SuggestionLabel(MigrationReviewRow row) => row.Suggestion switch
    {
        MigrationReviewDecisionKind.MergeInto when row.SuggestedKeepLabel is not null =>
            $"Merge into {row.SuggestedKeepLabel}",
        MigrationReviewDecisionKind.KeepApart => "Keep apart",
        MigrationReviewDecisionKind.FixInIncumbent => "Fix in the incumbent",
        MigrationReviewDecisionKind.AcceptAsRecorded => "Accept as recorded",
        MigrationReviewDecisionKind.ExcludeFromHistory => "Exclude from history",
        _ => null,
    };

    private static string CandidateLabel(int index)
    {
        var value = index + 1;
        var label = "";
        while (value > 0)
        {
            value--;
            label = (char)('A' + value % 26) + label;
            value /= 26;
        }
        return label;
    }

    private static List<MigrationReviewCount> Counts(IEnumerable<string> keys,
        IEnumerable<(string Key, long Count)> found)
    {
        var byKey = found.ToDictionary(row => row.Key, row => row.Count, StringComparer.Ordinal);
        return keys.Select(key => new MigrationReviewCount(key, byKey.GetValueOrDefault(key))).ToList();
    }

    private static string FamilyLabel(string code)
    {
        var words = code.Replace('-', ' ').Replace('_', ' ').ToLowerInvariant();
        return words.Length == 0 ? code : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static void ValidateDecision(string decision, string kind)
    {
        if (kind == MigrationReviewKind.HistoryNeedsReview)
        {
            if (decision is MigrationReviewDecisionKind.AcceptAsRecorded or
                MigrationReviewDecisionKind.ExcludeFromHistory or MigrationReviewDecisionKind.FixInIncumbent)
                return;
            throw new MigrationRuleException("migration_review_decision_invalid",
                "Choose accept_as_recorded, exclude_from_history, or fix_in_incumbent.");
        }
        if (decision is not (MigrationReviewDecisionKind.MergeInto or MigrationReviewDecisionKind.KeepApart or
            MigrationReviewDecisionKind.FixInIncumbent))
            throw new MigrationRuleException("migration_review_decision_invalid",
                "Choose merge_into, keep_apart, or fix_in_incumbent.");
    }

    private static string KindLabel(string kind) => kind switch
    {
        MigrationReviewKind.SameSerialOneItem => "Same serial on one item",
        MigrationReviewKind.SameSerialDifferentItems => "Same serial on different items",
        MigrationReviewKind.OrphanEquipmentCard => "Equipment card without a serial",
        MigrationReviewKind.SerialInUserField => "Serial in a custom field",
        MigrationReviewKind.HistoryNeedsReview => "History needs review",
        _ => kind,
    };

    private static void GuardActor(string actor)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 320)
            throw new MigrationRuleException("audit_actor_required", "The authenticated user has no actor id.");
    }

    private static string Required(string? value, int max, string rule, string message)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > max) throw new MigrationRuleException(rule, message);
        return trimmed;
    }

    private static string? Optional(string? value, int max, string rule, string message)
    {
        var trimmed = value?.Trim();
        if (trimmed?.Length > max) throw new MigrationRuleException(rule, message);
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string Encode(Cursor cursor) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(cursor)));

    private static Cursor? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try { return JsonSerializer.Deserialize<Cursor>(Encoding.UTF8.GetString(Convert.FromBase64String(cursor))); }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new MigrationRuleException("migration_cursor_invalid", "The page cursor is invalid.");
        }
    }

    private sealed record Cursor(DateTime At, Guid Id);
    private sealed class CandidateMovement
    {
        public required string SourceKey { get; init; }
        public decimal Quantity { get; init; }
        public DateTime? At { get; init; }
        public DateTime? CreatedDate { get; init; }
        public int? CreatedTime { get; init; }
        public int? LogEntry { get; init; }
        public string? PartyCode { get; init; }
        public int? DocumentNumber { get; init; }
        public int? DocumentLine { get; init; }
    }
    private sealed record CandidateFacts(string SourceKey, long Transactions, bool HasCard, bool HasContract,
        DateTime? FirstReceipt, string? ReceiptKey)
    {
        public static CandidateFacts Empty(string sourceKey) => new(sourceKey, 0, false, false, null, null);
    }
    private sealed record CrossGroup(string Rule, string Key, IReadOnlyList<MigrationUnitCandidate> Candidates);
}
