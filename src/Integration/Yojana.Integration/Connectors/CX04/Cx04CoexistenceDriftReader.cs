using System.Text.Json;
using Yojana.Integration.Coexistence;

namespace Yojana.Integration.Connectors.CX04;

internal sealed class Cx04CoexistenceDriftReader(Cx04Settings settings, Cx04Client client)
    : ICoexistenceDriftReader
{
    public string RecordType => "inventory_item";
    public IReadOnlyList<string> DefaultWatchedFields { get; } = ["Description", "ItemStatus"];
    public IReadOnlyList<string> WarrantyContractFields { get; } = [];
    public IReadOnlyList<string> DefaultExplainingFields { get; } = ["InventoryID"];

    public async Task<CoexistenceRecordPage> ReadPageAsync(Guid connectionId, IReadOnlyList<string> fields,
        string? cursor, int take, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        var entity = Cx04Protocol.Entity("items");
        var rows = await client.ReadCollectionAsync(connectionId, Cx04Protocol.Collection(configured, entity),
            Math.Min(take, configured.PollPageSize), configured.PageCap, ct);
        var selected = rows.Where(row => cursor is null
                || StringComparer.Ordinal.Compare(Cx04Mapper.Text(row, entity.KeyField), cursor) > 0)
            .OrderBy(row => Cx04Mapper.Text(row, entity.KeyField), StringComparer.Ordinal).Take(take + 1).ToList();
        var records = selected.Take(take).Select(row =>
        {
            var key = Cx04Mapper.Text(row, entity.KeyField) ?? Cx04Mapper.Text(row, "id") ?? "";
            var values = fields.ToDictionary(field => field, field => Cx04Mapper.Text(row, field),
                StringComparer.Ordinal);
            return new CoexistenceRemoteRecord(key, Cx04Mapper.Text(row, "Description") ?? key, values,
                DateTime.TryParse(Cx04Mapper.Text(row, entity.ModifiedField), out var changed) ? changed : null);
        }).ToList();
        return new(records, selected.Count > take ? records[^1].RecordKey : null);
    }
}
