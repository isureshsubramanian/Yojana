using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX01;

public sealed record ConnectorReconciliationRangeRequest(TenantId TenantId, Guid ConnectionId, string EntityType,
    DateTimeOffset FromInclusive, DateTimeOffset ToExclusive, string? Cursor, int MaximumEvents);

public sealed record ConnectorReconciliationRange(IReadOnlyList<InboundAcceptance> Events, string? NextCursor);
public sealed record ConnectorDailyFingerprint(DateOnly Date, string EntityType, long Count, string Sha256);
public sealed record ConnectorOnHandBalance(string ItemExternalId, string LocationExternalId, decimal Quantity);

public interface IConnectorReconciliationSource
{
    string ConnectorCode { get; }
    Task<ConnectorReconciliationRange> ReadRangeAsync(ConnectorReconciliationRangeRequest request,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectorDailyFingerprint>> DailyFingerprintsAsync(TenantId tenantId, Guid connectionId,
        string entityType, DateOnly fromDate, DateOnly toDate, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectorOnHandBalance>> OnHandByWarehouseAsync(TenantId tenantId, Guid connectionId,
        CancellationToken cancellationToken);
}

/// <summary>One adapter owns the remote session, polling, sweep reads and reference-only outbound translation.</summary>
public sealed class Cx01Adapter(
    ITenantContext tenant,
    IClock clock,
    IntegrationDbContext db,
    Cx01Settings settings,
    Cx01Client client,
    ICx01SerialHistory serialHistory,
    ICx01AttributionTaskSink attributionTasks) : IConnectorPoller, IConnectorReconciliationSource, IOutboundConnector
{
    public string ConnectorCode => Cx01Capabilities.ConnectorCode;
    public OutboundConnectorCapabilities Capabilities { get; } = new(false, OwnsConnectionLane: true);

    public async Task<ConnectorPollResult> PollAsync(ConnectorPollRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenant(request.TenantId);
        var entity = Entity(request.EntityType);
        var configured = await settings.GetAsync(request.ConnectionId, cancellationToken);
        var cursor = Cx01Watermark.Parse(request.Watermark);
        var from = cursor.Timestamp == DateTimeOffset.MinValue
            ? DateTimeOffset.UnixEpoch : cursor.Timestamp - request.OverlapWindow;
        var keys = await ChangedKeysAsync(request.ConnectionId, entity, from, null,
            request.MaximumEvents, cancellationToken);
        var acceptances = new List<InboundAcceptance>(keys.Count);
        var next = cursor;
        foreach (var changed in keys.OrderBy(x => x.Timestamp).ThenBy(x => x.Key, StringComparer.Ordinal))
        {
            using var document = await client.GetAsync(request.ConnectionId,
                Cx01Protocol.ByKey(entity, changed.Key), cancellationToken);
            if (Cx01Mapper.IsOwnWrite(document.RootElement, configured))
            {
                next = Cx01Watermark.Max(next, changed);
                continue;
            }
            var mapped = await MapWithSerialFallbackAsync(request.ConnectionId, entity, document.RootElement,
                configured, "poll", cancellationToken);
            acceptances.Add(mapped.Acceptance);
            if (mapped.MissingSerialLines.Count > 0)
                await attributionTasks.RaiseAsync(request.ConnectionId, entity.Collection, changed.Key,
                    mapped.MissingSerialLines, cancellationToken);
            next = Cx01Watermark.Max(next, changed);
        }
        return new(acceptances, next == cursor ? request.Watermark : next.ToString());
    }

    public async Task<ConnectorReconciliationRange> ReadRangeAsync(ConnectorReconciliationRangeRequest request,
        CancellationToken cancellationToken)
    {
        EnsureTenant(request.TenantId);
        var entity = Entity(request.EntityType);
        var configured = await settings.GetAsync(request.ConnectionId, cancellationToken);
        var after = Cx01Watermark.Parse(request.Cursor);
        var keys = await ChangedKeysAsync(request.ConnectionId, entity, request.FromInclusive,
            request.ToExclusive, request.MaximumEvents, cancellationToken);
        var selected = keys.Where(x => after == Cx01Watermark.Empty || x.CompareTo(after) > 0)
            .OrderBy(x => x.Timestamp).ThenBy(x => x.Key, StringComparer.Ordinal).Take(request.MaximumEvents).ToList();
        var events = new List<InboundAcceptance>(selected.Count);
        foreach (var changed in selected)
        {
            using var document = await client.GetAsync(request.ConnectionId,
                Cx01Protocol.ByKey(entity, changed.Key), cancellationToken);
            if (Cx01Mapper.IsOwnWrite(document.RootElement, configured)) continue;
            events.Add((await MapWithSerialFallbackAsync(request.ConnectionId, entity, document.RootElement,
                configured, "poll", cancellationToken)).Acceptance);
        }
        return new(events, selected.Count == request.MaximumEvents ? selected[^1].ToString() : null);
    }

    public async Task<IReadOnlyList<ConnectorDailyFingerprint>> DailyFingerprintsAsync(TenantId tenantId,
        Guid connectionId, string entityType, DateOnly fromDate, DateOnly toDate,
        CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        var entity = Entity(entityType);
        var result = new List<ConnectorDailyFingerprint>();
        for (var date = fromDate; date <= toDate; date = date.AddDays(1))
        {
            var from = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            var keys = await ChangedKeysAsync(connectionId, entity, from, from.AddDays(1), int.MaxValue,
                cancellationToken);
            var canonical = string.Join('\n', keys.OrderBy(x => x.Timestamp).ThenBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => x.ToString()));
            result.Add(new(date, entityType, keys.Count,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant()));
        }
        return result;
    }

    public async Task<IReadOnlyList<ConnectorOnHandBalance>> OnHandByWarehouseAsync(TenantId tenantId,
        Guid connectionId, CancellationToken cancellationToken)
    {
        EnsureTenant(tenantId);
        using var response = await client.GetAsync(connectionId,
            Cx01Protocol.Root + "ItemWarehouseInfoCollection?$select=ItemCode,WarehouseCode,InStock", cancellationToken);
        return Values(response.RootElement).Select(row => new ConnectorOnHandBalance(
            RequiredText(row, "ItemCode"), RequiredText(row, "WarehouseCode"), Number(row, "InStock"))).ToList();
    }

    public async Task<OutboundDocument?> FindByReferenceAsync(OutboundLookup lookup,
        CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(lookup.ConnectionId, cancellationToken);
        if (!await ReferenceOnlyAsync(lookup.ConnectionId, cancellationToken)) return null;
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
        if (!await ReferenceOnlyAsync(request.ConnectionId, cancellationToken))
            return OutboundCreateResult.Unsupported("CX01 document posting is reserved for P8-T19.");
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
        var body = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [field] = configured.OwnReferencePrefix + value,
        };
        using var response = await client.SendJsonAsync(request.ConnectionId, HttpMethod.Patch,
            Cx01Protocol.Root + collection + "(" + Cx01Protocol.KeyLiteral(key) + ")", body, true,
            cancellationToken);
        var marker = response is null ? null : Cx01Mapper.ChangeMarker(response.RootElement, key);
        return OutboundCreateResult.Created(key, marker);
    }

