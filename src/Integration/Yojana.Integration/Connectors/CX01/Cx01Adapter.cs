using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Outbox;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>One adapter owns capture and delegates document posting through the outbound coordinator.</summary>
public sealed class Cx01Adapter(
    ITenantContext tenant,
    IClock clock,
    IntegrationDbContext db,
    GatewayReferenceStore references,
    ICx01BomSyncStateReader bomSyncState,
    Cx01Settings settings,
    Cx01Client client,
    ICx01SerialHistory serialHistory,
    ICx01DocumentCaptureStore capturedDocuments,
    ICx01AttributionTaskSink attributionTasks,
    ICx01OwnWriteStore ownWrites,
    ICx01InboundRejectionSink inboundRejections,
    ICx01SurveyReader surveyReader,
    ICx01TrackingPermissionSink trackingPermissions,
    ITenantTimeZoneReadPort timeZones,
    Cx01OutboundDocuments? documents = null,
    Cx01NativeWarrantyWriter? nativeWarranty = null,
    Cx01BomSyncSchedule? bomSchedule = null) : IConnectorAdapter, IOutboundConnector,
    IConnectorWatermarkSeed, IInboundRetryPreparer
{
    public string ConnectorCode => Cx01Capabilities.ConnectorCode;
    public bool OwnsConnectionLane => true;
    public OutboundConnectorCapabilities Capabilities { get; } = new(false, OwnsConnectionLane: true);

    // HIDDEN COUPLING (OD-60) with SourceExtractor.SourceClockAsync: migration extraction supplies the source-local
    // clock and the connection identifies its server time zone. Converting here keeps the hand-over marker on the UTC
    // scale used by remote update stamps. If either side stops supplying source-local time or the connection identity,
    // the first poll after hand-over can skip or repeat documents.
    public async Task<string?> StartingWatermarkAsync(Guid connectionId, DateTime sourceLocalTime,
        CancellationToken cancellationToken)
    {
        var zone = await ServerTimeZoneAsync(connectionId, cancellationToken);
        var utc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(sourceLocalTime, DateTimeKind.Unspecified), zone);
        return new Cx01Watermark(new DateTimeOffset(utc, TimeSpan.Zero), "").ToString();
    }

    public string WatermarkAt(DateTime utcInstant) => new Cx01Watermark(
        new DateTimeOffset(DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc)), "").ToString();
    public DateTime? InstantOf(string watermark) => Cx01Watermark.Parse(watermark) is var value
        && value != Cx01Watermark.Empty ? value.Timestamp.UtcDateTime : null;
    public bool IsMasterData(string entityType) => entityType is "items" or "item"
        or "product_trees" or "product_tree" or "ProductTrees"
        or "business_partners" or "business_partner" or "Items" or "BusinessPartners";

    public bool Applies(InboundMessage message) => message.ConnectorCode == ConnectorCode
        && message.AttentionReasonCode is "erp_currency_not_mapped" or "currency_not_home";

    public async Task PrepareAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        var entity = Entity(message.ErpEntity);
        var configured = await settings.GetAsync(message.ConnectionId, cancellationToken);
        using var raw = JsonDocument.Parse(message.RawPayload);
        Cx01MappedAcceptance mapped;
        try
        {
            mapped = await MapWithSerialFallbackAsync(message.ConnectionId, entity, raw.RootElement, configured,
                message.CaptureMode, cancellationToken);
        }
        catch (Cx01CurrencyNotMappedException exception)
        {
            throw new Yojana.Platform.Contracts.CommandRefusedException("erp_currency_not_mapped",
                exception.Message, "Confirm this ERP currency on the connection settings screen.");
        }
        message.MessageType = mapped.Acceptance.MessageType;
        message.CanonicalMessage = mapped.Acceptance.CanonicalMessage;
        message.ErpChangeMarker = mapped.Acceptance.Envelope.ErpChangeMarker.Value;
        message.ContractVersion = mapped.Acceptance.Envelope.ContractVersion;
    }

    public async Task<Connections.FullHistoryEstimate> EstimateFullHistoryAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var entity in Cx01Protocol.Entities)
            {
                using var response = await client.GetAsync(connectionId,
                    Cx01Protocol.Collection(entity.Value) + "?$count=true&$top=0", cancellationToken);
                if (!response.RootElement.TryGetProperty("@odata.count", out var count)
                    || Cx01Json.Int64(count) is not { } value)
                    return new("unknown", null);
                counts[entity.Key] = value;
            }
            return new("known", counts.Values.Sum(), counts);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new("unknown", null);
        }
    }

    public async Task<ConnectorPollResult> PollAsync(ConnectorPollRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenant(request.TenantId);
        var entity = Entity(request.EntityType);
        if (entity.CanonicalMessage == nameof(BomUpserted))
            return await PollBomsAsync(request, entity, cancellationToken);
        var configured = await settings.GetAsync(request.ConnectionId, cancellationToken);
        var cursor = Cx01Watermark.Parse(request.Watermark,
            await ServerTimeZoneAsync(request.ConnectionId, cancellationToken));
        var from = cursor.Timestamp == DateTimeOffset.MinValue
            ? DateTimeOffset.UnixEpoch : cursor.Timestamp - request.OverlapWindow;
        var keys = await ChangedKeysAsync(request.ConnectionId, entity, from, null,
            request.MaximumEvents, cancellationToken);
        var acceptances = new List<InboundAcceptance>(keys.Count);
        var next = cursor;
        foreach (var changed in keys.OrderBy(x => x.Timestamp).ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            JsonDocument document;
            try
            {
                document = await client.GetAsync(request.ConnectionId,
                    Cx01Protocol.ByKey(entity, changed.Key), cancellationToken);
            }
            catch (Cx01RemoteException exception) when (exception.IsRecordGone)
            {
                next = Cx01Watermark.Max(next, changed);
                continue;
            }
            catch (Cx01RemoteException exception) when (exception.IsBusinessRejection)
            {
                await inboundRejections.ParkAsync(request.ConnectionId, entity.Collection, changed.Key, exception,
                    cancellationToken);
                next = Cx01Watermark.Max(next, changed);
                continue;
            }
            using (document)
            {
                if (!Cx01Mapper.ShouldMap(entity, document.RootElement))
                {
                    next = Cx01Watermark.Max(next, changed);
                    continue;
                }
                if (Cx01Mapper.IsCancelledOriginal(document.RootElement)
                    && await WasCapturedAsync(request.ConnectionId, entity, changed.Key, cancellationToken))
                {
                    next = Cx01Watermark.Max(next, changed);
                    continue;
                }
                if (await IsOwnWriteAsync(request.ConnectionId, entity, document.RootElement, cancellationToken))
                {
                    next = Cx01Watermark.Max(next, changed);
                    continue;
                }
                try
                {
                    var mapped = await MapWithSerialFallbackAsync(request.ConnectionId, entity, document.RootElement,
                        configured, "poll", cancellationToken);
                    acceptances.AddRange(mapped.Acceptances);
                    if (mapped.MissingSerialLines.Count > 0)
                        await attributionTasks.RaiseAsync(request.ConnectionId, entity.Collection, changed.Key,
                            mapped.MissingSerialLines, cancellationToken);
                }
                catch (Cx01CurrencyNotMappedException exception)
                {
                    await inboundRejections.ParkDocumentAsync(request.ConnectionId, entity.Collection,
                        entity.CanonicalMessage, changed.Key,
                        document.RootElement, "erp_currency_not_mapped", exception.Message, cancellationToken);
                }
                catch (Exception exception) when (IsDocumentReadFailure(exception))
                {
                    await inboundRejections.ParkDocumentAsync(request.ConnectionId, entity.Collection,
                        entity.CanonicalMessage, changed.Key,
                        document.RootElement, "erp_document_unreadable", exception.Message, cancellationToken);
                }
            }
            next = Cx01Watermark.Max(next, changed);
        }
        return new(acceptances, next == cursor ? request.Watermark : next.ToString());
    }

    public async Task<IReadOnlyList<ConnectorEntityDayDigest>> EntityDayDigestsAsync(Guid connectionId,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken)
    {
        var messages = new List<InboundAcceptance>();
        foreach (var entity in Cx01Protocol.Entities.Values.OrderBy(value => value.Collection,
                     StringComparer.Ordinal).Where(value => value.CanonicalMessage != nameof(BomUpserted)))
        {
            messages.AddRange(await ReadMappedRangeAsync(connectionId, entity, rangeStart, rangeEnd,
                cursor: null, pageSize: int.MaxValue, captureMode: "replay", cancellationToken));
        }

        return messages.Where(message =>
                DateOnly.FromDateTime(message.Envelope.OccurredAt.UtcDateTime) >= rangeStart
                && DateOnly.FromDateTime(message.Envelope.OccurredAt.UtcDateTime) <= rangeEnd)
            .GroupBy(message => new
            {
                message.Envelope.ErpEntity,
                Day = DateOnly.FromDateTime(message.Envelope.OccurredAt.UtcDateTime),
            })
            .Select(group =>
            {
                var metrics = ReconciliationHash.Metrics(group.Select(message => message.CanonicalMessage));
                return new ConnectorEntityDayDigest(group.Key.ErpEntity, group.Key.Day, group.LongCount(),
                    metrics.Quantity, metrics.DistinctSerials, ReconciliationHash.KeyFields(group.Select(message =>
                        (message.Envelope.ErpExternalId, message.CanonicalMessage))));
            }).ToList();
    }

    public async Task<IReadOnlyList<ConnectorOnHandBalance>> OnHandAsync(ConnectorOnHandRequest request,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(request.ConnectionId,
            Cx01Protocol.Root + "ItemWarehouseInfoCollection?$select=ItemCode,WarehouseCode,InStock", cancellationToken);
        var rows = Values(response.RootElement).Select(row => new
        {
            ItemCode = RequiredText(row, "ItemCode"),
            WarehouseCode = RequiredText(row, "WarehouseCode"),
            Quantity = Number(row, "InStock"),
        }).Where(row => !request.WarehouseStatusMapping.TryGetValue(row.WarehouseCode, out var included)
            || included).ToList();
        var result = new List<ConnectorOnHandBalance>(rows.Count);
        foreach (var row in rows)
        {
            var itemId = await references.ResolveAsync(request.ConnectionId, "item", row.ItemCode,
                provisional: false, cancellationToken);
            var locationId = await references.ResolveAsync(request.ConnectionId, "location", row.WarehouseCode,
                provisional: false, cancellationToken);
            var itemName = await db.ReferenceProjections.AsNoTracking().Where(value =>
                    value.TenantId == tenant.TenantId && value.EntityType == "item" && value.ModuleId == itemId)
                .Select(value => value.DisplayName).SingleOrDefaultAsync(cancellationToken);
            result.Add(new(itemId, row.ItemCode, itemName, locationId, row.WarehouseCode, null, row.Quantity));
        }
        return result;
    }

    public Task<bool> SupportsRangedReadsAsync(Guid connectionId, string entity,
        CancellationToken cancellationToken) => Task.FromResult(TryEntity(entity, out var definition)
            && definition.CanonicalMessage != nameof(BomUpserted));

    public async Task<ConnectorRangePage> ReadRangePageAsync(Guid connectionId, string entity,
        DateOnly rangeStart, DateOnly rangeEnd, string? cursor, int pageSize,
        CancellationToken cancellationToken)
    {
        var definition = Entity(entity);
        if (definition.CanonicalMessage == nameof(BomUpserted))
            throw new InvalidOperationException("CX01 production trees have no source change date and do not support ranged reads.");
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var from = new DateTimeOffset(rangeStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(rangeEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var after = Cx01Watermark.Parse(cursor);
        var allKeys = await RangeKeysAsync(connectionId, definition, from, to, cancellationToken);
        var keys = allKeys.Where(key => after == Cx01Watermark.Empty || key.CompareTo(after) > 0)
            .OrderBy(key => key.Timestamp).ThenBy(key => key.Key, StringComparer.Ordinal).ToList();
        var messages = new List<InboundAcceptance>(Math.Min(keys.Count, pageSize));
        var examined = 0;
        Cx01Watermark? last = null;
        foreach (var changed in keys)
        {
            examined++;
            last = changed;
            using var document = await client.GetAsync(connectionId,
                Cx01Protocol.ByKey(definition, changed.Key), cancellationToken);
            if (!Cx01Mapper.ShouldMap(definition, document.RootElement)) continue;
            if (Cx01Mapper.IsCancelledOriginal(document.RootElement)
                && await WasCapturedAsync(connectionId, definition, changed.Key, cancellationToken)) continue;
            if (!await IsOwnWriteAsync(connectionId, definition, document.RootElement, cancellationToken))
                messages.AddRange((await MapWithSerialFallbackAsync(connectionId, definition, document.RootElement,
                    configured, "backfill", cancellationToken)).Acceptances);
            if (messages.Count == pageSize) break;
        }
        return new(messages, examined < keys.Count ? last?.ToString() : null, allKeys.Count);
    }

    public async Task<OutboundDocument?> FindByReferenceAsync(OutboundLookup lookup,
        CancellationToken cancellationToken)
    {
        if (lookup.CommandType == nameof(PushNativeWarrantyObject))
            return await nativeWarranty!.FindAsync(lookup, cancellationToken);
        var configured = await settings.GetAsync(lookup.ConnectionId, cancellationToken);
        var mode = await OutboundModeAsync(lookup.ConnectionId, cancellationToken);
        if (mode == "documents" && !(lookup.CommandType == nameof(CreateServiceOrder) && configured.Documents?.ServiceOrderAction == "annotate"))
        {
            try { return await documents!.FindAsync(lookup, cancellationToken); }
            catch (Cx01DocumentRuleException) { return null; }
        }
        if (mode is not ("reference_only" or "documents")) return null;
        var field = lookup.CommandType switch
        {
            nameof(AttachDocument) => configured.CertificateUrlField,
            nameof(CreateServiceOrder) => configured.RmaReferenceField,
            _ => null,
        };
        if (field is null) return null;
        var collection = lookup.CommandType == nameof(AttachDocument) ? "CustomerEquipmentCards" : "Returns";
        var expected = configured.OwnReferencePrefix + lookup.ErpReferenceText;
        var path = Cx01Protocol.Root + collection + "?$select=DocEntry,UpdateDate,UpdateTime&$filter="
            + Uri.EscapeDataString(field + " eq '" + expected.Replace("'", "''", StringComparison.Ordinal) + "'")
            + "&$top=1";
        using var response = await client.GetAsync(lookup.ConnectionId, path, cancellationToken);
        var row = Values(response.RootElement).FirstOrDefault();
        if (row.ValueKind == JsonValueKind.Undefined) return null;
        var id = Text(row, "DocEntry") ?? Text(row, "EquipmentCardNum");
        return id is null ? null : new(id, Cx01Mapper.ChangeMarker(row, id));
    }

    public async Task<OutboundCreateResult> CreateAsync(OutboundCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CommandType == nameof(PushNativeWarrantyObject))
            return await nativeWarranty!.UpdateAsync(request, cancellationToken);
        var mode = await OutboundModeAsync(request.ConnectionId, cancellationToken);
        if (mode == "documents")
        {
            var configuredDocuments = await settings.GetAsync(request.ConnectionId, cancellationToken);
            if (!(request.CommandType == nameof(CreateServiceOrder) && configuredDocuments.Documents?.ServiceOrderAction == "annotate"))
                return await documents!.CreateAsync(request, cancellationToken);
        }
        else if (mode != "reference_only") return OutboundCreateResult.Unsupported("Outbound posting is disabled on this connection.");
        var configured = await settings.GetAsync(request.ConnectionId, cancellationToken);
        using var payload = JsonDocument.Parse(request.Payload);
        var root = payload.RootElement.TryGetProperty("payload", out var nested) ? nested : payload.RootElement;
        string? field;
        string? value;
        string? collection;
        if (request.CommandType == nameof(AttachDocument))
        {
            field = configured.CertificateUrlField;
            value = Text(root, "url");
            collection = "CustomerEquipmentCards";
        }
        else if (request.CommandType == nameof(CreateServiceOrder))
        {
            field = configured.RmaReferenceField;
            value = request.ErpReferenceText;
            collection = "Returns";
        }
        else
        {
            return OutboundCreateResult.Unsupported("CX01 reference-only mode never creates stock or financial postings.");
        }
        if (field is null || value is null)
            return OutboundCreateResult.Rejected("The required CX01 reference field is not configured.");
        var target = Text(root, "target_record_ref") ?? Text(root, "module_document_ref")
            ?? request.ErpReferenceText;
        var key = target.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(key))
            return OutboundCreateResult.Rejected("The CX01 reference target is missing.");
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [field] = configured.OwnReferencePrefix + value,
        };
        await Cx01MandatoryDefaults.ApplyAsync(surveyReader, request.ConnectionId, collection, body, configured, cancellationToken);
        try
        {
            using var response = await client.SendJsonAsync(request.ConnectionId, HttpMethod.Patch,
                Cx01Protocol.Root + collection + "(" + Cx01Protocol.KeyLiteral(key) + ")", body, true,
                cancellationToken);
            using var observed = await client.GetAsync(request.ConnectionId,
                Cx01Protocol.Root + collection + "(" + Cx01Protocol.KeyLiteral(key) + ")", cancellationToken);
            return OutboundCreateResult.Created(key, Cx01Mapper.ChangeMarker(observed.RootElement, key));
        }
        catch (Cx01RemoteException exception) when (exception.IsBusinessRejection)
        {
            return OutboundCreateResult.Rejected(exception.BusinessMessage());
        }
    }

    internal async Task<Cx01MappedAcceptance?> CaptureKeyAsync(Guid connectionId, string collection, string key,
        CancellationToken cancellationToken)
    {
        var entity = Cx01Protocol.Entities.Values.SingleOrDefault(x =>
            StringComparer.Ordinal.Equals(x.Collection, collection));
        if (entity is null) return null;
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        JsonDocument document;
        try { document = await client.GetAsync(connectionId, Cx01Protocol.ByKey(entity, key), cancellationToken); }
        catch (Cx01RemoteException exception) when (exception.IsRecordGone)
        {
            return entity.CanonicalMessage == nameof(BomUpserted)
                ? await MapDeletedBomIfChangedAsync(connectionId, entity, key, "webhook", cancellationToken)
                : null;
        }
        catch (Cx01RemoteException exception) when (exception.IsBusinessRejection)
        {
            await inboundRejections.ParkAsync(connectionId, entity.Collection, key, exception, cancellationToken);
            return null;
        }
        using (document)
        {
            if (!Cx01Mapper.ShouldMap(entity, document.RootElement)) return null;
            if (Cx01Mapper.IsCancelledOriginal(document.RootElement)
                && await WasCapturedAsync(connectionId, entity, key, cancellationToken)) return null;
            if (await IsOwnWriteAsync(connectionId, entity, document.RootElement, cancellationToken)) return null;
            Cx01MappedAcceptance mapped;
            try
            {
                mapped = await MapWithSerialFallbackAsync(connectionId, entity, document.RootElement, configured,
                    "webhook", cancellationToken);
            }
            catch (Cx01CurrencyNotMappedException exception)
            {
                await inboundRejections.ParkDocumentAsync(connectionId, entity.Collection, entity.CanonicalMessage,
                    key, document.RootElement,
                    "erp_currency_not_mapped", exception.Message, cancellationToken);
                return null;
            }
            catch (Exception exception) when (IsDocumentReadFailure(exception))
            {
                await inboundRejections.ParkDocumentAsync(connectionId, entity.Collection, entity.CanonicalMessage,
                    key, document.RootElement,
                    "erp_document_unreadable", exception.Message, cancellationToken);
                return null;
            }
            if (entity.CanonicalMessage == nameof(BomUpserted)
                && !await BomChangedAsync(connectionId, key, mapped, deleted: false, cancellationToken)) return null;
            if (mapped.MissingSerialLines.Count > 0)
                await attributionTasks.RaiseAsync(connectionId, entity.Collection, key, mapped.MissingSerialLines,
                    cancellationToken);
            return mapped;
        }
    }

    private async Task<ConnectorPollResult> PollBomsAsync(ConnectorPollRequest request,
        Cx01EntityDefinition entity, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(request.ConnectionId, cancellationToken);
        // Without a schedule (tests that build the adapter by hand) every wake reads, as before.
        if (bomSchedule is not null
            && !await bomSchedule.DueAsync(configured, request.Watermark, clock.UtcNow, cancellationToken))
            return new([], request.Watermark);
        var states = new Dictionary<string, Cx01BomSyncState>(
            await bomSyncState.ReadAsync(request.ConnectionId, cancellationToken), StringComparer.Ordinal);
        var observed = clock.UtcNow;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var acceptances = new List<InboundAcceptance>();
        var filter = Uri.EscapeDataString("TreeType eq 'iProductionTree'");
        var path = Cx01Protocol.Collection(entity) + "?$filter=" + filter + "&$top="
            + configured.PollPageSize.ToString(CultureInfo.InvariantCulture);
        while (path is not null)
        {
            using var page = await client.GetAsync(request.ConnectionId, path, cancellationToken);
            foreach (var raw in Values(page.RootElement))
            {
                if (!Cx01Mapper.ShouldMap(entity, raw)) continue;
                var key = RequiredText(raw, entity.KeyField);
                seen.Add(key);
                var mapped = Cx01Mapper.Map(tenant.TenantId, request.ConnectionId, entity, raw,
                    new Dictionary<int, IReadOnlyList<CanonicalSerial>>(),
                    new Dictionary<int, IReadOnlyList<CanonicalSerial>>(), new HashSet<int>(), "poll", observed);
                var hash = mapped.ContentHash!;
                if (states.TryGetValue(key, out var state) && !state.Deleted && state.ContentHash == hash) continue;
                acceptances.Add(mapped.Acceptance);
                states[key] = new(hash, false);
            }
            path = page.RootElement.TryGetProperty("@odata.nextLink", out var next)
                ? Cx01Protocol.NextPage(next.GetString()) : null;
        }

        // Absence means deletion only after every page completed. An exception leaves this method before this point.
        foreach (var missing in states.Where(pair => !pair.Value.Deleted && !seen.Contains(pair.Key))
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var deleted = MapDeletedBom(request.ConnectionId, entity, missing.Key, "poll", observed);
            acceptances.Add(deleted.Acceptance);
        }
        return new(acceptances, Cx01BomSyncSchedule.Watermark(observed));
    }

    private async Task<Cx01MappedAcceptance?> MapDeletedBomIfChangedAsync(Guid connectionId,
        Cx01EntityDefinition entity, string key, string captureMode, CancellationToken cancellationToken)
    {
        var states = await bomSyncState.ReadAsync(connectionId, cancellationToken);
        if (!states.TryGetValue(key, out var state) || state.Deleted) return null;
        return MapDeletedBom(connectionId, entity, key, captureMode, clock.UtcNow);
    }

    private Cx01MappedAcceptance MapDeletedBom(Guid connectionId, Cx01EntityDefinition entity, string key,
        string captureMode, DateTime observed)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            TreeCode = key,
            TreeType = "iProductionTree",
            Deleted = "tYES",
            ProductTreeLines = Array.Empty<object>(),
        }, Cx01Protocol.Json));
        return Cx01Mapper.Map(tenant.TenantId, connectionId, entity, document.RootElement,
            new Dictionary<int, IReadOnlyList<CanonicalSerial>>(),
            new Dictionary<int, IReadOnlyList<CanonicalSerial>>(), new HashSet<int>(), captureMode, observed);
    }

    private async Task<bool> BomChangedAsync(Guid connectionId, string key, Cx01MappedAcceptance mapped,
        bool deleted, CancellationToken cancellationToken)
    {
        var states = await bomSyncState.ReadAsync(connectionId, cancellationToken);
        return !states.TryGetValue(key, out var state) || state.Deleted != deleted
            || state.ContentHash != mapped.ContentHash;
    }

    private async Task<Cx01MappedAcceptance> MapWithSerialFallbackAsync(Guid connectionId,
        Cx01EntityDefinition entity, JsonElement raw, Cx01ConnectionSettings configured,
        string captureMode, CancellationToken cancellationToken)
    {
        var key = RequiredText(raw, entity.KeyField);
        var previous = entity.CanonicalMessage == "DeliveryPosted"
            ? await serialHistory.ForDocumentAsync(connectionId, entity.Collection, key, cancellationToken)
            : new Dictionary<int, IReadOnlyList<CanonicalSerial>>();
        var candidates = Cx01Mapper.MissingSerialLines(raw)
            .Where(x => !previous.TryGetValue(x.LineNo, out var earlier) || earlier.Count == 0).ToList();
        var managed = new HashSet<int>();
        foreach (var line in candidates)
        {
            using var item = await client.GetAsync(connectionId,
                Cx01Protocol.Root + "Items(" + Cx01Protocol.KeyLiteral(line.ItemCode) + ")", cancellationToken);
            if (Text(item.RootElement, "ManageSerialNumbers") is "tYES" or "Y") managed.Add(line.LineNo);
        }
        var recovered = managed.Count == 0
            ? new Dictionary<int, IReadOnlyList<CanonicalSerial>>()
            : await RecoverSerialsAsync(connectionId, entity, key, candidates.Where(x => managed.Contains(x.LineNo)),
                configured, cancellationToken);
        return Cx01Mapper.Map(tenant.TenantId, connectionId, entity, raw, previous, recovered, managed, captureMode,
            clock.UtcNow, Cx01Mapper.IsCancelledOriginal(raw),
            new Cx01FieldReader(Cx01FieldMap.Effective(configured.FieldMap)), configured.CurrencyMap);
    }

    private async Task<Dictionary<int, IReadOnlyList<CanonicalSerial>>> RecoverSerialsAsync(Guid connectionId,
        Cx01EntityDefinition entity, string externalId, IEnumerable<Cx01MissingSerialLine> lines,
        Cx01ConnectionSettings configured, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, IReadOnlyList<CanonicalSerial>>();
        if (configured.SerialLinkViewEnabled && configured.SerialLinkViewName is not null)
        {
            var filter = Uri.EscapeDataString("DocumentType eq '" + entity.Collection.Replace("'", "''",
                StringComparison.Ordinal) + "' and DocumentEntry eq '" + externalId.Replace("'", "''",
                StringComparison.Ordinal) + "'");
            using var view = await client.GetAsync(connectionId,
                Cx01Protocol.Root + configured.SerialLinkViewName + "?$filter=" + filter, cancellationToken);
            AddLinkedSerials(result, Values(view.RootElement));
            if (result.Count > 0) return result;
        }

        var permitted = Cx01TrackingPermissions.Declared(configured).Where(pair => pair.Value.State == "allowed")
            .Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (permitted.IsSupersetOf(["OITL", "ITL1", "OSRN"]))
        {
            try
            {
                using var governed = await client.GetAsync(connectionId,
                    Cx01Protocol.Root + Cx01Protocol.SqlQueries + "('" + Cx01Protocol.SerialLinkQueryCode
                    + "')/List?documentType=" + Uri.EscapeDataString(Cx01Protocol.ObjectType(entity))
                    + "&documentEntry=" + Uri.EscapeDataString(externalId), cancellationToken);
                AddLinkedSerials(result, Values(governed.RootElement), entity.CanonicalMessage == "DeliveryPosted");
                if (result.Count > 0) return result;
            }
            catch (Cx01RemoteException exception) when (exception.RemoteCode == "702")
            {
                await trackingPermissions.RecordDeniedAsync(connectionId, exception.BusinessMessage(), cancellationToken);
                await settings.SetAsync(connectionId, configured with { AllowlistedTrackingTables = [] },
                    cancellationToken);
                await attributionTasks.RaiseQueryPermissionAsync(connectionId, exception.BusinessMessage(),
                    cancellationToken);
            }
        }
        return result;
    }

    private async Task<List<InboundAcceptance>> ReadMappedRangeAsync(Guid connectionId,
        Cx01EntityDefinition entity, DateOnly rangeStart, DateOnly rangeEnd, string? cursor,
        int pageSize, string captureMode, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var from = new DateTimeOffset(rangeStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(rangeEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var after = Cx01Watermark.Parse(cursor);
        var keys = await RangeKeysAsync(connectionId, entity, from, to, cancellationToken);
        var messages = new List<InboundAcceptance>();
        foreach (var changed in keys.Where(key => after == Cx01Watermark.Empty || key.CompareTo(after) > 0)
                     .OrderBy(key => key.Timestamp).ThenBy(key => key.Key, StringComparer.Ordinal))
        {
            using var document = await client.GetAsync(connectionId,
                Cx01Protocol.ByKey(entity, changed.Key), cancellationToken);
            if (!Cx01Mapper.ShouldMap(entity, document.RootElement)) continue;
            if (Cx01Mapper.IsCancelledOriginal(document.RootElement)
                && await WasCapturedAsync(connectionId, entity, changed.Key, cancellationToken)) continue;
            if (await IsOwnWriteAsync(connectionId, entity, document.RootElement, cancellationToken)) continue;
            messages.AddRange((await MapWithSerialFallbackAsync(connectionId, entity, document.RootElement,
                configured, captureMode, cancellationToken)).Acceptances);
            if (messages.Count == pageSize) break;
        }
        return messages;
    }

    private async Task<List<Cx01Watermark>> ChangedKeysAsync(Guid connectionId, Cx01EntityDefinition entity,
        DateTimeOffset fromInclusive, DateTimeOffset? toExclusive, int maximum, CancellationToken cancellationToken)
        => await QueryKeysAsync(connectionId, entity, "UpdateDate", fromInclusive, toExclusive,
            maximum, restrictToWatermarkRange: true, cancellationToken);

    private async Task<List<Cx01Watermark>> RangeKeysAsync(Guid connectionId, Cx01EntityDefinition entity,
        DateTimeOffset fromInclusive, DateTimeOffset toExclusive, CancellationToken cancellationToken)
    {
        var businessDateField = entity.CanonicalMessage is "ItemUpserted" or "PartyUpserted"
            ? "UpdateDate" : "DocDate";
        return await QueryKeysAsync(connectionId, entity, businessDateField, fromInclusive, toExclusive,
            int.MaxValue, restrictToWatermarkRange: false, cancellationToken);
    }

    private async Task<List<Cx01Watermark>> QueryKeysAsync(Guid connectionId, Cx01EntityDefinition entity,
        string filterField, DateTimeOffset fromInclusive, DateTimeOffset? toExclusive, int maximum,
        bool restrictToWatermarkRange, CancellationToken cancellationToken)
    {
        var result = new List<Cx01Watermark>();
        var zone = await ServerTimeZoneAsync(connectionId, cancellationToken);
        var filterFrom = restrictToWatermarkRange ? TimeZoneInfo.ConvertTime(fromInclusive, zone) : fromInclusive;
        var filterUntil = restrictToWatermarkRange && toExclusive is { } utcUntil
            ? TimeZoneInfo.ConvertTime(utcUntil, zone) : toExclusive;
        var filter = filterField + " ge '"
            + filterFrom.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
        if (filterUntil is { } until)
            filter += " and " + filterField + " lt '"
                + until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
        var path = Cx01Protocol.Collection(entity) + "?$select=" + entity.KeyField
            + (entity.HasUpdateTime ? ",UpdateDate,UpdateTime" : ",UpdateDate") + (filterField == "UpdateDate" ? "" : "," + filterField)
            + "&$filter=" + Uri.EscapeDataString(filter)
            + "&$orderby=UpdateDate," + (entity.HasUpdateTime ? "UpdateTime," : "") + entity.KeyField + "&$top="
            + Math.Min(maximum, 1_000).ToString(CultureInfo.InvariantCulture);
        while (result.Count < maximum)
        {
            using var response = await client.GetAsync(connectionId, path, cancellationToken);
            foreach (var row in Values(response.RootElement))
            {
                var key = RequiredText(row, entity.KeyField);
                var marker = Cx01Watermark.From(row, key, zone);
                // Without a change time a marker is the day's midnight; compare whole days, or a record changed later
                // that day falls before a time-of-day watermark and is never read. Same-day re-reads deduplicate.
                var from = entity.HasUpdateTime ? fromInclusive : new DateTimeOffset(
                    TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(filterFrom.Date, DateTimeKind.Unspecified), zone),
                    TimeSpan.Zero);
                if (!restrictToWatermarkRange || marker.Timestamp >= from
                    && (toExclusive is null || marker.Timestamp < toExclusive))
                    result.Add(marker);
                if (result.Count == maximum) break;
            }
            if (result.Count == maximum || !response.RootElement.TryGetProperty("@odata.nextLink", out var next)
                || string.IsNullOrWhiteSpace(next.GetString())) break;
            path = Cx01Protocol.NextPage(next.GetString())!;
        }
        return result;
    }

    private async Task<TimeZoneInfo> ServerTimeZoneAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.TryGetAsync(connectionId, cancellationToken);
        var id = configured?.ErpServerTimeZone ?? await timeZones.GetAsync(cancellationToken);
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    private static bool IsDocumentReadFailure(Exception exception) =>
        exception is not Cx01RemoteException
        && (exception is JsonException or FormatException or InvalidOperationException);

    private async Task<string> OutboundModeAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.ConnectionGatewaySettings.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).Select(x => x.OutboundMode).SingleAsync(cancellationToken);

    private void EnsureTenant(TenantId requested)
    {
        if (requested != tenant.TenantId) throw new InvalidOperationException("CX01 tenant mismatch.");
    }

    private static Cx01EntityDefinition Entity(string entityType) => TryEntity(entityType, out var entity)
        ? entity : throw new InvalidOperationException($"CX01 entity type '{entityType}' is not supported.");

    private static bool TryEntity(string entityType, out Cx01EntityDefinition entity)
    {
        var key = entityType switch
        {
            "item" => "items",
            "product_tree" => "product_trees",
            "business_partner" => "business_partners",
            "delivery" => "deliveries",
            "return" => "returns",
            "invoice" => "invoices",
            _ => entityType,
        };
        if (Cx01Protocol.Entities.TryGetValue(key, out var keyed))
        {
            entity = keyed;
            return true;
        }
        var byCollection = Cx01Protocol.Entities.Values.FirstOrDefault(value =>
            StringComparer.Ordinal.Equals(value.Collection, entityType));
        entity = byCollection!;
        return byCollection is not null;
    }

    private static List<JsonElement> Values(JsonElement root) =>
        root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().ToList() : [];

    private static void AddLinkedSerials(Dictionary<int, IReadOnlyList<CanonicalSerial>> target,
        IEnumerable<JsonElement> rows, bool deliveryOnly = false)
    {
        var eligible = rows.Where(row => !deliveryOnly || Number(row, "Quantity") < 0);
        foreach (var group in eligible.GroupBy(row => Int(row, "DocLine") != 0
                     ? Int(row, "DocLine") : Int(row, "DocumentLine")))
        {
            var serials = group.Select(row => new CanonicalSerial(
                    Text(row, "DistNumber") ?? RequiredText(row, "SerialNumber"), null,
                    Text(row, "SysNumber") ?? Text(row, "SystemSerialNumber")))
                .DistinctBy(serial => serial.Value).ToList();
            if (serials.Count > 0) target[group.Key] = serials;
        }
    }

    private async Task<bool> IsOwnWriteAsync(Guid connectionId, Cx01EntityDefinition entity, JsonElement document,
        CancellationToken cancellationToken)
    {
        var key = RequiredText(document, entity.KeyField);
        return await ownWrites.MatchesAsync(connectionId, key, Cx01Mapper.ChangeMarker(document, key),
            cancellationToken) || await ownWrites.MatchesAsync(connectionId,
            Cx01DocumentCatalogue.ExternalId(entity.Collection, key), Cx01Mapper.ChangeMarker(document, key), cancellationToken);
    }

    private Task<bool> WasCapturedAsync(Guid connectionId, Cx01EntityDefinition entity, string key,
        CancellationToken cancellationToken) => capturedDocuments.WasCapturedAsync(connectionId,
        entity.Collection, key, cancellationToken);

    private static string RequiredText(JsonElement value, string property) => Text(value, property)
        ?? throw new InvalidOperationException("CX01 response is missing a required property.");
    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;
    private static int Int(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) ? Cx01Json.Int32(found) ?? 0 : 0;
    private static decimal Number(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) ? Cx01Json.Decimal(found) ?? 0 : 0;
}

