using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Ports;

/// <summary>The complete set of external source ports on which Serialisation's business rules depend.</summary>
public static class PortInventory
{
    public static IReadOnlyList<Type> PortTypes { get; } =
    [
        typeof(IPurchasingDocumentQueryPort),
        typeof(IManufacturingDocumentQueryPort),
        typeof(IInventoryDocumentQueryPort),
        typeof(ISalesDocumentQueryPort),
        typeof(IItemQueryPort),
        typeof(IBomQueryPort),
        typeof(IItemNamePort),
        typeof(IWarrantyClaimsPort),
        typeof(IPartyNamePort),
        typeof(ILocationNamePort),
        typeof(IDocumentNumberPort),
        typeof(IItemPolicyPort),
        typeof(ICustomerContractPort),
        typeof(IVendorWarrantyTermsPort),
        typeof(IWarrantyCertificatePort),
        typeof(ITimeZoneSourcePort),
        typeof(IStockQuantityPort),
        typeof(IStockReclassificationPort),
        typeof(IStockUnitEventPort),
        typeof(ILabelPrinterPort),
        typeof(ISerialSchemeSource),
        typeof(IItemFamilyNamePort),
        typeof(IPartyContactPort),
        typeof(IProductionOrderAllocationPort),
        typeof(IOwnershipSourceDocumentQueryPort),
        typeof(IServiceDocumentQueryPort),
        typeof(IInternalSaleDocumentQueryPort),
    ];

    public static PortInventoryResult Inspect(IServiceProvider services)
    {
        var rows = PortTypes.Select(portType =>
        {
            var implementation = services.GetRequiredService(portType);
            var developmentOnlyType = DevelopmentOnlyType(implementation, []);
            return new PortInventoryRow(portType, developmentOnlyType ?? implementation.GetType(),
                implementation is IUnavailablePort unavailable ? unavailable.UnavailableCode : null,
                developmentOnlyType is not null);
        }).ToList();
        return new PortInventoryResult(rows);
    }

    private static Type? DevelopmentOnlyType(object implementation, HashSet<object> visited)
    {
        if (!visited.Add(implementation)) return null;
        var implementationType = implementation.GetType();
        if (implementationType.GetCustomAttribute<DevelopmentOnlyAttribute>() is not null)
            return implementationType;
        if (implementation is not ISerialisationPortComposition composition) return null;
        return composition.Sources.Select(source => DevelopmentOnlyType(source, visited))
            .FirstOrDefault(type => type is not null);
    }
}

public sealed record PortInventoryRow(
    Type PortType,
    Type ImplementationType,
    string? UnavailableCode,
    bool DevelopmentOnly);

public sealed record PortInventoryResult(IReadOnlyList<PortInventoryRow> Rows)
{
    public int RealCount => Rows.Count(row => row.UnavailableCode is null && !row.DevelopmentOnly);
    public int UnavailableCount => Rows.Count(row => row.UnavailableCode is not null);
    public int DevelopmentOnlyCount => Rows.Count(row => row.DevelopmentOnly);
}

/// <summary>Checks the final DI graph, after every module has had the opportunity to replace a source adapter.</summary>
public sealed class SerialisationPortCheckHostedService(
    IServiceScopeFactory scopes,
    IHostEnvironment environment,
    ILogger<SerialisationPortCheckHostedService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var result = PortInventory.Inspect(scope.ServiceProvider);
        logger.LogInformation("Port check: {PortCount} ports. Real {RealCount}, Unavailable {UnavailableCount}, development-only {DevelopmentOnlyCount}.",
            result.Rows.Count, result.RealCount, result.UnavailableCount, result.DevelopmentOnlyCount);
        if (result.UnavailableCount > 0)
        {
            logger.LogInformation("  Unavailable: {UnavailablePorts}", string.Join(", ", result.Rows
                .Where(row => row.UnavailableCode is not null)
                .OrderBy(row => row.PortType.Name, StringComparer.Ordinal)
                .Select(row => $"{row.PortType.Name} ({row.UnavailableCode})")));
        }

        var forbidden = result.Rows.Where(row => row.DevelopmentOnly).ToList();
        if (!environment.IsDevelopment() && forbidden.Count > 0)
        {
            var details = string.Join(", ", forbidden.Select(row =>
                $"{row.PortType.Name} -> {row.ImplementationType.FullName}"));
            throw new InvalidOperationException(
                $"Development-only Serialisation ports cannot run in {environment.EnvironmentName}: {details}");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