    internal async Task<Cx01MappedAcceptance?> CaptureKeyAsync(Guid connectionId, string collection, string key,
        CancellationToken cancellationToken)
    {
        var entity = Cx01Protocol.Entities.Values.SingleOrDefault(x =>
            StringComparer.Ordinal.Equals(x.Collection, collection));
        if (entity is null) return null;
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        using var document = await client.GetAsync(connectionId, Cx01Protocol.ByKey(entity, key), cancellationToken);
        if (Cx01Mapper.IsOwnWrite(document.RootElement, configured)) return null;
        var mapped = await MapWithSerialFallbackAsync(connectionId, entity, document.RootElement, configured,
            "webhook", cancellationToken);
        if (mapped.MissingSerialLines.Count > 0)
            await attributionTasks.RaiseAsync(connectionId, entity.Collection, key, mapped.MissingSerialLines,
                cancellationToken);
        return mapped;
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
            clock.UtcNow);
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

        var permitted = configured.AllowlistedTrackingTables?.ToHashSet(StringComparer.OrdinalIgnoreCase) ?? [];
        if (permitted.IsSupersetOf(["OITL", "ITL1", "OSRN"]))
        {
            using var governed = await client.GetAsync(connectionId,
                Cx01Protocol.Root + Cx01Protocol.SqlQueries + "('YOJANA_SERIAL_LINK')/List?document="
                + Uri.EscapeDataString(externalId), cancellationToken);
            AddLinkedSerials(result, Values(governed.RootElement));
            if (result.Count > 0) return result;
        }

