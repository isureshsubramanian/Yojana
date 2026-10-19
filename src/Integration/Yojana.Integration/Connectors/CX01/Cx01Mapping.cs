using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX01;

internal sealed record Cx01MappedAcceptance(InboundAcceptance Acceptance,
    IReadOnlyList<Cx01MissingSerialLine> MissingSerialLines);

public sealed record Cx01MissingSerialLine(int LineNo, string ItemCode, string? WarehouseCode,
    decimal Quantity);

public interface ICx01SerialHistory
{
    Task<IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>>> ForDocumentAsync(Guid connectionId,
        string entity, string externalId, CancellationToken cancellationToken);
}

public sealed class EfCx01SerialHistory(IntegrationDbContext db, ITenantContext tenant) : ICx01SerialHistory
{
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>>> ForDocumentAsync(Guid connectionId,
        string entity, string externalId, CancellationToken cancellationToken)
    {
        var json = await db.InboundMessages.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.ErpEntity == entity && x.ErpExternalId == externalId
                && x.IsLatest)
            .Select(x => x.CanonicalMessage).SingleOrDefaultAsync(cancellationToken);
        if (json is null) return new Dictionary<int, IReadOnlyList<CanonicalSerial>>();
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("payload", out var payload)
            || !payload.TryGetProperty("lines", out var lines))
            return new Dictionary<int, IReadOnlyList<CanonicalSerial>>();
        return lines.EnumerateArray().Where(line => line.TryGetProperty("line_no", out _))
            .ToDictionary(line => line.GetProperty("line_no").GetInt32(), line =>
                (IReadOnlyList<CanonicalSerial>)(line.TryGetProperty("serials", out var serials)
                    ? serials.Deserialize<List<CanonicalSerial>>(IntegrationContracts.Json) ?? [] : []));
    }
}

public interface ICx01AttributionTaskSink
{
    Task RaiseAsync(Guid connectionId, string entity, string externalId,
        IReadOnlyList<Cx01MissingSerialLine> lines, CancellationToken cancellationToken);
}

