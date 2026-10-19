using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>
/// Where one canonical attribute comes from in the customer's company. Customers usually have their own user-defined
/// fields for drop shipment, return reasons or warranty terms, so the source is a per-connection setting rather than a
/// field name fixed in the mapping. <see cref="Field"/> null means the attribute is not used on this connection.
/// </summary>
/// <param name="TrueValues">For yes/no attributes: the stored values that mean yes (case-insensitive).</param>
/// <param name="ValueMap">For text attributes: the customer's value translated to the value this product uses;
/// values without an entry pass through unchanged.</param>
public sealed record Cx01FieldSource(
    string? Field,
    IReadOnlyList<string>? TrueValues = null,
    IReadOnlyDictionary<string, string>? ValueMap = null);

public enum Cx01MappedFieldKind
{
    YesNo,
    Text,
}

public sealed record Cx01MappedFieldDefinition(
    string Code,
    string Label,
    Cx01MappedFieldKind Kind,
    string Collection,
    string? DefaultField);

public static partial class Cx01FieldMap
{
    public const string ItemWarrantyProduct = "item.warranty_product";
    public const string ItemWarrantyTemplate = "item.warranty_template";
    public const string DeliveryDropShip = "delivery.drop_ship";
    public const string ReturnReason = "return.reason";
    public const string ReturnRmaReference = "return.rma_reference";
    public const string AdjustmentIsCount = "adjustment.is_count";

    public static readonly IReadOnlyList<string> DefaultTrueValues = ["Y", "tYES", "YES", "1", "TRUE"];

    // Defaults keep the fields an onboarding may add to the customer's company; a customer who already has an
    // equivalent field points the attribute at it instead, and a customer with neither sets it to not used.
    public static readonly IReadOnlyList<Cx01MappedFieldDefinition> Definitions =
    [
        new(ItemWarrantyProduct, "Item is a sellable warranty", Cx01MappedFieldKind.YesNo, "Items",
            "U_YojanaWarrantyProduct"),
        new(ItemWarrantyTemplate, "Item's warranty template", Cx01MappedFieldKind.Text, "Items", null),
        new(DeliveryDropShip, "Delivery is a drop shipment", Cx01MappedFieldKind.YesNo, "DeliveryNotes",
            "U_YojanaDropShip"),
        new(ReturnReason, "Return reason", Cx01MappedFieldKind.Text, "Returns", "U_YojanaReturnReason"),
        new(ReturnRmaReference, "Return authorisation number", Cx01MappedFieldKind.Text, "Returns",
            "U_YojanaRmaNumber"),
        new(AdjustmentIsCount, "Stock adjustment is a count correction", Cx01MappedFieldKind.YesNo,
            "InventoryGenExits", "U_YojanaCount"),
    ];

    private static readonly Dictionary<string, Cx01MappedFieldDefinition> ByCode =
        Definitions.ToDictionary(x => x.Code, StringComparer.Ordinal);

    public static Cx01MappedFieldDefinition Definition(string code) => ByCode.TryGetValue(code, out var found)
        ? found : throw new InvalidOperationException($"Unknown CX01 mapped field '{code}'.");

    /// <summary>Defaults overlaid with the connection's configured sources.</summary>
    public static IReadOnlyDictionary<string, Cx01FieldSource> Effective(
        IReadOnlyDictionary<string, Cx01FieldSource>? configured) =>
        Definitions.ToDictionary(x => x.Code,
            x => configured is not null && configured.TryGetValue(x.Code, out var source) ? source
                : new Cx01FieldSource(x.DefaultField), StringComparer.Ordinal);

    public static void Validate(IReadOnlyDictionary<string, Cx01FieldSource>? configured)
    {
        if (configured is null) return;
        foreach (var (code, source) in configured)
        {
            var definition = Definition(code);
            if (source.Field is not null && !FieldName().IsMatch(source.Field))
                throw new InvalidOperationException(
                    $"The CX01 field for '{code}' must be a field name of letters, digits and underscores.");
            if (definition.Kind == Cx01MappedFieldKind.YesNo && source.ValueMap is { Count: > 0 })
                throw new InvalidOperationException($"'{code}' is yes/no; use true values, not a value map.");
            if (definition.Kind == Cx01MappedFieldKind.Text && source.TrueValues is { Count: > 0 })
                throw new InvalidOperationException($"'{code}' is text; use a value map, not true values.");
        }
    }

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]{0,63}$")]
    private static partial Regex FieldName();
}

