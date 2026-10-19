using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Imports;

public sealed record ImportFileMaintenanceResult(int MigratedCopies, int ArchivedCopies, bool HasMore, int FailedCopies = 0);

public interface IImportFileArchiveObserver
{
    Task CopiedAsync(Guid jobId, string copyKind, CancellationToken cancellationToken);
    Task RepointedAsync(Guid jobId, string copyKind, CancellationToken cancellationToken);
}

public sealed class NoOpImportFileArchiveObserver : IImportFileArchiveObserver
{
    public Task CopiedAsync(Guid jobId, string copyKind, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task RepointedAsync(Guid jobId, string copyKind, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Small, resumable batches for legacy-byte migration and verified moves into named archive stores.</summary>
public sealed class ImportFileMaintenance(SerialisationDbContext db, ImportFileStore files,
    TenantImportFileSettings settings, ImportFileStorageOptions options, ITenantContext tenant, IClock clock,
    IImportFileArchiveObserver observer, ILogger<ImportFileMaintenance> logger)
{
    // Copies that failed in this run: skipped by later batches of the same run so one bad file cannot stop the
    // rest of the tenant's archive; the next nightly run tries them again.
    private readonly HashSet<(Guid JobId, string Kind)> failed = [];

    public async Task<ImportFileMaintenanceResult> RunBatchAsync(CancellationToken cancellationToken)
    {
        var batchSize = Math.Clamp(options.BatchSize, 1, 1000);
        var jobs = await db.ImportJobs.Where(x => x.TenantId == tenant.TenantId &&
                ((x.OriginalContent != null && x.OriginalFileReference == null) ||
                 (x.ConvertedContent != null && x.ConvertedFileReference == null)))
            .OrderBy(x => x.RecordedTime).ThenBy(x => x.JobId).Take(batchSize).ToListAsync(cancellationToken);
        var migrated = 0;
        foreach (var job in jobs)
        {
            migrated += await MigrateAsync(job, ImportFileCopyKind.Original, cancellationToken);
            migrated += await MigrateAsync(job, ImportFileCopyKind.Converted, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        var cleanupJobs = await db.ImportJobs.Where(x => x.TenantId == tenant.TenantId &&
                (x.OriginalFileCleanupReference != null || x.ConvertedFileCleanupReference != null))
            .OrderBy(x => x.RecordedTime).ThenBy(x => x.JobId).Take(batchSize).ToListAsync(cancellationToken);
        foreach (var job in cleanupJobs)
        {
            await CleanupAsync(job, ImportFileCopyKind.Original, cancellationToken);
            await CleanupAsync(job, ImportFileCopyKind.Converted, cancellationToken);
        }

        var policy = await settings.PolicyAsync(cancellationToken);
        var archived = 0;
        var archiveJobs = new List<ImportJob>();
        if (policy.ArchiveAge > 0)
        {
            var cutoff = policy.Cutoff(clock.UtcNow);
            var skip = failed.Select(x => x.JobId).Distinct().ToList();
            archiveJobs = await db.ImportJobs.Where(x => x.TenantId == tenant.TenantId && x.RecordedTime <= cutoff &&
                    !skip.Contains(x.JobId) &&
                    ((x.OriginalFileReference != null && !x.OriginalFileReference.StartsWith("archive:")) ||
                     (x.ConvertedFileReference != null && !x.ConvertedFileReference.StartsWith("archive:"))))
                .OrderBy(x => x.RecordedTime).ThenBy(x => x.JobId).Take(batchSize).ToListAsync(cancellationToken);
            foreach (var job in archiveJobs)
            {
                archived += await ArchiveOrSkipAsync(job, ImportFileCopyKind.Original, policy.ArchiveStore, cancellationToken);
                archived += await ArchiveOrSkipAsync(job, ImportFileCopyKind.Converted, policy.ArchiveStore, cancellationToken);
            }
        }

        return new(migrated, archived, jobs.Count == batchSize || cleanupJobs.Count == batchSize || archiveJobs.Count == batchSize,
            failed.Count);
    }

    /// <summary>
    /// References that name an archive store this deployment no longer configures; their downloads refuse with
    /// import_file_archive_store_unavailable. Counted per tenant inside the nightly job (tenant-scoped like every
    /// other query) rather than across all tenants at host startup.
    /// </summary>
    public async Task<long> UnavailableArchiveReferencesAsync(CancellationToken cancellationToken)
    {
        var original = db.ImportJobs.Where(x => x.TenantId == tenant.TenantId && x.OriginalFileReference != null
            && x.OriginalFileReference.StartsWith("archive:"));
        var converted = db.ImportJobs.Where(x => x.TenantId == tenant.TenantId && x.ConvertedFileReference != null
            && x.ConvertedFileReference.StartsWith("archive:"));
        foreach (var prefix in options.ArchiveStores.Select(store => $"archive:{store.Name}:"))
        {
            original = original.Where(x => !x.OriginalFileReference!.StartsWith(prefix));
            converted = converted.Where(x => !x.ConvertedFileReference!.StartsWith(prefix));
        }
        return await original.LongCountAsync(cancellationToken) + await converted.LongCountAsync(cancellationToken);
    }

    private async Task<int> ArchiveOrSkipAsync(ImportJob job, string kind, string storeName, CancellationToken cancellationToken)
    {
        if (failed.Contains((job.JobId, kind))) return 0;
        try { return await ArchiveAsync(job, kind, storeName, cancellationToken); }
        catch (CommandRefusedException ex)
        {
            failed.Add((job.JobId, kind));
            logger.LogWarning("Import file archive skipped {CopyKind} copy of job {JobId} for tenant {TenantId}: {Rule} {Reason}",
                kind, job.JobId, tenant.TenantId.Value, ex.Rule, ex.Message);
            // Leave the job as it was stored so the next run starts from a clean state.
            await db.Entry(job).ReloadAsync(cancellationToken);
            return 0;
        }
    }

    private async Task<int> MigrateAsync(ImportJob job, string kind, CancellationToken cancellationToken)
    {
        var content = kind == ImportFileCopyKind.Original ? job.OriginalContent : job.ConvertedContent;
        var reference = kind == ImportFileCopyKind.Original ? job.OriginalFileReference : job.ConvertedFileReference;
        if (content is null || reference is not null) return 0;
        await using var stream = new MemoryStream(content, writable: false);
        var fileName = FileName(job, kind);
        var stored = await files.PutAsync(job.JobId, fileName, kind, stream, job.RecordedTime, cancellationToken);
        Verify(stored, ExpectedHash(job, kind), content.LongLength, job.JobId, kind, "migration");
        if (kind == ImportFileCopyKind.Original)
        {
            job.OriginalFileReference = stored.Reference; job.OriginalLength = stored.ContentLength;
            job.OriginalArchived = false; job.OriginalContent = null;
        }
        else
        {
            job.ConvertedFileReference = stored.Reference; job.ConvertedLength = stored.ContentLength;
            job.ConvertedArchived = false; job.ConvertedContent = null;
        }
        return 1;
    }

    private async Task<int> ArchiveAsync(ImportJob job, string kind, string storeName, CancellationToken cancellationToken)
    {
        var sourceReference = Reference(job, kind);
        if (sourceReference is null || ImportFileStore.ArchiveStoreName(sourceReference) is not null) return 0;
        await using var source = await files.OpenReadAsync(sourceReference, cancellationToken);
        var expectedLength = Length(job, kind) ?? source.ContentLength;
        var copied = await files.CopyToArchiveAsync(storeName, job.JobId, FileName(job, kind), kind, source.Content,
            job.RecordedTime, cancellationToken);
        Verify(copied, ExpectedHash(job, kind), expectedLength, job.JobId, kind, "archive");
        await observer.CopiedAsync(job.JobId, kind, cancellationToken);

        if (kind == ImportFileCopyKind.Original)
        {
            job.OriginalFileCleanupReference = sourceReference;
            job.OriginalFileReference = copied.Reference;
            job.OriginalArchived = true;
        }
        else
        {
            job.ConvertedFileCleanupReference = sourceReference;
            job.ConvertedFileReference = copied.Reference;
            job.ConvertedArchived = true;
        }
        await db.SaveChangesAsync(cancellationToken);
        await observer.RepointedAsync(job.JobId, kind, cancellationToken);
        await CleanupAsync(job, kind, cancellationToken);
        return 1;
    }

    private async Task CleanupAsync(ImportJob job, string kind, CancellationToken cancellationToken)
    {
        var reference = kind == ImportFileCopyKind.Original
            ? job.OriginalFileCleanupReference : job.ConvertedFileCleanupReference;
        if (reference is null) return;
        var current = Reference(job, kind);
        if (current is null || ImportFileStore.ArchiveStoreName(current) is null)
            throw new CommandRefusedException("import_file_archive_cleanup_not_repointed",
                "The active import file cannot be deleted until its job points at an archive copy.");
        await files.DeleteActiveAsync(reference, cancellationToken);
        if (kind == ImportFileCopyKind.Original) job.OriginalFileCleanupReference = null;
        else job.ConvertedFileCleanupReference = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Verify(StoredImportFile stored, string? expectedHash, long expectedLength, Guid jobId,
        string kind, string operation)
    {
        if (expectedHash is null || !StringComparer.Ordinal.Equals(stored.Sha256, expectedHash) ||
            stored.ContentLength != expectedLength)
            throw new CommandRefusedException($"import_file_{operation}_verification_failed",
                $"The {kind} copy for import job {jobId} failed length or SHA-256 verification.");
    }
    private static string FileName(ImportJob job, string kind) => kind == ImportFileCopyKind.Original
        ? job.FileName : Path.ChangeExtension(job.FileName, ".xlsx");
    private static string? Reference(ImportJob job, string kind) => kind == ImportFileCopyKind.Original
        ? job.OriginalFileReference : job.ConvertedFileReference;
    private static string? ExpectedHash(ImportJob job, string kind) => kind == ImportFileCopyKind.Original
        ? job.OriginalHash : job.ConvertedHash;
    private static long? Length(ImportJob job, string kind) => kind == ImportFileCopyKind.Original
        ? job.OriginalLength : job.ConvertedLength;
}

[DisallowConcurrentExecution]
public sealed class ImportFileMaintenanceJob(ITenantContextSetter setter, ImportFileMaintenance maintenance,
    ILogger<ImportFileMaintenanceJob> logger) : TenantJob(setter)
{
    public const string JobName = "serialisation.import_file_maintenance";
    public const string Cron = "0 15 2 * * ?";
    protected override async Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        ImportFileMaintenanceResult result;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = await maintenance.RunBatchAsync(cancellationToken);
        } while (result.HasMore && result.MigratedCopies + result.ArchivedCopies > 0);
        var unavailable = await maintenance.UnavailableArchiveReferencesAsync(cancellationToken);
        if (unavailable > 0)
            logger.LogWarning("{UnavailableReferences} import file references name an archive store that is not configured", unavailable);
    }
}