public sealed class EfCx01AttributionTaskSink(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock) : ICx01AttributionTaskSink
{
    public async Task RaiseAsync(Guid connectionId, string entity, string externalId,
        IReadOnlyList<Cx01MissingSerialLine> lines, CancellationToken cancellationToken)
    {
        var subject = $"{entity}:{externalId}";
        if (await db.ExceptionTasks.AnyAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.Kind == "serial_attribution"
            && x.SubjectRef == subject && x.Status == "open", cancellationToken)) return;
        db.ExceptionTasks.Add(new IntegrationExceptionTask
        {
            TenantId = tenant.TenantId,
            ExceptionTaskId = TimeOrderedId.New(clock.UtcNow),
            ConnectionId = connectionId,
            Kind = "serial_attribution",
            SubjectRef = subject,
            Detail = $"Serial attribution is incomplete on line(s) {string.Join(", ", lines.Select(x => x.LineNo))}.",
            Status = "open",
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal static class Cx01Mapper
{
    public static Cx01MappedAcceptance Map(TenantId tenantId, Guid connectionId, Cx01EntityDefinition entity,
        JsonElement raw, IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>> previousSerials,
        IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>> recoveredSerials,
        IReadOnlySet<int> serialManagedLines, string captureMode, DateTime observedUtc)
    {
        var key = Text(raw, entity.KeyField) ?? throw new InvalidOperationException("CX01 response has no key.");
        var changed = ChangeMarker(raw, key);
        var occurred = Date(raw, "DocDate") ?? Date(raw, "CreateDate") ?? Date(raw, "UpdateDate")
            ?? new DateTimeOffset(observedUtc, TimeSpan.Zero);
        var missing = new List<Cx01MissingSerialLine>();
        object payload;
        var completeness = "complete";
        var messageType = entity.CanonicalMessage;
        if (messageType is not ("ItemUpserted" or "PartyUpserted" or "DocumentCancelled")
            && Flag(raw, "Cancelled"))
        {
            messageType = "DocumentCancelled";
            payload = new DocumentCancelled(DocumentRef(raw, key), OpenEnum.Known("cancelled"));
        }
        else switch (messageType)
        {
            case "ItemUpserted":
                payload = new ItemUpserted(key, Text(raw, "ItemName") ?? key,
                    OpenEnum.Known(Flag(raw, "ManageSerialNumbers") ? "serial"
                        : Flag(raw, "ManageBatchNumbers") ? "lot" : "none"),
                    Text(raw, "InventoryUOM") ?? "EA", Flag(raw, "U_YojanaWarrantyProduct"), null,
                    Flag(raw, "Frozen"));
                break;
            case "PartyUpserted":
                payload = new PartyUpserted(key, OpenEnum.Known(PartnerKind(Text(raw, "CardType"))),
                    Text(raw, "CardName") ?? key, Text(raw, "FederalTaxID"), Text(raw, "Country") ?? "", null,
                    Flag(raw, "Frozen"));
                break;
            case "DocumentCancelled":
                payload = new DocumentCancelled(DocumentRef(raw, key), OpenEnum.Known("cancelled"));
                break;
            default:
            {
                var lines = Lines(raw, previousSerials, recoveredSerials, serialManagedLines, missing);
                if (missing.Count > 0) completeness = "partial";
                payload = entity.CanonicalMessage switch
                {
                    "DeliveryPosted" => new DeliveryPosted(DocumentRef(raw, key), Text(raw, "CardCode") ?? "",
                        Text(raw, "ShipToCode") ?? "", lines, null, Flag(raw, "U_YojanaDropShip")),
                    "ReturnReceived" => new ReturnReceived(DocumentRef(raw, key), Text(raw, "CardCode") ?? "",
                        lines, BaseDocument(lines), Text(raw, "U_YojanaReturnReason"),
                        Text(raw, "U_YojanaRmaNumber")),
                    "InvoicePosted" => new InvoicePosted(DocumentRef(raw, key), Text(raw, "CardCode") ?? "",
                        lines.Select(x => x.BaseDocumentRef).Where(x => x is not null).Cast<ExternalDocumentRef>()
                            .Distinct().ToList(), lines,
                        new Money(Decimal(raw, "DocTotal"), Text(raw, "DocCurrency") ?? "")),
                    "GoodsReceiptPosted" => new GoodsReceiptPosted(DocumentRef(raw, key),
                        Text(raw, "CardCode") ?? "", lines, BaseDocument(lines)),
                    "ProductionCompleted" => new ProductionCompleted(DocumentRef(raw, key),
                        new("production_order", Text(raw, "BaseRef") ?? key, DateOnly.FromDateTime(occurred.Date)),
                        lines.Count == 0 ? "" : lines[0].ItemExternalId, lines, []),
                    "TransferPosted" => new TransferPosted(DocumentRef(raw, key),
                        Text(raw, "FromWarehouse") ?? (lines.Count == 0 ? null : lines[0].LocationExternalId) ?? "",
                        Text(raw, "ToWarehouse") ?? "", lines),
                    "InventoryAdjusted" => new InventoryAdjusted(DocumentRef(raw, key),
                        OpenEnum.Known(Flag(raw, "U_YojanaCount") ? "count" : "out"), lines,
                        Text(raw, "Comments")),
                    _ => throw new InvalidOperationException("Unsupported CX01 entity mapping."),
                };
                break;
            }
        }

        var envelope = new CanonicalEnvelope(IntegrationContracts.CurrentVersion, Guid.CreateVersion7(),
            tenantId.Value, connectionId, Cx01Capabilities.ConnectorCode, entity.Collection, key,
            new(changed, OpenEnum.Known("timestamp")), occurred,
            new DateTimeOffset(observedUtc, TimeSpan.Zero), OpenEnum.Known(captureMode),
            OpenEnum.Known(completeness), $"cx01/{entity.Collection}/{key}/{changed}");
        var canonicalType = typeof(CanonicalMessage<>).MakeGenericType(payload.GetType());
        var canonical = Activator.CreateInstance(canonicalType, envelope, messageType, payload)
            ?? throw new InvalidOperationException("Could not create the canonical CX01 message.");
        return new(new InboundAcceptance(envelope, messageType,
            JsonSerializer.Serialize(canonical, canonicalType, IntegrationContracts.Json), raw.GetRawText()), missing);
    }

    public static string ChangeMarker(JsonElement value, string key)
    {
        var date = Text(value, "UpdateDate") ?? Text(value, "CreateDate") ?? "0001-01-01";
        var time = Text(value, "UpdateTime") ?? Text(value, "CreateTime") ?? "000000";
        return $"{date}T{NormaliseTime(time)}|{key}";
    }

    public static bool IsOwnWrite(JsonElement value, Cx01ConnectionSettings settings)
    {
        if (StartsWith(value, settings.RmaReferenceField, settings.OwnReferencePrefix)
            || StartsWith(value, settings.CertificateUrlField, settings.OwnReferencePrefix)) return true;
        return LineElements(value).SelectMany(SerialElements).Any(serial =>
            StringComparer.Ordinal.Equals(Text(serial, "DataSource"), settings.OwnSerialDataSourceMarker));
    }

    public static IReadOnlyList<Cx01MissingSerialLine> MissingSerialLines(JsonElement value)
    {
        var result = new List<Cx01MissingSerialLine>();
        foreach (var line in LineElements(value))
        {
            if (SerialElements(line).Any()) continue;
            result.Add(new(Int(line, "LineNum"), Text(line, "ItemCode") ?? "",
                Text(line, "WarehouseCode"), Decimal(line, "Quantity")));
        }
        return result;
    }

    private static List<CanonicalLine> Lines(JsonElement document,
        IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>> previous,
        IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>> recovered,
        IReadOnlySet<int> serialManagedLines,
        List<Cx01MissingSerialLine> missing)
    {
        var result = new List<CanonicalLine>();
        foreach (var line in LineElements(document))
        {
            var lineNo = Int(line, "LineNum");
            var current = SerialElements(line).Select(serial => new CanonicalSerial(
                Text(serial, "InternalSerialNumber") ?? Text(serial, "ManufacturerSerialNumber") ?? "",
                null, Text(serial, "SystemSerialNumber"))).Where(x => x.Value.Length > 0).ToList();
            IReadOnlyList<CanonicalSerial> serials = current.Count > 0 ? current
                : previous.TryGetValue(lineNo, out var earlier) && earlier.Count > 0 ? earlier
                : recovered.TryGetValue(lineNo, out var fallback) ? fallback : [];
            var quantity = Decimal(line, "Quantity");
            if (serials.Count == 0 && quantity != 0 && serialManagedLines.Contains(lineNo))
                missing.Add(new(lineNo, Text(line, "ItemCode") ?? "", Text(line, "WarehouseCode"), quantity));
            var baseEntry = Text(line, "BaseEntry");
            var baseRef = baseEntry is null ? null : new ExternalDocumentRef(Text(line, "BaseType") ?? "document",
                baseEntry, DateOnly.FromDateTime((Date(document, "DocDate") ?? DateTimeOffset.UnixEpoch).Date),
                baseEntry, Int(line, "BaseLine"));
            result.Add(new CanonicalLine(lineNo, lineNo.ToString(CultureInfo.InvariantCulture),
                Text(line, "ItemCode") ?? "", quantity, Text(line, "UoMCode") ?? "EA", serials, [],
                Text(line, "WarehouseCode"), baseRef, Text(document, "CardCode"), DecimalOrNull(line, "Price"),
                Text(document, "DocCurrency")));
        }
        return result;
    }

    private static IEnumerable<JsonElement> LineElements(JsonElement value)
    {
        foreach (var name in new[] { "DocumentLines", "StockTransferLines" })
            if (value.TryGetProperty(name, out var lines) && lines.ValueKind == JsonValueKind.Array)
                foreach (var line in lines.EnumerateArray()) yield return line;
    }

    private static IEnumerable<JsonElement> SerialElements(JsonElement line) =>
        line.TryGetProperty("SerialNumbers", out var serials) && serials.ValueKind == JsonValueKind.Array
            ? serials.EnumerateArray() : [];

    private static ExternalDocumentRef DocumentRef(JsonElement value, string key) => new(
        Text(value, "DocObjectCode") ?? "document", Text(value, "DocNum") ?? key,
        DateOnly.FromDateTime((Date(value, "DocDate") ?? DateTimeOffset.UnixEpoch).Date), key);

    private static ExternalDocumentRef? BaseDocument(IEnumerable<CanonicalLine> lines) =>
        lines.Select(x => x.BaseDocumentRef).FirstOrDefault(x => x is not null);

    private static string PartnerKind(string? value) => value switch
    {
        "cCustomer" => "customer",
        "cSupplier" => "vendor",
        _ => "both",
    };

    private static string NormaliseTime(string value)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray()).PadLeft(6, '0');
        return digits.Length > 6 ? digits[^6..] : digits;
    }

    private static bool StartsWith(JsonElement value, string? property, string prefix) =>
        property is not null && value.TryGetProperty(property, out var found)
            && found.ValueKind == JsonValueKind.String
            && (found.GetString() ?? "").StartsWith(prefix, StringComparison.Ordinal);

    private static string? Text(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var found) || found.ValueKind == JsonValueKind.Null) return null;
        return found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString();
    }

    private static int Int(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.TryGetInt32(out var number) ? number : 0;

    private static decimal Decimal(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.TryGetDecimal(out var number) ? number : 0;

    private static decimal? DecimalOrNull(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.TryGetDecimal(out var number) ? number : null;

    private static bool Flag(JsonElement value, string property) =>
        Text(value, property) is "tYES" or "Y" or "true" or "True";

    private static DateTimeOffset? Date(JsonElement value, string property) =>
        DateTimeOffset.TryParse(Text(value, property), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var result) ? result : null;
}
