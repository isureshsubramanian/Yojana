using System.Text.RegularExpressions;
using Xunit;

namespace Yojana.Architecture.Tests;

public sealed class CurrencyUsageArchitectureTests
{
    private static readonly string Root = FindRoot();

    [Fact]
    public void Every_currency_bearing_entity_is_registered_with_the_module_usage_probe()
    {
        var entities = Path.Combine(Root, "src/Serialisation/Yojana.Serialisation/Data/Entities");
        var currencyEntities = Directory.EnumerateFiles(entities, "*.cs")
            .SelectMany(path => Classes(File.ReadAllText(path)))
            .Where(value => Regex.IsMatch(value.Body, @"\bstring\?*\s+(Sale)?Currency\b"))
            .Select(value => value.Name)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var probe = File.ReadAllText(Path.Combine(Root,
            "src/Serialisation/Yojana.Serialisation/Currency/SerialisationCurrencyUsage.cs"));

        var missing = currencyEntities.Where(name => !probe.Contains($"\"{name}\"", StringComparison.Ordinal)).ToList();
        Assert.True(missing.Count == 0,
            "Currency-bearing entities missing from SerialisationCurrencyUsage.CoveredEntityNames: " + string.Join(", ", missing));

        // These values use the home currency implicitly or inside JSON, so a property-name scan cannot discover them.
        Assert.Contains("\"ReturnReason\"", probe, StringComparison.Ordinal);
        Assert.Contains("\"WarrantyPolicyVersion\"", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void Serialisation_registers_both_currency_usage_contracts()
    {
        var module = File.ReadAllText(Path.Combine(Root,
            "src/Serialisation/Yojana.Serialisation/SerialisationModule.cs"));
        Assert.Contains("AddScoped<ICurrencyUsage>", module, StringComparison.Ordinal);
        Assert.Contains("AddScoped<ICurrencyUsageReport>", module, StringComparison.Ordinal);
    }

    [Fact]
    public void Old_accepted_field_is_deserialised_for_one_release_but_never_used()
    {
        var host = File.ReadAllText(Path.Combine(Root, "src/Web/Yojana.Web/Program.cs"));
        Assert.Contains("IReadOnlyList<string>? Accepted = null", host, StringComparison.Ordinal);
        Assert.DoesNotContain("request.Accepted", host, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_operational_money_entry_path_invokes_the_home_currency_guard()
    {
        AssertGuard("Claims/ClaimHandlers.cs", "SubmitClaimHandler", "ApproveRecoveryClaimHandler");
        AssertGuard("Entitlements/ExtendedWarrantyHandler.cs", "AttachExtendedWarrantyHandler");
        AssertGuard("Entitlements/WarrantySalesLineHandler.cs", "SalesInvoicePostedHandler");
        AssertGuard("Entitlements/SourceDocumentCorrectionEntitlements.cs", "SourceDocumentCorrectionEntitlements");
        AssertGuard("Entitlements/PolicyStore.cs", "PolicyStore");
        AssertGuard("Jurisdictions/JurisdictionCatalogueService.cs", "JurisdictionCatalogueService");
        AssertGuard("Refurbishments/RefurbishmentService.cs", "RefurbishmentService");
        AssertGuard("Replacements/WarrantyReplacementHandler.cs", "CreateWarrantyReplacementHandler");
        AssertGuard("Replacements/AdvanceReplacementHandlers.cs", "BillNonReturnHandler");
        AssertGuard("Replacements/AlternativeReplacementHandlers.cs", "ConvertLoanToReplacementHandler", "ReceiveVendorReplacementHandler");
        AssertGuard("Rmas/RmaHandlers.cs", "RequestRmaHandler");
        AssertGuard("Rmas/RmaHandlers.cs", "DefineReturnReasonHandler");
        AssertGuard("MigrationLoads/MigrationLoadHandlers.cs", "ApplyMigratedUnitsHandler");
    }

    private static void AssertGuard(string relativePath, params string[] entryPoints)
    {
        var source = File.ReadAllText(Path.Combine(Root, "src/Serialisation/Yojana.Serialisation", relativePath));
        Assert.Contains("HomeCurrencyGuard.RequireAsync", source, StringComparison.Ordinal);
        foreach (var entryPoint in entryPoints) Assert.Contains(entryPoint, source, StringComparison.Ordinal);
    }

    private static IEnumerable<(string Name, string Body)> Classes(string source)
    {
        var matches = Regex.Matches(source, @"public sealed class\s+(?<name>\w+)(?<body>.*?)(?=public sealed class|\z)",
            RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return matches.Select(match => (match.Groups["name"].Value, match.Groups["body"].Value));
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Packages.props")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
