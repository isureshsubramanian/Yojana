using System.Globalization;
using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Integration.Inbox;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX04;

public static class Cx04Mapper
{
    public static InboundAcceptance Map(TenantId tenantId, Guid connectionId, Cx04EntityDefinition entity,
        JsonElement raw, string captureMode, DateTime observedUtc, Cx04AllocationQuantityMode quantityMode)
    {
        var externalId = Text(raw, entity.KeyField) ?? Text(raw, "id")
            ?? throw new InvalidOperationException("CX04 response is missing its external identity.");
        var marker = Text(raw, entity.ModifiedField) ?? Text(raw, "rowNumber")
            ?? observedUtc.ToString("O", CultureInfo.InvariantCulture);
        var occurred = Date(raw, "Date") ?? Date(raw, "ShipmentDate") ?? Date(raw, "ReceiptDate")
            ?? Date(raw, entity.ModifiedField) ?? new DateTimeOffset(observedUtc, TimeSpan.Zero);
        var (messageType, payload, completeness) = Payload(entity, raw, externalId, occurred, quantityMode);
        var envelope = new CanonicalEnvelope(IntegrationContracts.CurrentVersion, Guid.CreateVersion7(observedUtc),
            tenantId.Value, connectionId, Cx04Capabilities.ConnectorCode, entity.Resource, externalId,
            new(marker, OpenEnum.Known(Date(raw, entity.ModifiedField) is null ? "row_version" : "timestamp")),
            occurred, new DateTimeOffset(observedUtc, TimeSpan.Zero), OpenEnum.Known(captureMode),
            OpenEnum.Known(completeness), $"cx04/{entity.Resource}/{externalId}/{marker}");
        var canonical = JsonSerializer.Serialize(new { envelope, message_type = messageType, payload },
            IntegrationContracts.Json);
        return new(envelope, messageType, canonical, raw.GetRawText());
    }

    private static (string MessageType, object Payload, string Completeness) Payload(
        Cx04EntityDefinition entity, JsonElement raw, string externalId, DateTimeOffset occurred,
        Cx04AllocationQuantityMode quantityMode)
    {
        if (entity.Resource == "InventoryItem")
        {
            var tracking = (Text(raw, "LotSerialClass") ?? Text(raw, "TrackingMode") ?? "none").ToLowerInvariant();
            var mode = tracking.Contains("serial", StringComparison.Ordinal) ? "serial"
                : tracking.Contains("lot", StringComparison.Ordinal) ? "lot" : "none";
            return (nameof(ItemUpserted), new ItemUpserted(Text(raw, "InventoryID") ?? externalId,
                Text(raw, "Description") ?? externalId, OpenEnum.Known(mode), Text(raw, "BaseUOM") ?? "EA",
                Bool(raw, "WarrantyItem"), Text(raw, "WarrantyTemplate"), Bool(raw, "Inactive")), "complete");
        }
        if (entity.Resource == "Customer")
            return (nameof(PartyUpserted), new PartyUpserted(Text(raw, "CustomerID") ?? externalId,
                OpenEnum.Known("customer"), Text(raw, "CustomerName") ?? externalId,
                Text(raw, "TaxRegistrationID"), Text(raw, "Country") ?? "", null, Bool(raw, "Inactive")),
                "complete");
        if (Bool(raw, "Cancelled") || Bool(raw, "Voided"))
            return (nameof(DocumentCancelled), new DocumentCancelled(
                Document(raw, DocumentType(entity), externalId, occurred), OpenEnum.Known("cancelled")), "complete");

        var lines = Lines(raw, quantityMode);
        var completeness = lines.Count == 0 ? "unknown"
            : lines.Any(line => line.Quantity != 0 && line.Serials.Count == 0 && line.Lots.Count == 0)
                ? "partial" : "complete";
        return entity.CanonicalMessage switch
        {
            nameof(GoodsReceiptPosted) => (nameof(GoodsReceiptPosted), new GoodsReceiptPosted(
                Document(raw, "receipt", externalId, occurred), Text(raw, "VendorID") ?? "", lines,
                Reference(raw, "POOrderNbr", "purchase_order", occurred)), completeness),
            nameof(DeliveryPosted) => (nameof(DeliveryPosted), new DeliveryPosted(
                Document(raw, "delivery", externalId, occurred), Text(raw, "CustomerID") ?? "",
                Text(raw, "ShipToID") ?? "", lines, Reference(raw, "OrderNbr", "sales_order", occurred)),
                completeness),
            nameof(InvoicePosted) => (nameof(InvoicePosted), new InvoicePosted(
                Document(raw, "invoice", externalId, occurred), Text(raw, "CustomerID") ?? "", [], lines,
                new(Decimal(raw, "Amount", "Balance"), Text(raw, "CurrencyID") ?? "")), completeness),
            nameof(ReturnReceived) => (nameof(ReturnReceived), new ReturnReceived(
                Document(raw, "return_receipt", externalId, occurred), Text(raw, "CustomerID") ?? "", lines,
                Reference(raw, "OriginalOrderNbr", "delivery", occurred), Text(raw, "ReasonCode"),
                Text(raw, "OrderNbr")), completeness),
            nameof(TransferPosted) => (nameof(TransferPosted), new TransferPosted(
                Document(raw, "transfer", externalId, occurred), Text(raw, "FromWarehouse") ?? "",
                Text(raw, "Warehouse") ?? Text(raw, "ToWarehouse") ?? "", lines), completeness),
            nameof(InventoryAdjusted) => (nameof(InventoryAdjusted), new InventoryAdjusted(
                Document(raw, "adjustment", externalId, occurred), OpenEnum.Known("count"), lines,
                Text(raw, "ReasonCode")), completeness),
            nameof(ProductionCompleted) => (nameof(ProductionCompleted), new ProductionCompleted(
                Document(raw, "production_receipt", externalId, occurred),
                new("production_order", Text(raw, "ProductionNbr") ?? externalId,
                    DateOnly.FromDateTime(occurred.Date)), Text(raw, "InventoryID") ?? "", lines, []), completeness),
            _ => throw new InvalidOperationException($"CX04 resource '{entity.Resource}' cannot be canonicalised."),
        };
    }