internal readonly record struct Cx01Watermark(DateTimeOffset Timestamp, string Key) : IComparable<Cx01Watermark>
{
    public static Cx01Watermark Empty { get; } = new(DateTimeOffset.MinValue, "");

    public int CompareTo(Cx01Watermark other)
    {
        var time = Timestamp.CompareTo(other.Timestamp);
        return time != 0 ? time : StringComparer.Ordinal.Compare(Key, other.Key);
    }

    // "|utc" marks the UTC scale (CX01FIX-01). Watermarks stored before it carry no marker: a keyed one came from an
    // ERP row whose server-local clock was written as if it were UTC; an unkeyed one is a registration seed that was
    // already true UTC. Parse with the server zone re-reads the keyed legacy form as server-local, so an upgraded
    // connection neither skips changes (zones east of UTC) nor waits hours for them.
    private const string UtcMarker = "utc";

    public override string ToString() =>
        Timestamp.ToString("O", CultureInfo.InvariantCulture) + "|" + Key + "|" + UtcMarker;

    public static Cx01Watermark Parse(string? value, TimeZoneInfo? legacyServerTimeZone = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return Empty;
        var utcScale = value.EndsWith("|" + UtcMarker, StringComparison.Ordinal);
        var body = utcScale ? value[..^(UtcMarker.Length + 1)] : value;
        var separator = body.LastIndexOf('|');
        if (separator <= 0 || !DateTimeOffset.TryParse(body[..separator], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var timestamp)) return Empty;
        var key = body[(separator + 1)..];
        if (!utcScale && key.Length > 0 && legacyServerTimeZone is not null && timestamp != DateTimeOffset.MinValue)
            timestamp = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(timestamp.UtcDateTime, DateTimeKind.Unspecified), legacyServerTimeZone), TimeSpan.Zero);
        return new(timestamp, key);
    }

    public static Cx01Watermark From(JsonElement value, string key, TimeZoneInfo serverTimeZone)
    {
        var date = value.TryGetProperty("UpdateDate", out var dateValue) && dateValue.ValueKind == JsonValueKind.String
            ? Cx01Protocol.DatePart(dateValue.GetString()) : null;
        var time = value.TryGetProperty("UpdateTime", out var timeValue) ? timeValue.ToString() : "0";
        var digits = new string((time ?? "0").Where(char.IsAsciiDigit).ToArray()).PadLeft(6, '0');
        if (digits.Length > 6) digits = digits[^6..];
        var text = $"{date}T{digits[..2]}:{digits[2..4]}:{digits[4..6]}";
        if (!DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var local)) return new(DateTimeOffset.MinValue, key);
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        var utc = TimeZoneInfo.ConvertTimeToUtc(local, serverTimeZone);
        return new(new DateTimeOffset(utc, TimeSpan.Zero), key);
    }

    public static Cx01Watermark Max(Cx01Watermark left, Cx01Watermark right) =>
        left.CompareTo(right) >= 0 ? left : right;
}
