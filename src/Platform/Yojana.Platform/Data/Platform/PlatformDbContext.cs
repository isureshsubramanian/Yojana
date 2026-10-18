using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Data.Platform;

/// <summary>The platform's own schema: tenant registry, settings, users, memberships and the smoke log.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenant)
    : YojanaDbContext(options, tenant)
{
    public const string SchemaName = "platform";

    public override string Schema => SchemaName;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<TenantSetting> TenantSettings => Set<TenantSetting>();

    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<UserTenantMembership> Memberships => Set<UserTenantMembership>();

    public DbSet<SmokeLog> SmokeLogs => Set<SmokeLog>();

    public DbSet<DocumentPresence> DocumentPresence => Set<DocumentPresence>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("tenant");
            b.HasKey(t => t.Id);
            b.Property(t => t.Code).HasMaxLength(20);
            b.Property(t => t.Name).HasMaxLength(200);
            b.HasIndex(t => t.Code).IsUnique();
        });

        modelBuilder.Entity<TenantSetting>(b =>
        {
            b.ToTable("tenant_setting", t => t.IsTemporal(h => h.UseHistoryTable("tenant_setting_history")));
            b.HasKey(s => new { s.TenantId, s.Key });
            b.Property(s => s.Key).HasMaxLength(100);
        });

        modelBuilder.Entity<AppUser>(b =>
        {
            b.ToTable("app_user");
            b.HasKey(u => u.Id);
            b.Property(u => u.Email).HasMaxLength(320);
            b.Property(u => u.DisplayName).HasMaxLength(200);
            b.Property(u => u.PasswordHash).HasMaxLength(400);
            b.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<UserTenantMembership>(b =>
        {
            b.ToTable("user_tenant_membership");
            b.HasKey(m => new { m.UserId, m.TenantId });
            b.HasOne(m => m.User).WithMany(u => u.Memberships).HasForeignKey(m => m.UserId);
            b.HasOne(m => m.Tenant).WithMany().HasForeignKey(m => m.TenantId);
        });

        modelBuilder.Entity<SmokeLog>(b =>
        {
            b.ToTable("smoke_log");
            b.HasKey(s => new { s.TenantId, s.Id });
            b.Property(s => s.Source).HasMaxLength(20);
            b.Property(s => s.Note).HasMaxLength(400);
            b.HasIndex(s => new { s.TenantId, s.RecordedAt });
        });

        modelBuilder.Entity<DocumentPresence>(b =>
        {
            b.ToTable("document_presence");
            b.HasKey(p => new { p.TenantId, p.UserId, p.DocumentType, p.DocumentId });
            b.Property(p => p.DocumentType).HasMaxLength(80);
            b.Property(p => p.DocumentId).HasMaxLength(160);
            b.Property(p => p.Mode).HasMaxLength(10);
            b.HasIndex(p => new { p.TenantId, p.DocumentType, p.DocumentId, p.LastHeartbeat });
            b.HasIndex(p => new { p.TenantId, p.LastHeartbeat });
        });

        // Base conventions (naming, filters) run after the explicit mapping so they see the final shape.
        base.OnModelCreating(modelBuilder);
    }
}

/// <summary>Design-time factory for `dotnet ef`; the connection string is never opened at design time.</summary>
public sealed class PlatformDbContextDesignTimeFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseSqlServer("Server=localhost;Database=design_time;Encrypt=True;TrustServerCertificate=True",
                sql => sql.MigrationsHistoryTable("__migrations", PlatformDbContext.SchemaName))
            .Options;
        return new PlatformDbContext(options, NoTenantContext.Instance);
    }
}
