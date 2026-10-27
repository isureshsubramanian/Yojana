using Yojana.Integration.Coexistence;
using Yojana.Integration.Relays;
using Yojana.Integration.Connections;
using Azure.Identity;
using Yojana.Integration.Transport;
using Azure.Storage.Blobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Yojana.Integration.Inbox;
using Yojana.Integration.Gateway;
using Yojana.Serialisation.Contracts;
using Yojana.Integration.Ordering;
using Yojana.Integration.Notifications;
using Yojana.Integration.Outbox;
using Yojana.Integration.DeadLetters;
using Yojana.Platform.Scheduling;
using Yojana.Integration.Data;
using Yojana.Platform;
using Yojana.Platform.Contracts.Privacy;
using Yojana.Integration.Privacy;
using Yojana.Platform.Contracts.Security;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Credentials;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Persistence;
using Yojana.Integration.Security.Webhooks;
using Yojana.Integration.Security.Workers;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Connectors.CX04;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Backfills;
using Yojana.Integration.Connectors;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Integration.Contracts;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Integration.Ports;
using Yojana.Integration.ErpChange;
using Yojana.Integration.Verification;
using Yojana.Platform.Contracts.Records;

namespace Yojana.Integration;

/// <summary>Module registration. Owns the <c>integration</c> schema through <see cref="IntegrationDbContext"/>.</summary>
public static class IntegrationModule
{
    public const string Schema = IntegrationDbContext.SchemaName;

