using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yojana.Integration.Connectors.CX02.Testing;

namespace Yojana.Integration.Tests;

public sealed class Cx02ExtensionContractTests
{
    private static readonly string[] ExpectedSchemas =
    [
        "item-entry-relation.schema.json", "post-tracked-document-request.schema.json",
        "posted-return-receipt-line.schema.json",
        "posted-return-receipt.schema.json", "posted-transfer-receipt-line.schema.json",
        "posted-transfer-receipt.schema.json", "posted-transfer-shipment-line.schema.json",
        "posted-transfer-shipment.schema.json", "reservation-entry.schema.json", "service-item.schema.json",
        "tracked-document-request.schema.json", "tracked-item-ledger-entry.schema.json",
        "tracking-specification.schema.json",
    ];

    [Fact]
    public void V1_contract_has_one_well_formed_schema_per_page_and_action_shape()
    {
        var directory = ContractDirectory();
        var files = Directory.GetFiles(directory, "*.schema.json").Select(Path.GetFileName)
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(ExpectedSchemas, files);
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in files)
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, file!)));
            var root = schema.RootElement;
            Assert.Equal("https://json-schema.org/draft/2020-12/schema", root.GetProperty("$schema").GetString());
            Assert.Contains("/v1.0/", root.GetProperty("$id").GetString(), StringComparison.Ordinal);
            Assert.True(ids.Add(root.GetProperty("$id").GetString()!));
            Assert.Equal("object", root.GetProperty("type").GetString());
            Assert.False(root.GetProperty("additionalProperties").GetBoolean());
            var properties = root.GetProperty("properties");
            foreach (var required in root.GetProperty("required").EnumerateArray())
                Assert.True(properties.TryGetProperty(required.GetString()!, out _), $"{file}: undeclared required property");
            foreach (var property in properties.EnumerateObject())
                Assert.True(property.Name == "@odata.etag" || char.IsLower(property.Name[0]),
                    $"{file}: '{property.Name}' is not camelCase");

            if (!file!.StartsWith("post-tracked-document-", StringComparison.Ordinal))
            {
                Assert.Contains("id", root.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
                Assert.Contains("lastModifiedDateTime", root.GetProperty("required").EnumerateArray()
                    .Select(item => item.GetString()));
                Assert.Contains("@odata.etag", root.GetProperty("required").EnumerateArray()
                    .Select(item => item.GetString()));
            }
        }
    }

    [Fact]
    public async Task Simulator_page_and_action_payloads_validate_against_the_published_schemas()
    {
        await using var simulator = new Cx02Simulator(new Cx02TimeProvider(
            new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero)));
        using var client = await AuthenticatedClientAsync(simulator);
        using var trackedReply = await client.GetAsync(simulator.CustomApiUri(
            $"companies({Cx02Simulator.CompanyId})/trackedItemLedgerEntries"));
        trackedReply.EnsureSuccessStatusCode();
        using var trackedBody = JsonDocument.Parse(await trackedReply.Content.ReadAsStringAsync());
        using var trackedSchema = Schema("tracked-item-ledger-entry.schema.json");
        Assert.Empty(Validate(trackedSchema.RootElement, trackedBody.RootElement.GetProperty("value")[0], "$"));

        var requestValue = JsonSerializer.SerializeToElement(new
        {
            documentType = "salesShipment",
            tracking = new[]
            {
                new { sourceLineNumber = 10, itemNumber = "HW-SERVER-100", locationCode = "WH-A",
                    quantity = 1m, serialNumber = "SN-FICTIONAL-001",
                    lotNumber = "", packageNumber = "" },
            },
            documentId = Guid.CreateVersion7(),
            logicalReference = "FIC-SHIP-100",
            metadata = new { customerNumber = "FIC-CUSTOMER-100", shipToCode = "FIC-SHIP" },
        });
        using var requestSchema = Schema("post-tracked-document-request.schema.json");
        Assert.Empty(Validate(requestSchema.RootElement, requestValue, "$"));
        var documentId = Guid.CreateVersion7();
        using var actionReply = await client.PostAsync(simulator.CustomApiUri(
            $"companies({Cx02Simulator.CompanyId})/trackedDocumentRequests({documentId})/Microsoft.NAV.postTrackedDocument"),
            new StringContent(requestValue.GetRawText(), Encoding.UTF8, "application/json"));
        actionReply.EnsureSuccessStatusCode();
        using var actionBody = JsonDocument.Parse(await actionReply.Content.ReadAsStringAsync());
        using var responseSchema = Schema("tracked-document-request.schema.json");
        Assert.Empty(Validate(responseSchema.RootElement, actionBody.RootElement, "$"));
    }

    [Fact]
    public void Action_contract_has_per_kind_metadata_and_document_number_join_limits()
    {
        using var request = Schema("post-tracked-document-request.schema.json");
        Assert.Equal(8, request.RootElement.GetProperty("oneOf").GetArrayLength());
        Assert.Contains("itemJournal", request.RootElement.GetProperty("properties").GetProperty("documentType")
            .GetProperty("enum").EnumerateArray().Select(value => value.GetString()));
        Assert.True(request.RootElement.GetProperty("properties").GetProperty("tracking").GetProperty("items")
            .GetProperty("properties").TryGetProperty("sourceLineNumber", out _));

        foreach (var file in new[] { "item-entry-relation.schema.json", "reservation-entry.schema.json",
                     "tracking-specification.schema.json" })
        {
            using var schema = Schema(file);
            var properties = schema.RootElement.GetProperty("properties");
            Assert.Equal(20, properties.GetProperty("sourceDocumentNumber").GetProperty("maxLength").GetInt32());
            Assert.True(properties.TryGetProperty("sourceDocumentId", out _));
            Assert.False(properties.TryGetProperty("sourceId", out _));
        }
    }

    private static List<string> Validate(JsonElement schema, JsonElement value, string path)
    {
        var errors = new List<string>();
        if (schema.TryGetProperty("const", out var constant) && constant.GetRawText() != value.GetRawText())
            errors.Add($"{path} differs from const");
        if (schema.TryGetProperty("enum", out var choices)
            && !choices.EnumerateArray().Any(choice => choice.GetRawText() == value.GetRawText()))
            errors.Add($"{path} is outside enum");
        if (schema.TryGetProperty("type", out var type) && !MatchesType(type, value))
        {
            errors.Add($"{path} has wrong type");
            return errors;
        }
        if (value.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out var properties))
        {
            if (schema.TryGetProperty("required", out var required))
                errors.AddRange(required.EnumerateArray().Where(item => !value.TryGetProperty(item.GetString()!, out _))
                    .Select(item => $"{path}.{item.GetString()} is required"));
            if (schema.TryGetProperty("additionalProperties", out var additional) && !additional.GetBoolean())
                errors.AddRange(value.EnumerateObject().Where(property => !properties.TryGetProperty(property.Name, out _))
                    .Select(property => $"{path}.{property.Name} is not declared"));
            foreach (var property in value.EnumerateObject())
                if (properties.TryGetProperty(property.Name, out var propertySchema))
                    errors.AddRange(Validate(propertySchema, property.Value, path + "." + property.Name));
        }
        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            foreach (var (item, index) in value.EnumerateArray().Select((item, index) => (item, index)))
                errors.AddRange(Validate(items, item, $"{path}[{index}]"));
        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("format", out var format))
        {
            var text = value.GetString();
            var valid = format.GetString() switch
            {
                "uuid" => Guid.TryParse(text, out _),
                "date" => DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                "date-time" => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _),
                _ => true,
            };
            if (!valid) errors.Add($"{path} has invalid {format.GetString()} format");
        }
        return errors;
    }

    private static bool MatchesType(JsonElement type, JsonElement value)
    {
        var names = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(item => item.GetString()).ToArray()
            : [type.GetString()];
        return names.Any(name => name switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "null" => value.ValueKind == JsonValueKind.Null,
            _ => false,
        });
    }

    private static JsonDocument Schema(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractDirectory(), file)));

    private static string ContractDirectory() => Path.Combine(AppContext.BaseDirectory,
        "Connectors", "CX02", "Contract", "v1.0");

    private static async Task<HttpClient> AuthenticatedClientAsync(Cx02Simulator simulator)
    {
        var client = new HttpClient();
        using var token = await client.PostAsync(simulator.TokenUri(), new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = "fictional-client",
                ["client_secret"] = "fictional-secret", ["scope"] = Cx02SimulatorOptions.RequiredScope,
            }));
        token.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            body.RootElement.GetProperty("access_token").GetString());
        return client;
    }
}
