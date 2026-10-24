using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
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
    public List<ImportFileArchiveStoreOptions> ArchiveStores { get; set; } = [];
}

public sealed class ImportFileArchiveStoreOptions
{
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public string? Path { get; set; }
    public string? ConnectionString { get; set; }
}

public interface IImportFileBackend
{
    string Scheme { get; }
    Task<StoredImportFile> PutAsync(Guid jobId, string fileName, string copyKind, Stream content,
        DateTime storedAt, CancellationToken cancellationToken);
    Task<OpenImportFile> OpenReadAsync(string reference, CancellationToken cancellationToken);
    Task DeleteAsync(string reference, CancellationToken cancellationToken);
}

/// <summary>Chooses an active backend only when writing; every later operation is routed by the opaque reference.</summary>
public sealed class ImportFileStore(TenantImportFileSettings settings, IEnumerable<IImportFileBackend> activeBackends,
    ImportFileStorageOptions options, ITenantContext tenant, IClock clock) : IImportFileStore
{
    private readonly Dictionary<string, IImportFileBackend> byScheme = activeBackends.ToDictionary(x => x.Scheme);
    private readonly Dictionary<string, ImportFileArchiveStoreOptions> archives = options.ArchiveStores
        .ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);

    public async Task<StoredImportFile> PutAsync(Guid jobId, string fileName, string copyKind, Stream content,
        DateTime storedAt, CancellationToken cancellationToken)
    {
        var policy = await settings.PolicyAsync(cancellationToken);
        return await Active(policy.Storage).PutAsync(jobId, fileName, copyKind, content, storedAt, cancellationToken);
    }

    public Task<OpenImportFile> OpenReadAsync(string reference, CancellationToken cancellationToken)
    {
        if (!TryArchiveReference(reference, out var name, out var kind, out var inner))
            return Active(Scheme(reference)).OpenReadAsync(reference, cancellationToken);
        var store = Archive(name);
        return kind switch
        {
            "file" when store.Kind == "folder" => OpenFolderArchiveAsync(store, inner, cancellationToken),
            "database" when store.Kind == "database" => OpenDatabaseArchiveAsync(store, inner, cancellationToken),
            _ => throw Unavailable(name),
        };
    }

    internal async Task<StoredImportFile> CopyToArchiveAsync(string storeName, Guid jobId, string fileName,
        string copyKind, Stream content, DateTime storedAt, CancellationToken cancellationToken)
    {
        var store = Archive(storeName);
        return store.Kind switch
        {
            "folder" => await PutFolderArchiveAsync(store, jobId, fileName, copyKind, content, storedAt, cancellationToken),
            "database" => await PutDatabaseArchiveAsync(store, jobId, fileName, copyKind, content, storedAt, cancellationToken),
            _ => throw Unavailable(storeName),
        };
    }

    internal Task DeleteActiveAsync(string reference, CancellationToken cancellationToken) =>
        Active(Scheme(reference)).DeleteAsync(reference, cancellationToken);

    /// <summary>Irreversibly removes an import copy selected by the ADR-0017 personal-data holder.</summary>
    public async Task PurgeAsync(string reference, CancellationToken cancellationToken)
    {
        if (!TryArchiveReference(reference, out var name, out var kind, out var inner))
        {
            await Active(Scheme(reference)).DeleteAsync(reference, cancellationToken);
            return;
        }
        var store = Archive(name);
        if (kind == "file" && store.Kind == "folder")
        {
            var path = ResolveArchivePath(store, FileRelative(inner));
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        if (kind == "database" && store.Kind == "database")
        {
            await using var db = ArchiveContext(store);
            await db.Files.Where(x => x.TenantId == tenant.TenantId && x.FileId == DatabaseId(inner))
                .ExecuteDeleteAsync(cancellationToken);
            return;
        }
        throw Unavailable(name);
    }

    internal static string? ArchiveStoreName(string? reference) => reference is not null &&
        TryArchiveReference(reference, out var name, out _, out _) ? name : null;

    private IImportFileBackend Active(string scheme) => byScheme.TryGetValue(scheme, out var backend) ? backend
        : throw new CommandRefusedException("import_file_backend_unknown", "The file reference names an unavailable storage backend.");

    private ImportFileArchiveStoreOptions Archive(string name) => archives.TryGetValue(name, out var store) ? store
        : throw Unavailable(name);

    private static CommandRefusedException Unavailable(string name) => new("import_file_archive_store_unavailable",
        $"The import-file archive store '{name}' is not configured on this deployment.");

    private async Task<StoredImportFile> PutFolderArchiveAsync(ImportFileArchiveStoreOptions store, Guid jobId,
        string fileName, string copyKind, Stream content, DateTime storedAt, CancellationToken cancellationToken)
    {
        ValidateKind(copyKind);
        var relative = Path.Combine(tenant.TenantId.Value.ToString("N"), storedAt.ToString("yyyy"), storedAt.ToString("MM"),
            $"{jobId:N}-{copyKind}{SafeExtension(fileName)}");
        var path = ResolveArchivePath(store, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            await using var existing = OpenFile(path);
            var existingHash = await HashAsync(existing, cancellationToken);
            if (content.CanSeek) content.Position = 0;
            var incomingHash = await HashAsync(content, cancellationToken);
            if (!StringComparer.Ordinal.Equals(existingHash, incomingHash)) throw WriteConflict();
            return new(ArchiveReference(store.Name, "file", FileReference(relative)), existing.Length, existingHash, true);
        }
        if (content.CanSeek) content.Position = 0;
        var written = await WriteFileAsync(path, content, cancellationToken);
        return written with { Reference = ArchiveReference(store.Name, "file", FileReference(relative)), Archived = true };
    }

    private Task<OpenImportFile> OpenFolderArchiveAsync(ImportFileArchiveStoreOptions store, string inner,
        CancellationToken cancellationToken)
    {
        var relative = FileRelative(inner);
        var path = ResolveArchivePath(store, relative);
        if (!File.Exists(path)) throw Missing();
        Stream stream = OpenFile(path);
        return Task.FromResult(new OpenImportFile(stream, stream.Length, string.Empty, true));
    }

    private async Task<StoredImportFile> PutDatabaseArchiveAsync(ImportFileArchiveStoreOptions store, Guid jobId,
        string fileName, string copyKind, Stream content, DateTime storedAt, CancellationToken cancellationToken)
    {
        ValidateKind(copyKind);
        await using var db = ArchiveContext(store);
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var row = await db.Files.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.JobId == jobId &&
            x.CopyKind == copyKind, cancellationToken);
        if (row is null)
        {
            row = new StoredImportFileRow { TenantId = tenant.TenantId, FileId = Guid.NewGuid(), JobId = jobId,
                CopyKind = copyKind, FileName = Path.GetFileName(fileName), Content = bytes,
                ContentLength = bytes.LongLength, Sha256 = hash, StoredAt = storedAt, ArchivedAt = clock.UtcNow };
            db.Files.Add(row);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!StringComparer.Ordinal.Equals(row.Sha256, hash)) throw WriteConflict();
        return new(ArchiveReference(store.Name, "database", DatabaseReference(row.FileId)), row.ContentLength, row.Sha256, true);
    }

    private async Task<OpenImportFile> OpenDatabaseArchiveAsync(ImportFileArchiveStoreOptions store, string inner,
        CancellationToken cancellationToken)
    {
        var id = DatabaseId(inner);
        await using var db = ArchiveContext(store);
        var row = await db.Files.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.FileId == id,
            cancellationToken) ?? throw Missing();
        return new(new MemoryStream(row.Content, writable: false), row.ContentLength, row.Sha256, true);
    }

    private ImportFilesDbContext ArchiveContext(ImportFileArchiveStoreOptions store)
    {
        var dbOptions = new DbContextOptionsBuilder<ImportFilesDbContext>().UseSqlServer(store.ConnectionString!, sql =>
            sql.MigrationsHistoryTable("__migrations", ImportFilesDbContext.SchemaName)).Options;
        return new(dbOptions, tenant);
    }

    private string ResolveArchivePath(ImportFileArchiveStoreOptions store, string relative) =>
        ResolvePath(store.Path!, relative, tenant.TenantId);

    internal static bool TryArchiveReference(string reference, out string name, out string kind, out string inner)
    {
        name = kind = inner = string.Empty;
        if (!reference.StartsWith("archive:", StringComparison.Ordinal)) return false;
        var parts = reference.Split(':', 4);
        if (parts.Length != 4 || string.IsNullOrWhiteSpace(parts[1]) || parts[2] is not ("file" or "database"))
            throw InvalidReference();
        name = parts[1]; kind = parts[2]; inner = parts[3];
        return true;
    }

    private static string ArchiveReference(string name, string kind, string inner) => $"archive:{name}:{kind}:{inner}";
    private static string Scheme(string reference)
    {
        var separator = reference.IndexOf(':');
        if (separator <= 0) throw InvalidReference();
        return reference[..separator];
    }

    internal static string FileReference(string relative) => "v1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(relative));
    internal static string FileRelative(string reference)
    {
        const string prefix = "v1:";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal)) throw InvalidPath();
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(reference[prefix.Length..])); }
        catch (FormatException) { throw InvalidPath(); }
    }
    internal static string DatabaseReference(Guid id) => $"v1:{id:N}";
    internal static Guid DatabaseId(string reference) => reference.StartsWith("v1:", StringComparison.Ordinal)
        && Guid.TryParseExact(reference[3..], "N", out var id) ? id : throw InvalidReference();

    internal static string ResolvePath(string root, string relative, TenantId tenantId)
    {
        root = Path.GetFullPath(root);
        if (Path.IsPathRooted(relative) || Segments(relative).Any(segment => segment is "." or "..")) throw InvalidPath();
        var path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !Segments(relative)[0].Equals(tenantId.Value.ToString("N"), StringComparison.Ordinal)) throw InvalidPath();
        return path;
    }

    internal static string[] Segments(string path) => path.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
    internal static string SafeExtension(string fileName)
    {
        var extension = Path.GetExtension(Path.GetFileName(fileName)).ToLowerInvariant();
        return extension is ".csv" or ".xlsx" or ".xls" ? extension : ".bin";
    }
    internal static void ValidateKind(string kind)
    {
        if (kind is not (ImportFileCopyKind.Original or ImportFileCopyKind.Converted)) throw InvalidPath();
    }
    internal static FileStream OpenFile(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
        FileOptions.Asynchronous | FileOptions.SequentialScan);
    internal static async Task<StoredImportFile> WriteFileAsync(string path, Stream content, CancellationToken cancellationToken)
    {
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920]; long length = 0; int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                hash.AppendData(buffer, 0, read); length += read;
            }
            await output.FlushAsync(cancellationToken); output.Close(); File.Move(temporary, path, false);
            return new(string.Empty, length, Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static async Task<string> HashAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920]; int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer, 0, read);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
    private static CommandRefusedException InvalidPath() => new("import_file_path_invalid", "The import file path is invalid.");
    private static CommandRefusedException InvalidReference() => new("import_file_reference_invalid", "The file reference is invalid.");
    private static CommandRefusedException Missing() => new("import_file_missing", "The stored import file is missing.");
    private static CommandRefusedException WriteConflict() => new("import_file_write_conflict", "A different file copy already exists for this import job.");
}

