using System.Globalization;
using System.Text.Json;
using Yojana.Integration.Coexistence;

namespace Yojana.Integration.Connectors.CX02;

internal sealed class Cx02CoexistenceDriftReader(Cx02Settings settings, Cx02Client client)
    : ICoexistenceDriftReader
{
    public string RecordType => "service_item";
    public IReadOnlyList<string> DefaultWatchedFields { get; } = ["customerNumber", "status"];
    public IReadOnlyList<string> WarrantyContractFields { get; } = [];
    public IReadOnlyList<string> DefaultExplainingFields { get; } = ["serialNumber", "itemNumber"];

    public async Task<CoexistenceRecordPage> ReadPageAsync(Guid connectionId, IReadOnlyList<string> fields,
        string? cursor, int take, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var rows = await client.ReadCollectionAsync(connectionId,
            Cx02Protocol.Collection(configured, Cx02Protocol.Entity("service_items")),
            Math.Min(take, configured.PollPageSize), configured.PageCap, cancellationToken);
        var selected = rows.Where(row => cursor is null
                || StringComparer.Ordinal.Compare(Text(row, "number"), cursor) > 0)
            .OrderBy(row => Text(row, "number"), StringComparer.Ordinal).Take(take + 1).ToList();
        var records = selected.Take(take).Select(row =>
        {
            var key = Text(row, "number") ?? Text(row, "id") ?? "";
            var values = fields.ToDictionary(field => field, field => Text(row, field), StringComparer.Ordinal);
            var label = string.Join(' ', new[] { Text(row, "itemNumber"), Text(row, "serialNumber") }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            return new CoexistenceRemoteRecord(key, label, values, Changed(row));
        }).ToList();
        return new(records, selected.Count > take ? records[^1].RecordKey : null);
    }

    private static DateTime? Changed(JsonElement row) => DateTimeOffset.TryParse(Text(row,
        "lastModifiedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
        ? value.UtcDateTime : null;

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;
}
