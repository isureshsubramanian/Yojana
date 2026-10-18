using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Imports;

/// <summary>
/// Database-backed default for the narrow file-store boundary. A host may replace it with object storage without
/// changing intake. References are opaque; callers never reach into these columns.
/// </summary>
public sealed class DatabaseImportFileStore(SerialisationDbContext db, ITenantContext tenant) : IImportFileStore
{
    public async Task<string> RetainInboxAsync(Guid jobId, string fileName, byte[] content, CancellationToken cancellationToken)
    {
        var job = await JobAsync(jobId, cancellationToken);
        job.OriginalContent = content;
        return job.InboxReference = $"import-inbox:{jobId:N}:original";
    }

    public async Task<string> ArchiveAsync(Guid jobId, string fileName, byte[] content, string copyKind, CancellationToken cancellationToken)
    {
        var job = await JobAsync(jobId, cancellationToken);
        if (copyKind == "converted") job.ConvertedContent = content;
        else job.OriginalContent = content;
        return $"import-archive:{jobId:N}:{copyKind}";
    }

    private Task<Data.Entities.ImportJob> JobAsync(Guid jobId, CancellationToken cancellationToken) =>
        db.ImportJobs.FirstAsync(row => row.TenantId == tenant.TenantId && row.JobId == jobId, cancellationToken);
}