public sealed class FileImportFileBackend(ImportFileStorageOptions options, ITenantContext tenant) : IImportFileBackend
{
    public string Scheme => "file";
    public async Task<StoredImportFile> PutAsync(Guid jobId, string fileName, string copyKind, Stream content,
        DateTime storedAt, CancellationToken cancellationToken)
    {
        ImportFileStore.ValidateKind(copyKind);
        var relative = Path.Combine(tenant.TenantId.Value.ToString("N"), storedAt.ToString("yyyy"), storedAt.ToString("MM"),
            $"{jobId:N}-{copyKind}{ImportFileStore.SafeExtension(fileName)}");
        var path = Resolve(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            await using var existing = ImportFileStore.OpenFile(path);
            var existingHash = await ImportFileStore.HashAsync(existing, cancellationToken);
            if (content.CanSeek) content.Position = 0;
            var incomingHash = await ImportFileStore.HashAsync(content, cancellationToken);
            if (!StringComparer.Ordinal.Equals(existingHash, incomingHash))
                throw new CommandRefusedException("import_file_write_conflict", "A different file copy already exists for this import job.");
            return new(Reference(relative), existing.Length, existingHash, false);
        }
        if (content.CanSeek) content.Position = 0;
        var written = await ImportFileStore.WriteFileAsync(path, content, cancellationToken);
        return written with { Reference = Reference(relative) };
    }
    public Task<OpenImportFile> OpenReadAsync(string reference, CancellationToken cancellationToken)
    {
        var relative = Relative(reference); var path = Resolve(relative);
        if (!File.Exists(path)) throw new CommandRefusedException("import_file_missing", "The stored import file is missing.");
        Stream stream = ImportFileStore.OpenFile(path);
        return Task.FromResult(new OpenImportFile(stream, stream.Length, string.Empty,
            ImportFileStore.Segments(relative).Contains("archive", StringComparer.Ordinal)));
    }
    public Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        var path = Resolve(Relative(reference));
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
    private string Root => string.IsNullOrWhiteSpace(options.FileRoot)
        ? throw new CommandRefusedException("import_file_root_not_configured", $"Configuration '{ImportFileStorageOptions.SectionName}:FileRoot' is required for file storage.")
        : Path.GetFullPath(options.FileRoot);
    private string Resolve(string relative) => ImportFileStore.ResolvePath(Root, relative, tenant.TenantId);
    private static string Relative(string reference)
    {
        const string prefix = "file:";
        if (!reference.StartsWith(prefix, StringComparison.Ordinal))
            throw new CommandRefusedException("import_file_path_invalid", "The import file path is invalid.");
        return ImportFileStore.FileRelative(reference[prefix.Length..]);
    }
    private static string Reference(string relative) => "file:" + ImportFileStore.FileReference(relative);
}