    private static List<CanonicalLine> Lines(JsonElement raw, Cx04AllocationQuantityMode quantityMode)
    {
        if (!raw.TryGetProperty("Details", out var details) || details.ValueKind != JsonValueKind.Array) return [];
        return details.EnumerateArray().Select((line, index) =>
        {
            var allocations = line.TryGetProperty("Allocations", out var values)
                && values.ValueKind == JsonValueKind.Array ? values.EnumerateArray().ToList() : [];
            var serials = allocations.Select(value => Text(value, "LotSerialNbr"))
                .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(value => new CanonicalSerial(value!)).ToList();
            var allocationQuantity = allocations.Sum(value => Decimal(value, "Qty", "Quantity"));
            var lineQuantity = Decimal(line, "ReceiptQty", "ShippedQty", "Qty", "Quantity");
            var quantity = quantityMode == Cx04AllocationQuantityMode.AllocationsOnly && allocations.Count > 0
                ? allocationQuantity : lineQuantity;
            return new CanonicalLine(Int(line, "LineNbr", index + 1), Text(line, "id")
                    ?? (index + 1).ToString(CultureInfo.InvariantCulture), Text(line, "InventoryID") ?? "",
                quantity, Text(line, "UOM") ?? "EA", serials, [], Text(line, "LocationID")
                    ?? allocations.Select(value => Text(value, "LocationID") ?? Text(value, "Location"))
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
                null, Text(line, "CustomerID"), NullableDecimal(line, "UnitPrice"), Text(line, "CurrencyID"));
        }).ToList();
    }

    private static ExternalDocumentRef Document(JsonElement raw, string type, string externalId,
        DateTimeOffset occurred) => new(type, Text(raw, "ReferenceNbr") ?? Text(raw, "ReceiptNbr")
        ?? Text(raw, "ShipmentNbr") ?? Text(raw, "OrderNbr") ?? externalId,
        DateOnly.FromDateTime(occurred.Date), externalId);
    private static ExternalDocumentRef? Reference(JsonElement raw, string field, string type,
        DateTimeOffset occurred) => Text(raw, field) is { Length: > 0 } number
            ? new(type, number, DateOnly.FromDateTime(occurred.Date), number) : null;
    private static string DocumentType(Cx04EntityDefinition entity) => entity.CanonicalMessage switch
    {
        nameof(DeliveryPosted) => "delivery", nameof(GoodsReceiptPosted) => "receipt",
        nameof(ReturnReceived) => "return_receipt", nameof(TransferPosted) => "transfer",
        nameof(InvoicePosted) => "invoice", _ => "document",
    };

    internal static string? Text(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found)
            || found.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (found.ValueKind == JsonValueKind.Object && found.TryGetProperty("value", out var wrapped)) found = wrapped;
        return found.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null
            : found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString();
    }
    private static bool Bool(JsonElement value, string property) =>
        bool.TryParse(Text(value, property), out var result) && result;
    private static int Int(JsonElement value, string property, int fallback) =>
        int.TryParse(Text(value, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result : fallback;
    private static decimal Decimal(JsonElement value, params string[] properties)
    {
        foreach (var property in properties)
            if (decimal.TryParse(Text(value, property), NumberStyles.Number, CultureInfo.InvariantCulture,
                    out var result)) return result;
        return 0;
    }
    private static decimal? NullableDecimal(JsonElement value, string property) =>
        decimal.TryParse(Text(value, property), NumberStyles.Number, CultureInfo.InvariantCulture,
            out var result) ? result : null;
    private static DateTimeOffset? Date(JsonElement value, string property) =>
        DateTimeOffset.TryParse(Text(value, property), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var result) ? result : null;
}
