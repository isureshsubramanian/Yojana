using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data;

namespace Yojana.Serialisation.Imports;

public sealed class StoredImportFileRow : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid FileId { get; set; }
    public Guid JobId { get; set; }
    public required string CopyKind { get; set; }
    public required string FileName { get; set; }
    public required byte[] Content { get; set; }
    public long ContentLength { get; set; }
    public required string Sha256 { get; set; }
    public DateTime StoredAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
}

/// <summary>Import bytes only; this context always uses the dedicated import-files connection string.</summary>
public sealed class ImportFilesDbContext(DbContextOptions<ImportFilesDbContext> options, ITenantContext tenant)
    : YojanaDbContext(options, tenant)
{
    public const string SchemaName = "import_files";
    public override string Schema => SchemaName;
    public DbSet<StoredImportFileRow> Files => Set<StoredImportFileRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredImportFileRow>(b =>
        {
            b.ToTable("stored_file", t => t.HasCheckConstraint("ck_stored_file_kind", "copy_kind IN ('original', 'converted')"));
            b.HasKey(row => new { row.TenantId, row.FileId });
            b.Property(row => row.CopyKind).HasMaxLength(12).IsUnicode(false);
            b.Property(row => row.FileName).HasMaxLength(260);
            b.Property(row => row.Content).HasColumnType("varbinary(max)");
            b.Property(row => row.Sha256).HasMaxLength(64).IsUnicode(false).IsFixedLength();
            b.HasIndex(row => new { row.TenantId, row.JobId, row.CopyKind }).IsUnique();
            b.HasIndex(row => new { row.TenantId, row.ArchivedAt, row.StoredAt });
        });
        base.OnModelCreating(modelBuilder);
    }
}

public sealed class ImportFilesDbContextFactory : IDesignTimeDbContextFactory<ImportFilesDbContext>
{
    public ImportFilesDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("YOJANA_IMPORT_FILES_SQL")
            ?? "Server=localhost,1433;Database=yojana_import_files;User Id=sa;Password=not-used-at-design-time;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<ImportFilesDbContext>().UseSqlServer(connection,
            sql => sql.MigrationsHistoryTable("__migrations", ImportFilesDbContext.SchemaName)).Options;
        return new(options, new DesignTenantContext());
    }

    private sealed class DesignTenantContext : ITenantContext
    {
        public bool HasTenant => false;
        public TenantId TenantId => default;
    }
}
