using System.Text.Json;
using Yojana.Integration.Contracts;
using Xunit;

namespace Yojana.Integration.Tests;

public sealed class ContractCompatibilityTests
{
    private static readonly Dictionary<string, Type> PayloadTypes = new(StringComparer.Ordinal)
    {
        ["ItemUpserted"] = typeof(ItemUpserted), ["BomUpserted"] = typeof(BomUpserted),
        ["PartyUpserted"] = typeof(PartyUpserted),
        ["GoodsReceiptPosted"] = typeof(GoodsReceiptPosted), ["ProductionCompleted"] = typeof(ProductionCompleted),
        ["DeliveryPosted"] = typeof(DeliveryPosted), ["InvoicePosted"] = typeof(InvoicePosted),
        ["ReturnReceived"] = typeof(ReturnReceived), ["TransferPosted"] = typeof(TransferPosted),
        ["InventoryAdjusted"] = typeof(InventoryAdjusted), ["WarrantyLineSold"] = typeof(WarrantyLineSold),
        ["DocumentCancelled"] = typeof(DocumentCancelled), ["CreateReplacementDelivery"] = typeof(CreateReplacementDelivery),
        ["CreateCreditMemo"] = typeof(CreateCreditMemo), ["PostWarrantyExpense"] = typeof(PostWarrantyExpense),
        ["ChangeStockStatusOrLocation"] = typeof(ChangeStockStatusOrLocation), ["AttachDocument"] = typeof(AttachDocument),
        ["CreateServiceOrder"] = typeof(CreateServiceOrder),
        ["PushNativeWarrantyObject"] = typeof(PushNativeWarrantyObject),
    };

    [Fact]
    public void Every_message_has_a_schema_and_structurally_valid_example()
    {
        var examples = IntegrationSchemaCatalog.Resources.Where(name => name.Contains(".examples.", StringComparison.Ordinal)
            && name.EndsWith(".json", StringComparison.Ordinal)).ToList();
        Assert.Equal(19, examples.Count);
        foreach (var resource in examples)
        {
            var slug = resource[(resource.LastIndexOf(".examples.", StringComparison.Ordinal) + 10)..^5];
            using var example = JsonDocument.Parse(IntegrationSchemaCatalog.Read($"examples/{slug}.json"));
            using var schema = JsonDocument.Parse(IntegrationSchemaCatalog.Read($"{slug}.schema.json"));
            StructuralSchemaValidator.Validate(example.RootElement, schema.RootElement,
                name => JsonDocument.Parse(IntegrationSchemaCatalog.Read(name)).RootElement.Clone());
        }
    }

    [Fact]
    public void Every_example_round_trips_through_its_typed_record()
    {
        foreach (var resource in IntegrationSchemaCatalog.Resources.Where(name => name.Contains(".examples.", StringComparison.Ordinal)
                     && name.EndsWith(".json", StringComparison.Ordinal)))
        {
            var slug = resource[(resource.LastIndexOf(".examples.", StringComparison.Ordinal) + 10)..^5];
            using var example = JsonDocument.Parse(IntegrationSchemaCatalog.Read($"examples/{slug}.json"));
            var typeName = example.RootElement.GetProperty("message_type").GetString()!;
            var messageType = typeof(CanonicalMessage<>).MakeGenericType(PayloadTypes[typeName]);
            var value = JsonSerializer.Deserialize(example.RootElement.GetRawText(), messageType, IntegrationContracts.Json);
            Assert.NotNull(value);
            var serialised = JsonSerializer.Serialize(value, messageType, IntegrationContracts.Json);
            var again = JsonSerializer.Deserialize(serialised,
                messageType, IntegrationContracts.Json);
            var serialisedAgain = JsonSerializer.Serialize(again, messageType, IntegrationContracts.Json);
            using var first = JsonDocument.Parse(serialised);
            using var second = JsonDocument.Parse(serialisedAgain);
            Assert.True(JsonElement.DeepEquals(first.RootElement, second.RootElement));
        }
    }

    [Fact]
    public void Unknown_fields_are_ignored_and_unknown_enum_values_keep_the_raw_value()
    {
        var original = IntegrationSchemaCatalog.Read("examples/item-upserted.json");
        var node = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
        node["future_envelope_field"] = 7;
        node["payload"]!["future_payload_field"] = true;
        node["payload"]!["tracking_mode"] = "quantum_tracking";
        var parsed = JsonSerializer.Deserialize<CanonicalMessage<ItemUpserted>>(node.ToJsonString(), IntegrationContracts.Json)!;
        Assert.Equal(OpenEnum.Other, parsed.Payload.TrackingMode.Value);
        Assert.Equal("quantum_tracking", parsed.Payload.TrackingMode.RawValue);
    }

    [Fact]
    public void Previous_item_payloads_without_inventory_flag_keep_the_inventory_default()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(
            IntegrationSchemaCatalog.Read("examples/item-upserted.json"))!.AsObject();
        node["payload"]!.AsObject().Remove("is_inventory_item");

        var parsed = JsonSerializer.Deserialize<CanonicalMessage<ItemUpserted>>(
            node.ToJsonString(), IntegrationContracts.Json)!;

        Assert.True(parsed.Payload.IsInventoryItem);
    }

    [Theory]
    [InlineData("1.0", true)]
    [InlineData("0.9", true)]
    [InlineData("2.0", false)]
    [InlineData("not-a-version", false)]
    public void Current_and_previous_major_are_accepted(string version, bool expected) =>
        Assert.Equal(expected, IntegrationContracts.Accepts(version));
}

internal static class StructuralSchemaValidator
{
    public static void Validate(JsonElement value, JsonElement schema, Func<string, JsonElement> resolve)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            Validate(value, resolve(reference.GetString()!), resolve);
            return;
        }
        if (schema.TryGetProperty("const", out var constant)) Assert.True(JsonElement.DeepEquals(value, constant));
        if (!schema.TryGetProperty("type", out var type)) return;
        var accepted = type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>([type.GetString()!], StringComparer.Ordinal);
        var actual = value.ValueKind switch
        {
            JsonValueKind.Object => "object", JsonValueKind.Array => "array", JsonValueKind.String => "string",
            JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null", _ => "unknown",
        };
        Assert.Contains(actual, accepted);
        if (actual == "object")
        {
            if (schema.TryGetProperty("required", out var required))
                foreach (var name in required.EnumerateArray()) Assert.True(value.TryGetProperty(name.GetString()!, out _), $"Missing {name.GetString()}");
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in value.EnumerateObject())
                    if (properties.TryGetProperty(property.Name, out var child)) Validate(property.Value, child, resolve);
        }
        if (actual == "array" && schema.TryGetProperty("items", out var items))
            foreach (var item in value.EnumerateArray()) Validate(item, items, resolve);
    }
}
