using System.Globalization;
using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Integration.Inbox;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX02;

public static class Cx02Mapper
{
    public static InboundAcceptance Map(TenantId tenantId, Guid connectionId, string entityType,
        JsonElement raw, string captureMode, DateTime observedUtc,
        IReadOnlyDictionary<string, string>? serviceItemNumbers = null)
    {
        var entity = Cx02Protocol.Entity(entityType);
        var externalId = ExternalId(entity, raw);
        var marker = Text(raw, "lastModifiedDateTime") ?? Text(raw, "@odata.etag")
            ?? observedUtc.ToString("O", CultureInfo.InvariantCulture);
        var occurred = ParseDateTimeOffset(raw, "postingDate") ?? ParseDateTimeOffset(raw, "invoiceDate")
            ?? ParseDateTimeOffset(raw, "shipmentDate") ?? ParseDateTimeOffset(raw, "lastModifiedDateTime")
            ?? new DateTimeOffset(observedUtc, TimeSpan.Zero);
        var (messageType, payload, completeness) = Payload(entity, raw, externalId, occurred,
            serviceItemNumbers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var envelope = new CanonicalEnvelope(IntegrationContracts.CurrentVersion, Guid.CreateVersion7(observedUtc),
            tenantId.Value, connectionId, Cx02Capabilities.ConnectorCode, entity.Resource, externalId,
            new(marker, OpenEnum.Known(Text(raw, "lastModifiedDateTime") is null ? "etag" : "timestamp")),
            occurred, new DateTimeOffset(observedUtc, TimeSpan.Zero), OpenEnum.Known(captureMode),
            OpenEnum.Known(completeness), $"cx02/{entity.Resource}/{externalId}/{marker}");
        var canonical = JsonSerializer.Serialize(new { envelope, message_type = messageType, payload },
            IntegrationContracts.Json);
        return new(envelope, messageType, canonical, raw.GetRawText());
    }

    private static (string MessageType, object Payload, string Completeness) Payload(Cx02EntityDefinition entity,
        JsonElement raw, string externalId, DateTimeOffset occurred,
        IReadOnlyDictionary<string, string> serviceItemNumbers)
    {
        if (entity.Resource == "items")
        {
            var tracking = Text(raw, "itemTrackingCode") ?? Text(raw, "trackingMode") ?? "none";
            tracking = tracking.ToLowerInvariant() switch
            {
                "serial" or "serial specific" => "serial",
                "lot" or "lot specific" => "lot",
                "serial_and_lot" => "serial_and_lot",
                _ => "none",
            };
            return (nameof(ItemUpserted), new ItemUpserted(Text(raw, "number") ?? externalId,
                Text(raw, "displayName") ?? Text(raw, "description") ?? externalId,
                OpenEnum.Known(tracking), Text(raw, "baseUnitOfMeasureCode") ?? "EA",
                Bool(raw, "isWarrantyProduct"), Text(raw, "warrantyTemplateRef"),
                Bool(raw, "blocked") || Bool(raw, "deleted")), "complete");
        }
        if (entity.Resource == "customers")
            return (nameof(PartyUpserted), new PartyUpserted(Text(raw, "number") ?? externalId,
                OpenEnum.Known("customer"), Text(raw, "displayName") ?? externalId,
                Text(raw, "taxRegistrationNumber"), Text(raw, "countryLetterCode") ?? "",
                Text(raw, "mergedFromNumber"), Bool(raw, "blocked") || Bool(raw, "deleted")), "complete");

        if (entity.Resource == "salesCreditMemos" || Bool(raw, "cancelled"))
        {
            var original = Text(raw, "cancelledDocumentNumber") ?? Text(raw, "cancelledInvoiceNumber")
                ?? Text(raw, "baseInvoiceNumber") ?? Text(raw, "appliesToInvoiceNumber") ?? externalId;
            var originalType = Text(raw, "cancelledDocumentType") ?? "invoice";
            var reference = new ExternalDocumentRef(originalType, original,
                DateOnly.FromDateTime(occurred.Date), original);
            var reversing = entity.Resource == "salesCreditMemos"
                ? new ExternalDocumentRef("credit_memo", Text(raw, "number") ?? externalId,
                    DateOnly.FromDateTime(occurred.Date), externalId) : null;
            return (nameof(DocumentCancelled), new DocumentCancelled(reference, OpenEnum.Known("reversed"), reversing),
                "complete");
        }

        if (entity.Resource == "salesInvoices")
        {
            var lines = Lines(raw, serviceItemNumbers);
            var document = Document(raw, "invoice", externalId, occurred);
            var baseDeliveries = lines.Select(line => line.BaseDocumentRef).Where(value => value is not null)
                .Cast<ExternalDocumentRef>().Distinct().ToList();
            return (nameof(InvoicePosted), new InvoicePosted(document,
                Text(raw, "customerNumber") ?? "", baseDeliveries, lines,
                new Money(Decimal(raw, "totalAmountIncludingTax", "totalAmount"), Text(raw, "currencyCode") ?? "")),
                Completeness(lines, raw));
        }

        if (entity.Resource is "postedTransferShipments" or "postedTransferReceipts")
        {
            var lines = Lines(raw, serviceItemNumbers);
            return (nameof(TransferPosted), new TransferPosted(Document(raw, "transfer", externalId, occurred),
                Text(raw, "transferFromCode") ?? "", Text(raw, "transferToCode") ?? "", lines),
                Completeness(lines, raw));
        }
        if (entity.Resource == "postedReturnReceipts")
        {
            var lines = Lines(raw, serviceItemNumbers);
            return (nameof(ReturnReceived), new ReturnReceived(Document(raw, "return_receipt", externalId, occurred),
                Text(raw, "customerNumber") ?? "", lines, Reference(raw, "baseDelivery", occurred),
                Text(raw, "returnReasonCode"), Text(raw, "rmaExternalRef")), Completeness(lines, raw));
        }
        if (entity.Resource is "salesShipments")
        {
            var lines = Lines(raw, serviceItemNumbers);
            return (nameof(DeliveryPosted), new DeliveryPosted(Document(raw, "delivery", externalId, occurred),
                Text(raw, "customerNumber") ?? "", Text(raw, "shipToCode") ?? "", lines,
                Reference(raw, "baseOrder", occurred), Bool(raw, "dropShipment")), Completeness(lines, raw));
        }
        if (entity.Resource is "purchaseReceipts")
        {
            var lines = Lines(raw, serviceItemNumbers);
            return (nameof(GoodsReceiptPosted), new GoodsReceiptPosted(Document(raw, "receipt", externalId, occurred),
                Text(raw, "vendorNumber") ?? "", lines, Reference(raw, "baseOrder", occurred)), Completeness(lines, raw));
        }

        if (entity.Resource == "trackedItemLedgerEntries")
            return Ledger(raw, externalId, occurred, serviceItemNumbers);

        throw new InvalidOperationException($"CX02 resource '{entity.Resource}' is an enrichment page, not a canonical event source.");
    }

    private static (string MessageType, object Payload, string Completeness) Ledger(JsonElement raw,
        string externalId, DateTimeOffset occurred, IReadOnlyDictionary<string, string> serviceItemNumbers)
    {
        var documentNumber = Text(raw, "documentNumber") ?? externalId;
        var documentType = Text(raw, "documentType") ?? "adjustment";
        var line = LedgerLine(raw, serviceItemNumbers);
        var lines = new List<CanonicalLine> { line };
        var normalised = new string(documentType.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        var completeness = line.Serials.Count == 0 && Math.Abs(line.Quantity) > 0 ? "partial" : "complete";
        if (normalised.Contains("purchasereceipt", StringComparison.Ordinal))
            return (nameof(GoodsReceiptPosted), new GoodsReceiptPosted(
                new("receipt", documentNumber, DateOnly.FromDateTime(occurred.Date), documentNumber),
                Text(raw, "sourceNumber") ?? "", lines), completeness);
        if (normalised.Contains("salesshipment", StringComparison.Ordinal))
        {
            var deliveryLine = line with { Quantity = Math.Abs(line.Quantity) };
            return (nameof(DeliveryPosted), new DeliveryPosted(
                new("delivery", documentNumber, DateOnly.FromDateTime(occurred.Date), documentNumber),
                Text(raw, "sourceNumber") ?? "", Text(raw, "shipToCode") ?? "", [deliveryLine]), completeness);
        }
        if (normalised.Contains("returnreceipt", StringComparison.Ordinal))
            return (nameof(ReturnReceived), new ReturnReceived(
                new("return_receipt", documentNumber, DateOnly.FromDateTime(occurred.Date), documentNumber),
                Text(raw, "sourceNumber") ?? "", lines), completeness);
        if (normalised.Contains("transfer", StringComparison.Ordinal))
            return (nameof(TransferPosted), new TransferPosted(
                new("transfer", documentNumber, DateOnly.FromDateTime(occurred.Date), documentNumber),
                Text(raw, "transferFromCode") ?? Text(raw, "locationCode") ?? "",
                Text(raw, "transferToCode") ?? Text(raw, "locationCode") ?? "", lines), completeness);
        if (normalised.Contains("output", StringComparison.Ordinal)
            || (Text(raw, "entryType") ?? "").Contains("Output", StringComparison.OrdinalIgnoreCase))
            return (nameof(ProductionCompleted), new ProductionCompleted(
                new("production_receipt", documentNumber, DateOnly.FromDateTime(occurred.Date), documentNumber),
                new("production_order", Text(raw, "sourceNumber") ?? documentNumber,
                    DateOnly.FromDateTime(occurred.Date)), line.ItemExternalId, lines, []), completeness);
        var quantity = line.Quantity;
        var kind = quantity > 0 ? "in" : quantity < 0 ? "out" : "count";
        return (nameof(InventoryAdjusted), new InventoryAdjusted(
            new("adjustment", documentNumber, DateOnly.FromDateTime(occurred.Date), documentNumber),
            OpenEnum.Known(kind), lines, Text(raw, "reasonCode")), completeness);
    }

    private static List<CanonicalLine> Lines(JsonElement raw,
        IReadOnlyDictionary<string, string> serviceItemNumbers)
    {
        if (!raw.TryGetProperty("lines", out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray().Select((line, index) => Line(line, index + 1, serviceItemNumbers)).ToList();
    }

    private static CanonicalLine Line(JsonElement raw, int fallbackLine,
        IReadOnlyDictionary<string, string> serviceItemNumbers)
    {
        var serials = Serials(raw, serviceItemNumbers);
        var lots = Lots(raw);
        var number = Int(raw, "lineNumber", Int(raw, "sequence", fallbackLine));
        return new(number, Text(raw, "id") ?? number.ToString(CultureInfo.InvariantCulture),
            Text(raw, "itemNumber") ?? Text(raw, "lineObjectNumber") ?? "",
            Decimal(raw, "quantity"), Text(raw, "unitOfMeasureCode") ?? "EA", serials, lots,
            Text(raw, "locationCode"), Reference(raw, "baseDocument", ParseDateTimeOffset(raw,
                "postingDate") ?? System.DateTimeOffset.UnixEpoch), Text(raw, "partyNumber"),
            NullableDecimal(raw, "unitPrice"), Text(raw, "currencyCode"));
    }

    private static CanonicalLine LedgerLine(JsonElement raw,
        IReadOnlyDictionary<string, string> serviceItemNumbers)
    {
        var entry = Int(raw, "entryNumber", 0);
        return new(entry, entry.ToString(CultureInfo.InvariantCulture), Text(raw, "itemNumber") ?? "",
            Decimal(raw, "quantity"), Text(raw, "unitOfMeasureCode") ?? "EA", Serials(raw, serviceItemNumbers),
            Lots(raw), Text(raw, "locationCode"), null, Text(raw, "sourceNumber"));
    }

    private static List<CanonicalSerial> Serials(JsonElement raw,
        IReadOnlyDictionary<string, string> serviceItemNumbers)
    {
        var values = new List<string>();
        if (raw.TryGetProperty("serials", out var serials) && serials.ValueKind == JsonValueKind.Array)
            values.AddRange(serials.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString() : Text(value, "value")).Where(value => !string.IsNullOrWhiteSpace(value))!);
        if (Text(raw, "serialNumber") is { Length: > 0 } one) values.Add(one);
        return values.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => new CanonicalSerial(value!, null,
            serviceItemNumbers.GetValueOrDefault(value!))).ToList();
    }

    private static List<CanonicalLot> Lots(JsonElement raw)
    {
        var values = new List<string>();
        if (raw.TryGetProperty("lots", out var lots) && lots.ValueKind == JsonValueKind.Array)
            values.AddRange(lots.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String
                ? value.GetString() : Text(value, "value")).Where(value => !string.IsNullOrWhiteSpace(value))!);
        if (Text(raw, "lotNumber") is { Length: > 0 } one) values.Add(one);
        return values.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => new CanonicalLot(value!)).ToList();
    }

