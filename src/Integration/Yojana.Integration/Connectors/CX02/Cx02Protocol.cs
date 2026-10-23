using System.Globalization;
using System.Text.Json;

namespace Yojana.Integration.Connectors.CX02;

internal sealed record Cx02EntityDefinition(
    string EntityType,
    string Resource,
    string CanonicalMessage,
    bool Custom,
    bool NotificationSource = true);

internal static class Cx02Protocol
{
    public const string ExtensionPublisher = "yojana";
    public const string ExtensionGroup = "integration";
    public const string ExtensionVersion = "v1.0";
    public const string Scope = "https://api.businesscentral.dynamics.com/.default";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly IReadOnlyDictionary<string, Cx02EntityDefinition> Entities =
        new Dictionary<string, Cx02EntityDefinition>(StringComparer.Ordinal)
        {
            ["items"] = new("items", "items", "ItemUpserted", false),
            ["customers"] = new("customers", "customers", "PartyUpserted", false),
            ["sales_invoices"] = new("sales_invoices", "salesInvoices", "InvoicePosted", false),
            ["sales_invoice_lines"] = new("sales_invoice_lines", "salesInvoiceLines", "InvoicePosted", false, false),
            ["sales_credit_memos"] = new("sales_credit_memos", "salesCreditMemos", "DocumentCancelled", false),
            ["sales_shipments"] = new("sales_shipments", "salesShipments", "DeliveryPosted", false, false),
            ["purchase_receipts"] = new("purchase_receipts", "purchaseReceipts", "GoodsReceiptPosted", false, false),
            ["tracked_ledger_entries"] = new("tracked_ledger_entries", "trackedItemLedgerEntries", "InventoryAdjusted", true),
            ["entry_relations"] = new("entry_relations", "itemEntryRelations", "InventoryAdjusted", true),
            ["reservations"] = new("reservations", "reservationEntries", "InventoryAdjusted", true),
            ["tracking_specifications"] = new("tracking_specifications", "trackingSpecifications", "InventoryAdjusted", true),
            ["service_items"] = new("service_items", "serviceItems", "InventoryAdjusted", true),
            ["transfer_shipments"] = new("transfer_shipments", "postedTransferShipments", "TransferPosted", true),
            ["transfer_shipment_lines"] = new("transfer_shipment_lines", "postedTransferShipmentLines", "TransferPosted", true),
            ["transfer_receipts"] = new("transfer_receipts", "postedTransferReceipts", "TransferPosted", true),
            ["transfer_receipt_lines"] = new("transfer_receipt_lines", "postedTransferReceiptLines", "TransferPosted", true),
            ["return_receipts"] = new("return_receipts", "postedReturnReceipts", "ReturnReceived", true),
            ["return_receipt_lines"] = new("return_receipt_lines", "postedReturnReceiptLines", "ReturnReceived", true),
        };

    public static readonly IReadOnlyList<Cx02EntityDefinition> PollEntities =
        Entities.Values.Where(entity => entity.EntityType is not
            ("entry_relations" or "reservations" or "tracking_specifications" or "service_items"
            or "sales_invoice_lines" or "sales_shipments" or "purchase_receipts" or "transfer_shipment_lines"
            or "transfer_receipt_lines" or "return_receipt_lines"))
            .ToList();

    public static readonly IReadOnlyList<Cx02EntityDefinition> NotificationEntities =
        Entities.Values.Where(entity => entity.Custom && entity.NotificationSource).ToList();

    public static Cx02EntityDefinition Entity(string value)
    {
        if (Entities.TryGetValue(value, out var direct)) return direct;
        return Entities.Values.FirstOrDefault(entity => entity.Resource == value)
            ?? throw new InvalidOperationException($"CX02 entity type '{value}' is not supported.");
    }

    public static Uri StandardRoot(Cx02ConnectionSettings settings) => new(settings.ServiceUri,
        $"v2.0/{Uri.EscapeDataString(settings.TenantSegment)}/{Uri.EscapeDataString(settings.Environment)}/api/v2.0/");

    public static Uri CustomRoot(Cx02ConnectionSettings settings) => new(settings.ServiceUri,
        $"v2.0/{Uri.EscapeDataString(settings.TenantSegment)}/{Uri.EscapeDataString(settings.Environment)}/api/"
        + $"{ExtensionPublisher}/{ExtensionGroup}/{ExtensionVersion}/");

    public static Uri RuntimeRoot(Cx02ConnectionSettings settings) => new(settings.ServiceUri,
        $"v2.0/{Uri.EscapeDataString(settings.TenantSegment)}/{Uri.EscapeDataString(settings.Environment)}/api/microsoft/runtime/v1.0/");

    public static Uri TokenUri(Cx02ConnectionSettings settings) => settings.TokenUri
        ?? new Uri($"https://login.microsoftonline.com/{Uri.EscapeDataString(settings.DirectoryTenant)}/oauth2/v2.0/token");

    public static Uri Collection(Cx02ConnectionSettings settings, Cx02EntityDefinition entity) =>
        new(entity.Custom ? CustomRoot(settings) : StandardRoot(settings),
            $"companies({settings.CompanyId:D})/{entity.Resource}");

    public static Uri Subscriptions(Cx02ConnectionSettings settings, bool custom) =>
        new(custom ? CustomRoot(settings) : StandardRoot(settings), "subscriptions");

    public static string SubscriptionResource(Cx02ConnectionSettings settings, Cx02EntityDefinition entity) =>
        entity.Custom
            ? $"/api/{ExtensionPublisher}/{ExtensionGroup}/{ExtensionVersion}/companies({settings.CompanyId:D})/{entity.Resource}"
            : $"/api/v2.0/companies({settings.CompanyId:D})/{entity.Resource}";

    public static string ModifiedSince(DateTimeOffset value) =>
        "$filter=" + Uri.EscapeDataString("lastModifiedDateTime gt "
            + value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
}
