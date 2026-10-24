using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Imports;

public sealed class ImportComparisonService(
    SerialisationDbContext db,
    AssignIdentifiersService assignments,
    ImportService imports,
    IMessageBus bus,
    ITenantContext tenant,
    IClock clock)
{
    public async Task<CompareImportsResult> CompareAsync(CompareImports command, CancellationToken cancellationToken)
    {
        if ((command.JobBId is null) == (command.SelectionId is null))
            throw new CommandRefusedException("comparison_source_invalid", "Supply either job B or a selection set, but not both.");
        var existing = await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.JobId == command.CommandId, cancellationToken);
        if (existing is not null) return await ResultAsync(existing.JobId, true, cancellationToken);

        var jobA = await JobAsync(command.JobAId, cancellationToken);
        var aRows = await SourceRowsAsync(jobA.JobId, cancellationToken);
        ImportJob? jobB = null;
        List<ComparisonValue> bRows;
        string secondName;
        string secondHash;
        if (command.JobBId is { } jobBId)
        {
            jobB = await JobAsync(jobBId, cancellationToken);
            bRows = await SourceRowsAsync(jobBId, cancellationToken);
            secondName = jobB.FileName;
            secondHash = jobB.OriginalHash;
        }
        else
        {
            var selectionId = command.SelectionId!.Value;
            bRows = await db.SelectionSetMembers.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.SelectionId == selectionId && row.Serial != null)
                .OrderBy(row => row.UnitId).Select(row => new ComparisonValue(0, null, row.Serial!, row.Serial!, row.UnitId)).ToListAsync(cancellationToken);
            secondName = "selection";
            secondHash = selectionId.ToString("N");
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobA.OriginalHash + ":" + secondHash))).ToLowerInvariant();
        var replay = await FindReplayAsync(jobA, hash, cancellationToken);
        if (replay is not null) return await ResultAsync(replay.JobId, true, cancellationToken);

        var allValues = aRows.Concat(bRows).Select(row => row.Normalised).Distinct(StringComparer.Ordinal).ToList();
        var collisions = new Dictionary<string, Collision>(StringComparer.Ordinal);
        foreach (var chunk in allValues.Chunk(2000))
        {
            var found = await (from identifier in db.Identifiers.AsNoTracking()
                               join episode in db.Episodes.AsNoTracking().Where(row => row.Kind == EpisodeKind.Ownership && row.ValidTo == null)
                                   on new { identifier.TenantId, identifier.UnitId } equals new { episode.TenantId, episode.UnitId } into owners
                               from owner in owners.DefaultIfEmpty()
                               where identifier.TenantId == tenant.TenantId && identifier.ValidTo == null && chunk.Contains(identifier.NormalisedValue)
                               select new { identifier.NormalisedValue, identifier.UnitId, Owner = owner == null ? null : owner.PartyRef }).ToListAsync(cancellationToken);
            foreach (var row in found) collisions.TryAdd(row.NormalisedValue, new(row.UnitId, row.Owner));
        }

        var aCounts = aRows.GroupBy(row => row.Normalised, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var bCounts = bRows.GroupBy(row => row.Normalised, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var resultRows = new List<ImportRowResult>();
        void Add(ComparisonValue row, bool fromA)
        {
            var duplicate = (fromA ? aCounts : bCounts)[row.Normalised] > 1;
            var collision = collisions.GetValueOrDefault(row.Normalised);
            var partition = collision is not null ? ImportComparisonPartition.ExistingCollision
                : duplicate ? ImportComparisonPartition.DuplicateInFile
                : (fromA ? bCounts.ContainsKey(row.Normalised) : aCounts.ContainsKey(row.Normalised)) ? ImportComparisonPartition.Both
                : fromA ? ImportComparisonPartition.OnlyA : ImportComparisonPartition.OnlyB;
            var action = partition == ImportComparisonPartition.OnlyA ? ImportReviewAction.Assign
                : partition is ImportComparisonPartition.Both or ImportComparisonPartition.DuplicateInFile ? ImportReviewAction.Skip
                : ImportReviewAction.Flag;
            resultRows.Add(new ImportRowResult
            {
                TenantId = tenant.TenantId,
                RowResultId = TimeOrderedId.New(clock.UtcNow),
                JobId = command.CommandId,
                RowNumber = row.RowNumber,
                Sheet = row.Sheet,
                RawValue = row.Raw,
                NormalisedValue = row.Normalised,
                Outcome = ImportOutcome.Skipped,
                Rule = fromA ? "comparison_a" : "comparison_b",
                UnitId = row.UnitId,
                CollisionUnitId = collision?.UnitId,
                CollisionOwnerPartyRef = collision?.OwnerPartyRef,
                ComparisonPartition = partition,
                ProposedAction = action,
                RecordedTime = clock.UtcNow,
            });
        }
        foreach (var row in aRows) Add(row, true);
        foreach (var row in bRows) Add(row, false);

        // ParentJobId is comparison provenance here, not a split-import command child. ImportService's command replay
        // distinguishes the two by file identity; removing that distinction makes a source upload replay two jobs.
        // TIGHT COUPLING (file-comparison history provenance)
        // WHAT: ImportQueries reconstructs job B from this exact "A versus B" name and SHA-256(A hash + ':' + B hash).
        // WHY: the existing row stores only job A's id, with no schema field for job B. WHAT BREAKS IF SEPARATED:
        // changing either convention makes historical comparisons show the input file as unavailable rather than report the wrong one.
        var comparison = new ImportJob
        {
            TenantId = tenant.TenantId, JobId = command.CommandId, CommandId = command.CommandId, ParentJobId = jobA.JobId,
            Source = jobA.Source, FileName = $"{jobA.FileName} versus {secondName}", OriginalHash = hash, Format = jobA.Format,
            Mode = ImportMode.Compare, TargetType = jobA.TargetType, TargetId = jobA.TargetId, TargetLine = jobA.TargetLine,
            Status = ImportJobStatus.Compared, SkippedCount = resultRows.Count, ActorRef = command.ActorRef,
            RecordedTime = clock.UtcNow, CompletedTime = clock.UtcNow,
        };
        var ownsTransaction = db.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            db.ImportJobs.Add(comparison);
            // Claim the logical comparison before persisting its rows. The unique replay key blocks a concurrent
            // comparer until this transaction has committed the complete result.
            await db.SaveChangesAsync(cancellationToken);
            jobA.Status = ImportJobStatus.Compared;
            if (jobB is not null) jobB.Status = ImportJobStatus.Compared;
            db.ImportRowResults.AddRange(resultRows);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                await transaction.DisposeAsync();
                transaction = null;
            }
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                transaction = null;
            }

            db.ChangeTracker.Clear();
            var winner = await FindReplayAsync(jobA, hash, cancellationToken);
            if (winner is not null) return await ResultAsync(winner.JobId, true, cancellationToken);
            throw;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        return await ResultAsync(command.CommandId, false, cancellationToken);
    }

    public async Task<ApplyImportReviewResult> ApplyAsync(ApplyImportReview command, CancellationToken cancellationToken)
    {
        var job = await JobAsync(command.JobId, cancellationToken);
        if (job.Mode != ImportMode.Compare) throw new CommandRefusedException("comparison_job_required", "Only a compare-mode job has a review result.");
        var rows = await db.ImportRowResults.Where(row => row.TenantId == tenant.TenantId && row.JobId == job.JobId)
            .OrderBy(row => row.RowNumber).ThenBy(row => row.RowResultId).ToListAsync(cancellationToken);
        if (job.Status is ImportJobStatus.Reviewed or ImportJobStatus.HandedOff or ImportJobStatus.Applied or ImportJobStatus.Partial)
            return new(job.JobId, job.AppliedCount, job.SkippedCount, rows.Count(row => row.ReviewedAction == ImportReviewAction.Flag),
                ImportService.View(job, rows).Rows, true);
        if (job.Status != ImportJobStatus.Compared)
            throw new CommandRefusedException("comparison_not_ready", "The compare job has not produced a result ready for review.");
        var overrides = command.Overrides.ToDictionary(row => row.RowResultId, row => row.Action);
        foreach (var row in rows)
        {
            var action = overrides.GetValueOrDefault(row.RowResultId) ?? row.ProposedAction ?? ImportReviewAction.Flag;
            if (!ImportReviewAction.All.Contains(action)) throw new CommandRefusedException("review_action_unknown", $"Review action '{action}' is not supported.");
            if (action == ImportReviewAction.Assign && row.Rule == "comparison_b")
                throw new CommandRefusedException("comparison_assignment_source_b", "Only rows from file A can be assigned; file B is the comparison baseline.");
            row.ReviewedAction = action;
        }
        var selected = rows.Where(row => row.Rule == "comparison_a" && row.ReviewedAction == ImportReviewAction.Assign).ToList();
        if (selected.Count > 0)
        {
            var target = job.TargetId is { } targetId && job.TargetType is { } targetType ? new DocumentRef(targetType, targetId, job.TargetLine)
                : throw new CommandRefusedException("target_not_resolved", "The reviewed result has no production-order target.");
            var result = await assignments.ExecuteAsync(new AssignIdentifiers(command.CommandId, target, IdentifierAssignmentMode.AllocationOrder,
                selected.Select(row => new IdentifierAssignmentInput(row.RawValue!)).ToList(), AssignIdentifiersReason.VendorList,
                command.EventTime, command.ActorRef, command.LegalEntityId,
                Correction: new CorrectionMetadata(job.JobId, AssignIdentifiersReason.VendorList, InitiatedVia: CorrectionInitiatedVia.Integration,
                    InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor, Approvers: []),
                CommittedBy: command.CommittedBy is null ? null : new CorrectionApprover(command.CommittedBy.UserRef, command.CommittedBy.Roles)), cancellationToken);
            for (var index = 0; index < selected.Count; index++)
            {
                selected[index].UnitId = result.Lines[index].UnitId;
                selected[index].NormalisedValue = result.Lines[index].NormalisedValue;
                selected[index].Rule = result.Lines[index].Reason;
                selected[index].Outcome = result.Lines[index].Outcome == IdentifierAssignmentOutcome.Assigned ? ImportOutcome.Applied : ImportOutcome.Rejected;
            }
        }
        foreach (var row in rows.Where(row => row.Outcome != ImportOutcome.Applied && row.ReviewedAction != ImportReviewAction.Assign))
        {
            row.Outcome = ImportOutcome.Skipped;
            row.Rule = row.ReviewedAction == ImportReviewAction.Flag ? "reviewer_flagged" : "reviewer_skipped";
        }
        job.AppliedCount = rows.Count(row => row.Outcome == ImportOutcome.Applied);
        job.SkippedCount = rows.Count(row => row.Outcome == ImportOutcome.Skipped);
        job.RejectedCount = rows.Count(row => row.Outcome == ImportOutcome.Rejected);
        job.Status = selected.Count == 0 ? ImportJobStatus.Reviewed : ImportJobStatus.HandedOff;
        job.CompletedTime = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var recorded = clock.UtcNow;
        await bus.PublishAsync(new ImportCompleted(new PublishedSummaryHeader(ImportCompleted.Schema, ImportCompleted.Version,
                TimeOrderedId.New(recorded), command.EventTime, recorded, command.LegalEntityId, command.ActorRef,
                job.TargetId is { } id && job.TargetType is { } type ? new DocumentRef(type, id, job.TargetLine) : null),
            job.JobId, job.Source, job.OriginalHash, job.ConvertedHash, job.Status, job.AppliedCount, job.SkippedCount, job.RejectedCount),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
        return new(job.JobId, job.AppliedCount, job.SkippedCount, rows.Count(row => row.ReviewedAction == ImportReviewAction.Flag),
            ImportService.View(job, rows).Rows, false);
    }

    private async Task<CompareImportsResult> ResultAsync(Guid jobId, bool replayed, CancellationToken cancellationToken)
    {
        var view = (await imports.ViewsAsync([jobId], cancellationToken)).Single();
        return new(jobId, view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.OnlyA),
            view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.OnlyB),
            view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.Both),
            view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.DuplicateInFile),
            view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.ExistingCollision), view.Rows, replayed);
    }

    private Task<ImportJob?> FindReplayAsync(ImportJob source, string hash, CancellationToken cancellationToken) =>
        db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.Source == source.Source && row.OriginalHash == hash && row.Mode == ImportMode.Compare
            && row.TargetType == source.TargetType && row.TargetId == source.TargetId && row.TargetLine == source.TargetLine,
            cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private async Task<ImportJob> JobAsync(Guid jobId, CancellationToken cancellationToken) =>
        await db.ImportJobs.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.JobId == jobId, cancellationToken)
            ?? throw new CommandRefusedException("import_job_unknown", $"Import job {jobId} was not found.");

    private async Task<List<ComparisonValue>> SourceRowsAsync(Guid jobId, CancellationToken cancellationToken) =>
        await db.ImportRowResults.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.JobId == jobId
                && row.NormalisedValue != null && row.Outcome != ImportOutcome.Rejected)
            .OrderBy(row => row.RowNumber).ThenBy(row => row.RowResultId)
            .Select(row => new ComparisonValue(row.RowNumber, row.Sheet, row.RawValue!, row.NormalisedValue!, row.UnitId)).ToListAsync(cancellationToken);

    private sealed record ComparisonValue(int RowNumber, string? Sheet, string Raw, string Normalised, Guid? UnitId);
    private sealed record Collision(Guid UnitId, Guid? OwnerPartyRef);
}

public static class CompareImportsHandler
{
    public static Task<CompareImportsResult> Handle(CompareImports command, ImportComparisonService service, CancellationToken cancellationToken) =>
        service.CompareAsync(command, cancellationToken);
}

public static class ApplyImportReviewHandler
{
    public static Task<ApplyImportReviewResult> Handle(ApplyImportReview command, ImportComparisonService service, CancellationToken cancellationToken) =>
        service.ApplyAsync(command, cancellationToken);
}
