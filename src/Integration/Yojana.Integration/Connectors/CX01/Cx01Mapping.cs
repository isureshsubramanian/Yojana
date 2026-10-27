using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX01;

internal sealed record Cx01ProductionOrderRead(string AbsoluteEntry, string? ItemNo, decimal? PlannedQuantity);

internal sealed record Cx01MappedAcceptance(InboundAcceptance Acceptance,
    IReadOnlyList<Cx01MissingSerialLine> MissingSerialLines, string? ContentHash = null,
    IReadOnlyList<InboundAcceptance>? AdditionalAcceptances = null)
{
    public IReadOnlyList<InboundAcceptance> Acceptances => [Acceptance, .. AdditionalAcceptances ?? []];
}

internal sealed class Cx01CurrencyNotMappedException(string erpCode)
    : InvalidOperationException($"ERP currency code '{erpCode}' has no confirmed ISO 4217 mapping.")
{
    public string ErpCode { get; } = erpCode;
}

public sealed record Cx01BomSyncState(string ContentHash, bool Deleted);

public interface ICx01BomSyncStateReader
{
    Task<IReadOnlyDictionary<string, Cx01BomSyncState>> ReadAsync(Guid connectionId,
        CancellationToken cancellationToken);
}

public sealed class EfCx01BomSyncStateReader(IntegrationDbContext db, ITenantContext tenant)
    : ICx01BomSyncStateReader
{
    public async Task<IReadOnlyDictionary<string, Cx01BomSyncState>> ReadAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var result = await db.BomProjections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId)
            .ToDictionaryAsync(row => row.ParentItemExternalId,
                row => new Cx01BomSyncState(row.ContentHash, row.Deleted), StringComparer.Ordinal,
                cancellationToken);
        var pending = await db.InboundMessages.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.ConnectionId == connectionId && row.ErpEntity == "ProductTrees" && row.IsLatest
                && row.MessageType == nameof(BomUpserted))
            .Select(row => new { row.ErpExternalId, row.CanonicalMessage }).ToListAsync(cancellationToken);
        foreach (var row in pending)
        {
            using var document = JsonDocument.Parse(row.CanonicalMessage);
            var payload = document.RootElement.GetProperty("payload");
            var bom = payload.Deserialize<BomUpserted>(IntegrationContracts.Json)
                ?? throw new InvalidOperationException("Stored CX01 BOM message is invalid.");
            result[row.ErpExternalId] = new(BomContentHash.Compute(bom.Lines), bom.Deleted);
        }
        return result;
    }
}

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
    // TIGHT COUPLING (own-write recognition): OutboundAcknowledgement defines the persisted composite marker
    // and its exact-match decoding here. Remote replies must survive restarts; separating the encoding and decoding
    // would hide our own writes or suppress later foreign edits. Per-document receipts also cover pending commands.
    public async Task<bool> MatchesAsync(Guid connectionId, string externalId, string changeMarker,
        CancellationToken cancellationToken)
    {
        if (await db.Set<OutboundDocumentReceipt>().AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.ExternalId == externalId && x.ChangeMarker == changeMarker, cancellationToken)) return true;
        if (await db.OutboundCommands.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.ErpExternalId == externalId && x.ErpChangeMarker == changeMarker, cancellationToken)) return true;
        // Legacy composite acknowledgements are reached through the indexed external identity, never a marker scan.
        var moduleIds = db.ExternalIdMaps.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.EntityType == "document" && x.ExternalId == externalId && x.ValidTo == null).Select(x => x.ModuleId);
        if (!await moduleIds.AnyAsync(cancellationToken)) return false;
        var externalIds = db.ExternalIdMaps.Where(x => x.TenantId == tenant.TenantId && x.ConnectionId == connectionId
            && x.EntityType == "document" && moduleIds.Contains(x.ModuleId) && x.ValidTo == null).Select(x => x.ExternalId);
        var candidates = await db.OutboundCommands.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && externalIds.Contains(x.ErpExternalId!))
            .Select(x => x.ErpChangeMarker).ToListAsync(cancellationToken);
        return candidates.Any(x => OutboundAcknowledgement.Matches(x, externalId, changeMarker));
    }
}

public interface ICx01InboundRejectionSink
{
    Task ParkAsync(Guid connectionId, string entity, string externalId, Cx01RemoteException exception,
        CancellationToken cancellationToken);
    Task ParkDocumentAsync(Guid connectionId, string entity, string messageType, string externalId, JsonElement raw,
        string reasonCode, string failure, CancellationToken cancellationToken);
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

