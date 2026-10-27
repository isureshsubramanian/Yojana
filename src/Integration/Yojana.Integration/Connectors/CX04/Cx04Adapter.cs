using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connectors;
using Yojana.Integration.Data;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX04;

internal readonly record struct Cx04Watermark(DateTimeOffset Timestamp, string ExternalId)
    : IComparable<Cx04Watermark>
{
    public static Cx04Watermark Empty { get; } = new(DateTimeOffset.MinValue, "");
    public int CompareTo(Cx04Watermark other)
    {
        var time = Timestamp.CompareTo(other.Timestamp);
        return time != 0 ? time : StringComparer.Ordinal.Compare(ExternalId, other.ExternalId);
    }
    public override string ToString() => Timestamp.ToString("O", CultureInfo.InvariantCulture) + "|" + ExternalId;
    public static Cx04Watermark Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Empty;
        var split = value.Split('|', 2);
        return split.Length == 2 && DateTimeOffset.TryParse(split[0], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var time) ? new(time, split[1]) : Empty;
    }
}

public sealed class Cx04Adapter(ITenantContext tenant, IClock clock, IntegrationDbContext db,
    GatewayReferenceStore references, Cx04Settings settings, Cx04Client client) : IConnectorAdapter,
    IConnectorWatermarkSeed
{
    public string ConnectorCode => Cx04Capabilities.ConnectorCode;
    public bool OwnsConnectionLane => true;
    public Task<string?> StartingWatermarkAsync(Guid connectionId, DateTime sourceLocalTime,
        CancellationToken cancellationToken) => Task.FromResult<string?>(
            new Cx04Watermark(new(DateTime.SpecifyKind(sourceLocalTime, DateTimeKind.Utc)), "").ToString());

    public string WatermarkAt(DateTime utcInstant) =>
        new Cx04Watermark(new(DateTime.SpecifyKind(utcInstant, DateTimeKind.Utc)), "").ToString();
    public DateTime? InstantOf(string watermark) => Cx04Watermark.Parse(watermark) is var value
        && value != Cx04Watermark.Empty ? value.Timestamp.UtcDateTime : null;
    public bool IsMasterData(string entityType) => entityType is "items" or "parties";

    public async Task<ConnectorPollResult> PollAsync(ConnectorPollRequest request, CancellationToken cancellationToken)
    {
        if (request.TenantId != tenant.TenantId) throw new InvalidOperationException("CX04 tenant mismatch.");
        var configured = await settings.GetAsync(request.ConnectionId, cancellationToken);
        var entity = Cx04Protocol.Entity(request.EntityType);
        var cursor = Cx04Watermark.Parse(request.Watermark);
        var from = cursor.Timestamp == DateTimeOffset.MinValue ? DateTimeOffset.UnixEpoch
            : cursor.Timestamp - request.OverlapWindow;
        var rows = await ChangedAsync(request.ConnectionId, configured, entity, from, request.MaximumEvents, cancellationToken);
        var events = new List<InboundAcceptance>();
        var next = cursor;
        foreach (var row in rows.Select(value => (Row: value, Marker: Marker(entity, value)))
                     .OrderBy(value => value.Marker).Take(request.MaximumEvents))
        {
            events.Add(Cx04Mapper.Map(tenant.TenantId, request.ConnectionId, entity, row.Row, "poll",
                clock.UtcNow, configured.AllocationQuantityMode));
            if (row.Marker.CompareTo(next) > 0) next = row.Marker;
        }
        return new(events, next == cursor ? request.Watermark : next.ToString());
    }

    public async Task<IReadOnlyList<InboundAcceptance>> CaptureHintAsync(Guid connectionId,
        string resource, string key, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        var entity = Cx04Protocol.Entity(resource);
        try
        {
            using var row = await client.GetAsync(connectionId, Cx04Protocol.Record(configured, entity, key), ct);
            return [Cx04Mapper.Map(tenant.TenantId, connectionId, entity, row.RootElement, "webhook",
                clock.UtcNow, configured.AllocationQuantityMode)];
        }
        catch (Cx04RemoteException exception) when (exception.IsNotFound)
        {
            return [];
        }
    }

    public async Task<IReadOnlyList<ConnectorEntityDayDigest>> EntityDayDigestsAsync(Guid connectionId,
        DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken)
    {
        var messages = new List<InboundAcceptance>();
        foreach (var entity in Cx04Protocol.Entities.Values)
        {
            var page = await ReadRangePageAsync(connectionId, entity.EntityType, rangeStart, rangeEnd,
                null, 20_000, cancellationToken);
            messages.AddRange(page.Messages);
        }
        return messages.GroupBy(message => new
            {
                message.Envelope.ErpEntity,
                Day = DateOnly.FromDateTime(message.Envelope.OccurredAt.UtcDateTime),
            }).Select(group =>
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
        var adjustment = Cx04Protocol.Entity("adjustments");
        var rows = await client.ReadCollectionAsync(request.ConnectionId, Cx04Protocol.Collection(configured,
            adjustment), configured.PollPageSize, configured.PageCap, cancellationToken);
        var grouped = rows.SelectMany(row => Lines(row)).GroupBy(value => new
        {
            Item = Cx04Mapper.Text(value, "InventoryID") ?? "",
            Location = Cx04Mapper.Text(value, "LocationID") ?? "",
        }).Select(group => new { group.Key.Item, group.Key.Location,
            Quantity = group.Sum(value => decimal.TryParse(Cx04Mapper.Text(value, "Qty"),
                NumberStyles.Number, CultureInfo.InvariantCulture, out var quantity) ? quantity : 0) }).ToList();
        var result = new List<ConnectorOnHandBalance>();
        foreach (var value in grouped)
        {
            var itemId = await references.ResolveAsync(request.ConnectionId, "item", value.Item, false, cancellationToken);
            var locationId = await references.ResolveAsync(request.ConnectionId, "location", value.Location, false, cancellationToken);
            var name = await db.ReferenceProjections.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                    && row.EntityType == "item" && row.ModuleId == itemId)
                .Select(row => row.DisplayName).SingleOrDefaultAsync(cancellationToken);
            result.Add(new(itemId, value.Item, name, locationId, value.Location, null, value.Quantity));
        }
        return result;
    }

    public Task<bool> SupportsRangedReadsAsync(Guid connectionId, string entity, CancellationToken cancellationToken)
    {
        try { _ = Cx04Protocol.Entity(entity); return Task.FromResult(true); }
        catch (InvalidOperationException) { return Task.FromResult(false); }
    }

    public async Task<ConnectorRangePage> ReadRangePageAsync(Guid connectionId, string entity,
        DateOnly rangeStart, DateOnly rangeEnd, string? cursor, int pageSize, CancellationToken cancellationToken)
    {
        var definition = Cx04Protocol.Entity(entity);
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var after = Cx04Watermark.Parse(cursor);
        var from = new DateTimeOffset(rangeStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(rangeEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var rows = await ChangedAsync(connectionId, configured, definition, from, 20_000, cancellationToken);
        var eligible = rows.Select(row => (Row: row, Marker: Marker(definition, row)))
            .Where(value => value.Marker.Timestamp < to && (after == Cx04Watermark.Empty
                || value.Marker.CompareTo(after) > 0)).OrderBy(value => value.Marker).Take(pageSize + 1).ToList();
        var messages = eligible.Take(pageSize).Select(value => Cx04Mapper.Map(tenant.TenantId, connectionId,
            definition, value.Row, "backfill", clock.UtcNow, configured.AllocationQuantityMode)).ToList();
        return new(messages, eligible.Count > pageSize ? eligible[pageSize - 1].Marker.ToString() : null,
            rows.Count);
    }

    private async Task<IReadOnlyList<JsonElement>> ChangedAsync(Guid connectionId,
        Cx04ConnectionSettings configured, Cx04EntityDefinition entity, DateTimeOffset from, int maximum,
        CancellationToken ct)
    {
        var uri = new Uri(Cx04Protocol.Collection(configured, entity) + "?" + Cx04Protocol.ModifiedSince(entity, from));
        var rows = await client.ReadCollectionAsync(connectionId, uri, configured.PollPageSize,
            Math.Min(configured.PageCap, maximum), ct);
        return rows.Where(row => entity.TypeValue is null
                || string.Equals(Cx04Mapper.Text(row, "Type"), entity.TypeValue, StringComparison.Ordinal))
            .Take(maximum).ToList();
    }

    private static Cx04Watermark Marker(Cx04EntityDefinition entity, JsonElement row) =>
        new(DateTimeOffset.TryParse(Cx04Mapper.Text(row, entity.ModifiedField), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var time) ? time : DateTimeOffset.UnixEpoch,
            Cx04Mapper.Text(row, entity.KeyField) ?? Cx04Mapper.Text(row, "id") ?? "");

    private static List<JsonElement> Lines(JsonElement row) => row.TryGetProperty("Details", out var details)
        && details.ValueKind == JsonValueKind.Array ? details.EnumerateArray().ToList() : [];
}
