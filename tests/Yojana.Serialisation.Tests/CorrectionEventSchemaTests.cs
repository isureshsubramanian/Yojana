using System.Reflection;
using System.Text.Json;
using Xunit;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Tests;

/// <summary>P3-T15: every published correction event has an embedded 1.0 schema that accepts its wire representation.</summary>
public sealed class CorrectionEventSchemaTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly DateTime Occurred = new(2026, 10, 16, 9, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime Recorded = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);
    private static readonly PublishedCorrection Correction = new(Id(2), "correction_reason", "workbench", null, ["approver-1"]);

    private static readonly (string Schema, string Version, Type Type, object Sample, bool PerUnit)[] Contracts =
    [
        UnitEntry<UnitIdentifiersSwapped>(UnitIdentifiersSwapped.Schema, UnitIdentifiersSwapped.Version,
            h => new(h, Correction, Id(10), Id(11), new(Id(12), "vendor_serial", "OEM", "SER-001", Id(13)), new(Id(14), "vendor_serial", "OEM", "SER-002", Id(15)))),
        UnitEntry<UnitItemCorrected>(UnitItemCorrected.Schema, UnitItemCorrected.Version,
            h => new(h, Correction, Id(21), Id(22), Id(23), Id(24), true, "INV-RECLASS-9", true)),
        UnitEntry<UnitAsBuiltCorrected>(UnitAsBuiltCorrected.Schema, UnitAsBuiltCorrected.Version,
            h => new(h, Correction, "replace", Occurred, new(Id(31), Id(32), null, null, null, "component", "drive-1"), new(Id(33), Id(34), null, null, null, "component", "drive-1"))),
        SummaryEntry<ItemTrackingModeChanged>(ItemTrackingModeChanged.Schema, ItemTrackingModeChanged.Version,
            h => new(h, Correction, Id(35), "none", "serial", Occurred, false,
                [new(Id(36), "warehouse", 2, 1, 1, "damaged label", new("stocktake", Id(37), null))])),
        UnitEntry<UnitVoided>(UnitVoided.Schema, UnitVoided.Version,
            h => new(h, Correction, [Id(41)], "never_reuse", new("production_order", Id(42), 3))),
        UnitEntry<UnitVoidReversed>(UnitVoidReversed.Schema, UnitVoidReversed.Version,
            h => new(h, Correction with { CorrectsCorrectionId = Id(43) }, [Id(44)], "pre_release", new("production_order", Id(45), 3))),
        UnitEntry<UnitMerged>(UnitMerged.Schema, UnitMerged.Version, h => new(h, Correction, h.UnitId, Id(51), "survivor")),
        SummaryEntry<UnitIdentifiersAssigned>(UnitIdentifiersAssigned.Schema, UnitIdentifiersAssigned.Version,
            h => new(h, Correction, new("production_order", Id(60), null), [new(Id(61), Id(62), new("vendor_serial", "OEM", "V-61", Id(63)))], 1, [Id(64)])),
        SummaryEntry<UnitAllocationReassigned>(UnitAllocationReassigned.Schema, UnitAllocationReassigned.Version,
            h => new(h, Correction, [new(Id(70), new("production_order", Id(71), null), new("production_order", Id(72), null))])),
        SummaryEntry<DocumentSerialsCorrected>(DocumentSerialsCorrected.Schema, DocumentSerialsCorrected.Version,
            h => new(h, Correction, new("delivery", Id(80), null), [Id(81), Id(82)], [Id(83), Id(84)], true, true)),
        SummaryEntry<OwnershipSourceDocumentCorrected>(OwnershipSourceDocumentCorrected.Schema, OwnershipSourceDocumentCorrected.Version,
            h => new(h, Correction, [Id(90), Id(91)], new("sales_order", Id(92), null), new("sales_order", Id(93), null), true, Id(94), Id(95)))
    ];

    [Fact]
    public void Every_correction_event_has_a_matching_embedded_schema_and_pipeline_contract()
    {
        var nullability = new NullabilityInfoContext();

        foreach (var (schema, version, type, _, perUnit) in Contracts)
        {
            using var document = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
            Assert.Equal(schema, document.RootElement.GetProperty("title").GetString());
            Assert.Equal(perUnit, typeof(IPublishedUnitEvent).IsAssignableFrom(type));
            Assert.Equal(!perUnit, typeof(IPublishedSummaryEvent).IsAssignableFrom(type));

            var schemaProperties = document.RootElement.GetProperty("properties").EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            var required = document.RootElement.GetProperty("required").EnumerateArray().Select(element => element.GetString()!).ToHashSet(StringComparer.Ordinal);
            var recordProperties = type.GetProperties(BindingFlags.Instance | BindingFlags.Public).ToDictionary(property => SnakeCase(property.Name), StringComparer.Ordinal);

            Assert.True(recordProperties.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(schemaProperties), $"{schema}: record and schema properties differ");
            foreach (var (name, property) in recordProperties)
            {
                var isNullable = nullability.Create(property).ReadState == NullabilityState.Nullable;
                Assert.Equal(!isNullable, required.Contains(name));
            }
        }
    }

    [Fact]
    public void Every_correction_schema_accepts_a_sample_contract()
    {
        foreach (var (schema, version, _, sample, _) in Contracts)
        {
            using var schemaDocument = JsonDocument.Parse(PublishedEventSchemas.Read(schema, version));
            using var sampleDocument = JsonDocument.Parse(JsonSerializer.Serialize(sample, sample.GetType(), Json));
            var errors = Validate(sampleDocument.RootElement, schemaDocument.RootElement, schemaDocument.RootElement, "$");
            Assert.True(errors.Count == 0, $"{schema}: {string.Join("; ", errors)}");
        }
    }

    private static (string Schema, string Version, Type Type, object Sample, bool PerUnit) UnitEntry<T>(string schema, string version, Func<PublishedEventHeader, T> sample)
        where T : IPublishedUnitEvent => (schema, version, typeof(T), sample(UnitHeader(schema, version)), true);

    private static (string Schema, string Version, Type Type, object Sample, bool PerUnit) SummaryEntry<T>(string schema, string version, Func<PublishedSummaryHeader, T> sample)
        where T : IPublishedSummaryEvent => (schema, version, typeof(T), sample(SummaryHeader(schema, version)), false);

    private static PublishedEventHeader UnitHeader(string schema, string version) =>
        new(schema, version, Id(1), Id(3), Occurred, Recorded, 7, Id(4), "actor-1", new("production_order", Id(5), null));

    private static PublishedSummaryHeader SummaryHeader(string schema, string version) =>
        new(schema, version, Id(1), Occurred, Recorded, Id(4), "actor-1", new("production_order", Id(5), null));

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");

    private static List<string> Validate(JsonElement value, JsonElement schema, JsonElement root, string path)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var name = reference.GetString()!["#/$defs/".Length..];
            return Validate(value, root.GetProperty("$defs").GetProperty(name), root, path);
        }

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            var alternatives = anyOf.EnumerateArray().Select(option => Validate(value, option, root, path)).ToList();
            return alternatives.Any(errors => errors.Count == 0) ? [] : [$"{path} matches no anyOf alternative"];
        }

        var errors = new List<string>();
        if (schema.TryGetProperty("type", out var type) && !MatchesType(value, type))
        {
            errors.Add($"{path} has type {value.ValueKind}, expected {type}");
            return errors;
        }

        if (value.ValueKind == JsonValueKind.Null) return errors;
        if (schema.TryGetProperty("enum", out var values) && !values.EnumerateArray().Any(option => option.ToString() == value.ToString()))
            errors.Add($"{path} is not in the declared enum");

        if (value.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("required", out var required))
                errors.AddRange(required.EnumerateArray().Select(item => item.GetString()!).Where(name => !value.TryGetProperty(name, out _)).Select(name => $"{path}.{name} is required"));
            if (schema.TryGetProperty("properties", out var properties))
                foreach (var property in value.EnumerateObject())
                    if (properties.TryGetProperty(property.Name, out var propertySchema)) errors.AddRange(Validate(property.Value, propertySchema, root, $"{path}.{property.Name}"));
        }

        if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray()) errors.AddRange(Validate(item, items, root, $"{path}[{index++}]"));
        }

        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("format", out var format))
        {
            var text = value.GetString();
            if (format.GetString() == "uuid" && !Guid.TryParse(text, out _)) errors.Add($"{path} is not a UUID");
            if (format.GetString() == "date-time" && !DateTimeOffset.TryParse(text, out _)) errors.Add($"{path} is not a date-time");
        }

        return errors;
    }

    private static bool MatchesType(JsonElement value, JsonElement type) =>
        type.ValueKind == JsonValueKind.Array
            ? type.EnumerateArray().Any(item => MatchesType(value, item))
            : type.GetString() switch
            {
                "object" => value.ValueKind == JsonValueKind.Object,
                "array" => value.ValueKind == JsonValueKind.Array,
                "string" => value.ValueKind == JsonValueKind.String,
                "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
                "number" => value.ValueKind == JsonValueKind.Number,
                "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "null" => value.ValueKind == JsonValueKind.Null,
                _ => false
            };

    private static string SnakeCase(string value) => string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? "_" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
}