        foreach (var line in lines)
        {
            var filter = Uri.EscapeDataString("ItemCode eq '" + line.ItemCode.Replace("'", "''",
                StringComparison.Ordinal) + "'");
            using var master = await client.GetAsync(connectionId,
                Cx01Protocol.Root + Cx01Protocol.SerialNumberDetails + "?$filter=" + filter, cancellationToken);
            var linked = Values(master.RootElement).Where(row =>
                StringComparer.Ordinal.Equals(Text(row, "DocumentEntry"), externalId)
                && Int(row, "DocumentLine") == line.LineNo).ToList();
            AddLinkedSerials(result, linked);
        }
        return result;
    }

    private async Task<List<Cx01Watermark>> ChangedKeysAsync(Guid connectionId, Cx01EntityDefinition entity,
        DateTimeOffset fromInclusive, DateTimeOffset? toExclusive, int maximum, CancellationToken cancellationToken)
    {
        var result = new List<Cx01Watermark>();
        var filter = "UpdateDate ge '" + fromInclusive.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
        if (toExclusive is { } until)
            filter += " and UpdateDate lt '" + until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
        var path = Cx01Protocol.Collection(entity) + "?$select=" + entity.KeyField
            + ",UpdateDate,UpdateTime&$filter=" + Uri.EscapeDataString(filter)
            + "&$orderby=UpdateDate,UpdateTime," + entity.KeyField + "&$top="
            + Math.Min(maximum, 1_000).ToString(CultureInfo.InvariantCulture);
        while (result.Count < maximum)
        {
            using var response = await client.GetAsync(connectionId, path, cancellationToken);
            foreach (var row in Values(response.RootElement))
            {
                var key = RequiredText(row, entity.KeyField);
                var marker = Cx01Watermark.From(row, key);
                if (marker.Timestamp >= fromInclusive && (toExclusive is null || marker.Timestamp < toExclusive))
                    result.Add(marker);
                if (result.Count == maximum) break;
            }
            if (result.Count == maximum || !response.RootElement.TryGetProperty("@odata.nextLink", out var next)
                || string.IsNullOrWhiteSpace(next.GetString())) break;
            path = new Uri(next.GetString()!, UriKind.RelativeOrAbsolute).IsAbsoluteUri
                ? new Uri(next.GetString()!).PathAndQuery : next.GetString()!;
        }
        return result;
    }

    private async Task<bool> ReferenceOnlyAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await db.ConnectionGatewaySettings.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId).Select(x => x.OutboundMode).SingleAsync(cancellationToken)
            == "reference_only";

    private void EnsureTenant(TenantId requested)
    {
        if (requested != tenant.TenantId) throw new InvalidOperationException("CX01 tenant mismatch.");
    }

    private static Cx01EntityDefinition Entity(string entityType)
    {
        var key = entityType switch
        {
            "item" => "items",
            "business_partner" => "business_partners",
            "delivery" => "deliveries",
            "return" => "returns",
            "invoice" => "invoices",
            _ => entityType,
        };
        return Cx01Protocol.Entities.TryGetValue(key, out var entity) ? entity
            : throw new InvalidOperationException($"CX01 entity type '{entityType}' is not supported.");
    }

    private static List<JsonElement> Values(JsonElement root) =>
        root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().ToList() : [];

    private static void AddLinkedSerials(Dictionary<int, IReadOnlyList<CanonicalSerial>> target,
        IEnumerable<JsonElement> rows)
    {
        foreach (var group in rows.GroupBy(row => Int(row, "DocumentLine")))
        {
            var serials = group.Select(row => new CanonicalSerial(RequiredText(row, "SerialNumber"), null,
                Text(row, "SystemSerialNumber"))).ToList();
            if (serials.Count > 0) target[group.Key] = serials;
        }
    }

    private static string RequiredText(JsonElement value, string property) => Text(value, property)
        ?? throw new InvalidOperationException("CX01 response is missing a required property.");
    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;
    private static int Int(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.TryGetInt32(out var number) ? number : 0;
    private static decimal Number(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.TryGetDecimal(out var number) ? number : 0;
}

internal readonly record struct Cx01Watermark(DateTimeOffset Timestamp, string Key) : IComparable<Cx01Watermark>
{
    public static Cx01Watermark Empty { get; } = new(DateTimeOffset.MinValue, "");

    public int CompareTo(Cx01Watermark other)
    {
        var time = Timestamp.CompareTo(other.Timestamp);
        return time != 0 ? time : StringComparer.Ordinal.Compare(Key, other.Key);
    }

    public override string ToString() => Timestamp.ToString("O", CultureInfo.InvariantCulture) + "|" + Key;

    public static Cx01Watermark Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Empty;
        var separator = value.LastIndexOf('|');
        return separator > 0 && DateTimeOffset.TryParse(value[..separator], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var timestamp) ? new(timestamp, value[(separator + 1)..]) : Empty;
    }

    public static Cx01Watermark From(JsonElement value, string key)
    {
        var date = value.TryGetProperty("UpdateDate", out var dateValue) ? dateValue.GetString() : null;
        var time = value.TryGetProperty("UpdateTime", out var timeValue) ? timeValue.ToString() : "0";
        var digits = new string((time ?? "0").Where(char.IsAsciiDigit).ToArray()).PadLeft(6, '0');
        if (digits.Length > 6) digits = digits[^6..];
        var text = $"{date}T{digits[..2]}:{digits[2..4]}:{digits[4..6]}Z";
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal,
            out var timestamp) ? new(timestamp, key) : new(DateTimeOffset.MinValue, key);
    }

    public static Cx01Watermark Max(Cx01Watermark left, Cx01Watermark right) =>
        left.CompareTo(right) >= 0 ? left : right;
}
