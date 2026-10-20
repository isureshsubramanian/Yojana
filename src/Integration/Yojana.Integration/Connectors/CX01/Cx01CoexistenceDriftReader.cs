using System.Globalization;
using System.Text.Json;
using Yojana.Integration.Coexistence;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>
/// Pages through the remote equipment cards (the remote's installed-base record) for the coexistence drift scan.
/// The collection carries no update timestamp (seen on the sandbox, FP 2608), so drift is found by a resumable
/// keyset scan on the card number, never by a modified-since filter. Read-only.
/// </summary>
internal sealed class Cx01CoexistenceDriftReader(Cx01Client client) : ICoexistenceDriftReader
{
    private const string Collection = "CustomerEquipmentCards";
    private const string Key = "EquipmentCardNum";

    public string RecordType => "installed_base_record";

    // Owner (customer), card status and service-contract link are the module's during coexistence (ADR-0001).
    public IReadOnlyList<string> DefaultWatchedFields { get; } =
        ["CustomerCode", "StatusOfSerialNumber", "ContractCode", "ContractEndDate"];

    public IReadOnlyList<string> WarrantyContractFields { get; } = ["ContractCode", "ContractEndDate"];

    // A posted delivery or invoice re-points these links when it moves the card to a new owner.
    public IReadOnlyList<string> DefaultExplainingFields { get; } = ["DeliveryCode", "InvoiceCode"];

    public async Task<CoexistenceRecordPage> ReadPageAsync(Guid connectionId, IReadOnlyList<string> fields,
        string? cursor, int take, CancellationToken cancellationToken)
    {
        long after = 0;
        if (cursor is not null && !long.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out after))
            throw new InvalidOperationException("The equipment-card scan cursor is invalid.");
        // Field names were validated by CoexistenceConnectionSettings (letters, digits, underscores only).
        var select = string.Join(',', new[] { Key, "InternalSerialNum", "ItemCode" }.Concat(fields)
            .Distinct(StringComparer.Ordinal));
        var path = Cx01Protocol.Root + Collection + "?$select=" + select
            + "&$filter=" + Uri.EscapeDataString(Key + " gt " + after.ToString(CultureInfo.InvariantCulture))
            + "&$orderby=" + Key + "&$top=" + take.ToString(CultureInfo.InvariantCulture);
        using var response = await client.GetAsync(connectionId, path, cancellationToken);
        var root = response.RootElement;
        var records = new List<CoexistenceRemoteRecord>();
        long last = after;
        if (root.TryGetProperty("value", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in rows.EnumerateArray())
            {
                if (!row.TryGetProperty(Key, out var keyValue) || !keyValue.TryGetInt64(out var key)) continue;
                last = Math.Max(last, key);
                var values = fields.ToDictionary(field => field, field => Scalar(row, field), StringComparer.Ordinal);
                var label = $"{Scalar(row, "ItemCode")} {Scalar(row, "InternalSerialNum")} (card {key})".Trim();
                // The equipment-card collection exposes no reliable change timestamp. Leave it absent rather than
                // presenting this scan's observation time as the incumbent's change time.
                records.Add(new(key.ToString(CultureInfo.InvariantCulture), label, values, ChangedAt: null));
            }
        }
        // The service caps page size on its own; a next link or a full page means there may be more.
        var more = records.Count > 0 && (records.Count >= take || root.TryGetProperty("odata.nextLink", out _)
            || root.TryGetProperty("@odata.nextLink", out _));
        return new(records, more ? last.ToString(CultureInfo.InvariantCulture) : null);
    }

    private static string? Scalar(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText(),
        };
    }
}
