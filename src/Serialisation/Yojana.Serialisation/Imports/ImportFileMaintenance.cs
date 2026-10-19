using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Imports;

public sealed record ImportFileMaintenanceResult(int MigratedCopies, int ArchivedCopies, bool HasMore);

/// <summary>Small, resumable batches for legacy-byte migration and age-based archival.</summary>
public sealed class ImportFileMaintenance(SerialisationDbContext db, IImportFileStore files,
    TenantImportFileSettings settings, ImportFileStorageOptions options, ITenantContext tenant, IClock clock)
{
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

        var policy = await settings.PolicyAsync(cancellationToken);
        var archived = 0;
        if (policy.ArchiveAfterDays > 0)
        {
            var cutoff = clock.UtcNow.AddDays(-policy.ArchiveAfterDays);
            var archiveJobs = await db.ImportJobs.Where(x => x.TenantId == tenant.TenantId && x.RecordedTime <= cutoff &&
                    ((!x.OriginalArchived && x.OriginalFileReference != null) ||
                     (!x.ConvertedArchived && x.ConvertedFileReference != null)))
                .OrderBy(x => x.RecordedTime).ThenBy(x => x.JobId).Take(batchSize).ToListAsync(cancellationToken);
            foreach (var job in archiveJobs)
            {
                archived += await ArchiveAsync(job, ImportFileCopyKind.Original, cancellationToken);
                archived += await ArchiveAsync(job, ImportFileCopyKind.Converted, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        var hasMore = jobs.Count == batchSize || archived >= batchSize;
        return new(migrated, archived, hasMore);
    }

    private async Task<int> MigrateAsync(ImportJob job, string kind, CancellationToken cancellationToken)
    {
        var content = kind == ImportFileCopyKind.Original ? job.OriginalContent : job.ConvertedContent;
        var reference = kind == ImportFileCopyKind.Original ? job.OriginalFileReference : job.ConvertedFileReference;
        if (content is null || reference is not null) return 0;
        await using var stream = new MemoryStream(content, writable: false);
        var fileName = kind == ImportFileCopyKind.Original ? job.FileName : Path.ChangeExtension(job.FileName, ".xlsx");
        var stored = await files.PutAsync(job.JobId, fileName, kind, stream, job.RecordedTime, cancellationToken);
        var expected = kind == ImportFileCopyKind.Original ? job.OriginalHash : job.ConvertedHash;
        if (!StringComparer.Ordinal.Equals(stored.Sha256, expected))
            throw new CommandRefusedException("import_file_migration_hash_mismatch", $"The {kind} copy for import job {job.JobId} failed hash verification.");
        if (kind == ImportFileCopyKind.Original)
        {
            job.OriginalFileReference = stored.Reference;
            job.OriginalLength = stored.ContentLength;
            job.OriginalArchived = stored.Archived;
            job.OriginalContent = null;
        }
        else
        {
            job.ConvertedFileReference = stored.Reference;
            job.ConvertedLength = stored.ContentLength;
            job.ConvertedArchived = stored.Archived;
            job.ConvertedContent = null;
        }
        return 1;
    }

    private async Task<int> ArchiveAsync(ImportJob job, string kind, CancellationToken cancellationToken)
    {
        var reference = kind == ImportFileCopyKind.Original ? job.OriginalFileReference : job.ConvertedFileReference;
        var archived = kind == ImportFileCopyKind.Original ? job.OriginalArchived : job.ConvertedArchived;
        if (reference is null || archived) return 0;
        var moved = await files.ArchiveAsync(reference, cancellationToken);
        if (kind == ImportFileCopyKind.Original)
        {
            job.OriginalFileReference = moved.Reference;
            job.OriginalArchived = true;
        }
        else
        {
            job.ConvertedFileReference = moved.Reference;
            job.ConvertedArchived = true;
        }
        return 1;
    }
}

[DisallowConcurrentExecution]
public sealed class ImportFileMaintenanceJob(ITenantContextSetter setter, ImportFileMaintenance maintenance) : TenantJob(setter)
{
    public const string JobName = "serialisation.import_file_maintenance";
    public const string Cron = "0 15 2 * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        while ((await maintenance.RunBatchAsync(cancellationToken)).HasMore) cancellationToken.ThrowIfCancellationRequested();
    }
}
