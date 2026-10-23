using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data;
using Yojana.Platform.Tenancy;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Integration.Connectors.CX04;

namespace Yojana.Integration.Data;

/// <summary>Integration-owned persistence. No Serialisation table is mapped here.</summary>
public sealed class IntegrationDbContext(DbContextOptions<IntegrationDbContext> options, ITenantContext tenant)
    : YojanaDbContext(options, tenant)
{
    public DbSet<Yojana.Integration.Relays.SiteRelay> SiteRelays => Set<Yojana.Integration.Relays.SiteRelay>();
    public DbSet<Yojana.Integration.Relays.RelayRequestRow> RelayRequests => Set<Yojana.Integration.Relays.RelayRequestRow>();
    public DbSet<Yojana.Integration.Relays.RelayNonce> RelayNonces => Set<Yojana.Integration.Relays.RelayNonce>();

    public const string SchemaName = "integration";
    public override string Schema => SchemaName;

    public DbSet<ConnectionHealth> ConnectionHealth => Set<ConnectionHealth>();
    public DbSet<Cx04AuthorisationState> Cx04AuthorisationStates => Set<Cx04AuthorisationState>();
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
    public DbSet<ConnectionOnboardingConfirmation> ConnectionOnboardingConfirmations =>
        Set<ConnectionOnboardingConfirmation>();
    public DbSet<ConnectionThrottleProfileEntity> ConnectionThrottleProfiles => Set<ConnectionThrottleProfileEntity>();
    public DbSet<CoexistenceBaseline> CoexistenceBaselines => Set<CoexistenceBaseline>();
    public DbSet<CoexistenceConflict> CoexistenceConflicts => Set<CoexistenceConflict>();
    public DbSet<CoexistenceScanState> CoexistenceScanStates => Set<CoexistenceScanState>();
    public DbSet<NativeWarrantyPushState> NativeWarrantyPushStates => Set<NativeWarrantyPushState>();
    public DbSet<ParallelRunPlan> ParallelRunPlans => Set<ParallelRunPlan>();
    public DbSet<ParallelRunConfirmation> ParallelRunConfirmations => Set<ParallelRunConfirmation>();
    public DbSet<ParallelRunHandover> ParallelRunHandovers => Set<ParallelRunHandover>();
    public DbSet<GoLiveRecord> GoLiveRecords => Set<GoLiveRecord>();
    public DbSet<ConnectionNotificationSource> ConnectionNotificationSources => Set<ConnectionNotificationSource>();
    public DbSet<ConnectionPollWatermark> ConnectionPollWatermarks => Set<ConnectionPollWatermark>();
    public DbSet<ConnectionNotificationReceipt> ConnectionNotificationReceipts => Set<ConnectionNotificationReceipt>();
    public DbSet<ConnectionNotificationWatchdog> ConnectionNotificationWatchdogs => Set<ConnectionNotificationWatchdog>();
    public DbSet<DeadLetterItem> DeadLetterItems => Set<DeadLetterItem>();
    public DbSet<DeadLetterEvent> DeadLetterEvents => Set<DeadLetterEvent>();
    public DbSet<ManualOutboundTask> ManualOutboundTasks => Set<ManualOutboundTask>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationEntityDayResult> ReconciliationEntityDayResults => Set<ReconciliationEntityDayResult>();
    public DbSet<ReconciliationLocationResult> ReconciliationLocationResults => Set<ReconciliationLocationResult>();
    public DbSet<BackfillCursor> BackfillCursors => Set<BackfillCursor>();
    public DbSet<Cx01SurveyRecord> Cx01SurveyRecords => Set<Cx01SurveyRecord>();
    public DbSet<MigrationSource> MigrationSources => Set<MigrationSource>();
    public DbSet<MigrationBatch> MigrationBatches => Set<MigrationBatch>();
    public DbSet<MigrationBatchTable> MigrationBatchTables => Set<MigrationBatchTable>();
    public DbSet<TemplateMapping> TemplateMappings => Set<TemplateMapping>();
    public DbSet<TemplateMappingBlock> TemplateMappingBlocks => Set<TemplateMappingBlock>();
    public DbSet<MigrationProfileRun> MigrationProfileRuns => Set<MigrationProfileRun>();
    public DbSet<MigrationProfileResult> MigrationProfileResults => Set<MigrationProfileResult>();
    public DbSet<MigrationCanonRun> MigrationCanonRuns => Set<MigrationCanonRun>();
    public DbSet<MigrationUnitCandidate> MigrationUnitCandidates => Set<MigrationUnitCandidate>();
    public DbSet<MigrationUnitCandidateSource> MigrationUnitCandidateSources => Set<MigrationUnitCandidateSource>();
    public DbSet<MigrationEventCandidate> MigrationEventCandidates => Set<MigrationEventCandidate>();
    public DbSet<MigrationEntitlementCandidate> MigrationEntitlementCandidates => Set<MigrationEntitlementCandidate>();
    public DbSet<MigrationReviewItem> MigrationReviewItems => Set<MigrationReviewItem>();
    public DbSet<MigrationReviewDecision> MigrationReviewDecisions => Set<MigrationReviewDecision>();
    public DbSet<MigrationReconstructionRun> MigrationReconstructionRuns => Set<MigrationReconstructionRun>();
    public DbSet<MigrationHistoryEvent> MigrationHistoryEvents => Set<MigrationHistoryEvent>();
    public DbSet<MigrationOwnershipEpisode> MigrationOwnershipEpisodes => Set<MigrationOwnershipEpisode>();
    public DbSet<MigrationReconstructionLog> MigrationReconstructionLogs => Set<MigrationReconstructionLog>();
    public DbSet<MigrationMapRun> MigrationMapRuns => Set<MigrationMapRun>();
    public DbSet<MigrationMapWaitingCandidate> MigrationMapWaitingCandidates => Set<MigrationMapWaitingCandidate>();
    public DbSet<MigrationPlanUnit> MigrationPlanUnits => Set<MigrationPlanUnit>();
    public DbSet<MigrationPlanIdentifier> MigrationPlanIdentifiers => Set<MigrationPlanIdentifier>();
    public DbSet<MigrationPlanEvent> MigrationPlanEvents => Set<MigrationPlanEvent>();
    public DbSet<MigrationPlanOwnership> MigrationPlanOwnership => Set<MigrationPlanOwnership>();
    public DbSet<MigrationPlanReplacementLink> MigrationPlanReplacementLinks => Set<MigrationPlanReplacementLink>();
    public DbSet<LegacyMigration.LinkOverrides.MigrationLinkOverride> MigrationLinkOverrides => Set<LegacyMigration.LinkOverrides.MigrationLinkOverride>();
    public DbSet<MigrationPlanEntitlement> MigrationPlanEntitlements => Set<MigrationPlanEntitlement>();
    public DbSet<MigrationPlanWarrantyEvidence> MigrationPlanWarrantyEvidence => Set<MigrationPlanWarrantyEvidence>();
    public DbSet<MigrationPlanAttachment> MigrationPlanAttachments => Set<MigrationPlanAttachment>();
    public DbSet<MigrationPlanCertificate> MigrationPlanCertificates => Set<MigrationPlanCertificate>();
    public DbSet<MigrationPlanGap> MigrationPlanGaps => Set<MigrationPlanGap>();
    public DbSet<MigrationPlanLog> MigrationPlanLogs => Set<MigrationPlanLog>();
    public DbSet<MigrationValidationRun> MigrationValidationRuns => Set<MigrationValidationRun>();
    public DbSet<MigrationValidationCheck> MigrationValidationChecks => Set<MigrationValidationCheck>();
    public DbSet<MigrationValidationCheckPart> MigrationValidationCheckParts => Set<MigrationValidationCheckPart>();
    public DbSet<MigrationValidationDifference> MigrationValidationDifferences => Set<MigrationValidationDifference>();
    public DbSet<MigrationValidationSample> MigrationValidationSamples => Set<MigrationValidationSample>();
    public DbSet<MigrationValidationSignature> MigrationValidationSignatures => Set<MigrationValidationSignature>();
    public DbSet<MigrationSignoffRecord> MigrationSignoffRecords => Set<MigrationSignoffRecord>();
    public DbSet<MigrationSignoffLine> MigrationSignoffLines => Set<MigrationSignoffLine>();
    public DbSet<ErpChangeRun> ErpChangeRuns => Set<ErpChangeRun>();
    public DbSet<ErpChangeStepCheck> ErpChangeStepChecks => Set<ErpChangeStepCheck>();
    public DbSet<ErpChangeTransition> ErpChangeTransitions => Set<ErpChangeTransition>();
    public DbSet<ErpChangeMasterDataException> ErpChangeMasterDataExceptions => Set<ErpChangeMasterDataException>();
    public DbSet<ErpChangeStockDifference> ErpChangeStockDifferences => Set<ErpChangeStockDifference>();
    public DbSet<ErpChangeStockSignoff> ErpChangeStockSignoffs => Set<ErpChangeStockSignoff>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ErpChangeRun>(b =>
        {
            b.ToTable("erp_change_run", table => table.HasCheckConstraint("ck_erp_change_run_status",
                "status IN ('active','cancelled','completed')"));
            b.HasKey(x => new { x.TenantId, x.ErpChangeRunId });
            b.Property(x => x.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(x => x.StartedBy).HasMaxLength(320);
            b.Property(x => x.ClosedBy).HasMaxLength(320);
            b.Property(x => x.CloseReason).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.Status }).IsUnique()
                .HasFilter("[status] = 'active'");
            b.HasIndex(x => new { x.TenantId, x.StartedAt, x.ErpChangeRunId });
        });
        modelBuilder.Entity<ErpChangeStepCheck>(b =>
        {
            b.ToTable("erp_change_step_check");
            b.HasKey(x => new { x.TenantId, x.ErpChangeStepCheckId });
            b.Property(x => x.CountsJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.Detail).HasMaxLength(2000);
            b.Property(x => x.CheckedBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.ErpChangeRunId, x.Step, x.CheckedAt,
                x.ErpChangeStepCheckId });
            b.HasOne<ErpChangeRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.ErpChangeRunId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ErpChangeTransition>(b =>
        {
            b.ToTable("erp_change_transition");
            b.HasKey(x => new { x.TenantId, x.ErpChangeTransitionId });
            b.Property(x => x.Kind).HasMaxLength(24).IsUnicode(false);
            b.Property(x => x.ActorRef).HasMaxLength(320);
            b.Property(x => x.Reason).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.ErpChangeRunId, x.OccurredAt,
                x.ErpChangeTransitionId });
            b.HasOne<ErpChangeRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.ErpChangeRunId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ErpChangeMasterDataException>(b =>
        {
            b.ToTable("erp_change_master_exception", table =>
                table.HasCheckConstraint("ck_erp_change_master_exception_status",
                    "status IN ('open','mapped','not_needed')"));
            b.HasKey(x => new { x.TenantId, x.ErpChangeMasterDataExceptionId });
            b.Property(x => x.EntityKind).HasMaxLength(20).IsUnicode(false);
            b.Property(x => x.TargetExternalId).HasMaxLength(320);
            b.Property(x => x.DisplayName).HasMaxLength(320);
            b.Property(x => x.TaxId).HasMaxLength(160);
            b.Property(x => x.Country).HasMaxLength(80);
            b.Property(x => x.Problem).HasMaxLength(40).IsUnicode(false);
            b.Property(x => x.CandidateModuleIdsJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.CandidateLabelsJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(x => x.ResolvedBy).HasMaxLength(320);
            b.Property(x => x.ResolutionNote).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.ErpChangeRunId, x.Status, x.LiveUnitsAffected,
                x.ErpChangeMasterDataExceptionId });
            b.HasIndex(x => new { x.TenantId, x.ErpChangeRunId, x.EntityKind,
                x.TargetExternalId }).IsUnique();
            b.HasOne<ErpChangeRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.ErpChangeRunId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ErpChangeStockDifference>(b =>
        {
            b.ToTable("erp_change_stock_difference", table =>
                table.HasCheckConstraint("ck_erp_change_stock_difference_status",
                    "status IN ('open','resolved')"));
            b.HasKey(x => new { x.TenantId, x.ErpChangeStockDifferenceId });
            b.Property(x => x.Identifier).HasMaxLength(320);
            b.Property(x => x.ItemName).HasMaxLength(320);
            b.Property(x => x.ReasonClass).HasMaxLength(48).IsUnicode(false);
            b.Property(x => x.ModuleLocation).HasMaxLength(320);
            b.Property(x => x.SourceLocation).HasMaxLength(320);
            b.Property(x => x.TargetLocation).HasMaxLength(320);
            b.Property(x => x.ModuleStatus).HasMaxLength(80);
            b.Property(x => x.SourceStatus).HasMaxLength(80);
            b.Property(x => x.TargetStatus).HasMaxLength(80);
            b.Property(x => x.SuggestedResolution).HasMaxLength(80).IsUnicode(false);
            b.Property(x => x.Status).HasMaxLength(20).IsUnicode(false);
            b.Property(x => x.ResolutionCommand).HasMaxLength(80);
            b.Property(x => x.ResolvedBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.ErpChangeRunId, x.Status, x.HasLiveEntitlement,
                x.ReasonClass, x.ErpChangeStockDifferenceId });
            b.HasIndex(x => new { x.TenantId, x.ErpChangeRunId, x.UnitId, x.ReasonClass }).IsUnique()
                .HasFilter("[is_current] = 1 AND [unit_id] IS NOT NULL");
            b.HasOne<ErpChangeRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.ErpChangeRunId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ErpChangeStockSignoff>(b =>
        {
            b.ToTable("erp_change_stock_signoff");
            b.HasKey(x => new { x.TenantId, x.ErpChangeStockSignoffId });
            b.Property(x => x.Note).HasMaxLength(2000);
            b.Property(x => x.SignedOffBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.ErpChangeRunId }).IsUnique();
            b.HasOne<ErpChangeRun>().WithMany().HasForeignKey(x => new { x.TenantId, x.ErpChangeRunId })
                .OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<OutboundDocumentReceipt>(b =>
        {
            b.ToTable("outbound_document_receipt");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.ExternalId });
            b.Property(x => x.ExternalId).HasMaxLength(320);
            b.Property(x => x.ChangeMarker).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.OutboundCommandId });
        });
        modelBuilder.Entity<Connection>(b =>
        {
            b.ToTable("connection", table => table.HasCheckConstraint("ck_connection_status", "status IN ('active','paused','registering','archived')"));
            b.HasKey(x => new { x.TenantId, x.ConnectionId }).IsClustered(false);
            b.Property(x => x.ConnectorCode).HasMaxLength(16);
            b.Property(x => x.DisplayLabel).HasMaxLength(160);
            b.Property(x => x.Status).HasMaxLength(24);
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt, x.ConnectionId });
            b.HasIndex(x => new { x.TenantId, x.ConnectorCode, x.ConnectionId });
            b.HasIndex(x => new { x.TenantId, x.RegistrationRequestId }).IsUnique()
                .HasFilter("[registration_request_id] IS NOT NULL");
        });
        modelBuilder.Entity<ConnectionHealth>(b =>
        {
            b.ToTable("connection_health", table => table.HasCheckConstraint("ck_connection_health_status",
                "status IN ('healthy','degraded','down','paused','archived','reconsent_required','disabled')"));
            b.HasKey(x => new { x.TenantId, x.ConnectionId });
            b.Property(x => x.Status).HasMaxLength(24);
            b.Property(x => x.NotificationState).HasMaxLength(40);
            b.Property(x => x.LastDailyCheckResult).HasMaxLength(24);
            b.Property(x => x.LastErrorCode).HasMaxLength(80);
            b.Property(x => x.LastErrorMessage).HasMaxLength(320);
            b.Property(x => x.RecordTypesJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.CompletedInboundDaysJson).HasColumnType("nvarchar(max)");
            b.HasIndex(x => new { x.TenantId, x.Status, x.ConnectionId });
        });
        modelBuilder.Entity<Cx04AuthorisationState>(b =>
        {
            b.ToTable("cx04_authorisation_state", table =>
            {
                table.HasCheckConstraint("ck_cx04_authorisation_status",
                    "status IN ('reconsent_required','consent_pending','active','disabled')");
                table.HasCheckConstraint("ck_cx04_refresh_expiry_mode",
                    "refresh_expiry_mode IN ('absolute','sliding')");
                table.HasCheckConstraint("ck_cx04_refresh_lifetime_days", "refresh_lifetime_days BETWEEN 1 AND 365");
            });
            b.HasKey(x => new { x.TenantId, x.ConnectionId });
            b.Property(x => x.Status).HasMaxLength(32);
            b.Property(x => x.PendingStateHash).HasMaxLength(32);
            b.Property(x => x.RefreshExpiryMode).HasMaxLength(16);
            b.Property(x => x.ReminderDaysSentJson).HasColumnType("nvarchar(max)").HasDefaultValue("[]");
            b.Property(x => x.SignedInBy).HasMaxLength(320);
            b.Property(x => x.DisabledBy).HasMaxLength(320);
            b.Property(x => x.DisabledReason).HasMaxLength(200);
            b.Property(x => x.PauseReason).HasMaxLength(32);
            b.HasIndex(x => new { x.TenantId, x.Status, x.RefreshChainExpiresAt, x.ConnectionId });
        });
        modelBuilder.Entity<Cx01SurveyRecord>(b =>
        {
            b.ToTable("cx01_survey");
            b.HasKey(x => new { x.TenantId, x.ConnectionId });
            b.Property(x => x.ResultJson).HasColumnType("nvarchar(max)");
            b.HasIndex(x => new { x.TenantId, x.ObservedAt, x.ConnectionId });
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
            b.Property(x => x.CaptureMode).HasMaxLength(24).HasDefaultValue("observed");
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.AcceptedAt, x.MessageType });
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
                "mapping_source IN ('auto','migration','outbound','operator','provisional','manual')"));
            b.HasKey(x => new { x.TenantId, x.ExternalIdMapId });
            b.Property(x => x.EntityType).HasMaxLength(40);
            b.Property(x => x.ExternalId).HasMaxLength(320);
            b.Property(x => x.NaturalKeyHash).HasMaxLength(64).IsFixedLength();
            b.Property(x => x.MappingSource).HasMaxLength(32);
            b.Property(x => x.MappedBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.EntityType, x.ExternalId })
                .IsUnique().HasFilter("[valid_to] IS NULL");
            b.HasIndex(x => new { x.TenantId, x.ModuleId, x.EntityType, x.ValidTo });
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
            b.Property(x => x.OnHandStatusMappingJson).HasColumnType("nvarchar(max)").HasDefaultValue("{}");
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
            b.Property(x => x.PendingRemoteKey).HasMaxLength(100);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Status, x.NextAttemptAt, x.CreatedAt, x.OutboundCommandId });
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
        modelBuilder.Entity<ConnectionOnboardingConfirmation>(b =>
        {
            b.ToTable("connection_onboarding_confirmation");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.ItemCode });
            b.Property(x => x.ItemCode).HasMaxLength(160);
            b.Property(x => x.TextHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
            b.Property(x => x.Evidence).HasMaxLength(500);
            b.Property(x => x.ConfirmedBy).HasMaxLength(200);
        });
        modelBuilder.Entity<CoexistenceBaseline>(b =>
        {
            b.ToTable("coexistence_baseline");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.RecordType, x.RecordKey, x.FieldName });
            b.Property(x => x.RecordType).HasMaxLength(80);
            b.Property(x => x.RecordKey).HasMaxLength(200);
            b.Property(x => x.FieldName).HasMaxLength(128);
            b.Property(x => x.Value).HasMaxLength(1000);
            b.Property(x => x.ExplainingHash).HasMaxLength(64).IsFixedLength().IsUnicode(false);
            b.Property(x => x.Source).HasMaxLength(24);
        });
        modelBuilder.Entity<CoexistenceConflict>(b =>
        {
            b.ToTable("coexistence_conflict", table => table.HasCheckConstraint("ck_coexistence_conflict_status",
                "status IN ('open','awaiting_remote_revert','closed')"));
            b.HasKey(x => new { x.TenantId, x.ConflictId });
            b.Property(x => x.RecordType).HasMaxLength(80);
            b.Property(x => x.RecordKey).HasMaxLength(200);
            b.Property(x => x.RecordLabel).HasMaxLength(300);
            b.Property(x => x.Kind).HasMaxLength(64);
            b.Property(x => x.FieldName).HasMaxLength(128);
            b.Property(x => x.BaselineValue).HasMaxLength(1000);
            b.Property(x => x.RemoteValue).HasMaxLength(1000);
            b.Property(x => x.Status).HasMaxLength(32);
            b.Property(x => x.Decision).HasMaxLength(32);
            b.Property(x => x.DecisionNote).HasMaxLength(2000);
            b.Property(x => x.DecidedBy).HasMaxLength(320);
            // One unresolved conflict per remote field: a later scan refreshes it instead of stacking duplicates.
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.RecordType, x.RecordKey, x.FieldName })
                .IsUnique().HasFilter("[status] <> 'closed'");
            b.HasIndex(x => new { x.TenantId, x.Status, x.DetectedAt, x.ConflictId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Status, x.DetectedAt, x.ConflictId });
        });
        modelBuilder.Entity<CoexistenceScanState>(b =>
        {
            b.ToTable("coexistence_scan_state");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.RecordType });
            b.Property(x => x.RecordType).HasMaxLength(80);
            b.Property(x => x.Cursor).HasMaxLength(200);
        });
        modelBuilder.Entity<NativeWarrantyPushState>(b =>
        {
            b.ToTable("native_warranty_push_state");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.UnitId });
            b.Property(x => x.RemoteRecordKey).HasMaxLength(200);
            b.Property(x => x.DesiredModuleStatus).HasMaxLength(40);
            b.Property(x => x.LastPushedRemoteStatus).HasMaxLength(80);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.UpdatedAt, x.UnitId });
            b.HasIndex(x => new { x.TenantId, x.OutboundCommandId });
        });
        modelBuilder.Entity<ParallelRunPlan>(b =>
        {
            b.ToTable("parallel_run_plan", table => table.HasCheckConstraint("ck_parallel_run_plan_days",
                "days IN (30,60,90)"));
            b.HasKey(x => new { x.TenantId, x.ConnectionId });
            b.Property(x => x.StartedBy).HasMaxLength(320);
        });
        modelBuilder.Entity<ParallelRunConfirmation>(b =>
        {
            b.ToTable("parallel_run_confirmation");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.ItemCode });
            b.Property(x => x.ItemCode).HasMaxLength(80);
            b.Property(x => x.Evidence).HasMaxLength(500);
            b.Property(x => x.ConfirmedBy).HasMaxLength(320);
        });
        modelBuilder.Entity<ParallelRunHandover>(b =>
        {
            b.ToTable("parallel_run_handover");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.Kind });
            b.Property(x => x.Kind).HasMaxLength(40);
            b.Property(x => x.Evidence).HasMaxLength(500);
            b.Property(x => x.ConfirmedBy).HasMaxLength(320);
        });
        modelBuilder.Entity<GoLiveRecord>(b =>
        {
            b.ToTable("go_live_record");
            b.HasKey(x => new { x.TenantId, x.RecordId });
            b.Property(x => x.IssuedBy).HasMaxLength(320);
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.IssuedAt });
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
                table.HasCheckConstraint("ck_connection_poll_overlap", "overlap_seconds BETWEEN 0 AND 172800");
                table.HasCheckConstraint("ck_connection_poll_interval", "interval_seconds BETWEEN 1 AND 86400");
                table.HasCheckConstraint("ck_connection_poll_revision", "revision >= 0");
            });
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.EntityType });
            b.Property(x => x.EntityType).HasMaxLength(120);
            b.Property(x => x.CanonicalMessage).HasMaxLength(80);
            b.Property(x => x.Watermark).HasMaxLength(320);
            b.Property(x => x.LastError).HasMaxLength(2000);
            b.HasIndex(x => new { x.TenantId, x.NextPollAt, x.ConnectionId, x.EntityType });
        });
        modelBuilder.Entity<ConnectionNotificationReceipt>(b =>
        {
            b.ToTable("connection_notification_receipt");
            b.HasKey(x => new { x.TenantId, x.ConnectionId, x.EventId });
            b.Property(x => x.EventId).HasMaxLength(200);
            b.HasIndex(x => new { x.TenantId, x.ProcessedAt, x.ConnectionId, x.EventId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.FirstSeenPendingAt, x.EventId });
        });
        modelBuilder.Entity<ConnectionNotificationWatchdog>(b =>
        {
            b.ToTable("connection_notification_watchdog");
            b.HasKey(x => new { x.TenantId, x.ConnectionId });
            b.Property(x => x.StreamState).HasMaxLength(40);
            b.HasIndex(x => new { x.TenantId, x.UpdatedAt, x.ConnectionId });
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
            b.Property(x => x.Desk).HasMaxLength(80);
            b.Property(x => x.Draft).HasColumnType("nvarchar(max)");
            b.Property(x => x.Status).HasMaxLength(24);
            b.Property(x => x.ErpExternalId).HasMaxLength(320);
            b.Property(x => x.ErpChangeMarker).HasMaxLength(320);
            b.Property(x => x.CompletedBy).HasMaxLength(320);
            b.Property(x => x.Note).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.OutboundCommandId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Status, x.CreatedAt, x.ManualOutboundTaskId });
            b.HasIndex(x => new { x.TenantId, x.Status, x.Desk, x.CreatedAt, x.ManualOutboundTaskId });
            b.HasOne<DeadLetterItem>().WithOne().HasForeignKey<ManualOutboundTask>(x =>
                new { x.TenantId, x.DeadLetterItemId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReconciliationRun>(b =>
        {
            b.ToTable("reconciliation_run", table =>
                table.HasCheckConstraint("ck_reconciliation_run_status",
                    "status IN ('running','clean','mismatch','failed')"));
            b.HasKey(x => new { x.TenantId, x.ReconciliationRunId });
            b.Property(x => x.Trigger).HasMaxLength(24);
            b.Property(x => x.Status).HasMaxLength(24);
            b.Property(x => x.Failure).HasMaxLength(4000);
            b.Property(x => x.RequestedBy).HasMaxLength(320);
            b.Property(x => x.ExplainedBy).HasMaxLength(320);
            b.Property(x => x.ExplanationNote).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.StartedAt, x.ReconciliationRunId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.StartedAt, x.ReconciliationRunId });
        });
        modelBuilder.Entity<ReconciliationEntityDayResult>(b =>
        {
            b.ToTable("reconciliation_entity_day", table =>
                table.HasCheckConstraint("ck_reconciliation_entity_day_status",
                    "status IN ('clean','mismatch')"));
            b.HasKey(x => new { x.TenantId, x.ReconciliationEntityDayResultId });
            b.Property(x => x.Entity).HasMaxLength(120);
            b.Property(x => x.SourceQuantity).HasPrecision(28, 8);
            b.Property(x => x.ModuleQuantity).HasPrecision(28, 8);
            b.Property(x => x.SourceKeyFieldHash).HasMaxLength(64);
            b.Property(x => x.ModuleKeyFieldHash).HasMaxLength(64);
            b.Property(x => x.Status).HasMaxLength(24);
            b.HasIndex(x => new { x.TenantId, x.ReconciliationRunId, x.BusinessDate,
                x.Entity, x.ReconciliationEntityDayResultId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Entity, x.BusinessDate });
            b.HasOne<ReconciliationRun>().WithMany().HasForeignKey(x =>
                new { x.TenantId, x.ReconciliationRunId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ReconciliationLocationResult>(b =>
        {
            b.ToTable("reconciliation_location", table =>
                table.HasCheckConstraint("ck_reconciliation_location_status",
                    "status IN ('clean','mismatch')"));
            b.HasKey(x => new { x.TenantId, x.ReconciliationLocationResultId });
            b.Property(x => x.ItemCode).HasMaxLength(160);
            b.Property(x => x.ItemName).HasMaxLength(320);
            b.Property(x => x.LocationCode).HasMaxLength(160);
            b.Property(x => x.LocationName).HasMaxLength(320);
            b.Property(x => x.SourceOnHand).HasPrecision(28, 8);
            b.Property(x => x.Status).HasMaxLength(24);
            b.HasIndex(x => new { x.TenantId, x.ReconciliationRunId, x.ItemId,
                x.LocationId, x.ReconciliationLocationResultId });
            b.HasOne<ReconciliationRun>().WithMany().HasForeignKey(x =>
                new { x.TenantId, x.ReconciliationRunId }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<BackfillCursor>(b =>
        {
            b.ToTable("backfill_cursor", table =>
                table.HasCheckConstraint("ck_backfill_cursor_status",
                    "status IN ('queued','running','paused','awaiting_reconciliation','completed','cancelled')"));
            b.HasKey(x => new { x.TenantId, x.BackfillCursorId });
            b.Property(x => x.Entity).HasMaxLength(120);
            b.Property(x => x.Cursor).HasMaxLength(2000);
            b.Property(x => x.Status).HasMaxLength(32);
            b.Property(x => x.RequestedBy).HasMaxLength(320);
            b.Property(x => x.LastChangedBy).HasMaxLength(320);
            b.Property(x => x.LastFailure).HasMaxLength(4000);
            b.Property(x => x.RowVersion).IsRowVersion();
            b.HasIndex(x => new { x.TenantId, x.CreatedAt, x.BackfillCursorId });
            b.HasIndex(x => new { x.TenantId, x.ConnectionId, x.Entity, x.Status,
                x.CreatedAt, x.BackfillCursorId });
        });
        Yojana.Integration.Relays.RelayModel.Configure(modelBuilder);
        MigrationModel.Configure(modelBuilder);
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<ErpChangeStockDifference>()
            .HasIndex(x => new { x.TenantId, x.ErpChangeRunId, x.Status, x.HasLiveEntitlement,
                x.ReasonClass, x.ErpChangeStockDifferenceId })
            .HasDatabaseName("ix_erp_change_stock_difference_run_queue");
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