public sealed class DatabaseImportFileBackend(ImportFilesDbContext db, ITenantContext tenant) : IImportFileBackend
{
    public string Scheme => "database";
    public async Task<StoredImportFile> PutAsync(Guid jobId, string fileName, string copyKind, Stream content,
        DateTime storedAt, CancellationToken cancellationToken)
    {
        ImportFileStore.ValidateKind(copyKind);
        using var memory = new MemoryStream(); await content.CopyToAsync(memory, cancellationToken);
        var bytes = memory.ToArray(); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var row = await db.Files.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.JobId == jobId && x.CopyKind == copyKind, cancellationToken);
        if (row is null)
        {
            row = new StoredImportFileRow { TenantId = tenant.TenantId, FileId = Guid.NewGuid(), JobId = jobId,
                CopyKind = copyKind, FileName = Path.GetFileName(fileName), Content = bytes, ContentLength = bytes.LongLength,
                Sha256 = hash, StoredAt = storedAt };
            db.Files.Add(row); await db.SaveChangesAsync(cancellationToken);
        }
        else if (!StringComparer.Ordinal.Equals(row.Sha256, hash))
            throw new CommandRefusedException("import_file_write_conflict", "A different file copy already exists for this import job.");
        return View(row);
    }
    public async Task<OpenImportFile> OpenReadAsync(string reference, CancellationToken cancellationToken)
    {
        var id = Id(reference);
        var row = await db.Files.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.FileId == id, cancellationToken)
            ?? throw new CommandRefusedException("import_file_missing", "The stored import file is missing.");
        return new(new MemoryStream(row.Content, writable: false), row.ContentLength, row.Sha256, row.ArchivedAt is not null);
    }
    public async Task DeleteAsync(string reference, CancellationToken cancellationToken)
    {
        var id = Id(reference);
        var row = await db.Files.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.FileId == id, cancellationToken);
        if (row is not null) { db.Files.Remove(row); await db.SaveChangesAsync(cancellationToken); }
    }
    private static StoredImportFile View(StoredImportFileRow row) =>
        new($"database:{ImportFileStore.DatabaseReference(row.FileId)}", row.ContentLength, row.Sha256, row.ArchivedAt is not null);
    private static Guid Id(string reference) => reference.StartsWith("database:", StringComparison.Ordinal)
        ? ImportFileStore.DatabaseId(reference[9..])
        : throw new CommandRefusedException("import_file_reference_invalid", "The database file reference is invalid.");
}
