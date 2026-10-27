using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Labels;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation;

/// <summary>Development, demo and test composition for mutable Serialisation source facts.</summary>
[DevelopmentOnly]
public static class SerialisationDevelopmentPorts
{
    public static IHostApplicationBuilder AddSerialisationDevelopmentPorts(this IHostApplicationBuilder builder)
    {
        // HIDDEN COUPLING (registration order): AddSerialisationModule must run first so these later registrations
        // replace only its Unavailable adapters. A host then adds Integration after this method, allowing its real item,
        // BOM and document adapters to win. Reordering those calls restores development facts over real projections.
        // Keep the matching composition-root and SerialisationModule comments with this invariant.
        builder.Services.AddSingleton<InMemorySerialSchemes>();
        builder.Services.AddSingleton<ISerialSchemeSource>(sp => sp.GetRequiredService<InMemorySerialSchemes>());

        builder.Services.AddSingleton<InMemoryLabelPrinterPort>();
        builder.Services.AddSingleton<ILabelPrinterPort>(sp => sp.GetRequiredService<InMemoryLabelPrinterPort>());

        builder.Services.AddSingleton<InMemoryDocumentPorts>();
        builder.Services.AddSingleton<IPurchasingDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.AddSingleton<IManufacturingDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.AddSingleton<IProductionOrderAllocationPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.AddSingleton<IInventoryDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.AddSingleton<ISalesDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.AddSingleton<IOwnershipSourceDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.AddSingleton<IServiceDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());
        builder.Services.AddSingleton<IInternalSaleDocumentQueryPort>(sp => sp.GetRequiredService<InMemoryDocumentPorts>());

        builder.Services.AddSingleton<InMemoryStockUnitEventPort>();
        builder.Services.AddSingleton<IStockUnitEventPort>(sp => sp.GetRequiredService<InMemoryStockUnitEventPort>());
        builder.Services.AddSingleton<InMemoryStockReclassificationPort>();
        builder.Services.AddSingleton<IStockReclassificationPort>(sp => sp.GetRequiredService<InMemoryStockReclassificationPort>());
        builder.Services.AddSingleton<InMemoryStockQuantityPort>();
        builder.Services.AddSingleton<IStockQuantityPort>(sp => sp.GetRequiredService<InMemoryStockQuantityPort>());

        builder.Services.AddSingleton<InMemoryItemPorts>();
        builder.Services.AddSingleton<IItemQueryPort>(sp => sp.GetRequiredService<InMemoryItemPorts>());
        builder.Services.AddSingleton<IBomQueryPort>(sp => sp.GetRequiredService<InMemoryItemPorts>());
        builder.Services.AddSingleton(sp => new InMemoryItemNamePort(sp.GetRequiredService<InMemoryItemPorts>()));
        builder.Services.AddSingleton<IItemNamePort>(sp => sp.GetRequiredService<InMemoryItemNamePort>());
        builder.Services.AddKeyedSingleton<IItemNamePort>(SerialisationPortKeys.ItemNameCatalogue,
            (sp, _) => sp.GetRequiredService<InMemoryItemNamePort>());
        builder.Services.AddSingleton<InMemoryItemFamilyNamePort>();
        builder.Services.AddSingleton<IItemFamilyNamePort>(sp => sp.GetRequiredService<InMemoryItemFamilyNamePort>());
        builder.Services.AddSingleton<InMemoryPartyContactPort>();
        builder.Services.AddSingleton<IPartyContactPort>(sp => sp.GetRequiredService<InMemoryPartyContactPort>());
        builder.Services.AddSingleton<InMemoryPartyNamePort>();
        builder.Services.AddKeyedSingleton<IPartyNamePort>(SerialisationPortKeys.PartyNameSource,
            (sp, _) => sp.GetRequiredService<InMemoryPartyNamePort>());

        builder.Services.AddSingleton<InMemoryLocationNameRegistry>();
        builder.Services.AddScoped<ILocationNamePort>(sp => new DefaultLocationNamePort(
            sp.GetRequiredService<InMemoryLocationNameRegistry>(),
            sp.GetRequiredKeyedService<ILocationNamePort>(SerialisationPortKeys.LocationNameRecordedSource)));
        builder.Services.AddSingleton<InMemoryDocumentNumberRegistry>();
        builder.Services.AddScoped<IDocumentNumberPort>(sp => new DefaultDocumentNumberPort(
            sp.GetRequiredService<InMemoryDocumentNumberRegistry>(),
            sp.GetRequiredKeyedService<IDocumentNumberPort>(SerialisationPortKeys.DocumentNumberRecordedSource)));

        builder.Services.AddSingleton<InMemoryWarrantyPorts>();
        builder.Services.AddSingleton<IItemPolicyPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.AddSingleton<ICustomerContractPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.AddSingleton<IVendorWarrantyTermsPort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.AddSingleton<ITimeZoneSourcePort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.AddSingleton<IWarrantyCertificatePort>(sp => sp.GetRequiredService<InMemoryWarrantyPorts>());
        builder.Services.AddScoped<IWarrantyClaimsPort>(sp => new DevelopmentWarrantyClaimsPort(
            sp.GetRequiredKeyedService<IWarrantyClaimsPort>(SerialisationPortKeys.WarrantyClaimsRecordedSource),
            sp.GetRequiredService<InMemoryWarrantyPorts>()));

        return builder;
    }
}
