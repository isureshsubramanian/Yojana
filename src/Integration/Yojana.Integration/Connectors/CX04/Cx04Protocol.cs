using System.Globalization;
using System.Text.Json;

namespace Yojana.Integration.Connectors.CX04;

public sealed record Cx04EntityDefinition(string EntityType, string Endpoint, string Resource,
    string CanonicalMessage, string KeyField, string ModifiedField, string? TypeValue = null);

internal static class Cx04Protocol
{
    public const string EndpointVersion = "24.200.001";
    public const string ManufacturingEndpointVersion = "24.200.001";
    public const string Scope = "api offline_access";
    public const string NotificationSecretHeader = "X-Yojana-CX04-Secret";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
    };

    public static readonly IReadOnlyDictionary<string, Cx04EntityDefinition> Entities =
        new Dictionary<string, Cx04EntityDefinition>(StringComparer.Ordinal)
        {
            ["items"] = new("items", "Default", "InventoryItem", "ItemUpserted", "InventoryID", "LastModifiedDateTime"),
            ["parties"] = new("parties", "Default", "Customer", "PartyUpserted", "CustomerID", "LastModifiedDateTime"),
            ["receipts"] = new("receipts", "Default", "PurchaseReceipt", "GoodsReceiptPosted", "ReceiptNbr", "LastModifiedDateTime", "Receipt"),
            ["transfer_receipts"] = new("transfer_receipts", "Default", "PurchaseReceipt", "TransferPosted", "ReceiptNbr", "LastModifiedDateTime", "Transfer Receipt"),
            ["shipments"] = new("shipments", "Default", "Shipment", "DeliveryPosted", "ShipmentNbr", "LastModifiedDateTime"),
            ["invoices"] = new("invoices", "Default", "Invoice", "InvoicePosted", "ReferenceNbr", "LastModifiedDateTime"),
            ["returns"] = new("returns", "Default", "SalesOrder", "ReturnReceived", "OrderNbr", "LastModified"),
            ["adjustments"] = new("adjustments", "Default", "InventoryAdjustment", "InventoryAdjusted", "ReferenceNbr", "LastModifiedDateTime"),
            ["production_completions"] = new("production_completions", "MANUFACTURING", "MoveEntry", "ProductionCompleted", "ReferenceNbr", "LastModifiedDateTime"),
        };

    public static Cx04EntityDefinition Entity(string value) => Entities.TryGetValue(value, out var direct)
        ? direct : Entities.Values.FirstOrDefault(entity => entity.Resource == value)
        ?? throw new InvalidOperationException($"CX04 entity type '{value}' is not supported.");

    public static Uri Authorise(Cx04ConnectionSettings settings, string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = settings.ClientId,
            ["redirect_uri"] = settings.RedirectUri.ToString(), ["scope"] = Scope,
            ["state"] = state, ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
        };
        return new Uri(settings.ServiceUri, "identity/connect/authorize?" + string.Join('&', query.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value))));
    }

    public static Uri Token(Cx04ConnectionSettings settings) => new(settings.ServiceUri, "identity/connect/token");
    public static Uri Logout(Cx04ConnectionSettings settings) => new(settings.ServiceUri, "entity/auth/logout");
    public static Uri Root(Cx04ConnectionSettings settings, Cx04EntityDefinition entity) => new(settings.ServiceUri,
        $"entity/{entity.Endpoint}/{(entity.Endpoint == "Default" ? settings.EndpointVersion : settings.ManufacturingEndpointVersion)}/");
    public static Uri Collection(Cx04ConnectionSettings settings, Cx04EntityDefinition entity) =>
        new(Root(settings, entity), entity.Resource);
    public static Uri Record(Cx04ConnectionSettings settings, Cx04EntityDefinition entity, string key) =>
        new(Root(settings, entity), entity.Resource + "/" + Uri.EscapeDataString(key));
    public static string ModifiedSince(Cx04EntityDefinition entity, DateTimeOffset value) =>
        "$filter=" + Uri.EscapeDataString(entity.ModifiedField + " gt datetimeoffset'"
            + value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) + "'");
}
