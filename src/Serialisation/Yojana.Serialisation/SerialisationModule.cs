using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Yojana.Platform.Scheduling;
using Yojana.Platform;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Queries;
using Yojana.Serialisation.Search;
using Yojana.Serialisation.TimeZones;
using Yojana.Serialisation.Selection;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Projections;
using Yojana.Serialisation.Workbench;
using Yojana.Serialisation.Campaigns;
using Yojana.Serialisation.Rmas;
using Yojana.Serialisation.Condition;
using Yojana.Serialisation.Refurbishments;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Labels;
using Yojana.Serialisation.Jurisdictions;
using Yojana.Serialisation.Imports;
using Yojana.Serialisation.Reports;

namespace Yojana.Serialisation;

/// <summary>Module registration. Owns the <c>serialisation</c> schema through <see cref="SerialisationDbContext"/>.</summary>
public static class SerialisationModule
{
    public const string Schema = SerialisationDbContext.SchemaName;

    public static IHostApplicationBuilder AddSerialisationModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<SerialisationDbContext>(Schema);
        var interactiveReadTimeout = builder.Configuration.GetValue<int?>("Serialisation:InteractiveReadCommandTimeoutSeconds") ?? 10;
        builder.Services.AddSingleton(new InteractiveReadOptions(interactiveReadTimeout));

        // Ledger writer and inline projector (MOD-04, coupling 2, P1-T04): scoped, sharing the module's DbContext.
        builder.Services.AddScoped<UnitCurrentStateProjector>();
        builder.Services.AddScoped<PublishedEventMapper>();
        builder.Services.AddScoped<LedgerPublication>();
        builder.Services.AddScoped<UnitEventIdempotencyReader>();
        builder.Services.AddScoped<IUnitEventWriter, UnitEventWriter>();
        builder.Services.AddScoped<IUnitEventAppendObserver, CustomerSaleCertificateObserver>();
        builder.Services.AddScoped<IUnitEventAppendObserver, LedgerBaseOrderReferenceObserver>();
        builder.Services.AddScoped<UnitHistoryQuery>();
        builder.Services.AddScoped<BulkIngestion>();
        var baseOrderBackfill = builder.Configuration.GetSection(LedgerBaseOrderBackfillOptions.SectionName)
            .Get<LedgerBaseOrderBackfillOptions>() ?? new LedgerBaseOrderBackfillOptions();
        builder.Services.AddSingleton(baseOrderBackfill);
        builder.Services.AddScoped<LedgerBaseOrderBackfill>();

        // Identity rules and commands (P1-T07, P1-T09). Item and BOM facts come through ports; until a connector or an
        // in-house module registers them, the in-memory ports answer with defaults (development and tests only).
        builder.Services.AddSingleton<IIdentifierPolicySource, IdentifierPolicySet>();
        builder.Services.AddScoped<IdentifierService>();
        builder.Services.AddScoped<IdentifierAssignmentService>();
        builder.Services.AddSingleton<InMemorySerialSchemes>();
        builder.Services.TryAddSingleton<ISerialSchemeSource>(sp => sp.GetRequiredService<InMemorySerialSchemes>());
        builder.Services.AddScoped<SerialAllocationReservationStore>();
        builder.Services.AddScoped<SerialRangeService>();
        builder.Services.AddScoped<AsBuiltCompleteness>();
        builder.Services.AddScoped<EpisodeService>();
        builder.Services.AddScoped<UnitResolver>();
        builder.Services.AddScoped<PostingPeriods>();
        builder.Services.AddScoped<EffectiveItemProfiles>();

