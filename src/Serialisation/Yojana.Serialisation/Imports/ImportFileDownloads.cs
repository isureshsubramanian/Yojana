using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Imports;

public sealed record ImportFileDownload(Stream Content, long ContentLength, string FileName, string ContentType);

/// <summary>
/// Who may download a stored import file (owner 2026-10-19): the user who uploaded it, and the tenant administrator,
/// serial administrator and service manager for any file. Seeing the upload history is a wider right (the import roles).
/// </summary>
public static class ImportFileDownloadPolicy
{
    public static readonly IReadOnlySet<string> AnyFileRoles = new HashSet<string>(StringComparer.Ordinal)
    {
        YojanaRole.TenantAdministrator, YojanaRole.SerialAdministrator, YojanaRole.ServiceManager,
    };

    public static bool MayDownload(string uploaderRef, string actorRef, IEnumerable<string> actorRoles) =>
        StringComparer.Ordinal.Equals(uploaderRef, actorRef) || actorRoles.Any(AnyFileRoles.Contains);
}

public sealed class ImportFileDownloads(SerialisationDbContext db, IImportFileStore files, ITenantContext tenant, IClock clock)
{
    public async Task<ImportFileDownload> OpenAsync(Guid jobId, string copyKind, string actorRef,
        IReadOnlyCollection<string> actorRoles, CancellationToken cancellationToken)
    {
        if (copyKind is not (ImportFileCopyKind.Original or ImportFileCopyKind.Converted))
            throw new CommandRefusedException("import_file_kind_invalid", "The requested file copy is invalid.");
        var job = await db.ImportJobs.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.JobId == jobId, cancellationToken)
            ?? throw new CommandRefusedException("import_job_unknown", "The import job was not found.");
        if (!ImportFileDownloadPolicy.MayDownload(job.ActorRef, actorRef, actorRoles))
            throw new CommandRefusedException("import_file_download_not_permitted",
                "Only the uploader, a tenant administrator, a serial administrator or a service manager may download this file.");
        var reference = copyKind == ImportFileCopyKind.Original ? job.OriginalFileReference : job.ConvertedFileReference;
        var expectedHash = copyKind == ImportFileCopyKind.Original ? job.OriginalHash : job.ConvertedHash;
        if (reference is null || expectedHash is null)
            throw new CommandRefusedException("import_file_copy_unavailable", "That file copy is not available for this import job.");
        var opened = await files.OpenReadAsync(reference, cancellationToken);
        try
        {
            var actual = await HashAsync(opened.Content, cancellationToken);
            if (!StringComparer.Ordinal.Equals(actual, expectedHash))
                throw new CommandRefusedException("import_file_hash_mismatch", "The stored file failed integrity verification and cannot be downloaded.");
            if (!opened.Content.CanSeek)
                throw new CommandRefusedException("import_file_stream_not_seekable", "The verified file stream cannot be rewound for download.");
            opened.Content.Position = 0;
            db.ImportFileDownloadAudits.Add(new ImportFileDownloadAudit
            {
                TenantId = tenant.TenantId, AuditId = Guid.NewGuid(), JobId = jobId, CopyKind = copyKind,
                ActorRef = actorRef, DownloadedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
            var name = copyKind == ImportFileCopyKind.Original ? Path.GetFileName(job.FileName)
                : Path.ChangeExtension(Path.GetFileName(job.FileName), ".xlsx");
            return new(opened.Content, opened.ContentLength, name, ContentType(Path.GetExtension(name)));
        }
        catch
        {
            await opened.DisposeAsync();
            throw;
        }
    }

    private static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".csv" => "text/csv; charset=utf-8",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xls" => "application/vnd.ms-excel",
        _ => "application/octet-stream",
    };
}
