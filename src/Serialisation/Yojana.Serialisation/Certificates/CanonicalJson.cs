using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yojana.Serialisation.Certificates;

/// <summary>Stable certificate JSON: ordinal key order, compact UTF-8, invariant numbers and UTC ISO-8601 times.</summary>
public static class CanonicalJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Converters = { new UtcDateTimeConverter() },
    };

    public static string Serialise<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, Options);
        return Canonicalise(element);
    }

    public static string Serialize<T>(T value) => Serialise(value);

    public static string Canonicalise(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Canonicalise(document.RootElement);
    }

    public static string Hash(string canonicalJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    public static string HashExcludingRootProperties(string canonicalJson, params string[] excludedProperties)
    {
        using var document = JsonDocument.Parse(canonicalJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("A certificate payload must be a JSON object.", nameof(canonicalJson));
        var excluded = excludedProperties.ToHashSet(StringComparer.Ordinal);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject()
                         .Where(property => !excluded.Contains(property.Name))
                         .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                Write(writer, property.Value);
            }
            writer.WriteEndObject();
        }
        return Hash(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private static string Canonicalise(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            Write(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    Write(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray()) Write(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON token {element.ValueKind}.");
        }
    }

    private sealed class UtcDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Utc(DateTime.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(Utc(value).ToString("O", CultureInfo.InvariantCulture));

        private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();
    }
}
