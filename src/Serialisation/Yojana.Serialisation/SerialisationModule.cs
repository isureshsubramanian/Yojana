using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Yojana.Platform.Scheduling;
using Yojana.Platform;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Documents;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Queries;
using Yojana.Serialisation.Search;
using Yojana.Serialisation.Selection;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Projections;

namespace Yojana.Serialisation;

/// <summary>Module registration. Owns the <c>serialisation</c> schema through <see cref="SerialisationDbContext"/>.</summary>
public static class SerialisationModule
{
    public const string Schema = SerialisationDbContext.SchemaName;

    public static IHostApplicationBuilder AddSerialisationModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<SerialisationDbContext>(Schema);

        // Ledger writer and inline projector (MOD-04, coupling 2, P1-T04): scoped, sharing the module's DbContext.
        builder.Services.AddScoped<UnitCurrentStateProjector>();
        builder.Services.AddScoped<PublishedEventMapper>();
        builder.Services.AddScoped<LedgerPublication>();
        builder.Services.AddScoped<IUnitEventWriter, UnitEventWriter>();
        builder.Services.AddScoped<UnitHistoryQuery>();
        builder.Services.AddScoped<BulkIngestion>();

        // Identity rules and commands (P1-T07, P1-T09). Item and BOM facts come through ports; until a connector or an
        // in-house module registers them, the in-memory ports answer with defaults (development and tests only).
        builder.Services.AddSingleton<IIdentifierPolicySource, IdentifierPolicySet>();
        builder.Services.AddScoped<IdentifierService>();
        builder.Services.AddSingleton<InMemorySerialSchemes>();
        builder.Services.TryAddSingleton<ISerialSchemeSource>(sp => sp.GetRequiredService<InMemorySerialSchemes>());
        builder.Services.AddScoped<SerialRangeService>();
        builder.Services.AddScoped<AsBuiltCompleteness>();
        builder.Services.AddScoped<EpisodeService>();
        builder.Services.AddScoped<UnitResolver>();
        builder.Services.AddScoped<PostingPeriods>();

        // Selection sets and bulk operations (MOD-11 part built in P1, P1-T18); later phases add their IBulkOperation beside ReleaseUnitsOperation.
        builder.Services.AddScoped<SelectionSetResolver>();
        builder.Services.AddScoped<UnitQueries>();
        builder.Services.AddScoped<SerialRangeQueries>();
        builder.Services.AddScoped<CompletenessQueries>();
        builder.Services.AddScoped<BulkExecutor>();

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
        builder.Services.AddScoped(sp => new Entitlements.RefurbishedGoodsSettings(sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantSettings>()));
        builder.Services.AddScoped<Entitlements.EntitlementLedger>();
        builder.Services.AddScoped<Entitlements.EntitlementQueries>();
        builder.Services.AddScoped<Entitlements.EntitlementRequirementWriter>();
        builder.Services.AddScoped<Entitlements.EntitlementRequirementQueries>();
        builder.Services.AddScoped<Entitlements.EntitlementRequirementSweep>();
        builder.Services.AddScoped<Entitlements.EntitlementGapQueries>();

        builder.Services.AddScoped<SearchIndexer>();
        builder.Services.AddScoped<SearchBackfill>();
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
            options.PublishMessage<SearchReindexRequested>().ToLocalQueue(SearchIndexOptions.ReindexQueueName);
            options.LocalQueue(SearchIndexOptions.ReindexQueueName).UseDurableInbox().MaximumParallelMessages(search.MaximumParallelReindexMessages);
        });

        // Document handlers (P1-T15): thin consumed events, documents through the publisher's query ports; in-memory
        // ports stand in until a connector (P8) or an in-house module (P12) registers the real ones.
        var documents = builder.Configuration.GetSection(DocumentOptions.SectionName).Get<DocumentOptions>() ?? new DocumentOptions();
        builder.Services.AddSingleton(documents);
        builder.Services.AddScoped<DocumentContext>();
        builder.Services.AddSingleton<InMemoryDocumentPorts>();
        builder.Services.TryAddSingleton<IPurchasingDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<IManufacturingDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<IInventoryDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<ISalesDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.TryAddSingleton<IServiceDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.AddScoped<IReleaseGuard, ReleaseCompletenessGuard>();
        builder.Services.AddSingleton<InMemoryItemPorts>();
        builder.Services.TryAddSingleton<IItemQueryPort>(sp => sp.GetRequiredService<InMemoryItemPorts>());
        builder.Services.TryAddSingleton<IBomQueryPort>(sp => sp.GetRequiredService<InMemoryItemPorts>());
        builder.Services.AddSingleton<InMemoryWarrantyPorts>();
        builder.Services.TryAddSingleton<IItemPolicyPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.TryAddSingleton<ICustomerContractPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.TryAddSingleton<IVendorWarrantyTermsPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.TryAddSingleton<ITimeZonePort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.TryAddSingleton<IWarrantyClaimsPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.TryAddSingleton<IWarrantyCertificatePort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());

        // Replay-and-diff healer (P1-T05): per tenant, nightly, through the platform's recurring tenant jobs.
        builder.Services.AddScoped<UnitCurrentHealer>();
        builder.Services.AddSingleton(RecurringTenantJob.For<UnitCurrentHealerJob>(UnitCurrentHealerJob.JobName, UnitCurrentHealerJob.Cron));
        builder.Services.AddSingleton(RecurringTenantJob.For<Entitlements.EntitlementRequirementJob>(Entitlements.EntitlementRequirementJob.JobName, Entitlements.EntitlementRequirementJob.Cron));

        // Ledger partition lifecycle (MOD-04, P1-T03): options from configuration, the service per scope, one
        // database-wide Quartz job scheduled after the platform has migrated and started its scheduler.
        var lifecycle = builder.Configuration.GetSection(PartitionLifecycleOptions.SectionName).Get<PartitionLifecycleOptions>() ?? new PartitionLifecycleOptions();
        builder.Services.AddSingleton(lifecycle);
        builder.Services.AddScoped<PartitionLifecycleService>();
        builder.Services.AddHostedService<PartitionLifecycleRegistrar>();
        return builder;
    }
}
