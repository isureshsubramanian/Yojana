using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Imports;

public sealed class ImportFileStorageOptions
{
    public const string SectionName = "Serialisation:ImportFiles";
    public string? FileRoot { get; set; }
    public string? DatabaseConnectionString { get; set; }
    public int BatchSize { get; set; } = 100;
}

public interface IImportFileBackend
{
    string Scheme { get; }
    Task<StoredImportFile> PutAsync(Guid jobId, string fileName, string copyKind, Stream content,
        DateTime storedAt, CancellationToken cancellationToken);
    Task<OpenImportFile> OpenReadAsync(string reference, CancellationToken cancellationToken);
    Task<StoredImportFile> ArchiveAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>Chooses a backend only when writing; the reference routes every later operation.</summary>
public sealed class ImportFileStore(TenantImportFileSettings settings, IEnumerable<IImportFileBackend> backends)
    : IImportFileStore
{
    private readonly Dictionary<string, IImportFileBackend> byScheme = backends.ToDictionary(x => x.Scheme);

    public async Task<StoredImportFile> PutAsync(Guid jobId, string fileName, string copyKind, Stream content,
        DateTime storedAt, CancellationToken cancellationToken)
    {
        var policy = await settings.PolicyAsync(cancellationToken);
        return await Backend(policy.Storage).PutAsync(jobId, fileName, copyKind, content, storedAt, cancellationToken);
    }

    public Task<OpenImportFile> OpenReadAsync(string reference, CancellationToken cancellationToken) =>
        Backend(Scheme(reference)).OpenReadAsync(reference, cancellationToken);

    public Task<StoredImportFile> ArchiveAsync(string reference, CancellationToken cancellationToken) =>
        Backend(Scheme(reference)).ArchiveAsync(reference, cancellationToken);

    private IImportFileBackend Backend(string scheme) => byScheme.TryGetValue(scheme, out var backend) ? backend
        : throw new CommandRefusedException("import_file_backend_unknown", "The file reference names an unavailable storage backend.");

    private static string Scheme(string reference)
    {
        var separator = reference.IndexOf(':');
        if (separator <= 0) throw new CommandRefusedException("import_file_reference_invalid", "The file reference is invalid.");
        return reference[..separator];
    }
}

public sealed class FileImportFileBackend(ImportFileStorageOptions options, ITenantContext tenant) : IImportFileBackend
{
    public string Scheme => "file";

    public async Task<StoredImportFile> PutAsync(Guid jobId, string fileName, string copyKind, Stream content,
        DateTime storedAt, CancellationToken cancellationToken)
    {
        ValidateKind(copyKind);
        var extension = SafeExtension(fileName);
        var relative = Path.Combine(tenant.TenantId.Value.ToString("N"), storedAt.ToString("yyyy"), storedAt.ToString("MM"),
            $"{jobId:N}-{copyKind}{extension}");
        var path = Resolve(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            await using var existing = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            var existingHash = await HashAsync(existing, cancellationToken);
            if (content.CanSeek) content.Position = 0;
            var incomingHash = await HashAsync(content, cancellationToken);
            if (!StringComparer.Ordinal.Equals(existingHash, incomingHash))
                throw new CommandRefusedException("import_file_write_conflict", "A different file copy already exists for this import job.");
            return new(Reference(relative), existing.Length, existingHash, false);
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long length = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read);
                length += read;
            }
            await output.FlushAsync(cancellationToken);
            output.Close();
            File.Move(temporary, path, false);
            return new(Reference(relative), length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task<OpenImportFile> OpenReadAsync(string reference, CancellationToken cancellationToken)
    {
        var relative = Relative(reference);
        var path = Resolve(relative);
        if (!File.Exists(path)) throw new CommandRefusedException("import_file_missing", "The stored import file is missing.");
        Stream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var archived = Segments(relative).Contains("archive", StringComparer.Ordinal);
        return Task.FromResult(new OpenImportFile(stream, stream.Length, "", archived));
    }

    public async Task<StoredImportFile> ArchiveAsync(string reference, CancellationToken cancellationToken)
    {
        var relative = Relative(reference);
        if (Segments(relative).Contains("archive", StringComparer.Ordinal))
        {
            await using var existing = await OpenReadAsync(reference, cancellationToken);
            return new(reference, existing.ContentLength, await HashAsync(existing.Content, cancellationToken), true);
        }
        var source = Resolve(relative);
        if (!File.Exists(source)) throw new CommandRefusedException("import_file_missing", "The stored import file is missing.");
        var parts = Segments(relative);
        var archivedRelative = Path.Combine(parts[0], "archive", Path.Combine(parts.Skip(1).ToArray()));
        var destination = Resolve(archivedRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination)) File.Move(source, destination, false);
        else if (File.Exists(source)) File.Delete(source);
        await using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return new(Reference(archivedRelative), stream.Length, await HashAsync(stream, cancellationToken), true);
    }