    public static IHostApplicationBuilder AddIntegrationModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<IntegrationDbContext>(Schema);
        builder.Services.AddScoped<ExternalIdErasureProtector>();
        builder.Services.AddScoped<IntegrationPersonalDataHolder>();
        builder.Services.AddScoped<IPersonalDataHolder>(services =>
            services.GetRequiredService<IntegrationPersonalDataHolder>());
        builder.Services.AddKeyedScoped<IRecordReferenceResolver, IntegrationMessageRecordReferenceResolver>(RecordKind.IntegrationMessage);
        builder.Services.AddScoped<IConnectionBusyObserver, ConnectionBusyAuditObserver>();
        builder.Services.AddScoped<ConnectionCommands>();
        builder.Services.AddScoped<ConnectionQueries>();
        builder.Services.AddScoped<ConnectionOnboarding>();
        builder.Services.AddScoped<ConnectionCertificatePins>();
        builder.Services.AddScoped<ConnectionHealthSettings>();
        builder.Services.AddScoped<ConnectionHealthProjection>();
        builder.Services.AddScoped<ExtensionStatusService>();
        builder.Services.AddSingleton(RecurringTenantJob.For<ConnectionHealthJob>(ConnectionHealthJob.JobName, ConnectionHealthJob.Cron));
        builder.Services.AddKeyedScoped<IConnectionLifecycleAdapter, Cx01ConnectionLifecycleAdapter>(Cx01Capabilities.ConnectorCode);
        builder.Services.AddKeyedScoped<IConnectionLifecycleAdapter, Cx02ConnectionLifecycleAdapter>(Cx02Capabilities.ConnectorCode);
        builder.Services.AddKeyedScoped<IConnectionExtensionAdapter, Cx02ExtensionStatusAdapter>(Cx02Capabilities.ConnectorCode);
        builder.Services.AddScoped<WarrantyContractMaintenanceSettings>();
        // Factory keeps the Serialisation implementation behind its Contracts port out of Wolverine's handler graph;
        // the two module DbContexts never participate in one inferred transaction.
        builder.Services.AddScoped(sp => new NativeWarrantyPushService(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Time.IClock>(),
            sp.GetRequiredService<Yojana.Serialisation.Contracts.INativeWarrantyPushSourcePort>(),
            sp.GetRequiredService<WarrantyContractMaintenanceSettings>()));
        builder.Services.AddScoped<INativeWarrantyBackfillProcessor>(sp =>
            sp.GetRequiredService<NativeWarrantyPushService>());
        builder.Services.AddScoped<CoexistenceSettings>();
        builder.Services.AddScoped<CoexistenceDriftService>();
        builder.Services.AddScoped<CoexistenceConflicts>();
        builder.Services.AddScoped<SyncControl>();
        builder.Services.AddScoped<SyncNotificationControl>();
        builder.Services.AddSingleton(RecurringTenantJob.For<SyncNotificationControlJob>(
            SyncNotificationControlJob.JobName, SyncNotificationControlJob.Cron));
        builder.Services.AddScoped<ParallelRun>();
        builder.Services.AddScoped<ParallelRunDashboardService>();
        builder.Services.AddScoped<IMigrationGoLiveFacts, Yojana.Integration.LegacyMigration.Apply.MigrationGoLiveFactsReader>();
        builder.Services.AddSingleton(RecurringTenantJob.For<CoexistenceDriftJob>(CoexistenceDriftJob.JobName, CoexistenceDriftJob.Cron));
        builder.Services.AddKeyedScoped<ICoexistenceDriftReader, Cx01CoexistenceDriftReader>(Cx01Capabilities.ConnectorCode);
        builder.Services.AddKeyedScoped<ICoexistenceDriftReader, Cx02CoexistenceDriftReader>(Cx02Capabilities.ConnectorCode);
        builder.Services.AddScoped<IdempotencyIndex>();
        builder.Services.AddScoped<InboundMessageStore>();
        builder.Services.AddSingleton<IChangeMarkerComparer, ChangeMarkerComparer>();
        builder.Services.AddScoped<GatewayReferenceStore>();
        builder.Services.AddScoped<ConnectionCapabilityReader>();
        builder.Services.AddScoped<ConnectorCatalogue>();
        builder.Services.AddScoped<ConnectorOnboardingChecklistGenerator>();
        builder.Services.AddScoped<ConnectorOnboardingConfirmations>();
        builder.Services.AddSingleton<IConnectorOnboardingProfiles, ConnectorOnboardingProfiles>();
        builder.Services.AddKeyedSingleton<IConnectorOnboardingProfile, Cx01OnboardingProfile>(
            Cx01Capabilities.ConnectorCode);
        builder.Services.AddKeyedSingleton<IConnectorOnboardingProfile, Cx02OnboardingProfile>(
            Cx02Capabilities.ConnectorCode);
        builder.Services.AddScoped<InboundGateway>();
        builder.Services.AddSingleton(InboundTranslationRecoveryOptions.Default);
        builder.Services.AddScoped<InboundTranslationRecovery>();
        builder.Services.AddSingleton(RecurringTenantJob.For<InboundTranslationRecoveryJob>(
            InboundTranslationRecoveryJob.JobName, InboundTranslationRecoveryJob.Cron));
        builder.Services.AddScoped<ConnectionPollingService>();
        builder.Services.AddSingleton(RecurringTenantJob.For<ConnectionPollingJob>(
            ConnectionPollingJob.JobName, ConnectionPollingJob.Cron));
        builder.Services.AddSingleton<Cx01SessionRegistry>();
        builder.Services.AddScoped<Cx01Settings>();
        builder.Services.AddScoped<Cx01BomSyncSchedule>();
        builder.Services.AddScoped<Cx01WarrantyStatusHealth>();
        builder.Services.AddKeyedScoped<IConnectionHealthNoteSource, Cx01WarrantyStatusHealth>(
            Cx01Capabilities.ConnectorCode);
        builder.Services.AddScoped<Cx01Transport>();
        builder.Services.AddScoped<ICx01Transport, RelayCx01Transport>();
        builder.Services.AddKeyedScoped<IRelayConnectorProfile, Cx01RelayProfile>(Cx01Capabilities.ConnectorCode);
        builder.Services.AddScoped<RelayRegistry>();
        builder.Services.AddScoped<RelayAuthentication>();
        builder.Services.AddScoped<RelaySigningKeyProtection>();
        builder.Services.AddRelayRateLimiting();
        builder.Services.AddScoped<RelayRequestQueue>();
        builder.Services.AddScoped<RelayPayloadProtection>();
        builder.Services.AddScoped<RelayMaintenance>();
        builder.Services.AddSingleton(RecurringTenantJob.For<RelayProbeJob>(RelayProbeJob.JobName, RelayProbeJob.Cron));
        builder.Services.AddSingleton(RecurringTenantJob.For<RelayPurgeJob>(RelayPurgeJob.JobName, RelayPurgeJob.Cron));
        builder.Services.AddScoped<Cx01Client>();
        builder.Services.AddScoped<Cx01SessionMaintenance>();
        builder.Services.AddSingleton(RecurringTenantJob.For<Cx01SessionKeepAliveJob>(
            Cx01SessionKeepAliveJob.JobName, Cx01SessionKeepAliveJob.Cron));
        builder.Services.AddScoped<ICx01SerialHistory, EfCx01SerialHistory>();
        builder.Services.AddScoped<ICx01BomSyncStateReader, EfCx01BomSyncStateReader>();
        builder.Services.AddScoped<ICx01DocumentCaptureStore, EfCx01DocumentCaptureStore>();
        builder.Services.AddScoped<ICx01AttributionTaskSink, EfCx01AttributionTaskSink>();
        builder.Services.AddScoped<ICx01OwnWriteStore, EfCx01OwnWriteStore>();
        builder.Services.AddScoped<ICx01InboundRejectionSink, EfCx01InboundRejectionSink>();
        builder.Services.AddScoped<Cx01Adapter>();
        builder.Services.AddScoped<IInboundRetryPreparer>(services => services.GetRequiredService<Cx01Adapter>());
        builder.Services.AddScoped<Cx01CurrencyMapService>();
        builder.Services.AddKeyedScoped<IConnectorAdapter>(Cx01Capabilities.ConnectorCode, (services, _) =>
            services.GetRequiredService<Cx01Adapter>());
        builder.Services.AddKeyedScoped<IConnectorWatermarkSeed>(Cx01Capabilities.ConnectorCode, (services, _) =>
            services.GetRequiredService<Cx01Adapter>());
        builder.Services.AddKeyedScoped<IOutboundConnector>(Cx01Capabilities.ConnectorCode, (services, _) =>
            services.GetRequiredService<Cx01Adapter>());
        builder.Services.AddScoped<Cx01Onboarding>();
        builder.Services.AddScoped<ICx01SurveyRemotePort, Cx01SurveyRemotePort>();
        builder.Services.AddScoped<Cx01SurveyProbe>();
        builder.Services.AddScoped<Cx01SurveyService>();
        builder.Services.AddScoped<Cx01FieldMapService>();
        builder.Services.AddScoped<Cx01ApprovalService>();
        builder.Services.AddScoped<Cx01DocumentPoster>();
        builder.Services.AddScoped<Cx01DocumentsService>();
        builder.Services.AddScoped<Cx01OutboundDocuments>();
        builder.Services.AddScoped<Cx01NativeWarrantyWriter>();
        builder.Services.AddScoped<IOutboundProgressStore, OutboundProgressStore>();
        builder.Services.AddScoped<ICx01OutboundFacts, Cx01OutboundFacts>();
        builder.Services.AddScoped<ICx01AttachmentContent, Cx01LocalAttachmentContent>();
        builder.Services.AddKeyedScoped<IOutboundReviewTracker, Cx01ReviewTracker>(Cx01Capabilities.ConnectorCode);
        builder.Services.AddScoped<ICx01TrackingPermissionSink>(services => services.GetRequiredService<Cx01SurveyService>());
        builder.Services.AddScoped<ICx01SurveyReader>(services =>
            services.GetRequiredService<Cx01SurveyService>());
        builder.Services.AddScoped<Cx01WebhookMaintenance>();
        builder.Services.AddScoped<Cx01WebhookEventTracker>();
        builder.Services.AddSingleton(RecurringTenantJob.For<Cx01WebhookWatchdogJob>(
            Cx01WebhookWatchdogJob.JobName, Cx01WebhookWatchdogJob.Cron));
        builder.Services.AddSingleton<Cx02TokenRegistry>();
        builder.Services.AddScoped<Cx02Settings>();
        builder.Services.AddScoped<Cx02Transport>();
        builder.Services.AddScoped<ICx02Transport>(services => services.GetRequiredService<Cx02Transport>());
        builder.Services.AddScoped<Cx02Client>();
        builder.Services.AddScoped<OutboundAttachmentContentStore>();
        builder.Services.AddScoped<ICx02AttachmentContent, Cx02AttachmentContent>();
        builder.Services.AddScoped<ICx02OutboundFacts, Cx02OutboundFacts>();
        builder.Services.AddScoped<Cx02OutboundDocuments>();
        builder.Services.AddScoped<Cx02Adapter>();
        builder.Services.AddKeyedScoped<IConnectorAdapter>(Cx02Capabilities.ConnectorCode, (services, _) =>
            services.GetRequiredService<Cx02Adapter>());
        builder.Services.AddKeyedScoped<IConnectorWatermarkSeed>(Cx02Capabilities.ConnectorCode, (services, _) =>
            services.GetRequiredService<Cx02Adapter>());
        builder.Services.AddKeyedScoped<IOutboundConnector>(Cx02Capabilities.ConnectorCode, (services, _) =>
            services.GetRequiredService<Cx02Adapter>());
        builder.Services.AddKeyedScoped<IOutboundCompletenessCheck, Cx02OutboundCompleteness>(
            Cx02Capabilities.ConnectorCode);
        builder.Services.AddScoped<Cx02NotificationTracker>();
        builder.Services.AddScoped<Cx02WebhookMaintenance>();
        builder.Services.AddSingleton(RecurringTenantJob.For<Cx02WebhookMaintenanceJob>(
            Cx02WebhookMaintenanceJob.JobName, Cx02WebhookMaintenanceJob.Cron));
        // CX04 registrations are deliberately contiguous so the connector remains one replaceable adapter block.
        builder.Services.AddKeyedScoped<IConnectionLifecycleAdapter, Cx04ConnectionLifecycleAdapter>(
            Cx04Capabilities.ConnectorCode);
        builder.Services.AddKeyedScoped<ICoexistenceDriftReader, Cx04CoexistenceDriftReader>(
            Cx04Capabilities.ConnectorCode);
        builder.Services.AddKeyedSingleton<IConnectorOnboardingProfile, Cx04OnboardingProfile>(
            Cx04Capabilities.ConnectorCode);
        builder.Services.AddSingleton<Cx04TokenRegistry>();
        builder.Services.AddScoped<Cx04Settings>();
        builder.Services.AddScoped<Cx04Transport>();
        builder.Services.AddScoped<ICx04Transport>(services => services.GetRequiredService<Cx04Transport>());
        builder.Services.AddScoped<Cx04Client>();
        builder.Services.AddScoped<Cx04Authorisation>();
        builder.Services.AddScoped<Cx04ReminderSettings>();
        builder.Services.AddScoped<Cx04SignInNotifications>();
        builder.Services.AddScoped<Cx04ConsentExpiryReminders>();
        builder.Services.AddSingleton(RecurringTenantJob.For<Cx04ConsentExpiryJob>(
            Cx04ConsentExpiryJob.JobName, Cx04ConsentExpiryJob.Cron));
        builder.Services.AddScoped<Cx04Adapter>();
        builder.Services.AddKeyedScoped<IConnectorAdapter>(Cx04Capabilities.ConnectorCode, (services, _) =>
            services.GetRequiredService<Cx04Adapter>());
        builder.Services.AddKeyedScoped<IConnectorWatermarkSeed>(Cx04Capabilities.ConnectorCode, (services, _) =>
            services.GetRequiredService<Cx04Adapter>());
        builder.Services.AddScoped<ICx04OutboundFacts, Cx04OutboundFacts>();
        builder.Services.AddScoped<Cx04OutboundDocuments>();
        builder.Services.AddKeyedScoped<IOutboundConnector>(Cx04Capabilities.ConnectorCode, (services, _) =>
            services.GetRequiredService<Cx04OutboundDocuments>());
        // The published-event chains already contain Serialisation handlers with their own EF transaction. Resolve the
        // staging service through a factory so Wolverine does not infer IntegrationDbContext as a second transaction
        // owner for that same public message. The stager owns and saves its narrow staging unit of work explicitly.
        builder.Services.AddScoped(sp => new OutboundCommandStager(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Time.IClock>(),
            sp.GetRequiredService<ConnectionCapabilityReader>(),
            sp.GetRequiredService<IOutboundCommandParkingSink>(),
            sp.GetRequiredService<Yojana.Serialisation.Contracts.IOutboundUnitSourcePort>(),
            sp.GetRequiredService<Yojana.Serialisation.Contracts.IRenderedOutboundDocumentPort>(),
            sp.GetServices<IOutboundSourcePolicy>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboundCommandStager>>()));
        builder.Services.AddScoped<BoundedWaitResequencer>();
        builder.Services.AddSingleton(RecurringTenantJob.For<ResequencerJob>(ResequencerJob.JobName, ResequencerJob.Cron));
        builder.Services.AddScoped<OutboxRelaySettings>();
        builder.Services.AddScoped<IOutboundConnectorResolver, OutboundConnectorResolver>();
        builder.Services.AddScoped<DeadLetterParkingSink>();
        builder.Services.AddScoped<IOutboundCommandParkingSink>(services =>
            services.GetRequiredService<DeadLetterParkingSink>());
        builder.Services.AddScoped<IInboundMessageParkingSink>(services =>
            services.GetRequiredService<DeadLetterParkingSink>());
        builder.Services.AddScoped<IOutboundReviewTaskSink>(services =>
            services.GetRequiredService<DeadLetterParkingSink>());
        builder.Services.AddScoped<OutboundRelay>();
        builder.Services.AddScoped<OutboundCompleteness>();
        builder.Services.AddScoped<IOutboundSourcePolicy, Cx01OutboundSourcePolicy>();
        builder.Services.AddKeyedScoped<IOutboundCompletenessCheck, Cx01OutboundCompleteness>(Cx01Capabilities.ConnectorCode);
        builder.Services.AddScoped<DeadLetterService>();
        builder.Services.AddScoped<IOutboundRetryWindow>(services => services.GetRequiredService<DeadLetterService>());
        builder.Services.AddScoped<ManualTaskDeskSettings>();
        builder.Services.TryAddScoped<IManualTaskReferenceVerifier, UnavailableManualTaskReferenceVerifier>();
        builder.Services.AddSingleton(RecurringTenantJob.For<OutboxRelayJob>(OutboxRelayJob.JobName, OutboxRelayJob.Cron));
        // Factories deliberately hide IntegrationDbContext from Wolverine's SerialisationDbContext handler graphs.
        // The document and item ports are module contracts, but exposing both contexts as constructor-discoverable
        // dependencies makes Wolverine reject the otherwise loosely coupled handler at startup.
        builder.Services.AddScoped(sp => new IntegrationDocumentQueryPort(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>()));
        builder.Services.AddScoped<IPurchasingDocumentQueryPort>(sp => sp.GetRequiredService<IntegrationDocumentQueryPort>());
        builder.Services.AddScoped<IManufacturingDocumentQueryPort>(sp => sp.GetRequiredService<IntegrationDocumentQueryPort>());
        builder.Services.AddScoped<IInventoryDocumentQueryPort>(sp => sp.GetRequiredService<IntegrationDocumentQueryPort>());
        builder.Services.AddScoped<ISalesDocumentQueryPort>(sp => sp.GetRequiredService<IntegrationDocumentQueryPort>());
        builder.Services.AddScoped(sp => new IntegrationItemQueryPort(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>()));
        builder.Services.AddScoped<IItemQueryPort>(sp => sp.GetRequiredService<IntegrationItemQueryPort>());
        // HIDDEN COUPLING (registration order): this must follow AddSerialisationModule so it replaces the catalogue
        // name port; the catalogue stays reachable only through its published key (see SerialisationModule).
        builder.Services.AddScoped<IItemNamePort>(sp => new IntegrationItemNamePort(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>(),
            sp.GetRequiredKeyedService<IItemNamePort>(SerialisationPortKeys.ItemNameCatalogue)));
        builder.Services.AddScoped(sp => new IntegrationBomQueryPort(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>()));
        builder.Services.AddScoped<IBomQueryPort>(sp => sp.GetRequiredService<IntegrationBomQueryPort>());
        builder.Services.AddScoped<IErpBomCapabilityPort, ErpBomCapabilityPort>();
        builder.Services.TryAddScoped<IConnectorAdapterResolver, ConnectorAdapterResolver>();
        builder.Services.TryAddScoped<IReconciliationBackfillScheduler, NullReconciliationBackfillScheduler>();
        builder.Services.AddScoped(sp => new ReconciliationSettings(
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped<ReconciliationBusinessDay>();
        builder.Services.AddScoped<ReconciliationService>();
        builder.Services.AddSingleton(RecurringTenantJob.For<ReconciliationJob>(
            ReconciliationJob.JobName, ReconciliationJob.Cron));
        builder.Services.AddScoped<BackfillService>();
        builder.Services.AddSingleton<ConnectorVerificationManifestStore>();
        builder.Services.AddScoped<ConnectorAcceptanceService>();
        builder.Services.AddScoped<AcceptanceGateSettings>();
        builder.Services.AddScoped<ErpChangeRunService>();
        builder.Services.AddScoped<ErpChangeMasterDataService>();
        builder.Services.AddScoped<ErpChangeStockService>();
        builder.Services.AddScoped<ErpChangeCleanPeriodSettings>();
        builder.Services.AddScoped<ErpChangeNotificationSettings>();
        builder.Services.AddScoped<ErpChangeWorkService>();
        builder.Services.AddScoped<ErpChangeNotifications>();
        builder.Services.AddSingleton(RecurringTenantJob.For<ErpChangeNotificationJob>(
            ErpChangeNotificationJob.JobName, ErpChangeNotificationJob.Cron));
        builder.Services.AddScoped<ErpChangeDualRunService>();
        builder.Services.AddScoped<IErpChangeDivergenceSink>(services =>
            services.GetRequiredService<ErpChangeDualRunService>());
        builder.Services.AddScoped<ErpChangeCutoverService>();
        builder.Services.AddScoped<IReconciliationBackfillScheduler>(sp =>
            sp.GetRequiredService<BackfillService>());
        builder.Services.AddSingleton(RecurringTenantJob.For<BackfillJob>(
            BackfillJob.JobName, BackfillJob.Cron));
        builder.Services.AddScoped<MigrationLicenceSettings>();
        builder.Services.AddScoped<MigrationBulkWriter>();
        builder.Services.AddScoped<MigrationSourceSecret>();
        builder.Services.AddScoped<SourceExtractor>();
        builder.Services.AddScoped<MigrationSourceService>();
        builder.Services.AddScoped<SourceHistoryReader>();
        builder.Services.AddScoped<ValidationSourceReader>();
        builder.Services.AddScoped<StagingMismatchProfileReader>();
        builder.Services.AddScoped<MigrationBatchService>();
        builder.Services.AddScoped<TemplateKeywordRules>();
        builder.Services.AddScoped<TemplateMappingService>();
        builder.Services.AddScoped<IApprovedTemplateDecompositionQuery, ApprovedTemplateDecompositionQuery>();
        builder.Services.AddScoped<MigrationProfileOutcomeSettings>();
        builder.Services.AddScoped<IMigrationProfileQuery, SerialInMultipleWarehousesProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, NegativeQuantityProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, DuplicateSerialSameItemProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, DuplicateSerialDifferentItemsProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, SerialWhitespaceOrCaseProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, SerialFormatVariantsProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, PlaceholderSerialProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, EquipmentCardWithoutSerialProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, ShippedSerialWithoutCardProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, CardCustomerDeliveryMismatchProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, ActiveContractPastEndProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, AmbiguousDatesProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, SerialsInUserFieldsProfileQuery>();
        builder.Services.AddScoped<IMigrationProfileQuery, EmptyStatusProfileQuery>();
        builder.Services.AddScoped<MigrationProfileService>();
        builder.Services.AddScoped<IMigrationProfileQueue>(services =>
            services.GetRequiredService<MigrationProfileService>());
        builder.Services.AddScoped<IMigrationProfileStatus>(services =>
            services.GetRequiredService<MigrationProfileService>());
        builder.Services.AddScoped<CanonicalFamilySettings>();
        builder.Services.AddScoped<CanonicalisationWriter>();
        builder.Services.AddScoped<CanonicalisationService>();
        builder.Services.AddScoped<IMigrationReviewCarryOver>(services =>
            services.GetRequiredService<CanonicalisationService>());
        builder.Services.AddScoped<ReconstructionService>();
        builder.Services.AddScoped<MappingSettings>();
        builder.Services.AddScoped<ReplacementLinkValueValidator>();
        builder.Services.AddScoped<LegacyMigration.LinkOverrides.LinkOverrideService>();
        builder.Services.AddScoped<MappingService>();
        builder.Services.AddScoped<ValidationSettings>();
        builder.Services.AddScoped<PlanMigrationValidationTarget>();
        builder.Services.AddScoped<IMigrationValidationTarget>(services =>
            services.GetRequiredService<PlanMigrationValidationTarget>());
        builder.Services.AddScoped<ValidationService>();
        builder.Services.AddScoped<IMigrationSignoffStatus, MigrationSignoffStatus>();
        builder.Services.AddScoped<IMigrationValidationQueue>(services =>
            services.GetRequiredService<ValidationService>());
        builder.Services.AddScoped<Yojana.Integration.LegacyMigration.Apply.ApplyService>();
        builder.Services.AddScoped<Connections.IConnectionHandOverNotes, Yojana.Integration.LegacyMigration.Apply.MigrationHandOverNotes>();
        builder.Services.AddSingleton(RecurringTenantJob.For<MigrationExtractionJob>(
            MigrationExtractionJob.JobName, MigrationExtractionJob.Cron));

        builder.AddIntegrationTransport();

        builder.AddIntegrationSecurity();
        builder.Services.AddScoped<EfTenantDataKeyStore>();
        builder.Services.Replace(ServiceDescriptor.Scoped<ITenantDataKeyStore>(services =>
            services.GetRequiredService<EfTenantDataKeyStore>()));
        builder.Services.Replace(ServiceDescriptor.Scoped<ITenantDataKeyRewrapStore>(services =>
            services.GetRequiredService<EfTenantDataKeyStore>()));
        builder.Services.Replace(ServiceDescriptor.Scoped<ITenantDataKeyRewrapCheckpointStore,
            EfTenantDataKeyRewrapCheckpointStore>());
        builder.Services.Replace(ServiceDescriptor.Scoped<IConnectionCredentialStore,
            EfConnectionCredentialStore>());
        builder.Services.AddScoped<EfConnectionThrottleStore>();
        builder.Services.Replace(ServiceDescriptor.Scoped<IConnectionThrottleProfileStore>(services =>
            services.GetRequiredService<EfConnectionThrottleStore>()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IConnectionDailyBudgetStore,
            ScopedEfConnectionDailyBudgetStore>());
        builder.Services.AddScoped<EfConnectionAuditSink>();
        if (string.IsNullOrWhiteSpace(builder.Configuration["Integration:Security:Audit:Provider"]))
            builder.Services.Replace(ServiceDescriptor.Scoped<IConnectionAuditSink>(services =>
                services.GetRequiredService<EfConnectionAuditSink>()));
        else
        {
            var configured = builder.Services.Last(descriptor => descriptor.ServiceType == typeof(IConnectionAuditSink));
            builder.Services.Remove(configured);
            builder.Services.Add(ServiceDescriptor.Describe(typeof(ConfiguredConnectionAuditSink), services =>
                new ConfiguredConnectionAuditSink((IConnectionAuditSink)(configured.ImplementationInstance
                    ?? configured.ImplementationFactory?.Invoke(services)
                    ?? ActivatorUtilities.CreateInstance(services, configured.ImplementationType!))), configured.Lifetime));
            // TIGHT COUPLING (audit journal composition): JournalledConnectionAuditSink must pair the configured
            // immutable sink with EfConnectionAuditSink. Storage APIs offer no shared transaction; removing either
            // side loses the immutable record or the paged connection_event feed. The sink documents write ordering.
            builder.Services.AddScoped<IConnectionAuditSink>(services => new JournalledConnectionAuditSink(
                services.GetRequiredService<EfConnectionAuditSink>(),
                services.GetRequiredService<ConfiguredConnectionAuditSink>().Sink));
        }
        return builder;
    }

    /// <summary>Security services (P8-T14): key encryption, credential protection, audit trail, webhook verification and
    /// per-connection worker lanes. Separate from the database registration so it can be composed and tested alone.</summary>
    public static IHostApplicationBuilder AddIntegrationSecurity(this IHostApplicationBuilder builder)
    {
        var keyProvider = builder.Configuration["Integration:Security:KeyEncryption:Provider"];
        if (StringComparer.OrdinalIgnoreCase.Equals(keyProvider, "Local"))
        {
            var options = LocalKeyEncryptionOptions.From(builder.Configuration, builder.Environment);
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton<IKeyEncryptionKeyProvider, LocalKeyEncryptionKeyProvider>();
            builder.Services.TryAddSingleton<IUserSecretProtector, UserSecretProtector>();
        }
        else if (StringComparer.OrdinalIgnoreCase.Equals(keyProvider, "AzureKeyVault"))
        {
            var options = AzureKeyVaultKeyEncryptionOptions.From(builder.Configuration);
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton(AzureCredentialFactory.Create(options));
            builder.Services.TryAddSingleton<IAzureKeyVaultCryptography>(services =>
                new AzureSdkKeyVaultCryptography(options,
                    services.GetRequiredService<Azure.Core.TokenCredential>()));
            builder.Services.TryAddSingleton<IKeyEncryptionKeyProvider>(services =>
                new AzureKeyVaultKeyEncryptionKeyProvider(options,
                    services.GetRequiredService<IAzureKeyVaultCryptography>()));
            builder.Services.TryAddSingleton<IUserSecretProtector, UserSecretProtector>();
        }
        else if (!string.IsNullOrWhiteSpace(keyProvider))
            throw new InvalidOperationException(
                "Integration:Security:KeyEncryption:Provider must be Local or AzureKeyVault.");

        builder.Services.TryAddSingleton<InMemoryTenantDataKeyStore>();
        builder.Services.TryAddSingleton<ITenantDataKeyStore>(services =>
            services.GetRequiredService<InMemoryTenantDataKeyStore>());
        builder.Services.TryAddSingleton<ITenantDataKeyRewrapStore>(services =>
            services.GetRequiredService<InMemoryTenantDataKeyStore>());
        builder.Services.TryAddSingleton<ITenantDataKeyRewrapCheckpointStore,
            InMemoryTenantDataKeyRewrapCheckpointStore>();
        builder.Services.TryAddSingleton<IConnectionCredentialStore, InMemoryConnectionCredentialStore>();
        builder.Services.TryAddSingleton<IKeyEncryptionKeyProvider, MissingKeyEncryptionKeyProvider>();

        var auditProvider = builder.Configuration["Integration:Security:Audit:Provider"];
        if (StringComparer.OrdinalIgnoreCase.Equals(auditProvider, "LocalFile"))
        {
            var root = builder.Configuration["Integration:Security:Audit:LocalRoot"];
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException(
                    "Integration:Security:Audit:LocalRoot is required when the LocalFile audit provider is selected.");
            builder.Services.AddSingleton(new LocalConnectionAuditOptions(root));
            builder.Services.TryAddSingleton<IConnectionAuditSink, LocalAppendOnlyConnectionAuditSink>();
        }
        else if (StringComparer.OrdinalIgnoreCase.Equals(auditProvider, "AzureBlob"))
        {
            var options = AzureBlobConnectionAuditOptions.From(builder.Configuration);
            builder.Services.AddSingleton(options);
            builder.Services.TryAddSingleton(services =>
            {
                if (!string.IsNullOrWhiteSpace(options.ConnectionString))
                    return new BlobServiceClient(options.ConnectionString);
                var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = options.ManagedIdentityClientId,
                    ExcludeInteractiveBrowserCredential = true,
                    ExcludeVisualStudioCredential = true,
                    ExcludeVisualStudioCodeCredential = true,
                    ExcludeAzureCliCredential = true,
                    ExcludeAzurePowerShellCredential = true,
                    ExcludeAzureDeveloperCliCredential = true,
                    ExcludeBrokerCredential = true,
                });
                return new BlobServiceClient(options.ServiceUri!, credential);
            });
            builder.Services.TryAddSingleton(services =>
                services.GetRequiredService<BlobServiceClient>().GetBlobContainerClient(options.ContainerName));
            builder.Services.TryAddSingleton<IAppendBlobStore, AzureAppendBlobStore>();
            builder.Services.TryAddSingleton<IConnectionAuditSink>(services =>
                new AzureBlobConnectionAuditSink(services.GetRequiredService<IAppendBlobStore>()));
        }
        else if (!string.IsNullOrWhiteSpace(auditProvider))
            throw new InvalidOperationException(
                "Integration:Security:Audit:Provider must be LocalFile or AzureBlob.");

        builder.Services.TryAddScoped<TenantDataKeyService>();
        builder.Services.TryAddScoped<TenantDataKeyRewrapService>();
        builder.Services.TryAddScoped<ConnectionCredentialProtector>();
        builder.Services.TryAddScoped<CredentialExpiryAlertEvaluator>();
        builder.Services.TryAddSingleton<HmacSha256WebhookSignatureVerifier>();
        builder.Services.TryAddSingleton<IConnectionWorkerIsolation, ConnectionWorkerIsolation>();
        builder.Services.TryAddSingleton<InMemoryConnectionThrottleProfileStore>();
        builder.Services.TryAddSingleton<IConnectionThrottleProfileStore>(services =>
            services.GetRequiredService<InMemoryConnectionThrottleProfileStore>());
        builder.Services.TryAddSingleton<InMemoryConnectionDailyBudgetStore>();
        builder.Services.TryAddSingleton<IConnectionDailyBudgetStore>(services =>
            services.GetRequiredService<InMemoryConnectionDailyBudgetStore>());
        builder.Services.TryAddSingleton<IConnectionCallBudgetAlertPublisher,
            NullConnectionCallBudgetAlertPublisher>();
        builder.Services.TryAddSingleton<IConnectionThrottleDelay, SystemConnectionThrottleDelay>();
        builder.Services.TryAddSingleton<IConnectionRetryJitter, RandomConnectionRetryJitter>();
        builder.Services.TryAddSingleton<IConnectionThrottleWorkerRegistry,
            ConnectionThrottleWorkerRegistry>();
        builder.Services.TryAddScoped<ConnectionThrottleProfiles>();
        return builder;
    }
}
