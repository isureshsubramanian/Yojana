using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Outbox;
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

public interface ICx01DocumentCaptureStore
{
    Task<bool> WasCapturedAsync(Guid connectionId, string entity, string externalId,
        CancellationToken cancellationToken);
}

public sealed class EfCx01DocumentCaptureStore(IntegrationDbContext db, ITenantContext tenant)
    : ICx01DocumentCaptureStore
{
    public Task<bool> WasCapturedAsync(Guid connectionId, string entity, string externalId,
        CancellationToken cancellationToken) => db.InboundMessages.AsNoTracking().AnyAsync(x =>
        x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
        && x.ErpEntity == entity && x.ErpExternalId == externalId, cancellationToken);
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
    Task RaiseQueryPermissionAsync(Guid connectionId, string remoteMessage,
        CancellationToken cancellationToken);
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

    public async Task RaiseQueryPermissionAsync(Guid connectionId, string remoteMessage,
        CancellationToken cancellationToken)
    {
        const string subject = "serial-link-query";
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
            Detail = remoteMessage.Length <= 2_000 ? remoteMessage : remoteMessage[..2_000],
            Status = "open",
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

public interface ICx01OwnWriteStore
{
    Task<bool> MatchesAsync(Guid connectionId, string externalId, string changeMarker,
        CancellationToken cancellationToken);
}

public sealed class EfCx01OwnWriteStore(IntegrationDbContext db, ITenantContext tenant) : ICx01OwnWriteStore
{
    // TIGHT COUPLING (own-write recognition): this exact-marker lookup and OutboundRelay.CompleteAsync's persisted
    // ErpChangeMarker are one durable acknowledgement invariant. A process-local marker would be lost on restart;
    // separating either side makes immediate remote post-write changes look foreign. Later markers must not match.
    public Task<bool> MatchesAsync(Guid connectionId, string externalId, string changeMarker,
        CancellationToken cancellationToken) => db.OutboundCommands.AsNoTracking().AnyAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.ErpExternalId == externalId && x.ErpChangeMarker == changeMarker
            && x.Status == OutboundCommandStatus.Done, cancellationToken);
}

public interface ICx01InboundRejectionSink
{
    Task ParkAsync(Guid connectionId, string entity, string externalId, Cx01RemoteException exception,
        CancellationToken cancellationToken);
}

public sealed class EfCx01InboundRejectionSink(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    IInboundMessageParkingSink parking) : ICx01InboundRejectionSink
{
    public async Task ParkAsync(Guid connectionId, string entity, string externalId,
        Cx01RemoteException exception, CancellationToken cancellationToken)
    {
        var message = new InboundMessage
        {
            TenantId = tenant.TenantId,
            InboundMessageId = TimeOrderedId.New(clock.UtcNow),
            MessageId = Guid.CreateVersion7(),
            ConnectionId = connectionId,
            ConnectorCode = Cx01Capabilities.ConnectorCode,
            ErpEntity = entity,
            ErpExternalId = externalId,
            ErpChangeMarker = $"business-rejection:{exception.RemoteCode ?? (int)exception.StatusCode + ""}:"
                + clock.UtcNow.Ticks,
            MarkerKind = "remote_error",
            ContractVersion = IntegrationContracts.CurrentVersion,
            MessageType = "RemoteReadRejected",
            CaptureMode = "observed",
            RawPayload = "{}",
            CanonicalMessage = "{}",
            OccurredAt = clock.UtcNow,
            ObservedAt = clock.UtcNow,
            AcceptedAt = clock.UtcNow,
            IsLatest = false,
            TranslationStatus = InboundTranslationStatus.NeedsAttention,
            LastTranslationError = exception.BusinessMessage(),
            AttentionReasonCode = "business_rejection",
        };
        db.InboundMessages.Add(message);
        _ = await parking.ParkInboundAsync(message, "business_rejection", exception.BusinessMessage(),
            cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal static class Cx01Mapper
{
    public static Cx01MappedAcceptance Map(TenantId tenantId, Guid connectionId, Cx01EntityDefinition entity,
        JsonElement raw, IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>> previousSerials,
        IReadOnlyDictionary<int, IReadOnlyList<CanonicalSerial>> recoveredSerials,
        IReadOnlySet<int> serialManagedLines, string captureMode, DateTime observedUtc,
        bool mapCancelledOriginal = false, Cx01FieldReader? fields = null)
    {
        fields ??= Cx01FieldReader.Defaults;
        var key = Text(raw, entity.KeyField) ?? throw new InvalidOperationException("CX01 response has no key.");
        var changed = ChangeMarker(raw, key);
        var occurred = Date(raw, "DocDate") ?? Date(raw, "CreateDate") ?? Date(raw, "UpdateDate")
            ?? new DateTimeOffset(observedUtc, TimeSpan.Zero);
        var missing = new List<Cx01MissingSerialLine>();
        object payload;
        var completeness = "complete";
        var messageType = entity.CanonicalMessage;
        if (!mapCancelledOriginal && messageType is not ("ItemUpserted" or "PartyUpserted")
            && Text(raw, "CancelStatus") == "csCancellation")
        {
            messageType = "DocumentCancelled";
            payload = new DocumentCancelled(CancelledDocumentRef(raw, entity, key), OpenEnum.Known("cancelled"));
        }
        else switch (messageType)
        {
            case "ItemUpserted":
                payload = new ItemUpserted(key, Text(raw, "ItemName") ?? key,
                    OpenEnum.Known(Flag(raw, "ManageSerialNumbers") ? "serial"
                        : Flag(raw, "ManageBatchNumbers") ? "lot" : "none"),
                    Text(raw, "InventoryUOM") ?? "EA", fields.YesNo(raw, Cx01FieldMap.ItemWarrantyProduct),
                    fields.Text(raw, Cx01FieldMap.ItemWarrantyTemplate),
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
                        Text(raw, "ShipToCode") ?? "", lines, null, fields.YesNo(raw, Cx01FieldMap.DeliveryDropShip)),
                    "ReturnReceived" => new ReturnReceived(DocumentRef(raw, key), Text(raw, "CardCode") ?? "",
                        lines, BaseDocument(lines), fields.Text(raw, Cx01FieldMap.ReturnReason),
                        fields.Text(raw, Cx01FieldMap.ReturnRmaReference)),
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
                        OpenEnum.Known(fields.YesNo(raw, Cx01FieldMap.AdjustmentIsCount) ? "count" : "out"), lines,
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
            JsonSerializer.Serialize(canonical, canonicalType, IntegrationContracts.Json), RetainedRaw(raw)), missing);
    }

    public static string ChangeMarker(JsonElement value, string key)
    {
        var date = Text(value, "UpdateDate") ?? Text(value, "CreateDate") ?? "0001-01-01";
        var time = Text(value, "UpdateTime") ?? Text(value, "CreateTime") ?? "000000";
        return $"{date}T{NormaliseTime(time)}|{key}";
    }

    public static bool IsCancelledOriginal(JsonElement value) => Text(value, "CancelStatus") == "csYes";

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

    private static JsonElement.ArrayEnumerator SerialElements(JsonElement line) =>
        line.TryGetProperty("SerialNumbers", out var serials) && serials.ValueKind == JsonValueKind.Array
            ? serials.EnumerateArray() : default;

    private static ExternalDocumentRef DocumentRef(JsonElement value, string key) => new(
        Text(value, "DocObjectCode") ?? "document", Text(value, "DocNum") ?? key,
        DateOnly.FromDateTime((Date(value, "DocDate") ?? DateTimeOffset.UnixEpoch).Date), key);

    private static ExternalDocumentRef CancelledDocumentRef(JsonElement value, Cx01EntityDefinition entity,
        string cancellationKey)
    {
        var baseLine = LineElements(value).FirstOrDefault(line => Text(line, "BaseEntry") is not null);
        var original = baseLine.ValueKind == JsonValueKind.Undefined ? null : Text(baseLine, "BaseEntry");
        return new(entity.Collection, original ?? cancellationKey,
            DateOnly.FromDateTime((Date(value, "DocDate") ?? DateTimeOffset.UnixEpoch).Date),
            original ?? cancellationKey,
            baseLine.ValueKind == JsonValueKind.Undefined ? null : Int(baseLine, "BaseLine"));
    }

    private static string RetainedRaw(JsonElement raw)
    {
        const int maximumChars = 262_144;
        var complete = raw.GetRawText();
        if (complete.Length <= maximumChars) return complete;
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var retainedCharacters = 0;
        CollectUserFields(raw, "$", fields, ref retainedCharacters);
        return JsonSerializer.Serialize(new
        {
            raw_payload_truncated = true,
            user_defined_fields = fields,
        }, IntegrationContracts.Json);
    }

    private static void CollectUserFields(JsonElement value, string path, IDictionary<string, string> fields,
        ref int retainedCharacters)
    {
        if (fields.Count >= 256 || retainedCharacters >= 200_000) return;
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var property in value.EnumerateObject())
            {
                var propertyPath = path + "." + property.Name;
                if (property.Name.StartsWith("U_", StringComparison.Ordinal))
                {
                    var text = property.Value.GetRawText();
                    if (text.Length > 4_096) text = text[..4_096];
                    if (retainedCharacters + text.Length <= 200_000)
                    {
                        fields[propertyPath] = text;
                        retainedCharacters += text.Length;
                    }
                }
                else CollectUserFields(property.Value, propertyPath, fields, ref retainedCharacters);
            }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                CollectUserFields(item, path + "[" + index++ + "]", fields, ref retainedCharacters);
        }
    }

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
