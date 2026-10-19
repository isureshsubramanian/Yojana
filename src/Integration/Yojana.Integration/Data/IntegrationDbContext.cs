using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Data;

/// <summary>Integration-owned persistence. No Serialisation table is mapped here.</summary>
public sealed class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options, ITenantContext tenant)
    : YojanaDbContext(options, tenant)
{
    public const string SchemaName = "integration";
    public override string Schema => SchemaName;

    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<InboundMessage> InboundMessages => Set<InboundMessage>();
    public DbSet<IdempotencyClaim> IdempotencyIndex => Set<IdempotencyClaim>();
    public DbSet<ReferenceProjection> ReferenceProjections => Set<ReferenceProjection>();
    public DbSet<CanonicalDocument> CanonicalDocuments => Set<CanonicalDocument>();
    public DbSet<ConnectionCapability> ConnectionCapabilities => Set<ConnectionCapability>();
    public DbSet<ConnectionDegradationSetting> ConnectionDegradationSettings => Set<ConnectionDegradationSetting>();
    public DbSet<ConnectionGatewaySetting> ConnectionGatewaySettings => Set<ConnectionGatewaySetting>();
    public DbSet<OutboundCommandRow> OutboundCommands => Set<OutboundCommandRow>();
    public DbSet<ExternalIdMap> ExternalIdMaps => Set<ExternalIdMap>();
    public DbSet<IntegrationExceptionTask> ExceptionTasks => Set<IntegrationExceptionTask>();
    public DbSet<ResequenceItem> ResequenceItems => Set<ResequenceItem>();
    public DbSet<DeadLetterItem> DeadLetterItems => Set<DeadLetterItem>();
    public DbSet<DeadLetterEvent> DeadLetterEvents => Set<DeadLetterEvent>();
    public DbSet<ManualOutboundTask> ManualOutboundTasks => Set<ManualOutboundTask>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Connection>(b =>
        {
            b.ToTable("connection", table => table.HasCheckConstraint("ck_connection_status", "status IN ('active','archived')"));
            b.HasKey(x => new { x.TenantId, x.ConnectionId }).IsClustered(false);
            b.Property(x => x.ConnectorCode).HasMaxLength(16);
            b.Property(x => x.DisplayLabel).HasMaxLength(160);
            b.Property(x => x.Status).HasMaxLength(24);
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt, x.ConnectionId });
            b.HasIndex(x => new { x.TenantId, x.ConnectorCode, x.ConnectionId });
        });
        modelBuilder.Entity<InboundMessage>(b =>
        {
            b.ToTable("inbound_message", table => table.HasCheckConstraint("ck_inbound_message_translation_status",
                "translation_status IN ('pending','superseded','completed','late')"));
            b.HasKey(x => new { x.TenantId, x.InboundMessageId });
            b.Property(x => x.ConnectorCode).HasMaxLength(16);
            b.Property(x => x.ErpEntity).HasMaxLength(120);
            b.Property(x => x.ErpExternalId).HasMaxLength(320);
            b.Property(x => x.ErpChangeMarker).HasMaxLength(320);
            b.Property(x => x.MarkerKind).HasMaxLength(40);
            b.Property(x => x.ContractVersion).HasMaxLength(20);
            b.Property(x => x.MessageType).HasMaxLength(80);
            b.Property(x => x.RawPayload).HasColumnType("nvarchar(max)");
            b.Property(x => x.CanonicalMessage).HasColumnType("nvarchar(max)");
            b.Property(x => x.TranslationStatus).HasMaxLength(24);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ErpEntity, x.ErpExternalId, x.IsLatest })
                .IsUnique().HasFilter("[is_latest] = 1");
            b.HasIndex(x => new { x.TenantId, x.TranslationStatus, x.AcceptedAt, x.InboundMessageId });
        });
        modelBuilder.Entity<IdempotencyClaim>(b =>
        {
            b.ToTable("idempotency_index");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.ErpEntity, x.ErpExternalId, x.ErpChangeMarker });
            b.Property(x => x.ErpEntity).HasMaxLength(120);
            b.Property(x => x.ErpExternalId).HasMaxLength(320);
            b.Property(x => x.ErpChangeMarker).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.InboundMessageId }).IsUnique();
            b.HasOne<InboundMessage>().WithOne().HasForeignKey<IdempotencyClaim>(x => new { x.TenantId, x.InboundMessageId });
        });
        modelBuilder.Entity<ReferenceProjection>(b =>
        {
            b.ToTable("reference_projection");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.EntityType, x.ExternalId });
            b.Property(x => x.EntityType).HasMaxLength(40);
            b.Property(x => x.ExternalId).HasMaxLength(320);
            b.Property(x => x.Code).HasMaxLength(160);
            b.Property(x => x.DisplayName).HasMaxLength(320);
            b.Property(x => x.TrackingMode).HasMaxLength(40);
            b.Property(x => x.Uom).HasMaxLength(32);
            b.HasIndex(x => new { x.TenantId, x.ModuleId, x.EntityType });
        });
        modelBuilder.Entity<CanonicalDocument>(b =>
        {
            b.ToTable("canonical_document");
            b.HasKey(x => new { x.TenantId, x.DocumentId });
            b.Property(x => x.ErpEntity).HasMaxLength(120);
            b.Property(x => x.ErpExternalId).HasMaxLength(320);
            b.Property(x => x.MessageType).HasMaxLength(80);
            b.Property(x => x.Payload).HasColumnType("nvarchar(max)");
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ErpEntity, x.ErpExternalId }).IsUnique();
        });
        modelBuilder.Entity<ConnectionCapability>(b =>
        {
            b.ToTable("connection_capability", table => table.HasCheckConstraint("ck_connection_capability_support",
                "support_level IN ('native','tenant_code','quantity_only','manual','unsupported')"));
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.CanonicalMessage });
            b.Property(x => x.CanonicalMessage).HasMaxLength(80);
            b.Property(x => x.SupportLevel).HasMaxLength(24);
        });
        modelBuilder.Entity<ConnectionDegradationSetting>(b =>
        {
            b.ToTable("connection_degradation_setting", table => table.HasCheckConstraint("ck_connection_degradation_rule", "rule_number BETWEEN 1 AND 7"));
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.RuleNumber });
            b.Property(x => x.SettingsJson).HasColumnType("nvarchar(max)");
        });
        modelBuilder.Entity<ConnectionGatewaySetting>(b =>
        {
            b.ToTable("connection_gateway_setting", table => table.HasCheckConstraint("ck_connection_gateway_ordering_wait", "ordering_wait_minutes BETWEEN 0 AND 1440"));
            b.HasKey(x => new { x.TenantId, x.ConnectionId });
            b.Property(x => x.OutboundMode).HasMaxLength(32);
            b.Property(x => x.NativeWarrantyObjectMode).HasMaxLength(32);
            b.Property(x => x.OrderingWaitMinutes).HasDefaultValue(15);
        });
        modelBuilder.Entity<OutboundCommandRow>(b =>
        {
            b.ToTable("outbound_command");
            b.HasKey(x => new { x.TenantId, x.OutboundCommandId });
            b.Property(x => x.CommandType).HasMaxLength(80);
            b.Property(x => x.ModuleDocumentRef).HasMaxLength(320);
            b.Property(x => x.ErpReferenceText).HasMaxLength(320);
            b.Property(x => x.Payload).HasColumnType("nvarchar(max)");
            b.Property(x => x.Status).HasMaxLength(24);
            b.Property(x => x.ErpExternalId).HasMaxLength(320);
            b.Property(x => x.ErpChangeMarker).HasMaxLength(320);
            b.Property(x => x.Outcome).HasMaxLength(40);
            b.Property(x => x.LastFailure).HasMaxLength(2000);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Status, x.NextAttemptAt, x.CreatedAt, x.OutboundCommandId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.IdempotencyKey }).IsUnique();
        });
        modelBuilder.Entity<ExternalIdMap>(b =>
        {
            b.ToTable("external_id_map");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.EntityType, x.ExternalId });
            b.Property(x => x.EntityType).HasMaxLength(40);
            b.Property(x => x.ExternalId).HasMaxLength(320);
            b.Property(x => x.NaturalKeyHash).HasMaxLength(64);
            b.Property(x => x.MappingSource).HasMaxLength(32);
            b.Property(x => x.MappedBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.ModuleId, x.EntityType, x.ValidTo });
        });
        modelBuilder.Entity<IntegrationExceptionTask>(b =>
        {
            b.ToTable("exception_task");
            b.HasKey(x => new { x.TenantId, x.ExceptionTaskId });
            b.Property(x => x.Kind).HasMaxLength(80);
            b.Property(x => x.SubjectRef).HasMaxLength(320);
            b.Property(x => x.Detail).HasMaxLength(2000);
            b.Property(x => x.Status).HasMaxLength(24);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Status, x.CreatedAt, x.ExceptionTaskId });
        });
        modelBuilder.Entity<ResequenceItem>(b =>
        {
            b.ToTable("resequence_item", table => table.HasCheckConstraint("ck_resequence_status", "status IN ('held','released')"));
            b.HasKey(x => new { x.TenantId, x.ResequenceItemId });
            b.Property(x => x.DependencyKey).HasMaxLength(400);
            b.Property(x => x.Status).HasMaxLength(24);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.DependencyKey, x.Status, x.EventTime, x.ResequenceItemId });
            b.HasIndex(x => new { x.TenantId, x.Status, x.HoldUntil, x.ResequenceItemId });
            b.HasIndex(x => new { x.TenantId, x.InboundMessageId }).IsUnique();
        });
        modelBuilder.Entity<DeadLetterItem>(b =>
        {
            b.ToTable("dead_letter_item", table =>
            {
                table.HasCheckConstraint("ck_dead_letter_direction", "direction IN ('inbound','outbound')");
                table.HasCheckConstraint("ck_dead_letter_status", "status IN ('needs_attention','retry_pending','resolved','discarded')");
            });
            b.HasKey(x => new { x.TenantId, x.DeadLetterItemId });
            b.Property(x => x.Direction).HasMaxLength(16);
            b.Property(x => x.RecordType).HasMaxLength(80);
            b.Property(x => x.SourceKind).HasMaxLength(32);
            b.Property(x => x.ReasonCode).HasMaxLength(80);
            b.Property(x => x.RawPayload).HasColumnType("nvarchar(max)");
            b.Property(x => x.CanonicalAttempt).HasColumnType("nvarchar(max)");
            b.Property(x => x.Failure).HasMaxLength(4000);
            b.Property(x => x.Status).HasMaxLength(24);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.SourceKind, x.SourceId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt, x.DeadLetterItemId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ReasonCode, x.RecordType, x.CreatedAt, x.DeadLetterItemId });
        });
        modelBuilder.Entity<DeadLetterEvent>(b =>
        {
            b.ToTable("dead_letter_event");
            b.HasKey(x => new { x.TenantId, x.DeadLetterEventId });
            b.Property(x => x.Action).HasMaxLength(48);
            b.Property(x => x.ActorRef).HasMaxLength(320);
            b.Property(x => x.Reason).HasMaxLength(2000);
            b.Property(x => x.DetailsJson).HasColumnType("nvarchar(max)");
            b.HasIndex(x => new { x.TenantId, x.DeadLetterItemId, x.OccurredAt, x.DeadLetterEventId });
            b.HasOne<DeadLetterItem>().WithMany().HasForeignKey(x => new { x.TenantId, x.DeadLetterItemId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ManualOutboundTask>(b =>
        {
            b.ToTable("manual_outbound_task", table => table.HasCheckConstraint("ck_manual_outbound_task_status",
                "status IN ('open','completed')"));
            b.HasKey(x => new { x.TenantId, x.ManualOutboundTaskId });
            b.Property(x => x.CommandType).HasMaxLength(80);
            b.Property(x => x.Draft).HasColumnType("nvarchar(max)");
            b.Property(x => x.Status).HasMaxLength(24);
            b.Property(x => x.ErpExternalId).HasMaxLength(320);
            b.Property(x => x.ErpChangeMarker).HasMaxLength(320);
            b.Property(x => x.CompletedBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.OutboundCommandId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt, x.ManualOutboundTaskId });
            b.HasOne<DeadLetterItem>().WithOne().HasForeignKey<ManualOutboundTask>(x =>
                new { x.TenantId, x.DeadLetterItemId }).OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(modelBuilder);
    }

    public static DbContextOptions<IntegrationDbContext> DesignTimeOptions(string connectionString) =>
        new DbContextOptionsBuilder<IntegrationDbContext>().UseSqlServer(connectionString,
            sql => sql.MigrationsHistoryTable("__migrations", SchemaName)).Options;
}

/// <summary>Design-time factory for <c>dotnet ef</c>; the connection string is never opened at design time.</summary>
public sealed class IntegrationDbContextDesignTimeFactory : IDesignTimeDbContextFactory<IntegrationDbContext>
{
    public IntegrationDbContext CreateDbContext(string[] args)
    {
        var options = IntegrationDbContext.DesignTimeOptions(
            "Server=localhost;Database=design_time;Encrypt=True;TrustServerCertificate=True");
        return new IntegrationDbContext(options, NoTenantContext.Instance);
    }
}
