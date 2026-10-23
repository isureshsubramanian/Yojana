using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connectors;
using Yojana.Integration.Data;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX02;

/// <summary>CX02 anti-corruption boundary. Outbound posting delegates to its document coordinator.</summary>
public sealed class Cx02Adapter(
    ITenantContext tenant,
    IClock clock,
    IntegrationDbContext db,
    GatewayReferenceStore references,
    Cx02Settings settings,
    Cx02Client client,
    Cx02OutboundDocuments documents) : IConnectorAdapter, IOutboundConnector
{
    public string ConnectorCode => Cx02Capabilities.ConnectorCode;
    public bool OwnsConnectionLane => true;
    public OutboundConnectorCapabilities Capabilities { get; } = new(false, OwnsConnectionLane: true);

    public Task<OutboundDocument?> FindByReferenceAsync(OutboundLookup lookup,
        CancellationToken cancellationToken) => documents.FindAsync(lookup, cancellationToken);

    public Task<OutboundCreateResult> CreateAsync(OutboundCreateRequest request,
        CancellationToken cancellationToken) => documents.CreateAsync(request, cancellationToken);

    public string? StartingWatermark(DateTime sourceLocalTime) =>
        new Cx02Watermark(new DateTimeOffset(DateTime.SpecifyKind(sourceLocalTime, DateTimeKind.Utc)), "").ToString();

    public async Task<ConnectorPollResult> PollAsync(ConnectorPollRequest request,
        CancellationToken cancellationToken)
    {
        if (request.TenantId != tenant.TenantId) throw new InvalidOperationException("CX02 tenant mismatch.");
        var entity = Cx02Protocol.Entity(request.EntityType);
        if (!Cx02Protocol.PollEntities.Contains(entity))
            throw new InvalidOperationException($"CX02 entity '{request.EntityType}' is not a poll source.");
        var cursor = Cx02Watermark.Parse(request.Watermark);
        var from = cursor.Timestamp == DateTimeOffset.MinValue
            ? DateTimeOffset.UnixEpoch : cursor.Timestamp - request.OverlapWindow;
        var configured = await settings.GetAsync(request.ConnectionId, cancellationToken);
        var rows = await ReadChangedAsync(request.ConnectionId, configured, entity, from,
            request.MaximumEvents, cancellationToken);
        var serviceItems = await ServiceItemNumbersAsync(request.ConnectionId, configured, cancellationToken);
        var events = new List<InboundAcceptance>(rows.Count);
        var next = cursor;
        foreach (var row in rows.OrderBy(ChangeTime).ThenBy(value => ExternalId(entity, value), StringComparer.Ordinal))
        {
            var enriched = await EnrichAsync(request.ConnectionId, configured, entity, row, cancellationToken);
            events.Add(Cx02Mapper.Map(tenant.TenantId, request.ConnectionId, entity.EntityType, enriched,
                "poll", clock.UtcNow, serviceItems));
            next = Cx02Watermark.Max(next, new(ChangeTime(row), ExternalId(entity, row)));
        }
        return new(events, next == cursor ? request.Watermark : next.ToString());
    }

    public async Task<IReadOnlyList<InboundAcceptance>> CaptureHintAsync(Guid connectionId, string resource,
        string changeType, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var entity = EntityFromResource(resource);
        if (entity is null) return [];
        // Enrichment-page notifications are hints for the authoritative posted ledger. They never become their own
        // business event because reservation and relation row ordering is not a lifecycle ordering guarantee.
        if (!Cx02Protocol.PollEntities.Contains(entity))
            return (await PollAsync(new(tenant.TenantId, connectionId, "tracked_ledger_entries", null,
                TimeSpan.FromMinutes(5), 1_024), cancellationToken)).Events;
        var uri = HintUri(configured, entity, resource);
        IReadOnlyList<JsonElement> rows;
        if (changeType == "collection" || !ContainsEntityKey(resource))
            rows = await client.ReadCollectionAsync(connectionId, uri, configured.PollPageSize,
                configured.PageCap, cancellationToken);
        else
        {
            try
            {
                using var row = await client.GetAsync(connectionId, uri, cancellationToken);
                rows = [row.RootElement.Clone()];
            }
            catch (Cx02RemoteException exception) when (exception.IsNotFound)
            {
                // A deleted row is recovered by the modified-since poll and reconciliation; acknowledging the hint
                // prevents the remote from deleting a valid subscription because one entity has already gone.
                return [];
            }
        }
        var serviceItems = await ServiceItemNumbersAsync(connectionId, configured, cancellationToken);
        var result = new List<InboundAcceptance>(rows.Count);
        foreach (var row in rows)
        {
            var enriched = await EnrichAsync(connectionId, configured, entity, row, cancellationToken);
            result.Add(Cx02Mapper.Map(tenant.TenantId, connectionId, entity.EntityType, enriched,
                "webhook", clock.UtcNow, serviceItems));
        }
        return result;
    }

    public async Task<IReadOnlyList<ConnectorEntityDayDigest>> EntityDayDigestsAsync(Guid connectionId,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken)
    {
        var messages = new List<InboundAcceptance>();
        foreach (var entity in Cx02Protocol.PollEntities)
        {
            var page = await ReadRangePageAsync(connectionId, entity.EntityType, rangeStart, rangeEnd,
                null, 20_000, cancellationToken);
            messages.AddRange(page.Messages);
        }
        return messages.GroupBy(message => new
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
        var configured = await settings.GetAsync(request.ConnectionId, cancellationToken);
        var entity = Cx02Protocol.Entity("tracked_ledger_entries");
        var rows = await ReadAllAsync(request.ConnectionId, configured, entity, cancellationToken);
        var balances = rows.GroupBy(row => new
            {
                Item = Text(row, "itemNumber") ?? "",
                Location = Text(row, "locationCode") ?? "",
            }).Select(group => new
            {
                group.Key.Item,
                group.Key.Location,
                Quantity = group.Sum(row => Decimal(row, "quantity")),
            }).Where(row => !request.WarehouseStatusMapping.TryGetValue(row.Location, out var included) || included)
            .ToList();
        var result = new List<ConnectorOnHandBalance>(balances.Count);
        foreach (var row in balances)
        {
            var itemId = await references.ResolveAsync(request.ConnectionId, "item", row.Item, false,
                cancellationToken);
            var locationId = await references.ResolveAsync(request.ConnectionId, "location", row.Location, false,
                cancellationToken);
            var itemName = await db.ReferenceProjections.AsNoTracking().Where(value => value.TenantId == tenant.TenantId
                    && value.EntityType == "item" && value.ModuleId == itemId)
                .Select(value => value.DisplayName).SingleOrDefaultAsync(cancellationToken);
            result.Add(new(itemId, row.Item, itemName, locationId, row.Location, null, row.Quantity));
        }
        return result;
    }

    public Task<bool> SupportsRangedReadsAsync(Guid connectionId, string entity,
        CancellationToken cancellationToken)
    {
        try { return Task.FromResult(Cx02Protocol.PollEntities.Contains(Cx02Protocol.Entity(entity))); }
        catch (InvalidOperationException) { return Task.FromResult(false); }
    }

    public async Task<ConnectorRangePage> ReadRangePageAsync(Guid connectionId, string entity,
        DateOnly rangeStart, DateOnly rangeEnd, string? cursor, int pageSize,
        CancellationToken cancellationToken)
    {
        var definition = Cx02Protocol.Entity(entity);
        if (!Cx02Protocol.PollEntities.Contains(definition))
            throw new InvalidOperationException($"CX02 entity '{entity}' is not a ranged source.");
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var after = Cx02Watermark.Parse(cursor);
        var from = new DateTimeOffset(rangeStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(rangeEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var rows = await ReadChangedAsync(connectionId, configured, definition, from, 20_000, cancellationToken);
        var eligible = rows.Select(row => (Row: row, Marker: new Cx02Watermark(ChangeTime(row), ExternalId(definition, row))))
            .Where(value => value.Marker.Timestamp < to && (after == Cx02Watermark.Empty || value.Marker.CompareTo(after) > 0))
            .OrderBy(value => value.Marker).Take(pageSize + 1).ToList();
        var serviceItems = await ServiceItemNumbersAsync(connectionId, configured, cancellationToken);
        var messages = new List<InboundAcceptance>(Math.Min(pageSize, eligible.Count));
        foreach (var value in eligible.Take(pageSize))
        {
            var enriched = await EnrichAsync(connectionId, configured, definition, value.Row, cancellationToken);
            messages.Add(Cx02Mapper.Map(tenant.TenantId, connectionId, definition.EntityType, enriched,
                "backfill", clock.UtcNow, serviceItems));
        }
        return new(messages, eligible.Count > pageSize ? eligible[pageSize - 1].Marker.ToString() : null,
            rows.Count);
    }

    private async Task<IReadOnlyList<JsonElement>> ReadChangedAsync(Guid connectionId,
        Cx02ConnectionSettings configured, Cx02EntityDefinition entity, DateTimeOffset from, int maximum,
        CancellationToken cancellationToken)
    {
        var separator = Cx02Protocol.Collection(configured, entity).Query.Length == 0 ? "?" : "&";
        var uri = new Uri(Cx02Protocol.Collection(configured, entity) + separator + Cx02Protocol.ModifiedSince(from)
            + "&$top=" + Math.Min(maximum, configured.PageCap).ToString(CultureInfo.InvariantCulture));
        var values = await client.ReadCollectionAsync(connectionId, uri, configured.PollPageSize,
            configured.PageCap, cancellationToken);
        return values.Where(value => ChangeTime(value) >= from).Take(maximum).ToList();
    }

    private Task<IReadOnlyList<JsonElement>> ReadAllAsync(Guid connectionId, Cx02ConnectionSettings configured,
        Cx02EntityDefinition entity, CancellationToken cancellationToken) => client.ReadCollectionAsync(connectionId,
        Cx02Protocol.Collection(configured, entity), configured.PollPageSize, configured.PageCap, cancellationToken);

    private async Task<IReadOnlyDictionary<string, string>> ServiceItemNumbersAsync(Guid connectionId,
        Cx02ConnectionSettings configured, CancellationToken cancellationToken)
    {
        var rows = await ReadAllAsync(connectionId, configured, Cx02Protocol.Entity("service_items"),
            cancellationToken);
        return rows.Where(row => !string.IsNullOrWhiteSpace(Text(row, "serialNumber"))
                && !string.IsNullOrWhiteSpace(Text(row, "number")))
            .GroupBy(row => Text(row, "serialNumber")!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => Text(group.OrderByDescending(ChangeTime).First(), "number")!,
                StringComparer.OrdinalIgnoreCase);
    }

    private async Task<JsonElement> EnrichAsync(Guid connectionId, Cx02ConnectionSettings configured,
        Cx02EntityDefinition entity, JsonElement raw, CancellationToken cancellationToken)
    {
        var lineEntity = entity.Resource switch
        {
            "salesInvoices" => "salesInvoiceLines",
            "postedTransferShipments" => "postedTransferShipmentLines",
            "postedTransferReceipts" => "postedTransferReceiptLines",
            "postedReturnReceipts" => "postedReturnReceiptLines",
            _ => null,
        };
        if (lineEntity is null || raw.TryGetProperty("lines", out _)) return raw;
        var headerId = Text(raw, "id");
        var documentNumber = Text(raw, "number") ?? "";
        var documentType = entity.Resource switch
        {
            "salesInvoices" => "salesInvoice",
            "postedTransferShipments" => "transferShipment",
            "postedTransferReceipts" => "transferReceipt",
            "postedReturnReceipts" => "returnReceipt",
            _ => "",
        };
        var linesDefinition = Cx02Protocol.Entities.Values.Single(value => value.Resource == lineEntity);
        var allLines = await ReadAllAsync(connectionId, configured, linesDefinition, cancellationToken);
        var lines = allLines.Where(line => Text(line, "documentId") == headerId).Select(line => line.Clone()).ToList();
        var relations = await ReadAllAsync(connectionId, configured, Cx02Protocol.Entity("entry_relations"),
            cancellationToken);
        var ledger = await ReadAllAsync(connectionId, configured, Cx02Protocol.Entity("tracked_ledger_entries"),
            cancellationToken);
        var enriched = new List<JsonObject>();
        foreach (var line in lines)
        {
            var node = JsonNode.Parse(line.GetRawText())!.AsObject();
            var lineNumber = Int(line, "lineNumber");
            if (lineNumber == 0) lineNumber = Int(line, "sequence");
            var entryNumbers = relations.Where(relation => Text(relation, "sourceDocumentType") == documentType
                    && Text(relation, "sourceDocumentNumber") == documentNumber
                    && Int(relation, "sourceLineNumber") == lineNumber)
                .Select(relation => Int(relation, "itemLedgerEntryNumber")).ToHashSet();
            var linked = ledger.Where(entry => entry.TryGetProperty("entryNumber", out var entryNumber)
                && entryNumber.TryGetInt32(out var value) && entryNumbers.Contains(value)).ToList();
            node["serials"] = new JsonArray(linked.Select(entry => JsonValue.Create(Text(entry, "serialNumber")))
                .Where(value => value is not null).ToArray());
            node["lots"] = new JsonArray(linked.Select(entry => JsonValue.Create(Text(entry, "lotNumber")))
                .Where(value => value is not null).ToArray());
            node["locationCode"] ??= linked.Select(entry => Text(entry, "locationCode"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            enriched.Add(node);
        }
        var root = JsonNode.Parse(raw.GetRawText())!.AsObject();
        root["lines"] = new JsonArray(enriched.ToArray());
        return JsonSerializer.SerializeToElement(root);
    }

    private static Cx02EntityDefinition? EntityFromResource(string resource)
    {
        var path = resource.Split('?', 2)[0];
        var segment = path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
        var parenthesis = segment.IndexOf('(');
        if (parenthesis >= 0) segment = segment[..parenthesis];
        return Cx02Protocol.Entities.Values.FirstOrDefault(entity => entity.Resource == segment);
    }

    private static Uri HintUri(Cx02ConnectionSettings configured, Cx02EntityDefinition entity, string resource)
    {
        if (Uri.TryCreate(resource, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https") return absolute;
        var marker = "/companies(";
        var company = resource.IndexOf(marker, StringComparison.Ordinal);
        if (company < 0)
        {
            var parts = resource.Split('?', 2);
            if (parts[0].Trim('/') != entity.Resource)
                throw new InvalidOperationException("CX02 notification resource has no company scope.");
            return parts.Length == 1 ? Cx02Protocol.Collection(configured, entity)
                : new Uri(Cx02Protocol.Collection(configured, entity) + "?" + parts[1]);
        }
        return new Uri(entity.Custom ? Cx02Protocol.CustomRoot(configured) : Cx02Protocol.StandardRoot(configured),
            resource[(company + 1)..]);
    }

    private static bool ContainsEntityKey(string resource) =>
        resource.Split('?', 2)[0].EndsWith(')');

    private static string ExternalId(Cx02EntityDefinition entity, JsonElement row) =>
        Text(row, entity.Resource is "items" or "customers" ? "number" : "id")
        ?? Text(row, "number") ?? Text(row, "entryNumber") ?? "";

    private static DateTimeOffset ChangeTime(JsonElement row) => Text(row, "lastModifiedDateTime") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : DateTimeOffset.MinValue;

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;

    private static int Int(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.TryGetInt32(out var parsed) ? parsed : 0;

    private static decimal Decimal(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.TryGetDecimal(out var parsed) ? parsed : 0;
}

internal readonly record struct Cx02Watermark(DateTimeOffset Timestamp, string Key) : IComparable<Cx02Watermark>
{
    public static Cx02Watermark Empty { get; } = new(DateTimeOffset.MinValue, "");

    public int CompareTo(Cx02Watermark other)
    {
        var time = Timestamp.CompareTo(other.Timestamp);
        return time != 0 ? time : StringComparer.Ordinal.Compare(Key, other.Key);
    }

    public override string ToString() => Timestamp.ToString("O", CultureInfo.InvariantCulture) + "|" + Key;

    public static Cx02Watermark Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Empty;
        var separator = value.LastIndexOf('|');
        return separator > 0 && DateTimeOffset.TryParse(value[..separator], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp) ? new(timestamp, value[(separator + 1)..]) : Empty;
    }

    public static Cx02Watermark Max(Cx02Watermark left, Cx02Watermark right) =>
        left.CompareTo(right) >= 0 ? left : right;
}