    public async Task ParkDocumentAsync(Guid connectionId, string entity, string messageType, string externalId,
        JsonElement raw, string reasonCode, string failure, CancellationToken cancellationToken)
    {
        var marker = Cx01Mapper.ChangeMarker(raw, externalId);
        var existing = await db.InboundMessages.SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.ErpEntity == entity
            && x.ErpExternalId == externalId && x.ErpChangeMarker == marker, cancellationToken);
        if (existing is not null)
        {
            if (existing.TranslationStatus == InboundTranslationStatus.NeedsAttention)
                _ = await parking.ParkInboundAsync(existing, reasonCode, failure, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        foreach (var prior in await db.InboundMessages.Where(x => x.TenantId == tenant.TenantId
                     && x.ConnectionId == connectionId && x.ErpEntity == entity
                     && x.ErpExternalId == externalId && x.IsLatest).ToListAsync(cancellationToken))
            prior.IsLatest = false;
        var message = new InboundMessage
        {
            TenantId = tenant.TenantId,
            InboundMessageId = TimeOrderedId.New(clock.UtcNow),
            MessageId = Guid.CreateVersion7(),
            ConnectionId = connectionId,
            ConnectorCode = Cx01Capabilities.ConnectorCode,
            ErpEntity = entity,
            ErpExternalId = externalId,
            ErpChangeMarker = marker,
            MarkerKind = "timestamp",
            ContractVersion = IntegrationContracts.CurrentVersion,
            MessageType = messageType,
            CaptureMode = "observed",
            RawPayload = raw.GetRawText(),
            CanonicalMessage = "{}",
            OccurredAt = clock.UtcNow,
            ObservedAt = clock.UtcNow,
            AcceptedAt = clock.UtcNow,
            IsLatest = true,
            TranslationStatus = InboundTranslationStatus.NeedsAttention,
            LastTranslationError = failure,
            AttentionReasonCode = reasonCode,
        };
        db.InboundMessages.Add(message);
        _ = await parking.ParkInboundAsync(message, reasonCode, failure, cancellationToken);
        db.ExceptionTasks.Add(new IntegrationExceptionTask
        {
            TenantId = tenant.TenantId,
            ExceptionTaskId = TimeOrderedId.New(clock.UtcNow),
            ConnectionId = connectionId,
            InboundMessageId = message.InboundMessageId,
            Kind = reasonCode,
            SubjectRef = $"{entity}:{externalId}",
            Detail = failure,
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
        IReadOnlySet<int> serialManagedLines, string captureMode, DateTime observedUtc,
        bool mapCancelledOriginal = false, Cx01FieldReader? fields = null,
        IReadOnlyDictionary<string, string>? currencyMap = null,
        IReadOnlyDictionary<string, Cx01ProductionOrderRead?>? productionOrders = null,
        string? localCurrency = null)
    {
        fields ??= Cx01FieldReader.Defaults;
        var key = Text(raw, entity.KeyField) ?? throw new InvalidOperationException("CX01 response has no key.");
        var bomLines = entity.CanonicalMessage == nameof(BomUpserted) ? BomLines(raw) : null;
        var contentHash = bomLines is null ? null : BomContentHash.Compute(bomLines);
        var changed = contentHash is null ? ChangeMarker(raw, key)
            : observedUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture) + "|" + contentHash;
        // COUPLED to ReconciliationService.ModuleDigestsAsync and Cx01Adapter.EntityDayDigestsAsync: these are calendar
        // dates in the ERP company's day, kept as that date at 00:00 UTC; reconciliation buckets by the UTC date and
        // must not convert them to a zone. Only the observed-time fallback is a real instant.
        var occurred = Date(raw, "DocDate") ?? Date(raw, "CreateDate") ?? Date(raw, "UpdateDate")
            ?? new DateTimeOffset(observedUtc, TimeSpan.Zero);
        var missing = new List<Cx01MissingSerialLine>();
        object payload;
        var completeness = "complete";
        var messageType = entity.CanonicalMessage;
        if (!mapCancelledOriginal && messageType is not ("ItemUpserted" or "PartyUpserted" or "BomUpserted")
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
                    Flag(raw, "Frozen"), IsInventoryItem(raw));
                break;
            case "BomUpserted":
                payload = new BomUpserted(key, new DateTimeOffset(observedUtc, TimeSpan.Zero),
                    Flag(raw, "Frozen") || Flag(raw, "Deleted"), bomLines!);
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
                var lines = Lines(raw, previousSerials, recoveredSerials, serialManagedLines, missing, currencyMap,
                    localCurrency);
                if (entity.CanonicalMessage == nameof(InventoryAdjusted)) lines = WithoutProductionIssues(lines, missing);
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
                        new Money(Decimal(raw, "DocTotal"), DocumentTotalCurrency(raw, currencyMap, localCurrency) ?? "")),
                    "GoodsReceiptPosted" => new GoodsReceiptPosted(DocumentRef(raw, key),
                        Text(raw, "CardCode") ?? "", lines, BaseDocument(lines)),
                    "ProductionCompleted" => ProductionReceipt(raw, key, occurred, lines, productionOrders),
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
        var retainedRaw = RetainedRaw(raw);
        var primary = new InboundAcceptance(envelope, messageType,
            JsonSerializer.Serialize(canonical, canonicalType, IntegrationContracts.Json), retainedRaw);
        var additional = entity.CanonicalMessage == nameof(InventoryAdjusted)
            ? ProductionIssueAcceptances(raw, Lines(raw, previousSerials, recoveredSerials, serialManagedLines, [],
                currencyMap, localCurrency), envelope, retainedRaw, productionOrders)
            : [];
        if (payload is InventoryAdjusted { Lines.Count: 0 } && additional.Count > 0)
            return new(additional[0], missing, contentHash, additional.Skip(1).ToList());
        return new(primary, missing, contentHash, additional);
    }

    public static string ChangeMarker(JsonElement value, string key)
    {
        var date = Cx01Protocol.DatePart(Text(value, "UpdateDate")) ?? Cx01Protocol.DatePart(Text(value, "CreateDate")) ?? "0001-01-01";
        var time = Text(value, "UpdateTime") ?? Text(value, "CreateTime") ?? "000000";
        return $"{date}T{NormaliseTime(time)}|{key}";
    }

    public static bool IsCancelledOriginal(JsonElement value) => Text(value, "CancelStatus") == "csYes";

    /// <summary>
    /// Only production trees describe how a physical unit was built. Sales, assembly and template trees are
    /// commercial groupings; admitting them would make certificate parts depend on selling configuration.
    /// </summary>
    public static bool ShouldMap(Cx01EntityDefinition entity, JsonElement value) =>
        entity.CanonicalMessage != nameof(BomUpserted)
        || Text(value, "TreeType") == "iProductionTree";

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
        List<Cx01MissingSerialLine> missing, IReadOnlyDictionary<string, string>? currencyMap, string? localCurrency)
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
                baseEntry, IntOrNull(line, "BaseLine"));
            var (price, currency) = LineAmount(document, line, quantity, currencyMap, localCurrency);
            result.Add(new CanonicalLine(lineNo, lineNo.ToString(CultureInfo.InvariantCulture),
                Text(line, "ItemCode") ?? "", quantity, Text(line, "UoMCode") ?? "EA", serials, [],
                Text(line, "WarehouseCode"), baseRef, Text(document, "CardCode"), price, currency));
        }
        return result;
    }

    internal static List<BomUpsertedLine> BomLines(JsonElement document)
    {
        if (!document.TryGetProperty("ProductTreeLines", out var lines)
            || lines.ValueKind != JsonValueKind.Array) return [];
        return lines.EnumerateArray().Select(line =>
        {
            var kind = BomKind(Text(line, "ItemType") ?? Text(line, "LineType") ?? Text(line, "Type"));
            var externalId = kind.Value == "text" ? null : Text(line, "ItemCode");
            return new BomUpsertedLine(
                Int(line, "ChildNum", "VisualOrder"), kind, externalId,
                Text(line, "ItemName") ?? (kind.Value == "text" ? Text(line, "LineText") : null)
                    ?? externalId ?? "",
                Decimal(line, "Quantity"), Text(line, "InventoryUOM"));
        }).ToList();
    }

    private static OpenEnum BomKind(string? value)
    {
        var kind = value?.Trim() switch
        {
            "4" or "item" or "iItem" or "pit_Item" => "item",
            "290" or "resource" or "iResource" or "pit_Resource" => "resource",
            "-18" or "text" or "iText" or "pit_Text" => "text",
            null or "" => "item",
            var unknown => unknown,
        };
        return OpenEnum.Read(kind, new HashSet<string>(["item", "resource", "text"], StringComparer.Ordinal));
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
        return new(Text(value, "DocObjectCode") ?? entity.Collection, original ?? cancellationKey,
            DateOnly.FromDateTime((Date(value, "DocDate") ?? DateTimeOffset.UnixEpoch).Date),
            original ?? cancellationKey,
            baseLine.ValueKind == JsonValueKind.Undefined ? null : IntOrNull(baseLine, "BaseLine"));
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

    // ERP object type 202 is a production order. An issue line based on one is a component going into a build,
    // not stock leaving the business: sent as an adjustment out it would scrap the part. Such lines are left out
    // (with their missing-serial entries) until the as-built link is decided; the raw document is still retained.
    private const string ProductionOrderObjectType = "202";

    private static List<InboundAcceptance> ProductionIssueAcceptances(JsonElement raw,
        IReadOnlyList<CanonicalLine> lines, CanonicalEnvelope source, string retainedRaw,
        IReadOnlyDictionary<string, Cx01ProductionOrderRead?>? productionOrders)
    {
        var issued = lines.Where(line => line.BaseDocumentRef?.Type == ProductionOrderObjectType)
            .GroupBy(line => line.BaseDocumentRef!.ExternalId ?? line.BaseDocumentRef.Number,
                StringComparer.Ordinal);
        var result = new List<InboundAcceptance>();
        foreach (var order in issued)
        {
            var based = order.First().BaseDocumentRef!;
            var orderRead = productionOrders?.GetValueOrDefault(order.Key);
            var payload = new ComponentsIssued(DocumentRef(raw, source.ErpExternalId),
                based with { Type = "production_order" }, order.ToList(), orderRead?.ItemNo,
                orderRead?.PlannedQuantity);
            var envelope = source with
            {
                MessageId = Guid.CreateVersion7(),
                ErpExternalId = source.ErpExternalId + ":" + order.Key,
                ErpChangeMarker = new(source.ErpChangeMarker.Value + "|order:" + order.Key,
                    source.ErpChangeMarker.MarkerKind),
                PayloadRef = source.PayloadRef + "/order/" + order.Key,
            };
            var message = new CanonicalMessage<ComponentsIssued>(envelope, nameof(ComponentsIssued), payload);
            result.Add(new(envelope, nameof(ComponentsIssued),
                JsonSerializer.Serialize(message, IntegrationContracts.Json), retainedRaw));
        }
        return result;
    }

    private static List<CanonicalLine> WithoutProductionIssues(List<CanonicalLine> lines,
        List<Cx01MissingSerialLine> missing)
    {
        var issued = lines.Where(x => x.BaseDocumentRef?.Type == ProductionOrderObjectType)
            .Select(x => x.LineNo).ToHashSet();
        if (issued.Count == 0) return lines;
        missing.RemoveAll(x => issued.Contains(x.LineNo));
        return lines.Where(x => !issued.Contains(x.LineNo)).ToList();
    }

    private static ExternalDocumentRef? BaseDocument(IEnumerable<CanonicalLine> lines) =>
        lines.Select(x => x.BaseDocumentRef).FirstOrDefault(x => x is not null);

    private static ExternalDocumentRef ProductionOrderRef(IReadOnlyList<CanonicalLine> lines, JsonElement raw,
        string key, DateTimeOffset occurred)
    {
        if (BaseDocument(lines) is { } linked)
            return linked with { Type = "production_order" };
        return new("production_order", Text(raw, "BaseRef") ?? key,
            DateOnly.FromDateTime(occurred.Date));
    }

    private static ProductionCompleted ProductionReceipt(JsonElement raw, string key, DateTimeOffset occurred,
        List<CanonicalLine> lines, IReadOnlyDictionary<string, Cx01ProductionOrderRead?>? productionOrders)
    {
        var based = lines.Where(line => line.BaseDocumentRef?.Type == ProductionOrderObjectType).ToList();
        var orderRef = ProductionOrderRef(lines, raw, key, occurred);
        var orderKey = orderRef.ExternalId ?? orderRef.Number;
        var orderRead = productionOrders?.GetValueOrDefault(orderKey);
        var producedItem = orderRead?.ItemNo
            ?? based.FirstOrDefault(line => line.BaseDocumentRef?.Line is null)?.ItemExternalId;
        // With only based component rows and no order enrichment, treating the first component as output would
        // commission a returned part. Empty keeps every such row on the refusal-safe return path.
        producedItem ??= based.Count > 0 || lines.Count == 0 ? "" : lines[0].ItemExternalId;
        var produced = lines.Where(line => line.BaseDocumentRef?.Type != ProductionOrderObjectType
            || line.BaseDocumentRef.Line is null || line.ItemExternalId == producedItem).ToList();
        var returned = based.Where(line => line.BaseDocumentRef?.Line is not null
            && line.ItemExternalId != producedItem).ToList();
        return new(DocumentRef(raw, key), orderRef, producedItem, produced, [], returned,
            orderRead?.PlannedQuantity);
    }

    public static IReadOnlyList<string> ProductionOrderExternalIds(JsonElement raw) => LineElements(raw)
        .Where(line => IntOrNull(line, "BaseType") == 202)
        .Select(line => Text(line, "BaseEntry"))
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!)
        .Distinct(StringComparer.Ordinal)
        .ToList();

    private static string PartnerKind(string? value) => value switch
    {
        "cCustomer" => "customer",
        "cSupplier" => "vendor",
        _ => "both",
    };

    // ISO-shaped values are checked against the actual code list. Customer-defined codes are never guessed: the
    // onboarding survey may suggest a mapping, but only the administrator-confirmed per-connection map is applied.
    // ADR-0010: the ERP converts, Yojana never applies a rate. A line in a currency other than the company's local
    // currency carries the ERP's own local amount: LineTotal is always in local currency (after line discount), so the
    // unit amount is LineTotal / Quantity in the local currency. Without a known local currency the foreign price
    // passes through unchanged and the gateway holds the document for a person.
    private static (decimal? Price, string? Currency) LineAmount(JsonElement document, JsonElement line,
        decimal quantity, IReadOnlyDictionary<string, string>? currencyMap, string? localCurrency)
    {
        var remote = (Text(line, "Currency") ?? Text(document, "DocCurrency"))?.Trim();
        var local = localCurrency?.Trim();
        if (remote is not null && local is { Length: > 0 } && remote != local && quantity != 0
            && DecimalOrNull(line, "LineTotal") is { } localTotal)
            return (localTotal / quantity, Code(local, currencyMap));
        return (DecimalOrNull(line, "Price"), remote is null ? null : Code(remote, currencyMap));
    }

    // DocTotal is the ERP's local-currency total whatever the document currency.
    private static string? DocumentTotalCurrency(JsonElement document, IReadOnlyDictionary<string, string>? currencyMap,
        string? localCurrency) =>
        localCurrency?.Trim() is { Length: > 0 } local ? Code(local, currencyMap) : Currency(document, currencyMap);

    private static string? Currency(JsonElement document, IReadOnlyDictionary<string, string>? currencyMap)
    {
        var remote = Text(document, "DocCurrency")?.Trim();
        return remote is null ? null : Code(remote, currencyMap);
    }

    private static string Code(string remote, IReadOnlyDictionary<string, string>? currencyMap)
    {
        if (Iso4217Codes.Contains(remote)) return remote;
        if (currencyMap?.GetValueOrDefault(remote) is { } mapped && Iso4217Codes.Contains(mapped)) return mapped;
        throw new Cx01CurrencyNotMappedException(remote);
    }

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
        value.TryGetProperty(property, out var found) ? Cx01Json.Int32(found) ?? 0 : 0;

    private static int Int(JsonElement value, params string[] properties)
    {
        foreach (var property in properties)
            if (value.TryGetProperty(property, out var found) && Cx01Json.Int32(found) is { } number) return number;
        return 0;
    }

    private static int? IntOrNull(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) ? Cx01Json.Int32(found) : null;

    private static decimal Decimal(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) ? Cx01Json.Decimal(found) ?? 0 : 0;

    private static decimal? DecimalOrNull(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) ? Cx01Json.Decimal(found) : null;

    private static bool Flag(JsonElement value, string property) =>
        Text(value, property) is "tYES" or "Y" or "true" or "True";

    private static bool IsInventoryItem(JsonElement value) =>
        Text(value, "InventoryItem") is not ("tNO" or "N" or "false" or "False");

    private static DateTimeOffset? Date(JsonElement value, string property) =>
        DateTimeOffset.TryParse(Text(value, property), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var result) ? result : null;
}