        // Selection sets and bulk operations (MOD-11 part built in P1, P1-T18); later phases add their IBulkOperation beside ReleaseUnitsOperation.
        builder.Services.AddScoped<SelectionSetResolver>();
        // MOD-12 import application depends only on the spreadsheet/file/export contracts; hosts may replace storage.
        builder.Services.AddScoped<ImportService>();
        builder.Services.AddScoped<ImportComparisonService>();
        builder.Services.AddScoped<ImportQueries>();
        builder.Services.AddScoped<BackOfficeReportQueries>();
        builder.Services.AddScoped<ImportJobSource>();
        builder.Services.TryAddScoped<IImportJobSource>(sp => sp.GetRequiredService<ImportJobSource>());
        builder.Services.AddScoped<Portal.CustomerListSourceResolver>();
        builder.Services.AddScoped<Portal.CustomerListReconciliationService>();
        builder.Services.TryAddScoped<IImportFileStore, DatabaseImportFileStore>();
        builder.Services.AddScoped<ISpreadsheetReader, OpenXmlSpreadsheetReader>();
        builder.Services.AddScoped<ISpreadsheetReader, LegacyXlsSpreadsheetReader>();
        builder.Services.TryAddSingleton<ITabularExporter, TabularExporter>();
        builder.Services.AddScoped<UnitQueries>();
        builder.Services.AddScoped<SerialRangeQueries>();
        builder.Services.AddScoped<CompletenessQueries>();
        builder.Services.AddScoped<AwaitingVendorSerialQueries>();
        builder.Services.AddScoped<CampaignSelectionFacts>();
        builder.Services.AddScoped<CampaignQueries>();
        builder.Services.AddScoped<ConditionQueries>();
        builder.Services.TryAddScoped<IUnitConditionQueryPort>(sp => sp.GetRequiredService<ConditionQueries>());
        builder.Services.TryAddScoped<ICertificateConditionReadPort, CertificateConditionReadPort>();
        builder.Services.TryAddScoped<IUnitSaleShipmentGuard, ConditionShipmentGuard>();
        builder.Services.TryAddScoped<ILifeEpisodeWriter, LifeEpisodeWriter>();
        // P5 refurbishment block. RefurbishmentPolicySettings is factory-registered so its ITenantSettings-backed
        // PlatformDbContext remains hidden from Wolverine's SerialisationDbContext transaction graph.
        builder.Services.AddScoped(sp => new RefurbishmentPolicySettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped<RefurbishmentService>();
        builder.Services.AddScoped<RefurbishmentQueries>();
        builder.Services.TryAddScoped<IRefurbishmentOpener, DefaultRefurbishmentOpener>();
        builder.Services.TryAddScoped<ICertificateRefurbishmentReadPort, CertificateRefurbishmentReadPort>();
        // End P5 refurbishment block.
        // P6 labels block. The settings factory is coupled to LabelSettings as documented there: it hides the
        // PlatformDbContext behind ITenantSettings from Wolverine's SerialisationDbContext transaction graph.
        builder.Services.AddScoped(sp => new LabelSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped<LabelRenderer>();
        builder.Services.AddScoped<LabelPrintService>();
        builder.Services.AddScoped<LabelQueries>();
        builder.Services.AddSingleton<InMemoryLabelPrinterPort>();
        builder.Services.TryAddSingleton<ILabelPrinterPort>(sp => sp.GetRequiredService<InMemoryLabelPrinterPort>());
        builder.Services.TryAddSingleton<IBarcodeEncoder, ZxingBarcodeEncoder>();
        builder.Services.TryAddSingleton<ILabelLookupUrlBuilder>(new LabelLookupUrlBuilder(
            builder.Configuration["Labels:LookupUrl"] ?? "/api/units/lookup"));
        // End P6 labels block.
        // P6 jurisdiction-rules block. Feature modules see only these read ports; command/query endpoints use the
        // concrete service as the catalogue boundary.
        builder.Services.AddScoped<JurisdictionCatalogueService>();
        builder.Services.AddScoped<JurisdictionPublishedEventMapper>();
        builder.Services.TryAddScoped<IJurisdictionRuleResolver>(sp => sp.GetRequiredService<JurisdictionCatalogueService>());
        builder.Services.TryAddScoped<IJurisdictionRegionPort>(sp => sp.GetRequiredService<JurisdictionCatalogueService>());
        builder.Services.TryAddScoped<IJurisdictionLabelRulesPort>(sp => sp.GetRequiredService<JurisdictionCatalogueService>());
        builder.Services.TryAddScoped<IJurisdictionCertificateTextPort>(sp => sp.GetRequiredService<JurisdictionCatalogueService>());
        // End P6 jurisdiction-rules block.
        builder.Services.AddScoped<BulkExecutor>();
        builder.Services.AddScoped<BulkApprovalRequestQueries>();
        builder.Services.AddScoped<CorrectionLedger>();
        builder.Services.AddScoped<CorrectionKpiQueries>();
        // Coupled to WorkbenchChoiceSettings for the same two-DbContext Wolverine constraint as correction approvals:
        // the factory hides ITenantSettings' PlatformDbContext from workbench command dependency graphs.
        builder.Services.AddScoped(sp => new WorkbenchChoiceSettings(
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>(),
            sp.GetRequiredService<CorrectionApprovalSettings>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Time.IClock>()));
        builder.Services.AddScoped<IWorkbenchChoiceCatalog>(sp => sp.GetRequiredService<WorkbenchChoiceSettings>());
        builder.Services.AddScoped<WorkbenchEntryResolver>();
        builder.Services.AddScoped<WorkbenchLineageQueries>();
        builder.Services.AddScoped<WorkbenchPickerService>();
        builder.Services.AddScoped<WorkbenchCommandDispatcher>();
        builder.Services.AddScoped<WorkbenchRequestService>();
        builder.Services.AddScoped<WorkbenchRequestQueries>();
        builder.Services.AddScoped<CorrectAsBuiltPlanner>();
        builder.Services.AddScoped<AssignIdentifiersService>();
        builder.Services.AddScoped<LateSerializationPlanner>();
        builder.Services.AddScoped<LateSerializationService>();
        builder.Services.AddScoped<Manufacturing.ManufacturingContractService>();
        // Coupled to CorrectionApprovalSettings for the same Wolverine constraint documented on that type: replacing
        // this factory with constructor registration exposes PlatformDbContext beside SerialisationDbContext and stops host startup.
        builder.Services.AddScoped(sp => new CorrectionApprovalSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        // Coupled to SerialReuseSettings for the same reason as correction approvals: keep PlatformDbContext hidden from Wolverine.
        builder.Services.AddScoped(sp => new SerialReuseSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        // Coupled to CorrectionKpiTargetSettings for the same Wolverine constraint as correction approvals: keep the
        // PlatformDbContext behind ITenantSettings out of generated handler dependency graphs.
        builder.Services.AddScoped(sp => new CorrectionKpiTargetSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));

        // Global search (MOD-13, P1-T23): the index table and its maintenance handlers live here; other modules index through ISearchIndex.
        // Warranty policies (MOD-06, P2-T03).
        builder.Services.AddScoped<Entitlements.PolicyStore>();
        builder.Services.AddScoped<Entitlements.PolicyQueries>();
        builder.Services.AddScoped<Entitlements.PolicyResolver>();
        // A factory on purpose, coupled to AmendEntitlementHandler: Wolverine does not look inside a factory, so the
        // PlatformDbContext behind ITenantSettings stays out of the handler's generated graph and the handler keeps the
        // default transaction on SerialisationDbContext, opened after the tenant middleware. A plain registration makes
        // the host refuse to start ("multiple DbContext types detected").
        builder.Services.AddScoped(sp => new Entitlements.AmendmentApprovalSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>(), sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantCurrency>()));
        // Coupled to CreateEntitlementHandler and BackfillEntitlementsHandler for the same reason as the factory above:
        // ITenantSettings brings PlatformDbContext; exposing it to Wolverine would break their SerialisationDbContext transaction graph.
        builder.Services.AddScoped(sp => new Entitlements.RefurbishedGoodsSettings(
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>(),
            sp.GetRequiredService<IJurisdictionRegionPort>()));
        // Coupled to WarrantyReferenceSettings: as with amendment settings, this factory keeps PlatformDbContext out of
        // Wolverine's handler graph; replacing it with a plain registration makes transaction discovery ambiguous.
        builder.Services.AddScoped(sp => new Entitlements.WarrantyReferenceSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped<Entitlements.EntitlementLedger>();
        builder.Services.AddScoped<Entitlements.EntitlementQueries>();
        builder.Services.TryAddScoped<ICertificateEntitlementReadPort, Entitlements.CertificateEntitlementReadPort>();
        builder.Services.AddScoped<Entitlements.EntitlementRequirementWriter>();
        builder.Services.AddScoped<Entitlements.EntitlementRequirementQueries>();
        builder.Services.AddScoped<Entitlements.EntitlementRequirementSweep>();
        builder.Services.AddScoped<Entitlements.EntitlementGapQueries>();
        builder.Services.AddScoped<Entitlements.WarrantyLineExceptionQueries>();
        builder.Services.AddScoped<Entitlements.WarrantyLineValidationService>();
        builder.Services.AddScoped<Entitlements.DocumentCorrectionEntitlements>();
        builder.Services.AddScoped<Entitlements.ReplacementLinkCorrectionEntitlements>();
        builder.Services.AddScoped<ClaimLedger>();
        builder.Services.AddScoped<DocumentNumbers>();
        builder.Services.AddScoped<NumberingSettings>();
        builder.Services.AddScoped<ClaimLineDecider>();
        builder.Services.AddScoped<ClaimAdjudicationPreviewQuery>();
        builder.Services.AddScoped<ClaimUnitFacts>();
        builder.Services.AddScoped<ClaimQueries>();
        builder.Services.AddScoped<Campaigns.CampaignCoverQueries>();
        builder.Services.AddScoped<ICampaignCoverPort>(sp => sp.GetRequiredService<Campaigns.CampaignCoverQueries>());
        builder.Services.AddScoped<RecoveryQueries>();
        builder.Services.AddScoped<Replacements.WarrantyReplacementQueries>();
        builder.Services.AddScoped<Replacements.ReplacementLinkQueries>();
        builder.Services.TryAddScoped<ICertificateReplacementReadPort, Replacements.CertificateReplacementReadPort>();
        builder.Services.AddScoped<LegacyUnknownClaimUnitCreator>();
        builder.Services.AddScoped<RmaSaleFactsResolver>();
        builder.Services.AddScoped<RmaQueries>();
        builder.Services.AddScoped<RmaStageCounterWriter>();
        builder.Services.TryAddScoped<IRmaFollowUpQueryPort, RmaFollowUpQueryPort>();
        // P5 coverage block. ServicePartEntitlementSettings is factory-registered to hide ITenantSettings'
        // PlatformDbContext from ServicePartInstalledHandler; a direct registration makes Wolverine see two contexts.
        builder.Services.AddScoped(sp => new ServicePartEntitlementSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped<CoverageDecisionQueries>();
        builder.Services.TryAddScoped<IServiceOrderQueryPort, DefaultServiceOrderQueryPort>();
        // End P5 coverage block.
        // Coupled to ClaimReviewSettings and RecoverySettings for the same Wolverine constraint as entitlement approval settings: the
        // factory hides ITenantSettings' PlatformDbContext from AdjudicateClaimHandler's transactional graph.
        builder.Services.AddScoped(sp => new ClaimReviewSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped(sp => new RecoverySettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.TryAddScoped<IWarrantyClaimsPort, WarrantyClaimsPort>();
        builder.Services.TryAddScoped<ISettlementFormulaPort, FullSettlementFormula>();
        builder.Services.TryAddScoped<IEntitlementRecoveryQuery, Entitlements.RecoveryEntitlementQueries>();
        // Coupled to SourceDocumentCorrectionEntitlements for the same Wolverine constraint as the P2 settings-backed
        // handlers: this factory hides ITenantSettings' PlatformDbContext, leaving SerialisationDbContext transactional.
        builder.Services.AddScoped(sp => new Entitlements.SourceDocumentCorrectionEntitlements(
            sp.GetRequiredService<SerialisationDbContext>(),
            sp.GetRequiredService<Entitlements.PolicyStore>(),
            sp.GetRequiredService<ITimeZonePort>(),
            sp.GetRequiredService<IJurisdictionRegionPort>(),
            sp.GetRequiredService<IJurisdictionRuleResolver>(),
            sp.GetRequiredService<IItemQueryPort>(),
            sp.GetRequiredService<Entitlements.WarrantyReferenceSettings>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Time.IClock>()));

        builder.Services.AddScoped<SearchIndexer>();
        builder.Services.AddScoped<SearchBackfill>();
        builder.Services.AddScoped<SearchIndexHealer>();
        builder.Services.AddScoped<ISearchIndex>(sp => sp.GetRequiredService<SearchIndexer>());
        builder.Services.AddSingleton<SearchEngineCapabilities>();
        builder.Services.AddScoped<SearchMetadataProfiles>();
        builder.Services.AddScoped<SearchQueries>();
        builder.Services.AddScoped<IBulkOperation, ReleaseUnitsOperation>();
        builder.Services.AddScoped<IBulkOperation, Entitlements.BulkCreateEntitlementOperation>();
        builder.Services.AddScoped<IBulkOperation, Entitlements.BulkAmendEntitlementOperation>();
        builder.Services.AddScoped<IBulkOperation, Entitlements.BulkAttachExtendedOperation>();
        builder.Services.AddScoped<IBulkOperation, Entitlements.BulkSupersedeEntitlementOperation>();
        builder.Services.AddScoped<IBulkOperation, Entitlements.BulkVoidEntitlementOperation>();
        builder.Services.AddScoped<IBulkOperation, Entitlements.BulkCloseEntitlementOperation>();
        builder.Services.AddScoped<IBulkOperation, Entitlements.BulkBackfillOperation>();
        var search = builder.Configuration.GetSection(SearchIndexOptions.SectionName).Get<SearchIndexOptions>() ?? new SearchIndexOptions();
        if (search.MaximumParallelReindexMessages < 1) throw new InvalidOperationException($"{SearchIndexOptions.SectionName}:MaximumParallelReindexMessages must be at least 1.");
        builder.Services.AddSingleton(search);
        builder.Services.ConfigureWolverine(options =>
        {
            // TIGHT COUPLING with PublishedEventMapper and SearchProjectionRequestMapper: ledger publication places
            // SearchProjectionRequested in the same outbox, and this route keeps the actual writes on the bounded
            // durable endpoint. Bypassing it restores cross-index deadlocks and public-event queue stalls.
            options.PublishMessage<SearchProjectionRequested>().ToLocalQueue(SearchIndexOptions.ReindexQueueName);
            options.PublishMessage<SearchReindexRequested>().ToLocalQueue(SearchIndexOptions.ReindexQueueName);
            options.LocalQueue(SearchIndexOptions.ReindexQueueName).UseDurableInbox().MaximumParallelMessages(search.MaximumParallelReindexMessages);

            // Triggering published facts use a service-name-independent durable endpoint so a short-lived producer and
            // the web host share pending work. Their subscribers only enqueue narrow CertificateTriggerWork messages;
            // certificate persistence then runs alone on its own endpoint and DbContext unit of work.
            options.PublishMessage<UnitReleased>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<UnitAsBuiltCorrected>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<UnitIdentifierCorrected>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<UnitIdentifiersSwapped>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<UnitItemCorrected>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<UnitGraded>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<RefurbishmentReleased>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<UnitMerged>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<UnitIdentifiersAssigned>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<ReplacementShipped>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<ReplacementLinkCorrected>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<CertificateIssueRequested>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<EntitlementCoverageChanged>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<DocumentSerialsCorrected>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            options.PublishMessage<OwnershipSourceDocumentCorrected>().ToLocalQueue(CertificatePublishedEventQueue.Name);
            // TIGHT COUPLING: EntitlementCoverageChangedHandler re-adjudicates the same open claim/RMA rows that a
            // neighbouring coverage fact may also target. SQL rowversion is the correctness guard, but parallel
            // delivery would turn an ordinary ordered entitlement stream into noisy retries. Keep this source queue
            // serial; certificate rendering fans back out on its separate per-unit locked queue below.
            options.LocalQueue(CertificatePublishedEventQueue.Name).UseDurableInbox().MaximumParallelMessages(1);
            options.PublishMessage<CertificateTriggerWork>().ToLocalQueue(CertificateTriggerQueue.Name);
            options.PublishMessage<CertificateSaleReversalWork>().ToLocalQueue(CertificateTriggerQueue.Name);
            options.LocalQueue(CertificateTriggerQueue.Name).UseDurableInbox().MaximumParallelMessages(8);
            options.PublishMessage<CustomerSaleCertificateWork>().ToLocalQueue(CustomerSaleCertificateQueue.Name);
            // One envelope represents one ledger append. The handler fans independent unit chains out with the same
            // bounded parallelism as the ordinary trigger queue, so no ordering exists between unrelated units.
            options.LocalQueue(CustomerSaleCertificateQueue.Name).UseDurableInbox().MaximumParallelMessages(1);
        });

        // Document handlers (P1-T15): thin consumed events, documents through the publisher's query ports; in-memory
        // ports stand in until a connector (P8) or an in-house module (P12) registers the real ones.
        var documents = builder.Configuration.GetSection(DocumentOptions.SectionName).Get<DocumentOptions>() ?? new DocumentOptions();
        builder.Services.AddSingleton(documents);
        // Coupled to SerialConfirmationSettings for the same Wolverine constraint documented on that type: the
        // factory hides PlatformDbContext from document handlers whose transaction belongs to SerialisationDbContext.
        builder.Services.AddScoped(sp => new SerialConfirmationSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped<DocumentContext>();
        builder.Services.AddSingleton<InMemoryDocumentPorts>();
        builder.Services.TryAddSingleton<IPurchasingDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<IManufacturingDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<IProductionOrderAllocationPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<IInventoryDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<ISalesDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<IOwnershipSourceDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<IServiceDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<IInternalSaleDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.AddSingleton<InMemoryStockUnitEventPort>();
        builder.Services.TryAddSingleton<IStockUnitEventPort>(sp => sp.GetRequiredService<InMemoryStockUnitEventPort>());
        builder.Services.TryAddScoped<IReplacementSourceQueryPort, DefaultReplacementSourceQueryPort>();
        builder.Services.TryAddScoped<IReturnReasonQueryPort, DefaultReturnReasonQueryPort>();
        builder.Services.AddScoped<IReleaseGuard, ReleaseCompletenessGuard>();
        builder.Services.AddSingleton<InMemoryItemPorts>();
        builder.Services.TryAddSingleton<IItemQueryPort>(sp => sp.GetRequiredService<InMemoryItemPorts>());
        builder.Services.TryAddSingleton<IBomQueryPort>(sp => sp.GetRequiredService<InMemoryItemPorts>());
        builder.Services.AddSingleton<InMemoryItemNamePort>();
        builder.Services.TryAddSingleton<IItemNamePort>(sp => sp.GetRequiredService<InMemoryItemNamePort>());
        builder.Services.AddSingleton<InMemoryItemFamilyNamePort>();
        builder.Services.TryAddSingleton<IItemFamilyNamePort>(sp => sp.GetRequiredService<InMemoryItemFamilyNamePort>());
        builder.Services.AddSingleton<InMemoryPartyNamePort>();
        builder.Services.TryAddSingleton<IPartyNamePort>(sp => sp.GetRequiredService<InMemoryPartyNamePort>());
        builder.Services.AddSingleton<InMemoryLocationNameRegistry>();
        builder.Services.AddScoped<IRecordedLocationReferenceQuery, RecordedLocationReferenceQuery>();
        // Inventory owns the location master later; this default decorates its development catalogue with only the
        // tenant-filtered custody-history fallback exposed by the narrow location-name port.
        builder.Services.TryAddScoped<ILocationNamePort, DefaultLocationNamePort>();
        builder.Services.AddSingleton<InMemoryDocumentNumberRegistry>();
        builder.Services.AddScoped<IRecordedDocumentReferenceQuery, RecordedDocumentReferenceQuery>();
        // Unlike the in-memory registry, the fallback resolver is scoped because it reads this request's tenant-filtered
        // DbContext. A real document module may replace the narrow port registration before this module is added.
        builder.Services.TryAddScoped<IDocumentNumberPort, DefaultDocumentNumberPort>();
        builder.Services.AddSingleton<InMemoryStockReclassificationPort>();
        builder.Services.TryAddSingleton<IStockReclassificationPort>(sp => sp.GetRequiredService<InMemoryStockReclassificationPort>());
        builder.Services.AddSingleton<InMemoryStockQuantityPort>();
        builder.Services.TryAddSingleton<IStockQuantityPort>(sp => sp.GetRequiredService<InMemoryStockQuantityPort>());
        builder.Services.AddSingleton<InMemoryWarrantyPorts>();
        builder.Services.TryAddSingleton<IItemPolicyPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.TryAddSingleton<ICustomerContractPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.TryAddSingleton<IVendorWarrantyTermsPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.TryAddSingleton<ITimeZoneSourcePort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        // Coupled to TenantTimeZoneSettings for the same two-DbContext Wolverine constraint as the certificate settings:
        // the factories hide PlatformDbContext from entitlement handlers. Registering either implementation by type
        // makes Wolverine discover both database contexts and the transactional handler graph no longer starts.
        builder.Services.AddScoped(sp => new TenantTimeZoneSettings(
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>(),
            sp.GetRequiredService<IPartyNamePort>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Time.IClock>()));
        builder.Services.TryAddScoped<ITimeZonePort>(sp => new TenantTimeZonePort(
            sp.GetRequiredService<TenantTimeZoneSettings>(), sp.GetRequiredService<ITimeZoneSourcePort>()));
        builder.Services.TryAddSingleton<IWarrantyClaimsPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.TryAddSingleton<IWarrantyCertificatePort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());

        // P6 certificate block. Owner modules expose snapshots through the narrow ports above; the engine owns only
        // immutable certificate rows and state-neutral certificate ledger events.
        builder.Services.TryAddScoped<ICertificateUnitReadPort, CertificateUnitReadPort>();
        builder.Services.TryAddScoped<ICertificateConditionReadPort, CertificateConditionReadPort>();
        builder.Services.TryAddScoped<ICertificateRefurbishmentReadPort, CertificateRefurbishmentReadPort>();
        builder.Services.TryAddScoped<ICertificateReplacementReadPort, Replacements.CertificateReplacementReadPort>();
        builder.Services.TryAddScoped<ICertificateEntitlementReadPort, Entitlements.CertificateEntitlementReadPort>();
        builder.Services.TryAddScoped<ICertificatePresentationPort, TenantAssetCertificatePresentationPort>();
        builder.Services.TryAddSingleton<ICertificateTemplateLanguageCatalogue, BuiltInCertificateTemplateLanguageCatalogue>();
        builder.Services.AddSingleton<CertificateTemplateEngine>();
        builder.Services.AddSingleton<CertificateTemplateValidator>();
        var certificateRuns = builder.Configuration.GetSection(CertificateRunOptions.SectionName).Get<CertificateRunOptions>()
            ?? new CertificateRunOptions();
        if (certificateRuns.MaximumSynchronousUnits < 1)
            throw new InvalidOperationException($"{CertificateRunOptions.SectionName}:MaximumSynchronousUnits must be at least 1.");
        if (certificateRuns.PdfRequestTimeoutSeconds < 1)
            throw new InvalidOperationException($"{CertificateRunOptions.SectionName}:PdfRequestTimeoutSeconds must be at least 1.");
        builder.Services.AddScoped<CertificateHtmlComposer>();
        builder.Services.AddScoped<PlainHtmlCertificateRenderer>();
        if (certificateRuns.Renderer.Equals("pdf", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(certificateRuns.PdfWorkerBaseUrl, UriKind.Absolute, out var workerUri)
                || !workerUri.IsLoopback || workerUri.Scheme != Uri.UriSchemeHttp)
                throw new InvalidOperationException($"{CertificateRunOptions.SectionName}:PdfWorkerBaseUrl must be an absolute loopback HTTP URL when Renderer is pdf.");
            builder.Services.AddHttpClient<HttpPdfRenderer>(client =>
            {
                client.BaseAddress = workerUri;
                client.Timeout = TimeSpan.FromSeconds(certificateRuns.PdfRequestTimeoutSeconds);
            });
            builder.Services.TryAddScoped<IPdfRenderer>(services => services.GetRequiredService<HttpPdfRenderer>());
            builder.Services.AddScoped<PdfCertificateRenderer>();
            builder.Services.TryAddScoped<ICertificateRenderer>(services => services.GetRequiredService<PdfCertificateRenderer>());
            builder.Services.TryAddScoped<ICertificateBundleRenderer>(services => services.GetRequiredService<PdfCertificateRenderer>());
        }
        else if (certificateRuns.Renderer.Equals("html", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.TryAddScoped<ICertificateRenderer>(services => services.GetRequiredService<PlainHtmlCertificateRenderer>());
            builder.Services.TryAddScoped<ICertificateBundleRenderer>(services => services.GetRequiredService<PlainHtmlCertificateRenderer>());
        }
        else
        {
            throw new InvalidOperationException($"{CertificateRunOptions.SectionName}:Renderer must be html or pdf.");
        }
        builder.Services.AddSingleton(certificateRuns);
        builder.Services.TryAddSingleton<ICertificateRunChunkObserver, NullCertificateRunChunkObserver>();
        builder.Services.AddScoped<CertificateSelectionService>();
        builder.Services.AddScoped<ReleaseRecordPayloadBuilder>();
        builder.Services.AddScoped<WarrantyCertificatePayloadBuilder>();
        builder.Services.AddScoped<CertificatePayloadBuilder>();
        builder.Services.AddScoped<CertificateDatabaseLock>();
        // Coupled to ReleaseRecordIssuePointSettings for the same two-DbContext Wolverine constraint documented on
        // that type: the factory keeps PlatformDbContext outside certificate trigger handler discovery.
        builder.Services.AddScoped(sp => new ReleaseRecordIssuePointSettings(
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped<CertificateTemplateService>();
        builder.Services.TryAddScoped<ICertificateLayoutLanguageSource, CertificateLayoutLanguageSource>();
        // Coupled to CertificateDateFormatSettings for the same two-DbContext Wolverine constraint documented on
        // that type: the factory keeps PlatformDbContext outside certificate trigger handler discovery.
        builder.Services.AddScoped(sp => new CertificateDateFormatSettings(
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>(),
            sp.GetRequiredService<ICertificateLayoutLanguageSource>()));
        builder.Services.AddScoped<ReleaseRecordIssuePolicy>();
        builder.Services.AddScoped<CertificateVersioningEngine>();
        builder.Services.AddScoped<CertificateQueries>();
        builder.Services.AddScoped<TenantAssetService>();
        // End P6 certificate block.

        // P7 customer portal. All authenticated reads pass through PortalOwnershipScope; the public resolver has a
        // separate closed response contract and can only suppress fields from its fixed safe catalogue.
        builder.Services.AddScoped<Portal.PortalOwnershipScope>();
        builder.Services.AddScoped<Portal.PortalQueries>();
        builder.Services.AddScoped<Portal.DisclosurePolicyService>();
        builder.Services.AddScoped<Portal.PublicPortalResolver>();
        // End P7 customer portal.

        // Replay-and-diff healer (P1-T05): per tenant, nightly, through the platform's recurring tenant jobs.
        builder.Services.AddScoped<UnitCurrentHealer>();
        builder.Services.AddSingleton(RecurringTenantJob.For<UnitCurrentHealerJob>(UnitCurrentHealerJob.JobName, UnitCurrentHealerJob.Cron));
        builder.Services.AddSingleton(RecurringTenantJob.For<SearchIndexHealerJob>(SearchIndexHealerJob.JobName, SearchIndexHealerJob.Cron));
        builder.Services.AddSingleton(RecurringTenantJob.For<LedgerBaseOrderBackfillJob>(LedgerBaseOrderBackfillJob.JobName, baseOrderBackfill.Cron));
        builder.Services.AddSingleton(RecurringTenantJob.For<Entitlements.EntitlementRequirementJob>(Entitlements.EntitlementRequirementJob.JobName, Entitlements.EntitlementRequirementJob.Cron));
        // P5 advance-replacement block: one tenant-local nightly sweep, with publication through Wolverine's outbox.
        builder.Services.AddSingleton(RecurringTenantJob.For<Replacements.ReplacementOverdueJob>(Replacements.ReplacementOverdueJob.JobName, Replacements.ReplacementOverdueJob.Cron));

        // Ledger partition lifecycle (MOD-04, P1-T03): options from configuration, the service per scope, one
        // database-wide Quartz job scheduled after the platform has migrated and started its scheduler.
        var lifecycle = builder.Configuration.GetSection(PartitionLifecycleOptions.SectionName).Get<PartitionLifecycleOptions>() ?? new PartitionLifecycleOptions();
        builder.Services.AddSingleton(lifecycle);
        builder.Services.AddScoped<PartitionLifecycleService>();
        builder.Services.AddHostedService<PartitionLifecycleRegistrar>();
        return builder;
    }
}
