using System.Globalization;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>Foreign wire names are centralised here so no gateway or module code learns them.</summary>
internal static class Cx01Protocol
{
    public const string Root = "/b1s/v2/";
    public const string Login = Root + "Login";
    public const string Logout = Root + "Logout";
    public const string Metadata = Root + "$metadata";
    public const string SessionCookie = "B1SESSION";
    public const string RouteCookie = "ROUTEID";
    public const string HandshakeTokenHeader = "X-B1-Webhook-Token";
    public const string SignatureHeader = "X-B1-Webhook-Signature";
    public const string EventSubscriptions = "EventSubscriptions";
    public const string EventNotifications = "EventNotifications";
    public const string SerialNumberDetails = "SerialNumberDetails";
    public const string SqlQueries = "SQLQueries";

    public static readonly IReadOnlyDictionary<string, Cx01EntityDefinition> Entities =
        new Dictionary<string, Cx01EntityDefinition>(StringComparer.Ordinal)
        {
            ["items"] = new("Items", "ItemCode", "ItemUpserted"),
            ["business_partners"] = new("BusinessPartners", "CardCode", "PartyUpserted"),
            ["deliveries"] = new("DeliveryNotes", "DocEntry", "DeliveryPosted"),
            ["returns"] = new("Returns", "DocEntry", "ReturnReceived"),
            ["invoices"] = new("Invoices", "DocEntry", "InvoicePosted"),
            ["goods_receipts"] = new("PurchaseDeliveryNotes", "DocEntry", "GoodsReceiptPosted"),
            ["production_receipts"] = new("InventoryGenEntries", "DocEntry", "ProductionCompleted"),
            ["inventory_transfers"] = new("StockTransfers", "DocEntry", "TransferPosted"),
            ["inventory_adjustments"] = new("InventoryGenExits", "DocEntry", "InventoryAdjusted"),
            ["cancellations"] = new("DocumentCancellationRequests", "DocEntry", "DocumentCancelled"),
        };

    public static string Collection(Cx01EntityDefinition entity) => Root + entity.Collection;

    public static string ByKey(Cx01EntityDefinition entity, string key) =>
        Collection(entity) + "(" + KeyLiteral(key) + ")";

    public static string KeyLiteral(string value) => long.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out _) ? value : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

internal sealed record Cx01EntityDefinition(string Collection, string KeyField, string CanonicalMessage);
