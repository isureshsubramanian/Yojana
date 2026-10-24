using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>Foreign wire names are centralised here so no gateway or module code learns them.</summary>
internal static class Cx01Protocol
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
    };
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
    public const string SqlQueries = "SQLQueries";
    public const string SerialLinkQueryCode = "YOJANA_SERIAL_LINK";
    public const string AdminInfo = "CompanyService_GetAdminInfo";
    public const string UserFields = "UserFieldsMD";
    public const string ApprovalTemplates = "ApprovalTemplates";
    public const string Drafts = "Drafts";

    /// <summary>
    /// The request path for a page link. The live service returns <c>@odata.nextLink</c> relative to the service
    /// root ("Items?$skip=20"); requests resolve against the host, so a bare link must be put back under Root.
    /// </summary>
    public static string? NextPage(string? link) =>
        string.IsNullOrWhiteSpace(link) ? null
        : Uri.TryCreate(link, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
            ? absolute.PathAndQuery
        : link.StartsWith('/') ? link : Root + link;

    /// <summary>
    /// The calendar date of a remote date field. Version 2 of the service returns dates as midnight timestamps
    /// ("2026-10-24T00:00:00Z"), not bare dates, so only the leading yyyy-MM-dd is the date.
    /// </summary>
    public static string? DatePart(string? value) =>
        value is { Length: >= 10 } && DateOnly.TryParseExact(value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out _) ? value[..10] : null;

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
        };

    public static readonly IReadOnlyDictionary<string, string> TableByCollection =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Items"] = "OITM",
            ["BusinessPartners"] = "OCRD",
            ["DeliveryNotes"] = "ODLN",
            ["Returns"] = "ORDN",
            ["Invoices"] = "OINV",
            ["PurchaseDeliveryNotes"] = "OPDN",
            ["InventoryGenEntries"] = "OIGN",
            ["StockTransfers"] = "OWTR",
            ["InventoryGenExits"] = "OIGE",
            ["CustomerEquipmentCards"] = "OINS",
            ["CreditNotes"] = "ORIN",
            ["JournalEntries"] = "OJDT",
            ["ServiceCalls"] = "OSCL",
        };

    public static string Collection(Cx01EntityDefinition entity) => Root + entity.Collection;

    public static string ByKey(Cx01EntityDefinition entity, string key) =>
        Collection(entity) + "(" + KeyLiteral(key) + ")";

    // The remote key accepts a short client-chosen string. Twenty ASCII characters keeps the identifier well below
    // observed installation limits; 72 digest bits plus the YJ prefix make it stable per tenant connection.
    public static string WebhookId(Guid tenantId, Guid connectionId)
    {
        var input = Encoding.UTF8.GetBytes(tenantId.ToString("N") + connectionId.ToString("N"));
        return "YJ" + Convert.ToHexString(SHA256.HashData(input))[..18];
    }

    public static string WebhookAlgorithm(string configured) => configured.ToUpperInvariant() switch
    {
        "HMACSHA384" => "HMACSha384",
        "HMACSHA512" => "HMACSha512",
        _ => "HMACSha256",
    };

    public static string ObjectType(Cx01EntityDefinition entity) => entity.Collection switch
    {
        "DeliveryNotes" => "15",
        "Returns" => "16",
        "Invoices" => "13",
        "CreditNotes" => "14",
        "Orders" => "17",
        "PurchaseReturns" => "21",
        "PurchaseDeliveryNotes" => "20",
        "InventoryGenEntries" => "59",
        "StockTransfers" => "67",
        "InventoryGenExits" => "60",
        _ => entity.Collection,
    };

    public static string KeyLiteral(string value) => long.TryParse(value, NumberStyles.Integer,
        CultureInfo.InvariantCulture, out _) ? value : "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}

internal sealed record Cx01EntityDefinition(string Collection, string KeyField, string CanonicalMessage);
