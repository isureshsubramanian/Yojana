using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
        if (existing is not null) return await ResultAsync(existing.JobId, cancellationToken);

        var jobA = await JobAsync(command.JobAId, cancellationToken);
        var aRows = await SourceRowsAsync(jobA.JobId, cancellationToken);
        List<ComparisonValue> bRows;
        string secondName;
        string secondHash;
        if (command.JobBId is { } jobBId)
        {
            var jobB = await JobAsync(jobBId, cancellationToken);
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

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jobA.OriginalHash + ":" + secondHash))).ToLowerInvariant();
        db.ImportJobs.Add(new ImportJob
        {
            TenantId = tenant.TenantId, JobId = command.CommandId, CommandId = command.CommandId, ParentJobId = jobA.JobId,
            Source = jobA.Source, FileName = $"{jobA.FileName} versus {secondName}", OriginalHash = hash, Format = jobA.Format,
            Mode = ImportMode.Compare, TargetType = jobA.TargetType, TargetId = jobA.TargetId, TargetLine = jobA.TargetLine,
            Status = "compared", SkippedCount = resultRows.Count, ActorRef = command.ActorRef, RecordedTime = clock.UtcNow, CompletedTime = clock.UtcNow,
        });
        db.ImportRowResults.AddRange(resultRows);
        await db.SaveChangesAsync(cancellationToken);
        return await ResultAsync(command.CommandId, cancellationToken);
    }

    public async Task<ApplyImportReviewResult> ApplyAsync(ApplyImportReview command, CancellationToken cancellationToken)
    {
        var job = await JobAsync(command.JobId, cancellationToken);
        if (job.Mode != ImportMode.Compare) throw new CommandRefusedException("comparison_job_required", "Only a compare-mode job has a review result.");
        var rows = await db.ImportRowResults.Where(row => row.TenantId == tenant.TenantId && row.JobId == job.JobId)
            .OrderBy(row => row.RowNumber).ThenBy(row => row.RowResultId).ToListAsync(cancellationToken);
        if (job.Status == "applied")
            return new(job.JobId, job.AppliedCount, job.SkippedCount, rows.Count(row => row.ReviewedAction == ImportReviewAction.Flag),
                ImportService.View(job, rows).Rows, true);
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
        job.Status = job.RejectedCount == 0 ? "applied" : "partial";
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

    private async Task<CompareImportsResult> ResultAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var view = (await imports.ViewsAsync([jobId], cancellationToken)).Single();
        return new(jobId, view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.OnlyA),
            view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.OnlyB),
            view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.Both),
            view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.DuplicateInFile),
            view.Rows.Count(row => row.ComparisonPartition == ImportComparisonPartition.ExistingCollision), view.Rows);
    }

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
