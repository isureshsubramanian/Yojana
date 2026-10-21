using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.LegacyMigration.Profiling;

public interface IMigrationProfileQueue
{
    void EnqueueAutomatic(MigrationBatch batch);
    void DiscardAutomatic(Guid batchId);
}

public interface IMigrationProfileStatus
{
    Task<bool> IsLoadBlocked(Guid batchId, CancellationToken cancellationToken);
}

public sealed class MigrationProfileService(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    MigrationProfileOutcomeSettings settings,
    IEnumerable<IMigrationProfileQuery> queries) : IMigrationProfileQueue, IMigrationProfileStatus
{
    public void EnqueueAutomatic(MigrationBatch batch)
    {
        // TIGHT COUPLING (successful extraction/profile hand-off): MigrationBatchService calls this before its
        // success SaveChanges so the succeeded batch and its one automatic queued run commit together. Moving this
        // after that save can strand a successful extraction without the mandatory profile run.
        if (db.MigrationProfileRuns.Local.Any(run => run.BatchId == batch.BatchId))
            return;
        db.MigrationProfileRuns.Add(NewRun(batch.BatchId));
    }

    public void DiscardAutomatic(Guid batchId)
    {
        // This is the rollback half of the extraction/profile hand-off documented in MigrationBatchService. If the
        // shared success save fails, leaving the Added entry tracked would persist a profile for the failed batch when
        // the worker records that failure.
        foreach (var run in db.MigrationProfileRuns.Local.Where(row => row.BatchId == batchId &&
                     db.Entry(row).State == EntityState.Added).ToList())
            db.Entry(run).State = EntityState.Detached;
    }

    public async Task<StartMigrationProfileResponse> EnqueueAsync(Guid batchId,
        CancellationToken cancellationToken)
    {
        var batch = await db.MigrationBatches.AsNoTracking().SingleOrDefaultAsync(row =>
            row.TenantId == tenant.TenantId && row.BatchId == batchId, cancellationToken)
            ?? throw new MigrationRuleException("migration_batch_not_found", "The migration batch was not found.");
        if (batch.Status != MigrationBatchStatus.Succeeded)
            throw new MigrationRuleException("migration_profile_batch_not_succeeded",
                "Only a successful extraction batch can be profiled.");
        var newer = await db.MigrationBatches.AsNoTracking().AnyAsync(row =>
            row.TenantId == tenant.TenantId && row.SourceSystem == batch.SourceSystem &&
            row.Status == MigrationBatchStatus.Succeeded && row.RequestedAt > batch.RequestedAt,
            cancellationToken);
        if (newer)
            throw new MigrationRuleException("migration_profile_batch_not_current",
                "This batch is no longer the current staged snapshot; profile its stored run or re-extract.");
        var run = NewRun(batchId);
        db.MigrationProfileRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return new(run.RunId, run.BatchId, run.Status);
    }

    public async Task<int> RunPendingAsync(CancellationToken cancellationToken)
    {
        var completed = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var run = await ClaimAsync(cancellationToken);
            if (run is null) return completed;
            await ExecuteAsync(run, cancellationToken);
            completed++;
        }
        return completed;
    }

    public async Task<MigrationProfileView?> GetAsync(Guid batchId, CancellationToken cancellationToken)
    {
        var run = await db.MigrationProfileRuns.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batchId)
            .OrderByDescending(row => row.StartedAt == null).ThenByDescending(row => row.StartedAt)
            .ThenByDescending(row => row.RunId).FirstOrDefaultAsync(cancellationToken);
        if (run is null) return null;
        var results = await db.MigrationProfileResults.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == run.RunId)
            .ToDictionaryAsync(row => row.ClassCode, StringComparer.Ordinal, cancellationToken);
        var classes = MigrationProfileClasses.All.Select(item => results.TryGetValue(item.Code, out var result)
            ? new MigrationProfileClassView(item.Code, item.Name, result.Outcome,
                MigrationProfileOutcome.Label(result.Outcome), result.RowCount, result.Change, result.Note)
            : new MigrationProfileClassView(item.Code, item.Name, item.DefaultOutcome,
                MigrationProfileOutcome.Label(item.DefaultOutcome), null, null, null)).ToList();
        return new(run.RunId, run.BatchId, run.StartedAt, run.FinishedAt, run.Status, classes);
    }

    public async Task<MigrationProfileSamplesView?> SamplesAsync(Guid batchId, string classCode,
        CancellationToken cancellationToken)
    {
        if (!MigrationProfileClasses.All.Any(item => item.Code == classCode))
            throw new MigrationRuleException("migration_profile_class_not_found",
                "The profiling class was not found.");
        var runId = await db.MigrationProfileRuns.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batchId &&
                (row.Status == MigrationProfileRunStatus.Completed || row.Status == MigrationProfileRunStatus.Blocked))
            .OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.RunId)
            .Select(row => (Guid?)row.RunId).FirstOrDefaultAsync(cancellationToken);
        if (runId is null) return null;
        var json = await db.MigrationProfileResults.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.RunId == runId && row.ClassCode == classCode)
            .Select(row => row.SamplesJson).SingleOrDefaultAsync(cancellationToken);
        if (json is null) return null;
        return new(runId.Value, classCode,
            JsonSerializer.Deserialize<List<MigrationProfileSample>>(json) ?? []);
    }

    public async Task<bool> IsLoadBlocked(Guid batchId, CancellationToken cancellationToken)
    {
        var status = await db.MigrationProfileRuns.AsNoTracking().Where(row =>
                row.TenantId == tenant.TenantId && row.BatchId == batchId &&
                (row.Status == MigrationProfileRunStatus.Completed || row.Status == MigrationProfileRunStatus.Blocked))
            .OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.RunId)
            .Select(row => row.Status).FirstOrDefaultAsync(cancellationToken);
        return status == MigrationProfileRunStatus.Blocked;
    }

    private MigrationProfileRun NewRun(Guid batchId) => new()
    {
        TenantId = tenant.TenantId,
        RunId = Guid.CreateVersion7(clock.UtcNow),
        BatchId = batchId,
        Status = MigrationProfileRunStatus.Queued,
    };

    private async Task<MigrationProfileRun?> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
            cancellationToken);
        var run = await db.MigrationProfileRuns.Where(row =>
                row.TenantId == tenant.TenantId && row.Status == MigrationProfileRunStatus.Queued)
            .OrderBy(row => row.RunId).FirstOrDefaultAsync(cancellationToken);
        if (run is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        run.Status = MigrationProfileRunStatus.Running;
        run.StartedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return run;
    }

    private async Task ExecuteAsync(MigrationProfileRun run, CancellationToken cancellationToken)
    {
        try
        {
            var batch = await db.MigrationBatches.AsNoTracking().SingleAsync(row =>
                row.TenantId == tenant.TenantId && row.BatchId == run.BatchId, cancellationToken);
            var previousRunId = await (from previousRun in db.MigrationProfileRuns.AsNoTracking()
                                       join previousBatch in db.MigrationBatches.AsNoTracking()
                                           on new { previousRun.TenantId, previousRun.BatchId } equals
                                           new { previousBatch.TenantId, previousBatch.BatchId }
                                       where previousRun.TenantId == tenant.TenantId &&
                                             previousBatch.SourceSystem == batch.SourceSystem &&
                                             previousBatch.RequestedAt < batch.RequestedAt &&
                                             (previousRun.Status == MigrationProfileRunStatus.Completed ||
                                              previousRun.Status == MigrationProfileRunStatus.Blocked)
                                       orderby previousRun.FinishedAt descending, previousRun.RunId descending
                                       select (Guid?)previousRun.RunId).FirstOrDefaultAsync(cancellationToken);
            var previous = previousRunId is null
                ? new Dictionary<string, long?>(StringComparer.Ordinal)
                : await db.MigrationProfileResults.AsNoTracking().Where(result =>
                        result.TenantId == tenant.TenantId && result.RunId == previousRunId)
                    .ToDictionaryAsync(result => result.ClassCode, result => result.RowCount,
                        StringComparer.Ordinal, cancellationToken);
            var outcomes = await settings.ResolveAsync(cancellationToken);
            var orderedQueries = queries.ToDictionary(query => query.Class.Code, StringComparer.Ordinal);
            if (orderedQueries.Count != MigrationProfileClasses.All.Count)
                throw new InvalidOperationException("The fixed migration profiling query pack is incomplete.");
            var context = new MigrationProfileQueryContext(db, run.BatchId, batch.SourceSystem,
                batch.FinishedAt ?? batch.StartedAt ?? clock.UtcNow);
            var results = new List<MigrationProfileResult>(MigrationProfileClasses.All.Count);
            foreach (var item in MigrationProfileClasses.All)
            {
                var answer = await orderedQueries[item.Code].ExecuteAsync(context, cancellationToken);
                previous.TryGetValue(item.Code, out var previousCount);
                results.Add(new()
                {
                    TenantId = tenant.TenantId,
                    RunId = run.RunId,
                    ClassCode = item.Code,
                    Outcome = outcomes[item.Code],
                    RowCount = answer.Count,
                    PreviousRowCount = previousCount,
                    Change = answer.Count is not null && previousCount is not null
                        ? answer.Count - previousCount : null,
                    Note = answer.Note,
                    SamplesJson = JsonSerializer.Serialize(answer.Samples.Take(20)),
                });
            }
            db.MigrationProfileResults.AddRange(results);
            run.FinishedAt = clock.UtcNow;
            run.Status = results.Any(result => result.Outcome == MigrationProfileOutcome.StopLoad &&
                    result.RowCount > 0)
                ? MigrationProfileRunStatus.Blocked : MigrationProfileRunStatus.Completed;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiscardPendingResults(run.RunId);
            run.Status = MigrationProfileRunStatus.Failed;
            run.FinishedAt = clock.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch
        {
            DiscardPendingResults(run.RunId);
            run.Status = MigrationProfileRunStatus.Failed;
            run.FinishedAt = clock.UtcNow;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    private void DiscardPendingResults(Guid runId)
    {
        foreach (var result in db.MigrationProfileResults.Local.Where(row => row.RunId == runId &&
                     db.Entry(row).State == EntityState.Added).ToList())
            db.Entry(result).State = EntityState.Detached;
    }
}
