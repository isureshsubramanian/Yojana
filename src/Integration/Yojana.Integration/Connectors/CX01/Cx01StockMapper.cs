namespace Yojana.Integration.Connectors.CX01;

/// <summary>StockTransfers uses StockTransferLines; InventoryGenEntries/InventoryGenExits use DocumentLines.
/// Each requires item, quantity, warehouse and SerialNumbers.InternalSerialNumber/SystemSerialNumber; bin allocations
/// point to serial index zero. A status posting pair is enabled only after a per-installation test.</summary>
internal static class Cx01StockMapper
{
    public static Dictionary<string, object?> Transfer(Cx01OutboundUnit unit, Cx01Warehouse from, Cx01Warehouse to)
    {
        var line = Cx01DocumentBody.Line(unit);
        Cx01DocumentBody.Warehouse(line, from, true, true);
        Cx01DocumentBody.Warehouse(line, to, true);
        return new() { ["FromWarehouse"] = from.Warehouse, ["ToWarehouse"] = to.Warehouse, ["StockTransferLines"] = new[] { line } };
    }

    public static Dictionary<string, object?> Adjustment(Cx01OutboundUnit unit, Cx01Warehouse location, string account)
    {
        var line = Cx01DocumentBody.Line(unit);
        Cx01DocumentBody.Warehouse(line, location);
        line["AccountCode"] = account;
        return new() { ["DocumentLines"] = new[] { line } };
    }
}
