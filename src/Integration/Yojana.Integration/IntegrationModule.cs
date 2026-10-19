using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Inbox;
using Yojana.Integration.Gateway;
using Yojana.Serialisation.Contracts;
using Yojana.Integration.Ordering;
using Yojana.Platform.Scheduling;
using Yojana.Integration.Data;
using Yojana.Platform;

namespace Yojana.Integration;

/// <summary>Module registration. Owns the <c>integration</c> schema through <see cref="IntegrationDbContext"/>.</summary>
public static class IntegrationModule
{
    public const string Schema = IntegrationDbContext.SchemaName;

    public static IHostApplicationBuilder AddIntegrationModule(this IHostApplicationBuilder builder)
    {
        builder.AddModuleDbContext<IntegrationDbContext>(Schema);
        builder.Services.AddScoped<IdempotencyIndex>();
        builder.Services.AddScoped<InboundMessageStore>();
        builder.Services.AddSingleton<IChangeMarkerComparer, ChangeMarkerComparer>();
        builder.Services.AddScoped<GatewayReferenceStore>();
        builder.Services.AddScoped<ConnectionCapabilityReader>();
        builder.Services.AddScoped<InboundGateway>();
        // The published-event chains already contain Serialisation handlers with their own EF transaction. Resolve the
        // staging service through a factory so Wolverine does not infer IntegrationDbContext as a second transaction
        // owner for that same public message. The stager owns and saves its narrow staging unit of work explicitly.
        builder.Services.AddScoped(sp => new OutboundCommandStager(
            sp.GetRequiredService<IntegrationDbContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>(),
            sp.GetRequiredService<Yojana.Platform.Contracts.Time.IClock>(),
            sp.GetRequiredService<ConnectionCapabilityReader>()));
        builder.Services.AddScoped<BoundedWaitResequencer>();
        builder.Services.AddSingleton(RecurringTenantJob.For<ResequencerJob>(ResequencerJob.JobName, ResequencerJob.Cron));
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
        return builder;
    }
}
