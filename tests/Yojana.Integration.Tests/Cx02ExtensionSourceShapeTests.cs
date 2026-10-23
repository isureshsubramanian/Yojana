using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yojana.Integration.Tests;

public sealed partial class Cx02ExtensionSourceShapeTests
{
    private static readonly IReadOnlyDictionary<string, (string EntityName, string EntitySet)> SchemaPages =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["item-entry-relation.schema.json"] = ("itemEntryRelation", "itemEntryRelations"),
            ["posted-return-receipt-line.schema.json"] = ("postedReturnReceiptLine", "postedReturnReceiptLines"),
            ["posted-return-receipt.schema.json"] = ("postedReturnReceipt", "postedReturnReceipts"),
            ["posted-transfer-receipt-line.schema.json"] = ("postedTransferReceiptLine", "postedTransferReceiptLines"),
            ["posted-transfer-receipt.schema.json"] = ("postedTransferReceipt", "postedTransferReceipts"),
            ["posted-transfer-shipment-line.schema.json"] = ("postedTransferShipmentLine", "postedTransferShipmentLines"),
            ["posted-transfer-shipment.schema.json"] = ("postedTransferShipment", "postedTransferShipments"),
            ["reservation-entry.schema.json"] = ("reservationEntry", "reservationEntries"),
            ["service-item.schema.json"] = ("serviceItem", "serviceItems"),
            ["tracked-item-ledger-entry.schema.json"] = ("trackedItemLedgerEntry", "trackedItemLedgerEntries"),
            ["tracking-specification.schema.json"] = ("trackingSpecification", "trackingSpecifications"),
            ["post-tracked-document-response.schema.json"] = ("postTrackedDocumentResponse", "postTrackedDocumentResponses"),
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedTablePermissions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Yojana Tracking Request"] = "RIM",
            ["Item Ledger Entry"] = "R",
            ["Item Entry Relation"] = "R",
            ["Reservation Entry"] = "RIM",
            ["Tracking Specification"] = "R",
            ["Service Item"] = "RI",
            ["Transfer Shipment Header"] = "R",
            ["Transfer Shipment Line"] = "R",
            ["Transfer Receipt Header"] = "R",
            ["Transfer Receipt Line"] = "R",
            ["Return Receipt Header"] = "R",
            ["Return Receipt Line"] = "R",
            ["Sales Header"] = "RIM",
            ["Sales Line"] = "RI",
            ["Sales Shipment Header"] = "R",
            ["Sales Cr.Memo Header"] = "R",
            ["Purchase Header"] = "RIM",
            ["Purchase Line"] = "RI",
            ["Purch. Rcpt. Header"] = "R",
            ["Transfer Header"] = "RIM",
            ["Transfer Line"] = "RI",
            ["Service Header"] = "RIM",
        };

    [Fact]
    public void Every_contract_shape_has_an_exact_versioned_api_page()
    {
        var pages = ReadPages().ToDictionary(page => page.EntitySetName, StringComparer.Ordinal);
        Assert.Equal(SchemaPages.Count, pages.Count);

        foreach (var (schemaFile, api) in SchemaPages)
        {
            var page = pages[api.EntitySet];
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractDirectory(), schemaFile)));
            var contractFields = schema.RootElement.GetProperty("properties").EnumerateObject()
                .Select(property => property.Name)
                .Where(name => name != "@odata.etag")
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(contractFields.SetEquals(page.Fields),
                $"{api.EntitySet}: contract [{string.Join(", ", contractFields.OrderBy(name => name, StringComparer.Ordinal))}], source [{string.Join(", ", page.Fields.OrderBy(name => name, StringComparer.Ordinal))}]");
            Assert.Equal("yojana", page.Publisher);
            Assert.Equal("integration", page.Group);
            Assert.Equal("v1.0", page.Version);
            Assert.Equal(api.EntityName, page.EntityName);
            Assert.Equal(api.EntitySet, page.EntitySetName);
            Assert.Equal("API", Property(page.Source, "PageType"));
            Assert.Equal("true", Property(page.Source, "DelayedInsert"));
            Assert.Equal("false", Property(page.Source, "Extensible"));
            Assert.Equal("SystemId", page.ODataKeyFields);
            Assert.DoesNotContain("SourceTableTemporary = true", page.Source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Contract_pages_use_single_guid_keys_and_only_the_reservation_boundary_is_writable()
    {
        var pages = ReadPages().ToDictionary(page => page.EntitySetName, StringComparer.Ordinal);
        foreach (var entitySet in SchemaPages.Values.Select(api => api.EntitySet)
                     .Where(name => name != "postTrackedDocumentResponses"))
        {
            var page = pages[entitySet];
            Assert.Contains("id", page.Fields);
            Assert.Equal("SystemId", page.ODataKeyFields);
            Assert.Equal("true", Property(page.Source, "ChangeTrackingAllowed"));
            Assert.Equal(entitySet == "reservationEntries" ? "true" : "false",
                Property(page.Source, "InsertAllowed"));
            Assert.Equal("false", Property(page.Source, "ModifyAllowed"));
            Assert.Equal("false", Property(page.Source, "DeleteAllowed"));
        }
    }

    [Fact]
    public void Posting_action_parameters_and_created_response_match_the_contract()
    {
        var source = File.ReadAllText(Path.Combine(SourceDirectory(), "ReservationEntries.Page.al"));
        var match = ActionProcedure().Match(source);
        Assert.True(match.Success, "postTrackedDocument must be a service-enabled bound action");
        var parameters = match.Groups["parameters"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Trim().Split(':', 2)[0].Replace("var ", "", StringComparison.Ordinal).Trim())
            .ToHashSet(StringComparer.Ordinal);
        using var requestSchema = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractDirectory(),
            "post-tracked-document-request.schema.json")));
        var requestParameters = requestSchema.RootElement.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.True(requestParameters.SetEquals(parameters.Where(name => name != "actionContext")),
            $"request [{string.Join(", ", requestParameters.OrderBy(name => name, StringComparer.Ordinal))}], action [{string.Join(", ", parameters.OrderBy(name => name, StringComparer.Ordinal))}]");
        Assert.Contains("actionContext", parameters);

        var response = ReadPages().Single(page => page.EntitySetName == "postTrackedDocumentResponses");
        using var responseSchema = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractDirectory(),
            "post-tracked-document-response.schema.json")));
        var responseParameters = responseSchema.RootElement.GetProperty("properties").EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        Assert.True(responseParameters.SetEquals(response.Fields));
        var posting = File.ReadAllText(Path.Combine(SourceDirectory(), "TrackedDocumentPost.Codeunit.al"));
        Assert.Contains("WebServiceActionResultCode::Created", posting, StringComparison.Ordinal);
        Assert.Contains("Page::\"Yojana Post Result API\"", posting, StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_and_all_extension_object_ids_are_fixed_in_range_and_unique()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(SourceDirectory(), "app.json")));
        var root = manifest.RootElement;
        Assert.Equal("3f946a82-95bc-4c95-9e55-a8d81510dc02", root.GetProperty("id").GetString());
        Assert.Equal("Yojana CX02 Integration", root.GetProperty("name").GetString());
        Assert.Equal("Yojana", root.GetProperty("publisher").GetString());
        Assert.Equal("1.0.0.0", root.GetProperty("version").GetString());
        Assert.Equal("Cloud", root.GetProperty("target").GetString());
        var range = root.GetProperty("idRanges")[0];
        var first = range.GetProperty("from").GetInt32();
        var last = range.GetProperty("to").GetInt32();

        var declarations = Directory.GetFiles(SourceDirectory(), "*.al")
            .SelectMany(file => ObjectDeclaration().Matches(File.ReadAllText(file)).Cast<Match>()
                .Select(match => (File: Path.GetFileName(file), Id: int.Parse(match.Groups["id"].Value))))
            .ToList();
        Assert.NotEmpty(declarations);
        Assert.All(declarations, declaration => Assert.InRange(declaration.Id, first, last));
        var duplicates = declarations.GroupBy(declaration => declaration.Id).Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(item => item.File))}").ToArray();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Permission_set_has_the_exact_page_and_posting_table_allow_list()
    {
        var source = File.ReadAllText(Path.Combine(SourceDirectory(), "YojanaCx02.PermissionSet.al"));
        var actual = TablePermission().Matches(source).Cast<Match>().ToDictionary(
            match => match.Groups["table"].Value,
            match => match.Groups["rights"].Value,
            StringComparer.Ordinal);
        Assert.Equal(ExpectedTablePermissions.OrderBy(item => item.Key), actual.OrderBy(item => item.Key));

        foreach (var page in ReadPages())
        {
            Assert.True(actual.TryGetValue(page.SourceTable, out var rights),
                $"{page.EntitySetName} reads {page.SourceTable}, which is absent from the permission set");
            Assert.Contains('R', rights);
        }
        foreach (var executable in new[]
        {
            "Yojana Tracked Document Post", "Create Reserv. Entry", "Sales-Post", "Purch.-Post",
            "TransferOrder-Post Shipment", "TransferOrder-Post Receipt",
        })
            Assert.Matches($"codeunit \\\"{Regex.Escape(executable)}\\\" = X", source);
        Assert.DoesNotContain("SUPER", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch("(?:tabledata|page|codeunit)\\s+[^,;]*\\*", source);
    }

    private static ApiPage[] ReadPages() => Directory.GetFiles(SourceDirectory(), "*.Page.al")
        .Select(file =>
        {
            var source = File.ReadAllText(file);
            return new ApiPage(
                int.Parse(PageDeclaration().Match(source).Groups["id"].Value),
                Property(source, "APIPublisher"), Property(source, "APIGroup"), Property(source, "APIVersion"),
                Property(source, "EntityName"), Property(source, "EntitySetName"), Property(source, "SourceTable"),
                Property(source, "ODataKeyFields"), FieldDeclaration().Matches(source).Cast<Match>()
                    .Select(match => match.Groups["quoted"].Success
                        ? match.Groups["quoted"].Value : match.Groups["plain"].Value)
                    .ToHashSet(StringComparer.Ordinal), source);
        }).ToArray();

    private static string Property(string source, string name)
    {
        var match = Regex.Match(source, $"(?m)^\\s*{Regex.Escape(name)}\\s*=\\s*(?:'(?<quoted>[^']*)'|\"(?<quotedIdentifier>[^\"]*)\"|(?<plain>[^;\\r\\n]+))\\s*;");
        Assert.True(match.Success, $"Missing {name}");
        return match.Groups["quoted"].Success ? match.Groups["quoted"].Value
            : match.Groups["quotedIdentifier"].Success ? match.Groups["quotedIdentifier"].Value
            : match.Groups["plain"].Value.Trim();
    }

    private static string ContractDirectory() => Path.Combine(AppContext.BaseDirectory,
        "Connectors", "CX02", "Contract", "v1.0");

    private static string SourceDirectory() => Path.Combine(AppContext.BaseDirectory,
        "Connectors", "CX02", "Source");

    private sealed record ApiPage(int Id, string Publisher, string Group, string Version, string EntityName,
        string EntitySetName, string SourceTable, string ODataKeyFields, IReadOnlySet<string> Fields, string Source);

    [GeneratedRegex("(?m)^\\s*page\\s+(?<id>\\d+)\\s+\"[^\"]+\"")]
    private static partial Regex PageDeclaration();

    [GeneratedRegex("(?m)^\\s*(?:page|table|codeunit|permissionset)\\s+(?<id>\\d+)\\s+")]
    private static partial Regex ObjectDeclaration();

    [GeneratedRegex("\\bfield\\(\\s*(?:\"(?<quoted>[^\"]+)\"|(?<plain>[A-Za-z_][A-Za-z0-9_]*))\\s*;")]
    private static partial Regex FieldDeclaration();

    [GeneratedRegex("\\[ServiceEnabled\\]\\s*\\[Scope\\('Cloud'\\)\\]\\s*procedure\\s+postTrackedDocument\\s*\\((?<parameters>[^)]*)\\)", RegexOptions.Singleline)]
    private static partial Regex ActionProcedure();

    [GeneratedRegex("tabledata\\s+\"(?<table>[^\"]+)\"\\s*=\\s*(?<rights>[RIMD]+)")]
    private static partial Regex TablePermission();
}
