using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Developer.Contracts;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.Delivery;

public sealed class WebhookDeliveryQueries(DeveloperDbContext db, ITenantContext tenant, IClock clock)
{
    public async Task<WebhookDeliveryPage> ListAsync(Guid subscriptionId, string? status,
        string? eventType, DateTime? from, DateTime? to, string? cursor, int? requestedTake,
        CancellationToken ct)
    {
        if (status is not null && status is not (WebhookDeliveryStatus.Pending
            or WebhookDeliveryStatus.Succeeded or WebhookDeliveryStatus.Parked
            or WebhookDeliveryStatus.Discarded))
            throw new CommandRefusedException("webhook_delivery_status_unknown", "The delivery status filter is not known.");
        if (eventType is not null && !PublicEventTypes.Subscribable.Contains(eventType, StringComparer.Ordinal))
            throw new CommandRefusedException("webhook_event_type_unknown", "The event type filter is not in public catalogue v1.");
        if (from > to) throw new CommandRefusedException("webhook_date_range_invalid", "From must not be after to.");
        var take = Math.Clamp(requestedTake ?? 50, 1, 200);
        var after = Decode(cursor);
        var baseQuery = from delivery in db.Deliveries.AsNoTracking()
                    join publicEvent in db.PublicEvents.AsNoTracking()
                        on new { delivery.TenantId, delivery.EventId }
                        equals new { publicEvent.TenantId, publicEvent.EventId }
                    where delivery.TenantId == tenant.TenantId
                       && delivery.SubscriptionId == subscriptionId
                    select new { Delivery = delivery, EventType = publicEvent.Type, publicEvent.Body };
        if (eventType is not null) baseQuery = baseQuery.Where(row => row.EventType == eventType);
        if (from is { } lower) baseQuery = baseQuery.Where(row => row.Delivery.CreatedAt >= lower);
        if (to is { } upper) baseQuery = baseQuery.Where(row => row.Delivery.CreatedAt < upper);
        var counts = await baseQuery.GroupBy(row => row.Delivery.Status).Select(group => new
            { Status = group.Key, Count = group.LongCount() }).ToDictionaryAsync(row => row.Status,
                row => row.Count, ct);
        var statusCounts = new WebhookDeliveryStatusCounts(counts.Values.Sum(),
            counts.GetValueOrDefault(WebhookDeliveryStatus.Succeeded),
            counts.GetValueOrDefault(WebhookDeliveryStatus.Pending),
            counts.GetValueOrDefault(WebhookDeliveryStatus.Parked),
            counts.GetValueOrDefault(WebhookDeliveryStatus.Discarded));
        var query = status is null ? baseQuery : baseQuery.Where(row => row.Delivery.Status == status);
        var total = status is null ? statusCounts.All : counts.GetValueOrDefault(status);
        if (after is { } key) query = query.Where(row => row.Delivery.CreatedAt < key.At
            || row.Delivery.CreatedAt == key.At && row.Delivery.DeliveryId.CompareTo(key.Id) < 0);
        var rows = await query.OrderByDescending(row => row.Delivery.CreatedAt)
            .ThenByDescending(row => row.Delivery.DeliveryId)
            .Take(take + 1).ToListAsync(ct);
        var selected = rows.Take(take).ToList();
        var ids = selected.Select(row => row.Delivery.DeliveryId).ToList();
        var latestAttempts = ids.Count == 0 ? [] : (await db.DeliveryAttempts.AsNoTracking()
                .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.DeliveryId))
                .OrderByDescending(row => row.AttemptedAt).ThenByDescending(row => row.AttemptId).ToListAsync(ct))
            .GroupBy(row => row.DeliveryId).ToDictionary(group => group.Key, group => group.First());
        var page = selected.Select(row => Row(row.Delivery, row.EventType, row.Body,
            latestAttempts.GetValueOrDefault(row.Delivery.DeliveryId))).ToList();
        return new(page, rows.Count > take && page.Count > 0
            ? Encode(page[^1].CreatedAt, page[^1].Id) : null, total, statusCounts);
    }

    public async Task<WebhookDeliveryDetail?> DetailAsync(Guid subscriptionId, Guid deliveryId,
        CancellationToken ct)
    {
        var joined = await (from delivery in db.Deliveries.AsNoTracking()
                            join publicEvent in db.PublicEvents.AsNoTracking()
                                on new { delivery.TenantId, delivery.EventId }
                                equals new { publicEvent.TenantId, publicEvent.EventId }
                            where delivery.TenantId == tenant.TenantId
                               && delivery.SubscriptionId == subscriptionId
                               && delivery.DeliveryId == deliveryId
                            select new { Delivery = delivery, Event = publicEvent }).SingleOrDefaultAsync(ct);
        if (joined is null) return null;
        var attempts = await db.DeliveryAttempts.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.DeliveryId == deliveryId).OrderBy(row => row.AttemptedAt).ThenBy(row => row.AttemptId)
            .Select(row => new WebhookDeliveryAttemptView(row.AttemptId, row.AttemptedAt, row.Outcome,
                row.StatusCode, row.DurationMs, row.ResponseExcerpt, row.ErrorCode)).ToListAsync(ct);
        var last = attempts.LastOrDefault();
        return new(Row(joined.Delivery, joined.Event.Type, joined.Event.Body, last is null ? null
            : new WebhookDeliveryAttempt
            {
                AttemptId = last.Id,
                TenantId = tenant.TenantId,
                DeliveryId = joined.Delivery.DeliveryId,
                AttemptedAt = last.AttemptedAt,
                Outcome = last.Outcome,
                StatusCode = last.StatusCode,
                DurationMs = last.DurationMs,
                ResponseExcerpt = last.ResponseExcerpt,
                ErrorCode = last.ErrorCode,
            }), joined.Event.Body, attempts);
    }

    public async Task<RedeliverWebhookResponse> RedeliverAsync(Guid subscriptionId, Guid deliveryId,
        CancellationToken ct)
    {
        var source = await db.Deliveries.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.SubscriptionId == subscriptionId && row.DeliveryId == deliveryId, ct)
            ?? throw new CommandRefusedException("webhook_delivery_unknown", "The webhook delivery does not exist.");
        var id = Guid.CreateVersion7(clock.UtcNow);
        db.Deliveries.Add(Replay(source, id));
        await db.SaveChangesAsync(ct);
        return new(id, source.DeliveryId);
    }

    public async Task<RedeliverRangeResponse> RedeliverRangeAsync(Guid subscriptionId,
        RedeliverRangeRequest request, CancellationToken ct)
    {
        if (request.From >= request.To)
            throw new CommandRefusedException("webhook_date_range_invalid", "The replay range must have a from time before its to time.");
        var created = 0;
        Guid? after = null;
        while (true)
        {
            var query = db.Deliveries.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.SubscriptionId == subscriptionId && row.CreatedAt >= request.From
                && row.CreatedAt < request.To && row.ReplayOf == null);
            if (after is { } cursor) query = query.Where(row => row.DeliveryId.CompareTo(cursor) > 0);
            var batch = await query.OrderBy(row => row.DeliveryId).Take(500).ToListAsync(ct);
            if (batch.Count == 0) break;
            db.Deliveries.AddRange(batch.Select(source => Replay(source, Guid.CreateVersion7(clock.UtcNow))));
            await db.SaveChangesAsync(ct);
            created += batch.Count;
            after = batch[^1].DeliveryId;
        }
        return new(created);
    }

    public async Task<DiscardWebhookDeliveryResponse> DiscardAsync(Guid subscriptionId, Guid deliveryId,
        Guid actor, CancellationToken ct)
    {
        var delivery = await db.Deliveries.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.SubscriptionId == subscriptionId && row.DeliveryId == deliveryId, ct)
            ?? throw new CommandRefusedException("webhook_delivery_unknown", "The webhook delivery does not exist.");
        if (delivery.Status != WebhookDeliveryStatus.Parked)
            throw new CommandRefusedException("webhook_delivery_not_parked", "Only a parked delivery can be discarded.");
        delivery.Status = WebhookDeliveryStatus.Discarded;
        delivery.DiscardedAt = clock.UtcNow;
        delivery.DiscardedBy = actor;
        await db.SaveChangesAsync(ct);
        return new(delivery.DeliveryId, delivery.Status);
    }

    private WebhookDelivery Replay(WebhookDelivery source, Guid id) => new()
    {
        TenantId = tenant.TenantId, DeliveryId = id, SubscriptionId = source.SubscriptionId,
        EventId = source.EventId, ReplayOf = source.DeliveryId, Status = WebhookDeliveryStatus.Pending,
        CreatedAt = clock.UtcNow, NextAttemptAt = clock.UtcNow,
    };

    private static WebhookDeliveryRow Row(WebhookDelivery row, string type, string body,
        WebhookDeliveryAttempt? lastAttempt) => new(row.DeliveryId,
        row.EventId, type, row.Status, row.AttemptCount, row.CreatedAt, row.NextAttemptAt,
        row.LastAttemptAt, row.SucceededAt, row.ParkedAt, row.DiscardedAt, row.ReplayOf,
        RecordReference(body), lastAttempt?.Outcome, lastAttempt?.StatusCode, lastAttempt?.DurationMs,
        lastAttempt?.ResponseExcerpt, lastAttempt?.ErrorCode);

    private static string? RecordReference(string body)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("record", out var record)
                && record.TryGetProperty("reference", out var reference) ? reference.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string Encode(DateTime at, Guid id) => Convert.ToBase64String(Encoding.UTF8.GetBytes(
        at.Ticks.ToString(CultureInfo.InvariantCulture) + ":" + id.ToString("N")));

    private static (DateTime At, Guid Id)? Decode(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        try
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split(':');
            return parts.Length == 2 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks)
                && Guid.TryParseExact(parts[1], "N", out var id)
                ? (new DateTime(ticks, DateTimeKind.Utc), id) : null;
        }
        catch (FormatException)
        {
            throw new CommandRefusedException("webhook_delivery_cursor_invalid", "The delivery cursor is invalid.");
        }
    }
}
