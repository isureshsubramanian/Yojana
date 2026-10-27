using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Yojana.Architecture.Tests;

public sealed class SerialisationPortRules
{
    private static readonly string Root = FindRoot();
    private const string DevelopmentProject = "Yojana.Serialisation.DevelopmentPorts";

    private static readonly string[] SourcePorts =
    [
        "IPurchasingDocumentQueryPort", "IManufacturingDocumentQueryPort", "IInventoryDocumentQueryPort",
        "ISalesDocumentQueryPort", "IItemQueryPort", "IBomQueryPort", "IItemNamePort", "IWarrantyClaimsPort",
        "IPartyNamePort", "ILocationNamePort", "IDocumentNumberPort", "IItemPolicyPort", "ICustomerContractPort",
        "IVendorWarrantyTermsPort", "IWarrantyCertificatePort", "ITimeZoneSourcePort", "IStockQuantityPort",
        "IStockReclassificationPort", "IStockUnitEventPort", "ILabelPrinterPort", "ISerialSchemeSource",
        "IItemFamilyNamePort", "IPartyContactPort", "IProductionOrderAllocationPort",
        "IOwnershipSourceDocumentQueryPort", "IServiceDocumentQueryPort", "IInternalSaleDocumentQueryPort",
    ];

    [Fact]
    public void Every_serialisation_source_port_is_in_the_startup_inventory()
    {
        var inventory = File.ReadAllText(Path.Combine(Root,
            "src/Serialisation/Yojana.Serialisation/Ports/PortInventory.cs"));
        var listed = Regex.Matches(inventory, @"typeof\((I[A-Za-z0-9]+(?:Port|Source))\)")
            .Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(SourcePorts.Order(), listed.Order());
    }

    [Fact]
    public void Only_tests_demo_and_the_web_host_reference_development_ports()
    {
        var violations = Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal))
            .Where(path => XDocument.Load(path).Descendants("ProjectReference")
                .Any(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value)
                    == DevelopmentProject))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "tests" + Path.DirectorySeparatorChar,
                StringComparison.Ordinal)
                && !path.EndsWith("tools/Yojana.Demo/Yojana.Demo.csproj", StringComparison.Ordinal)
                && !path.EndsWith("tools/Yojana.Bench/Yojana.Bench.csproj", StringComparison.Ordinal)
                && !path.EndsWith("src/Web/Yojana.Web/Yojana.Web.csproj", StringComparison.Ordinal)
                && !path.EndsWith($"src/Serialisation/{DevelopmentProject}/{DevelopmentProject}.csproj",
                    StringComparison.Ordinal))
            .ToList();
        Assert.Empty(violations);
    }

    [Fact]
    public void Every_explicit_development_port_class_is_marked_development_only()
    {
        var root = Path.Combine(Root, "src/Serialisation", DevelopmentProject);
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source,
                         @"(?<attributes>(?:\[[^\]]+\]\s*)*)public\s+(?:sealed\s+)?(?:static\s+)?class\s+(?<name>[A-Za-z0-9_]+)"))
                if (!match.Groups["attributes"].Value.Contains("DevelopmentOnly", StringComparison.Ordinal))
                    missing.Add($"{Path.GetRelativePath(Root, file)}: {match.Groups["name"].Value}");
        }

        Assert.Empty(missing);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
