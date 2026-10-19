using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data;

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
    public DbSet<ExternalIdMap> ExternalIdMaps => Set<ExternalIdMap>();
    public DbSet<ConnectorCatalogueEntry> ConnectorCatalogue => Set<ConnectorCatalogueEntry>();
    public DbSet<CanonicalDocument> CanonicalDocuments => Set<CanonicalDocument>();
    public DbSet<ConnectionCapability> ConnectionCapabilities => Set<ConnectionCapability>();
    public DbSet<ConnectionDegradationSetting> ConnectionDegradationSettings => Set<ConnectionDegradationSetting>();
    public DbSet<ConnectionGatewaySetting> ConnectionGatewaySettings => Set<ConnectionGatewaySetting>();
    public DbSet<OutboundCommandRow> OutboundCommands => Set<OutboundCommandRow>();
    public DbSet<IntegrationExceptionTask> ExceptionTasks => Set<IntegrationExceptionTask>();
    public DbSet<ResequenceItem> ResequenceItems => Set<ResequenceItem>();
    public DbSet<TenantDataKeyEntity> TenantDataKeys => Set<TenantDataKeyEntity>();
    public DbSet<ConnectionCredentialEntity> ConnectionCredentials => Set<ConnectionCredentialEntity>();
    public DbSet<TenantDataKeyRewrapCheckpointEntity> TenantDataKeyRewrapCheckpoints => Set<TenantDataKeyRewrapCheckpointEntity>();
    public DbSet<ConnectionEventEntity> ConnectionEvents => Set<ConnectionEventEntity>();
    public DbSet<ConnectionThrottleProfileEntity> ConnectionThrottleProfiles => Set<ConnectionThrottleProfileEntity>();
    public DbSet<ConnectionNotificationSource> ConnectionNotificationSources => Set<ConnectionNotificationSource>();
    public DbSet<ConnectionPollWatermark> ConnectionPollWatermarks => Set<ConnectionPollWatermark>();

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
                "translation_status IN ('pending','processing','held','failed','superseded','completed','late','needs_attention')"));
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
            b.Property(x => x.LastTranslationError).HasMaxLength(2000);
            b.Property(x => x.AttentionReasonCode).HasMaxLength(80);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.ErpEntity, x.ErpExternalId, x.IsLatest })
                .IsUnique().HasFilter("[is_latest] = 1");
            b.HasIndex(x => new { x.TenantId, x.TranslationStatus, x.NextTranslationAttemptAt, x.AcceptedAt, x.InboundMessageId });
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
            b.HasKey(x => new { x.TenantId, x.EntityType, x.ModuleId });
            b.Property(x => x.EntityType).HasMaxLength(40);
            b.Property(x => x.Code).HasMaxLength(160);
            b.Property(x => x.DisplayName).HasMaxLength(320);
            b.Property(x => x.TrackingMode).HasMaxLength(40);
            b.Property(x => x.Uom).HasMaxLength(32);
            b.Property(x => x.AttributesJson).HasColumnType("nvarchar(max)").HasDefaultValue("{}");
            b.HasIndex(x => new { x.TenantId, x.EntityType, x.Code });
        });
        modelBuilder.Entity<ExternalIdMap>(b =>
        {
            b.ToTable("external_id_map", table => table.HasCheckConstraint("ck_external_id_map_source",
                "mapping_source IN ('manual','auto','migration')"));
            b.HasKey(x => new { x.TenantId, x.ExternalIdMapId });
            b.Property(x => x.EntityType).HasMaxLength(40);
            b.Property(x => x.ExternalId).HasMaxLength(320);
            b.Property(x => x.NaturalKeyHash).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.MappingSource).HasMaxLength(16);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.EntityType, x.ExternalId })
                .IsUnique().HasFilter("[valid_to] IS NULL");
            b.HasIndex(x => new { x.TenantId, x.ModuleRef, x.EntityType, x.ValidTo });
        });
        modelBuilder.Entity<ConnectorCatalogueEntry>(b =>
        {
            b.ToTable("connector_catalogue");
            b.HasKey(x => new { x.TenantId, x.ConnectorCode });
            b.Property(x => x.ConnectorCode).HasMaxLength(16);
            b.Property(x => x.DisplayName).HasMaxLength(160);
            b.Property(x => x.CustomerCostDisclosure).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.Enabled, x.DisplayName, x.ConnectorCode });
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
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Status, x.CreatedAt, x.OutboundCommandId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.IdempotencyKey }).IsUnique();
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
        modelBuilder.Entity<TenantDataKeyEntity>(b =>
        {
            b.ToTable("tenant_data_key");
            b.HasKey(x => new { x.TenantId, x.Version });
            b.Property(x => x.KeyEncryptionKeyId).HasMaxLength(500);
            b.Property(x => x.KeyEncryptionKeyVersion).HasMaxLength(100);
            b.Property(x => x.KeyEncryptionAlgorithm).HasMaxLength(32);
            b.Property(x => x.WrappedKeyNonce).HasMaxLength(12);
            b.Property(x => x.WrappedKeyCiphertext).HasColumnType("varbinary(max)");
            b.Property(x => x.WrappedKeyTag).HasMaxLength(16);
            // TIGHT COUPLING (active data-key replacement): this filtered index and
            // EfTenantDataKeyStore.ReplaceActiveAsync are one invariant. The transaction retires the current key and
            // inserts its replacement atomically; separating them can leave no active key or two active keys.
            b.HasIndex(x => new { x.TenantId, x.IsActive }).IsUnique().HasFilter("[is_active] = 1");
        });
        modelBuilder.Entity<ConnectionCredentialEntity>(b =>
        {
            b.ToTable("connection_credential");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.Purpose, x.CreatedAt });
            b.Property(x => x.Purpose).HasMaxLength(100);
            b.Property(x => x.Nonce).HasMaxLength(12);
            b.Property(x => x.Ciphertext).HasColumnType("varbinary(max)");
            b.Property(x => x.Tag).HasMaxLength(16);
            // TIGHT COUPLING (credential version replacement): current-row lookup and
            // EfConnectionCredentialStore.SaveAsync must share a transaction. Otherwise a crash can expose two current
            // credentials or none, and rotation readers cannot choose safely.
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Purpose, x.RotatedAt });
        });
        modelBuilder.Entity<TenantDataKeyRewrapCheckpointEntity>(b =>
        {
            b.ToTable("tenant_data_key_rewrap_checkpoint");
            b.HasKey(x => new { x.TenantId, x.OperationId });
            b.Property(x => x.OperationId).HasMaxLength(100);
            b.HasIndex(x => new { x.TenantId, x.UpdatedAt, x.OperationId });
        });
        modelBuilder.Entity<ConnectionEventEntity>(b =>
        {
            b.ToTable("connection_event");
            b.HasKey(x => new { x.TenantId, x.SequenceNo });
            b.HasAlternateKey(x => new { x.TenantId, x.EventId });
            b.Property(x => x.EventType).HasMaxLength(64);
            b.Property(x => x.ActorRef).HasMaxLength(200);
            b.Property(x => x.DetailsJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.PreviousRecordHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
            b.Property(x => x.RecordHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
            // TIGHT COUPLING (tenant audit sequence): EfConnectionAuditSink allocates SequenceNo while holding the
            // tenant's latest row under SERIALIZABLE isolation. A database sequence would not preserve per-tenant hash
            // ordering; a non-transactional read can fork the hash chain.
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.OccurredAt, x.SequenceNo });
        });
        modelBuilder.Entity<ConnectionThrottleProfileEntity>(b =>
        {
            b.ToTable("connection_throttle_profile", table =>
            {
                table.HasCheckConstraint("ck_connection_throttle_positive_caps",
                    "(requests_per_second IS NULL OR requests_per_second > 0) AND (requests_per_minute IS NULL OR requests_per_minute > 0) AND (requests_per_day IS NULL OR requests_per_day > 0)");
                table.HasCheckConstraint("ck_connection_throttle_limits",
                    "burst >= 1 AND max_concurrent_calls >= 1 AND retry_after_ceiling_seconds >= 1 AND alert_threshold_percent BETWEEN 1 AND 100 AND used_calls >= 0 AND budget_revision >= 0");
            });
            b.HasKey(x => new { x.TenantId, x.ConnectionId });
            b.Property(x => x.ConnectorCode).HasMaxLength(20);
            b.HasIndex(x => new { x.TenantId, x.ConnectorCode, x.ConnectionId });
        });
        modelBuilder.Entity<ConnectionNotificationSource>(b =>
        {
            b.ToTable("connection_notification_source");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.Source });
            b.Property(x => x.Source).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Enabled, x.Source });
        });
        modelBuilder.Entity<ConnectionPollWatermark>(b =>
        {
            b.ToTable("connection_poll_watermark", table =>
            {
                table.HasCheckConstraint("ck_connection_poll_overlap", "overlap_seconds BETWEEN 0 AND 86400");
                table.HasCheckConstraint("ck_connection_poll_interval", "interval_seconds BETWEEN 1 AND 86400");
                table.HasCheckConstraint("ck_connection_poll_revision", "revision >= 0");
            });
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.EntityType });
            b.Property(x => x.EntityType).HasMaxLength(120);
            b.Property(x => x.Watermark).HasMaxLength(320);
            b.Property(x => x.LastError).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.NextPollAt, x.ConnectionId, x.EntityType });
        });

        base.OnModelCreating(modelBuilder);
    }

    public static DbContextOptions<IntegrationDbContext> DesignTimeOptions(string connectionString) =>
        new DbContextOptionsBuilder<IntegrationDbContext>().UseSqlServer(connectionString,
            sql => sql.MigrationsHistoryTable("__migrations", SchemaName)).Options;
}