    private string Root => string.IsNullOrWhiteSpace(options.FileRoot)
        ? throw new CommandRefusedException("import_file_root_not_configured", $"Configuration '{ImportFileStorageOptions.SectionName}:FileRoot' is required for file storage.")
        : Path.GetFullPath(options.FileRoot);

    private string Resolve(string relative)
    {
        if (Path.IsPathRooted(relative)) throw InvalidPath();
        if (Segments(relative).Any(segment => segment is "." or "..")) throw InvalidPath();
        var path = Path.GetFullPath(Path.Combine(Root, relative));
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !Segments(relative)[0].Equals(tenant.TenantId.Value.ToString("N"), StringComparison.Ordinal)) throw InvalidPath();
        return path;
    }

    private static string Relative(string reference)
    {
        const string prefix = "file:v1:";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)) throw InvalidPath();
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(reference[prefix.Length..])); }
        catch (FormatException) { throw InvalidPath(); }
    }

    private static string[] Segments(string path) => path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    private static string Reference(string relative) => "file:v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(relative));
    private static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(Path.GetFileName(fileName)).ToLowerInvariant();
        return extension is ".csv" or ".xlsx" or ".xls" ? extension : ".bin";
    }
    private static void ValidateKind(string kind)
    {
        if (kind is not (ImportFileCopyKind.Original or ImportFileCopyKind.Converted)) throw InvalidPath();
    }
    private static CommandRefusedException InvalidPath() => new("import_file_path_invalid", "The import file path is invalid.");
    private static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}

public sealed class DatabaseImportFileBackend(ImportFilesDbContext db, ITenantContext tenant, IClock clock) : IImportFileBackend
{
    public string Scheme => "database";

    public async Task<StoredImportFile> PutAsync(Guid jobId, string fileName, string copyKind, Stream content,
        DateTime storedAt, CancellationToken cancellationToken)
    {
        if (copyKind is not (ImportFileCopyKind.Original or ImportFileCopyKind.Converted))
            throw new CommandRefusedException("import_file_kind_invalid", "The import file copy kind is invalid.");
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var existing = await db.Files.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.JobId == jobId && x.CopyKind == copyKind, cancellationToken);
        if (existing is null)
        {
            existing = new StoredImportFileRow { TenantId = tenant.TenantId, FileId = Guid.NewGuid(), JobId = jobId,
                CopyKind = copyKind, FileName = Path.GetFileName(fileName), Content = bytes, ContentLength = bytes.LongLength,
                Sha256 = hash, StoredAt = storedAt };
            db.Files.Add(existing);
        }
        else if (!StringComparer.Ordinal.Equals(existing.Sha256, hash))
            throw new CommandRefusedException("import_file_write_conflict", "A different file copy already exists for this import job.");
        await db.SaveChangesAsync(cancellationToken);
        return View(existing);
    }

    public async Task<OpenImportFile> OpenReadAsync(string reference, CancellationToken cancellationToken)
    {
        var id = Id(reference);
        var row = await db.Files.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.FileId == id, cancellationToken)
            ?? throw new CommandRefusedException("import_file_missing", "The stored import file is missing.");
        return new(new MemoryStream(row.Content, writable: false), row.ContentLength, row.Sha256, row.ArchivedAt is not null);
    }

    public async Task<StoredImportFile> ArchiveAsync(string reference, CancellationToken cancellationToken)
    {
        var id = Id(reference);
        var row = await db.Files.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.FileId == id, cancellationToken)
            ?? throw new CommandRefusedException("import_file_missing", "The stored import file is missing.");
        if (row.ArchivedAt is null)
        {
            row.ArchivedAt = clock.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        return View(row);
    }

    private static StoredImportFile View(StoredImportFileRow row) =>
        new($"database:v1:{row.FileId:N}", row.ContentLength, row.Sha256, row.ArchivedAt is not null);
    private static Guid Id(string reference) => reference.StartsWith("database:v1:", StringComparison.Ordinal)
        && Guid.TryParseExact(reference[12..], "N", out var id) ? id
        : throw new CommandRefusedException("import_file_reference_invalid", "The database file reference is invalid.");
}
