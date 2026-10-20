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

    public DbSet<SignInThrottle> SignInThrottles => Set<SignInThrottle>();

    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();

    public DbSet<MailMessage> MailMessages => Set<MailMessage>();

    public DbSet<SmokeLog> SmokeLogs => Set<SmokeLog>();

    public DbSet<DocumentPresence> DocumentPresence => Set<DocumentPresence>();

    public DbSet<UserInvitation> UserInvitations => Set<UserInvitation>();

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
            b.Property(u => u.SecurityStamp).HasDefaultValueSql("NEWID()");
            b.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<SignInThrottle>(b =>
        {
            b.ToTable("sign_in_throttle");
            b.HasKey(t => new { t.KeyKind, t.KeyHash });
            b.Property(t => t.KeyKind).HasMaxLength(20);
            b.Property(t => t.KeyHash).HasMaxLength(32).IsFixedLength();
            b.HasIndex(t => t.ExpiresAt);
        });

        modelBuilder.Entity<SecurityEvent>(b =>
        {
            b.ToTable("security_event");
            b.HasKey(e => e.Id);
            b.Property(e => e.Kind).HasMaxLength(60);
            b.Property(e => e.ClientAddress).HasMaxLength(45);
            b.Property(e => e.UserAgent).HasMaxLength(256);
            b.Property(e => e.Reason).HasMaxLength(100);
            b.Property(e => e.UnknownUserHash).HasMaxLength(64).IsFixedLength();
            b.HasIndex(e => new { e.TenantId, e.OccurredAt, e.Id });
            b.HasIndex(e => new { e.UserId, e.OccurredAt, e.Id });
            b.HasIndex(e => new { e.OccurredAt, e.Id });
            b.HasOne<AppUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<AppUser>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Tenant>().WithMany().HasForeignKey(e => e.TenantId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<MailMessage>(b =>
        {
            b.ToTable("mail_message");
            b.HasKey(m => m.Id);
            b.Property(m => m.Kind).HasMaxLength(60);
            b.Property(m => m.Recipient).HasMaxLength(320);
            b.Property(m => m.Subject).HasMaxLength(300);
            b.Property(m => m.Status).HasMaxLength(20);
            b.Property(m => m.Transport).HasMaxLength(20);
            b.Property(m => m.ProviderMessageId).HasMaxLength(200);
            b.Property(m => m.LastError).HasMaxLength(400);
            b.HasIndex(m => new { m.TenantId, m.QueuedAt, m.Id });
            b.HasIndex(m => new { m.UserId, m.QueuedAt });
            b.HasIndex(m => new { m.QueuedAt, m.Id });
            b.HasOne<AppUser>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<UserTenantMembership>(b =>
        {
            b.ToTable("user_tenant_membership");
            b.HasKey(m => new { m.UserId, m.TenantId });
            b.HasOne(m => m.User).WithMany(u => u.Memberships).HasForeignKey(m => m.UserId);
            b.HasOne(m => m.Tenant).WithMany().HasForeignKey(m => m.TenantId);
            b.Property(m => m.Version).IsRowVersion();
            b.HasOne<AppUser>().WithMany().HasForeignKey(m => m.DeactivatedByUserId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<UserInvitation>(b =>
        {
            b.ToTable("user_invitation");
            b.HasKey(i => i.Id);
            b.Property(i => i.Email).HasMaxLength(320);
            b.Property(i => i.DisplayName).HasMaxLength(200);
            b.Property(i => i.TokenHash).HasMaxLength(32).IsFixedLength();
            b.HasIndex(i => i.TokenHash).IsUnique();
            b.HasIndex(i => new { i.TenantId, i.Email, i.ExpiresAt });
            b.HasIndex(i => new { i.TenantId, i.DisplayName, i.Id });
            b.HasIndex(i => new { i.ExpiresAt, i.Id });
            b.HasOne(i => i.Tenant).WithMany().HasForeignKey(i => i.TenantId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<AppUser>().WithMany().HasForeignKey(i => i.CreatedByUserId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<AppUser>().WithMany().HasForeignKey(i => i.AcceptedUserId).OnDelete(DeleteBehavior.NoAction);
            b.HasOne<AppUser>().WithMany().HasForeignKey(i => i.RevokedByUserId).OnDelete(DeleteBehavior.NoAction);
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
