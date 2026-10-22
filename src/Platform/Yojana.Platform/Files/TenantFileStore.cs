using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Files;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Records;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Files;

public sealed class TenantFileStoreOptions
{
    public const string SectionName = "Platform:Files";
    public string? DatabaseConnectionString { get; set; }
}

public sealed class TenantStoredFile : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid FileId { get; set; }
    public required string RecordKind { get; set; }
    public Guid RecordId { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long ContentLength { get; set; }
    public required string Sha256 { get; set; }
    public required byte[] Content { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; }
}

/// <summary>
/// Shared application files use the same dedicated-database mechanism as import bytes, but this separate schema and
/// context are the seam: neither store reads the other's rows, settings or references, so import behaviour is unchanged.
/// Deploying the schemas to different databases later does not change either contract.
/// </summary>
public sealed class TenantFilesDbContext(DbContextOptions<TenantFilesDbContext> options, ITenantContext tenant)
    : YojanaDbContext(options, tenant)
{
    public const string SchemaName = "platform_files";
    public override string Schema => SchemaName;
    public DbSet<TenantStoredFile> Files => Set<TenantStoredFile>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantStoredFile>(b =>
        {
            b.ToTable("stored_file");
            b.HasKey(x => new { x.TenantId, x.FileId });
            b.Property(x => x.RecordKind).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.FileName).HasMaxLength(260);
            b.Property(x => x.ContentType).HasMaxLength(100).IsUnicode(false);
            b.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.Property(x => x.Content).HasColumnType("varbinary(max)");
            b.HasIndex(x => new { x.TenantId, x.RecordKind, x.RecordId, x.UploadedAt, x.FileId });
            b.HasIndex(x => new { x.TenantId, x.Sha256, x.FileId });
        });
        base.OnModelCreating(modelBuilder);
    }
}

public sealed class TenantFilesDesignTimeDbContextFactory : IDesignTimeDbContextFactory<TenantFilesDbContext>
{
    public TenantFilesDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("YOJANA_FILES_SQL")
            ?? Environment.GetEnvironmentVariable("YOJANA_TEST_SQL")
            ?? "Server=localhost,1433;Database=yojana_files;User Id=sa;Password=not-used-at-design-time;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<TenantFilesDbContext>().UseSqlServer(connection,
            sql => sql.MigrationsHistoryTable("__migrations", TenantFilesDbContext.SchemaName)).Options;
        return new(options, new TenantContext());
    }
}

public sealed class TenantFileSettings(ITenantSettings settings)
{
    public const string MaximumBytesKey = "files.maximum_bytes";
    public const string AllowedExtensionsKey = "files.allowed_extensions";
    public const long DefaultMaximumBytes = 10 * 1024 * 1024;
    public const string DefaultAllowedExtensions = ".pdf,.png,.jpg,.jpeg,.txt,.csv,.xlsx,.docx";

    public async Task<Policy> PolicyAsync(CancellationToken cancellationToken)
    {
        var rawMaximum = await settings.GetAsync(MaximumBytesKey, cancellationToken);
        var maximum = long.TryParse(rawMaximum, out var parsedMaximum) && parsedMaximum is >= 1 and <= 100 * 1024 * 1024
            ? parsedMaximum : DefaultMaximumBytes;
        var rawExtensions = await settings.GetAsync(AllowedExtensionsKey, cancellationToken);
        var values = string.IsNullOrWhiteSpace(rawExtensions) ? DefaultAllowedExtensions : rawExtensions;
        var extensions = values.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormaliseExtension).Where(x => AllowedContentTypes.ContainsKey(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(maximum, extensions.Count == 0
            ? DefaultAllowedExtensions.Split(',').ToHashSet(StringComparer.OrdinalIgnoreCase) : extensions);
    }

    public sealed record Policy(long MaximumBytes, IReadOnlySet<string> AllowedExtensions);

    internal static readonly IReadOnlyDictionary<string, string> AllowedContentTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".pdf"] = "application/pdf", [".png"] = "image/png", [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg", [".txt"] = "text/plain", [".csv"] = "text/csv",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        };

    private static string NormaliseExtension(string value) =>
        value.StartsWith('.') ? value.ToLowerInvariant() : "." + value.ToLowerInvariant();
}

public sealed class TenantFileStore(TenantFilesDbContext db, TenantFileSettings settings,
    IServiceProvider services, ITenantContext tenant, IClock clock) : ITenantFileStore, ITenantFileLifecycle
{
    public async Task<TenantFileMetadata> PutAsync(TenantFileOwner owner, TenantFileUpload upload,
        TenantFileCaller caller, CancellationToken cancellationToken)
    {
        await EnsureMayViewAsync(owner, caller, cancellationToken);
        var policy = await settings.PolicyAsync(cancellationToken);
        var fileName = Path.GetFileName(upload.FileName).Trim();
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 260)
            throw Rule("file_name_invalid", "The file name is missing or longer than 260 characters.");
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        if (!policy.AllowedExtensions.Contains(extension) ||
            !TenantFileSettings.AllowedContentTypes.TryGetValue(extension, out var contentType))
            throw Rule("file_type_not_allowed", "This tenant does not allow that file type.");
        if (upload.ContentLength < 0 || upload.ContentLength > policy.MaximumBytes)
            throw Rule("file_too_large", $"The file is larger than this tenant's {policy.MaximumBytes} byte limit.");

        using var memory = new MemoryStream(upload.ContentLength > int.MaxValue ? 0 : (int)upload.ContentLength);
        await CopyBoundedAsync(upload.Content, memory, policy.MaximumBytes, cancellationToken);
        var bytes = memory.ToArray();
        if (bytes.LongLength != upload.ContentLength)
            throw Rule("file_length_mismatch", "The uploaded file length does not match its declared length.");
        ValidateLeadingBytes(extension, bytes);
        var now = clock.UtcNow;
        var row = new TenantStoredFile
        {
            TenantId = tenant.TenantId, FileId = TimeOrderedId.New(now), RecordKind = owner.RecordKind,
            RecordId = owner.RecordId, FileName = fileName, ContentType = contentType,
            ContentLength = bytes.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            Content = bytes, UploadedBy = caller.UserId, UploadedAt = now,
        };
        db.Files.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return Metadata(row);
    }

