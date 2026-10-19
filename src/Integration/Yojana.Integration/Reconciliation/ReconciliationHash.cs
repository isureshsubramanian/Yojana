using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yojana.Integration.Reconciliation;

public static class ReconciliationHash
{
    private static readonly HashSet<string> KeyFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "number", "date", "external_id", "line", "line_no", "erp_line_id",
        "item_external_id", "party_external_id", "customer_party_external_id",
        "vendor_party_external_id", "produced_item_external_id", "warranty_product_external_id",
        "from_location_external_id", "to_location_external_id", "location_external_id",
        "quantity", "uom", "value", "issuer", "erp_serial_id", "expiry_date",
        "covered_unit_serials",
    };

    /// <summary>
    /// Connector and inbound-store sides use this exact framing: sorted external ids plus selected canonical identity,
    /// document, line, quantity, location and serial fields, separated by length prefixes. Non-key payload changes do
    /// not create a reconciliation mismatch.
    /// </summary>
    public static string KeyFields(IEnumerable<(string ExternalId, string CanonicalJson)> records)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var record in records.OrderBy(row => row.ExternalId, StringComparer.Ordinal)
                     .ThenBy(row => row.CanonicalJson, StringComparer.Ordinal))
        {
            Append(hash, record.ExternalId);
            Append(hash, KeyFieldProjection(record.CanonicalJson));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static (decimal Quantity, long DistinctSerials) Metrics(IEnumerable<string> canonicalMessages)
    {
        decimal quantity = 0;
        var serials = new HashSet<string>(StringComparer.Ordinal);
        foreach (var message in canonicalMessages)
        {
            using var document = JsonDocument.Parse(message);
            Visit(document.RootElement, ref quantity, serials, null);
        }
        return (quantity, serials.Count);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static string KeyFieldProjection(string canonicalJson)
    {
        using var document = JsonDocument.Parse(canonicalJson);
        var values = new List<string>();
        Collect(document.RootElement, "$", null, values);
        return string.Join('\n', values);
    }

    private static void Collect(JsonElement element, string path, string? propertyName,
        List<string> values)
    {
        if (propertyName is not null && KeyFieldNames.Contains(propertyName)
            && element.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            values.Add($"{path}={element.GetRawText()}");
            return;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject().OrderBy(row => row.Name, StringComparer.Ordinal))
                Collect(property.Value, $"{path}.{property.Name}", property.Name, values);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var child in element.EnumerateArray())
                Collect(child, $"{path}[{index++}]", propertyName, values);
        }
    }

    private static void Visit(JsonElement element, ref decimal quantity, HashSet<string> serials,
        string? propertyName)
    {
        if (propertyName == "quantity" && element.ValueKind == JsonValueKind.Number
            && element.TryGetDecimal(out var value)) quantity += value;
        if (propertyName is "serials" or "covered_unit_serials" && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var serial in element.EnumerateArray())
            {
                if (serial.ValueKind == JsonValueKind.String && serial.GetString() is { Length: > 0 } text)
                    serials.Add(text);
                else if (serial.ValueKind == JsonValueKind.Object
                    && serial.TryGetProperty("value", out var field) && field.GetString() is { Length: > 0 } objectText)
                    serials.Add(objectText);
            }
        }
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                Visit(property.Value, ref quantity, serials, property.Name);
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Visit(child, ref quantity, serials, propertyName);
    }
}