    private static ExternalDocumentRef Document(JsonElement raw, string type, string externalId,
        DateTimeOffset occurred) => new(type, Text(raw, "number") ?? Text(raw, "documentNumber") ?? externalId,
        DateOnly.FromDateTime(occurred.Date), externalId);

    private static ExternalDocumentRef? Reference(JsonElement raw, string prefix, DateTimeOffset occurred)
    {
        var number = Text(raw, prefix + "Number");
        return string.IsNullOrWhiteSpace(number) ? null : new(prefix, number,
            DateOnly.FromDateTime(occurred.Date), Text(raw, prefix + "Id") ?? number);
    }

    private static string Completeness(List<CanonicalLine> lines, JsonElement raw) =>
        lines.Count == 0 ? Bool(raw, "trackingExpected") ? "partial" : "unknown"
            : lines.Any(line => line.Serials.Count == 0 && Math.Abs(line.Quantity) > 0) ? "partial" : "complete";

    private static string ExternalId(Cx02EntityDefinition entity, JsonElement raw) =>
        Text(raw, entity.Resource is "items" or "customers" ? "number" : "id")
        ?? Text(raw, "number") ?? Text(raw, "entryNumber")
        ?? throw new InvalidOperationException("CX02 response is missing its external identity.");

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;

    private static bool Bool(JsonElement value, string property) => value.TryGetProperty(property, out var found)
        && (found.ValueKind == JsonValueKind.True || found.ValueKind == JsonValueKind.String
            && bool.TryParse(found.GetString(), out var parsed) && parsed);

    private static int Int(JsonElement value, string property, int fallback) =>
        value.TryGetProperty(property, out var found) && found.TryGetInt32(out var parsed) ? parsed : fallback;

    private static decimal Decimal(JsonElement value, params string[] properties)
    {
        foreach (var property in properties)
            if (value.TryGetProperty(property, out var found) && found.TryGetDecimal(out var parsed)) return parsed;
        return 0;
    }

    private static decimal? NullableDecimal(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.TryGetDecimal(out var parsed) ? parsed : null;

    private static DateTimeOffset? ParseDateTimeOffset(JsonElement value, string property) =>
        Text(value, property) is { } text && System.DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
}