    public async Task<TenantFileMetadata?> GetMetadataAsync(Guid fileId, TenantFileCaller caller,
        CancellationToken cancellationToken)
    {
        var row = await db.Files.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.FileId == fileId)
            .Select(x => new TenantStoredFile { TenantId = x.TenantId, FileId = x.FileId, RecordKind = x.RecordKind,
                RecordId = x.RecordId, FileName = x.FileName, ContentType = x.ContentType,
                ContentLength = x.ContentLength, Sha256 = x.Sha256, Content = Array.Empty<byte>(),
                UploadedBy = x.UploadedBy, UploadedAt = x.UploadedAt }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        await EnsureMayViewAsync(new(row.RecordKind, row.RecordId), caller, cancellationToken);
        return Metadata(row);
    }

    public async Task<OpenTenantFile?> OpenReadAsync(Guid fileId, TenantFileCaller caller,
        CancellationToken cancellationToken)
    {
        var row = await db.Files.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.FileId == fileId, cancellationToken);
        if (row is null) return null;
        await EnsureMayViewAsync(new(row.RecordKind, row.RecordId), caller, cancellationToken);
        return new(Metadata(row), new MemoryStream(row.Content, writable: false));
    }

    public async Task<int> PurgeAsync(IReadOnlyCollection<Guid> fileIds, CancellationToken cancellationToken)
    {
        if (fileIds.Count == 0) return 0;
        return await db.Files.Where(x => x.TenantId == tenant.TenantId && fileIds.Contains(x.FileId))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task EnsureMayViewAsync(TenantFileOwner owner, TenantFileCaller caller,
        CancellationToken cancellationToken)
    {
        if (caller.IsPortalUser) throw Rule("file_portal_forbidden", "Portal users cannot access staff attachments.");
        if (!RecordKind.All.Contains(owner.RecordKind, StringComparer.Ordinal))
            throw Rule("file_record_kind_unknown", "The owning record kind is not available for attachments.");
        var resolver = services.GetKeyedService<IRecordReferenceResolver>(owner.RecordKind)
            ?? throw Rule("file_record_kind_unavailable", "The owning record kind is not available in this host.");
        var found = await resolver.ResolveAsync([owner.RecordId], caller.Viewer, cancellationToken);
        if (!found.TryGetValue(owner.RecordId, out var record) || !record.Exists)
            throw Rule("file_record_not_found", "The owning record does not exist.");
        if (!record.MayView) throw Rule("file_record_not_visible", "The current user may not view the owning record.");
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long maximum,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maximum) throw Rule("file_too_large", $"The file is larger than this tenant's {maximum} byte limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static void ValidateLeadingBytes(string extension, byte[] bytes)
    {
        if (bytes.Length == 0) throw Rule("file_content_invalid", "The file is empty.");
        if (Has(bytes, 0x4d, 0x5a) || Has(bytes, 0x7f, 0x45, 0x4c, 0x46) || Has(bytes, 0x23, 0x21) ||
            Has(bytes, 0xca, 0xfe, 0xba, 0xbe) || Has(bytes, 0xcf, 0xfa, 0xed, 0xfe) ||
            Has(bytes, 0xfe, 0xed, 0xfa, 0xcf))
            throw Rule("file_executable_refused", "Executable and script content is not allowed.");
        var valid = extension switch
        {
            ".pdf" => Has(bytes, 0x25, 0x50, 0x44, 0x46, 0x2d),
            ".png" => Has(bytes, 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a),
            ".jpg" or ".jpeg" => Has(bytes, 0xff, 0xd8, 0xff),
            ".xlsx" or ".docx" => Has(bytes, 0x50, 0x4b, 0x03, 0x04) || Has(bytes, 0x50, 0x4b, 0x05, 0x06),
            ".txt" or ".csv" => IsText(bytes),
            _ => false,
        };
        if (!valid) throw Rule("file_content_type_mismatch", "The file content does not match its extension.");
    }

    private static bool IsText(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) return false;
        try { _ = new UTF8Encoding(false, true).GetString(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool Has(byte[] bytes, params byte[] prefix) =>
        bytes.Length >= prefix.Length && bytes.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static TenantFileMetadata Metadata(TenantStoredFile row) => new(row.FileId,
        new(row.RecordKind, row.RecordId), row.FileName, row.ContentType, row.ContentLength, row.Sha256,
        row.UploadedBy, row.UploadedAt);

    private static TenantFileRuleException Rule(string rule, string message) => new(rule, message);
}
