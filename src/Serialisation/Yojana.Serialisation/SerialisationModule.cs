using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        builder.Services.AddScoped<SearchIndexer>();
        builder.Services.AddScoped<ISearchIndex>(sp => sp.GetRequiredService<SearchIndexer>());
        builder.Services.AddSingleton<SearchEngineCapabilities>();
        builder.Services.AddScoped<SearchMetadataProfiles>();
        builder.Services.AddScoped<SearchQueries>();
        builder.Services.AddScoped<IBulkOperation, ReleaseUnitsOperation>();

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

        // Replay-and-diff healer (P1-T05): per tenant, nightly, through the platform's recurring tenant jobs.
        builder.Services.AddScoped<UnitCurrentHealer>();
        builder.Services.AddSingleton(RecurringTenantJob.For<UnitCurrentHealerJob>(UnitCurrentHealerJob.JobName, UnitCurrentHealerJob.Cron));

        // Ledger partition lifecycle (MOD-04, P1-T03): options from configuration, the service per scope, one
        // database-wide Quartz job scheduled after the platform has migrated and started its scheduler.
        var lifecycle = builder.Configuration.GetSection(PartitionLifecycleOptions.SectionName).Get<PartitionLifecycleOptions>() ?? new PartitionLifecycleOptions();
        builder.Services.AddSingleton(lifecycle);
        builder.Services.AddScoped<PartitionLifecycleService>();
        builder.Services.AddHostedService<PartitionLifecycleRegistrar>();
        return builder;
    }
}
