using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Yojana.Developer.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data;
using Yojana.Platform.Tenancy;

namespace Yojana.Developer.Data;

/// <summary>API clients, webhook subscriptions, public events and their deliveries (ADR-0018).</summary>
public sealed class DeveloperDbContext(DbContextOptions<DeveloperDbContext> options, ITenantContext tenant)
    : YojanaDbContext(options, tenant)
{
    public const string SchemaName = "developer";
    public override string Schema => SchemaName;

    public DbSet<ApiClient> ApiClients => Set<ApiClient>();
    public DbSet<ApiAccessToken> AccessTokens => Set<ApiAccessToken>();
    public DbSet<ApiIdempotencyRecord> IdempotencyRecords => Set<ApiIdempotencyRecord>();
    public DbSet<WebhookSubscription> Subscriptions => Set<WebhookSubscription>();
    public DbSet<PublicEvent> PublicEvents => Set<PublicEvent>();
    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();
    public DbSet<WebhookDeliveryAttempt> DeliveryAttempts => Set<WebhookDeliveryAttempt>();
    public DbSet<BulkOperation> BulkOperations => Set<BulkOperation>();
    public DbSet<BulkOperationUnit> BulkOperationUnits => Set<BulkOperationUnit>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiClient>(b =>
        {
            b.ToTable("api_client", table =>
            {
                table.HasCheckConstraint("ck_api_client_status", "status IN ('active','revoked')");
                table.HasCheckConstraint("ck_api_client_tier", "disclosure_tier IN ('customer','back_office')");
            });
            b.HasKey(x => new { x.TenantId, x.ClientId });
            b.Property(x => x.Name).HasMaxLength(120);
            b.Property(x => x.SecretHash).HasMaxLength(32);
            b.Property(x => x.PreviousSecretHash).HasMaxLength(32);
            b.Property(x => x.SecretHint).HasMaxLength(12).IsUnicode(false);
            b.Property(x => x.Scopes).HasMaxLength(1000).IsUnicode(false);
            b.Property(x => x.DisclosureTier).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.Status).HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt, x.ClientId });
        });
        modelBuilder.Entity<ApiAccessToken>(b =>
        {
            b.ToTable("api_access_token");
            b.HasKey(x => new { x.TenantId, x.TokenHash });
            b.Property(x => x.TokenHash).HasMaxLength(32);
            b.Property(x => x.Scopes).HasMaxLength(1000).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.ClientId, x.ExpiresAt });
            b.HasIndex(x => new { x.TenantId, x.ExpiresAt });
        });
        modelBuilder.Entity<ApiIdempotencyRecord>(b =>
        {
            b.ToTable("api_idempotency_record");
            b.HasKey(x => new { x.TenantId, x.ClientId, x.IdempotencyKey });
            b.Property(x => x.IdempotencyKey).HasMaxLength(200);
            b.Property(x => x.RequestHash).HasMaxLength(32);
            b.HasIndex(x => new { x.TenantId, x.ExpiresAt });
        });
        modelBuilder.Entity<WebhookSubscription>(b =>
        {
            b.ToTable("webhook_subscription", table => table.HasCheckConstraint("ck_webhook_subscription_status",
                "status IN ('pending_verification','active','paused','disabled')"));
            b.HasKey(x => new { x.TenantId, x.SubscriptionId });
            b.Property(x => x.Name).HasMaxLength(120);
            b.Property(x => x.Url).HasMaxLength(2048);
            b.Property(x => x.EventTypes).HasMaxLength(4000).IsUnicode(false);
            b.Property(x => x.Filters).HasMaxLength(4000).IsUnicode(false);
            b.Property(x => x.Status).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.SecretSealed).HasMaxLength(512);
            b.Property(x => x.PreviousSecretSealed).HasMaxLength(512);
            b.Property(x => x.VerificationChallengeHash).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.PausedReason).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.SubscriptionId });
        });
        modelBuilder.Entity<PublicEvent>(b =>
        {
            b.ToTable("public_event");
            b.HasKey(x => new { x.TenantId, x.EventId });
            b.Property(x => x.Type).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Version).HasMaxLength(8).IsUnicode(false);
            b.Property(x => x.RecordKind).HasMaxLength(32).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.RecordedAt, x.EventId });
            b.HasIndex(x => new { x.TenantId, x.RecordKind, x.RecordId, x.Sequence });
        });
        modelBuilder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("webhook_delivery", table => table.HasCheckConstraint("ck_webhook_delivery_status",
                "status IN ('pending','succeeded','parked','discarded')"));
            b.HasKey(x => new { x.TenantId, x.DeliveryId });
            b.Property(x => x.Status).HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.RowVersion).IsRowVersion();
            // One first delivery per event and subscription: the staging handler is at-least-once (D3).
            b.HasIndex(x => new { x.TenantId, x.SubscriptionId, x.EventId }).IsUnique()
                .HasFilter("[replay_of] IS NULL").HasDatabaseName("ux_webhook_delivery_first");
            b.HasIndex(x => new { x.TenantId, x.SubscriptionId, x.Status, x.NextAttemptAt, x.DeliveryId });
            b.HasIndex(x => new { x.TenantId, x.SubscriptionId, x.CreatedAt, x.DeliveryId });
        });
        modelBuilder.Entity<WebhookDeliveryAttempt>(b =>
        {
            b.ToTable("webhook_delivery_attempt");
            b.HasKey(x => new { x.TenantId, x.AttemptId });
            b.Property(x => x.Outcome).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.ResponseExcerpt).HasMaxLength(2048);
            b.Property(x => x.ErrorCode).HasMaxLength(64).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.DeliveryId, x.AttemptedAt, x.AttemptId });
            b.HasIndex(x => new { x.TenantId, x.AttemptedAt });
        });
        modelBuilder.Entity<BulkOperation>(b =>
        {
            b.ToTable("bulk_operation");
            b.HasKey(x => new { x.TenantId, x.OperationId });
            b.Property(x => x.Kind).HasMaxLength(64).IsUnicode(false);
        });
        modelBuilder.Entity<BulkOperationUnit>(b =>
        {
            b.ToTable("bulk_operation_unit");
            b.HasKey(x => new { x.TenantId, x.OperationId, x.UnitId });
        });
        base.OnModelCreating(modelBuilder);
    }
}

public sealed class DeveloperDesignTimeDbContextFactory : IDesignTimeDbContextFactory<DeveloperDbContext>
{
    public DeveloperDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_TEST_SQL")
            ?? "Server=localhost,1433;Database=yojana_design;User Id=sa;Password=Design_time_only1!;TrustServerCertificate=True";
        var options = new DbContextOptionsBuilder<DeveloperDbContext>()
            .UseSqlServer(connectionString, sql => sql.MigrationsHistoryTable("__migrations", DeveloperDbContext.SchemaName))
            .Options;
        return new DeveloperDbContext(options, new TenantContext());
    }
}
