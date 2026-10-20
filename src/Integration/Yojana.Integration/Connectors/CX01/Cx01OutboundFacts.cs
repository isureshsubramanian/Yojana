using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Connectors.CX01;

public sealed record Cx01OutboundUnit(string ItemCode, string Serial, string? SystemSerialNumber = null, string? Location = null);

/// <summary>Resolves module references without exposing another module's storage to document mappers.</summary>
public interface ICx01OutboundFacts
{
    Task<Cx01OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, CancellationToken ct, string? suppliedSerial = null);
    Task<string?> ReferenceAsync(Guid connectionId, string kind, string reference, CancellationToken ct);
}

internal sealed class Cx01OutboundFacts(IntegrationDbContext db, ITenantContext tenant,
    ICertificateUnitReadPort units, Cx01Client client) : ICx01OutboundFacts
{
    public async Task<Cx01OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, CancellationToken ct, string? suppliedSerial = null)
    {
        var fact = await units.ReadAsync(unitId, ct);
        if (fact is null) return null;
        var item = await ReferenceAsync(connectionId, "item", fact.ItemId.ToString(), ct);
        var serials = await units.PrimarySerialsAsync([unitId], ct);
        var serial = string.IsNullOrWhiteSpace(suppliedSerial) ? serials.GetValueOrDefault(unitId) : suppliedSerial;
        if (item is null || string.IsNullOrWhiteSpace(serial)) return null;
        using var response = await client.GetAsync(connectionId, Cx01Protocol.Root + "SerialNumberDetails?$filter="
            + Cx01ReviewKey.FilterEquals("ItemCode", item) + "%20and%20" + Cx01ReviewKey.FilterEquals("SerialNumber", serial) + "&$top=2", ct);
        var matches = response.RootElement.GetProperty("value");
        if (matches.GetArrayLength() > 1) throw new Cx01DocumentRuleException("The remote serial is ambiguous for this item.");
        return new(item, serial, matches.GetArrayLength() == 0 ? null : Cx01ReviewKey.Text(matches[0], "SystemNumber"));
    }

    public async Task<string?> ReferenceAsync(Guid connectionId, string kind, string reference, CancellationToken ct)
    {
        if (reference.StartsWith("unit-owner/", StringComparison.Ordinal) && Guid.TryParse(reference[11..], out var unitId))
        {
            var unit = await units.ReadAsync(unitId, ct);
            return unit?.Ownership?.HolderRef is { } party
                ? await ReferenceAsync(connectionId, "party", party.ToString(), ct) : null;
        }
        var qualified = reference.Split(':', 2);
        var collection = qualified.Length == 2 ? qualified[0] : null;
        var candidate = qualified.Length == 2 ? qualified[1] : reference;
        if (Guid.TryParse(candidate.Split('/').Last(), out var id))
        {
            var query = db.ExternalIdMaps.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.EntityType == kind && x.ModuleId == id && x.ValidTo == null);
            if (collection is not null) query = query.Where(x => x.ExternalId.StartsWith(collection + ":"));
            var matches = await query.Select(x => x.ExternalId).Take(2).ToListAsync(ct);
            if (matches.Count > 1) throw new Cx01UnmappableException("The document reference is ambiguous; specify its remote collection.");
            return matches.FirstOrDefault();
        }
        return string.IsNullOrWhiteSpace(reference) || reference == "owner" ? null : reference;
    }
}

internal sealed class Cx01UnmappableException(string message) : Exception(message);
internal static class Cx01DocumentBody
{
    public static Dictionary<string, object?> Line(Cx01OutboundUnit unit, decimal quantity = 1)
    {
        if (quantity != 1) throw new Cx01UnmappableException("A serial-managed line requires exactly one serial per unit of quantity.");
        var serial = new Dictionary<string, object?> { ["InternalSerialNumber"] = unit.Serial, ["Quantity"] = 1m };
        if (unit.SystemSerialNumber is { } system)
        {
            if (!int.TryParse(system, out var number)) throw new Cx01UnmappableException("The mapped system serial number is invalid.");
            serial["SystemSerialNumber"] = number;
        }
        return new() { ["ItemCode"] = unit.ItemCode, ["Quantity"] = quantity, ["SerialNumbers"] = new[] { serial } };
    }

    public static int Entry(string reference)
    {
        if (!int.TryParse(reference.Split(':').Last(), out var value) || value <= 0)
            throw new Cx01UnmappableException("The base document reference must resolve to a numeric remote document key.");
        return value;
    }

    public static void Base(Dictionary<string, object?> line, int type, string? entry, int? baseLine)
    {
        if (entry is null) return;
        if (baseLine is null or < 0) throw new Cx01UnmappableException("The base document line number is required; it cannot be inferred from line order.");
        var expected = type == 16 ? "Returns" : "Invoices";
        if (entry.Contains(':', StringComparison.Ordinal) && entry.Split(':')[0] != expected)
            throw new Cx01UnmappableException("The base document reference resolves to the wrong document family.");
        line["BaseType"] = type;
        line["BaseEntry"] = Entry(entry);
        line["BaseLine"] = baseLine;
    }

    public static void Warehouse(Dictionary<string, object?> line, Cx01Warehouse location, bool transfer = false, bool from = false)
    {
        line[from ? "FromWarehouseCode" : "WarehouseCode"] = location.Warehouse;
        if (location.Bin is not { } bin) return;
        var serialCount = line.TryGetValue("SerialNumbers", out var serialValue)
            && serialValue is Dictionary<string, object?>[] serials ? serials.Length : 0;
        var allocations = new List<Dictionary<string, object?>>();
        for (var index = 0; index < Math.Max(1, serialCount); index++)
        {
            var allocation = new Dictionary<string, object?>
            {
                ["BinAbsEntry"] = bin,
                ["Quantity"] = serialCount > 0 ? 1m : line["Quantity"],
            };
            if (serialCount > 0) allocation["SerialAndBatchNumbersBaseLine"] = index;
            if (transfer) allocation["BinActionType"] = from ? "batFromWarehouse" : "batToWarehouse";
            allocations.Add(allocation);
        }
        var key = transfer ? "StockTransferLinesBinAllocations" : "DocumentLinesBinAllocations";
        if (line.TryGetValue(key, out var existing)) ((List<Dictionary<string, object?>>)existing!).AddRange(allocations);
        else line[key] = allocations;
    }
}
