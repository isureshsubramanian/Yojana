namespace Yojana.Integration.LegacyMigration.Staging;

/// <summary>
/// Source-agnostic lifecycle movement names emitted by the staging boundary. Reconstruction consumes these names
/// and never interprets the incumbent's numeric document types.
/// </summary>
public static class SourceMovementKind
{
    public const string Received = "received";
    public const string Produced = "produced";
    public const string Consumed = "consumed";
    public const string Shipped = "shipped";
    public const string CustomerReturned = "customer_returned";
    public const string VendorReturned = "vendor_returned";
    public const string TransferredOut = "transferred_out";
    public const string TransferredIn = "transferred_in";
    public const string IssuedOut = "issued_out";
    public const string Ignored = "ignored";
    public const string Unknown = "unknown";
}

internal sealed record SourceDocumentMovement(
    string Kind,
    string DocumentLabel,
    string CancellationFamily,
    string? BaseCancellationFamily,
    bool SuppressWhenBasedOnCustomerReturn);

/// <summary>
/// The incumbent's numeric document types are protocol facts and therefore live only at the staging boundary.
/// </summary>
internal static class SourceDocumentTypes
{
    public static SourceDocumentMovement Map(int? documentType, decimal quantity, int? stockEffect,
        int? baseType)
    {
        var label = Label(documentType);
        var family = Family(documentType);
        var baseFamily = baseType is null or -1 ? null : Family(baseType);
        if (stockEffect != 1)
            return new(SourceMovementKind.Ignored, label, family, baseFamily, false);

        var kind = (documentType, Math.Sign(quantity)) switch
        {
            (20 or 59 or 18, 1) => SourceMovementKind.Received,
            (202, 1) => SourceMovementKind.Produced,
            (202, -1) => SourceMovementKind.Consumed,
            (15 or 13, -1) => SourceMovementKind.Shipped,
            (16, 1) => SourceMovementKind.CustomerReturned,
            (14, 1) => SourceMovementKind.CustomerReturned,
            (21, -1) => SourceMovementKind.VendorReturned,
            (67, -1) => SourceMovementKind.TransferredOut,
            (67, 1) => SourceMovementKind.TransferredIn,
            (60, -1) => SourceMovementKind.IssuedOut,
            _ => SourceMovementKind.Unknown,
        };
        return new(kind, label, family, baseFamily,
            documentType == 14 && baseType == 16 && quantity > 0m);
    }

    private static string Family(int? documentType) => documentType switch
    {
        20 => "purchase_receipt",
        59 => "inventory_receipt",
        18 => "supplier_invoice",
        202 => "production_order",
        15 => "delivery",
        13 => "customer_invoice",
        16 => "customer_return",
        14 => "customer_credit",
        21 => "supplier_return",
        67 => "inventory_transfer",
        60 => "inventory_issue",
        null => "missing_document_type",
        _ => $"source_document_{documentType.Value}",
    };

    private static string Label(int? documentType) => documentType switch
    {
        20 => "Purchase goods receipt",
        59 => "Inventory receipt",
        18 => "Supplier invoice receipt",
        202 => "Production order",
        15 => "Delivery",
        13 => "Customer invoice",
        16 => "Customer return",
        14 => "Customer credit memo",
        21 => "Supplier return",
        67 => "Inventory transfer",
        60 => "Inventory issue",
        null => "Unknown source document",
        _ => $"Unknown source document type {documentType.Value}",
    };
}