/// <summary>Reads mapped attributes from a captured document through the connection's effective field map.</summary>
internal sealed class Cx01FieldReader(IReadOnlyDictionary<string, Cx01FieldSource> map)
{
    public static readonly Cx01FieldReader Defaults = new(Cx01FieldMap.Effective(null));

    public bool YesNo(JsonElement document, string code)
    {
        var source = map[code];
        if (source.Field is null || RawText(document, source.Field) is not { } value) return false;
        var trueValues = source.TrueValues is { Count: > 0 } ? source.TrueValues : Cx01FieldMap.DefaultTrueValues;
        return trueValues.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    public string? Text(JsonElement document, string code)
    {
        var source = map[code];
        if (source.Field is null || RawText(document, source.Field) is not { } value) return null;
        value = value.Trim();
        if (value.Length == 0) return null;
        return source.ValueMap is not null && source.ValueMap.TryGetValue(value, out var translated)
            ? translated : value;
    }

    private static string? RawText(JsonElement value, string property) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;
}

public sealed record Cx01FieldMapEntry(
    string Code,
    string Label,
    Cx01MappedFieldKind Kind,
    string RecordType,
    string? DefaultField,
    Cx01FieldSource Effective,
    bool Customised,
    string? Warning,
    IReadOnlyList<string> SurveyedFields);

public sealed record Cx01FieldMapView(Guid ConnectionId, DateTime? SurveyObservedAt,
    IReadOnlyList<Cx01FieldMapEntry> Fields);

/// <summary>A null source restores the default; a source whose field is null marks the attribute not used.</summary>
public sealed record UpdateCx01FieldMap(IReadOnlyDictionary<string, Cx01FieldSource?> Fields);

public sealed class Cx01FieldMapRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}

/// <summary>Shows and changes a connection's field map, checked against the latest read-only survey.</summary>
public sealed class Cx01FieldMapService(Cx01Settings settings, Cx01SurveyService survey)
{
    public async Task<Cx01FieldMapView> GetAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var latest = await survey.LatestAsync(connectionId, cancellationToken);
        var effective = Cx01FieldMap.Effective(configured.FieldMap);
        return new(connectionId, latest?.ObservedAt, Cx01FieldMap.Definitions.Select(definition =>
        {
            var table = Cx01Protocol.TableByCollection[definition.Collection];
            var surveyed = latest?.UserFields.Where(x => x.TableName == table)
                .Select(x => x.Name.StartsWith("U_", StringComparison.Ordinal) ? x.Name : "U_" + x.Name)
                .Order(StringComparer.Ordinal).ToList() ?? [];
            var source = effective[definition.Code];
            return new Cx01FieldMapEntry(definition.Code, definition.Label, definition.Kind, definition.Collection,
                definition.DefaultField, source, configured.FieldMap?.ContainsKey(definition.Code) == true,
                Warning(source, latest, surveyed), surveyed);
        }).ToList());
    }

    public async Task<Cx01FieldMapView> SetAsync(Guid connectionId, UpdateCx01FieldMap request,
        CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var map = new Dictionary<string, Cx01FieldSource>(configured.FieldMap ?? new Dictionary<string, Cx01FieldSource>(),
            StringComparer.Ordinal);
        foreach (var (code, source) in request.Fields ?? new Dictionary<string, Cx01FieldSource?>())
        {
            if (Cx01FieldMap.Definitions.All(x => x.Code != code))
                throw new Cx01FieldMapRuleException("field_map_unknown_attribute", $"Unknown attribute '{code}'.");
            if (source is null) map.Remove(code);
            else map[code] = source with { Field = string.IsNullOrWhiteSpace(source.Field) ? null : source.Field.Trim() };
        }
        try
        {
            await settings.SetAsync(connectionId, configured with { FieldMap = map.Count == 0 ? null : map },
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex is not Cx01FieldMapRuleException)
        {
            throw new Cx01FieldMapRuleException("field_map_invalid", ex.Message);
        }
        return await GetAsync(connectionId, cancellationToken);
    }

    private static string? Warning(Cx01FieldSource source, Cx01SurveyResult? latest, IReadOnlyList<string> surveyed)
    {
        if (source.Field is null) return null;
        if (latest is null) return "No connection survey yet, so the field name is not checked.";
        // Only user-defined fields are surveyed; standard fields of the record are not listed and are not flagged.
        return source.Field.StartsWith("U_", StringComparison.Ordinal) && !surveyed.Contains(source.Field)
            ? $"Not found in the survey of {latest.ObservedAt:yyyy-MM-dd}; correct the field or run the survey again."
            : null;
    }
}
