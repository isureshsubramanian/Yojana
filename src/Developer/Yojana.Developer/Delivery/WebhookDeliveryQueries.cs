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
        string? eventType, DateTime? from, DateTime? to, string? sort, string? direction, string? cursor, int? take,
        CancellationToken ct)
    {
        if (status is not null && status is not (WebhookDeliveryStatus.Pending
            or WebhookDeliveryStatus.Succeeded or WebhookDeliveryStatus.Parked
            or WebhookDeliveryStatus.Discarded))
            throw new CommandRefusedException("webhook_delivery_status_unknown", "The delivery status filter is not known.");
        if (eventType is not null && !PublicEventTypes.Subscribable.Contains(eventType, StringComparer.Ordinal))
            throw new CommandRefusedException("webhook_event_type_unknown", "The event type filter is not in public catalogue v1.");
        if (from > to) throw new CommandRefusedException("webhook_date_range_invalid", "From must not be after to.");
        var paging = DeveloperEndpoints.DeliveryPages.Parse(sort, direction, take, cursor);
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
        var deliveries = await paging.Apply(query.Select(row => row.Delivery))
            .Take(paging.Take + 1).ToListAsync(ct);
        var selected = deliveries.Take(paging.Take).ToList();
        var ids = selected.Select(row => row.DeliveryId).ToList();
        var eventRows = ids.Count == 0 ? [] : await baseQuery
            .Where(row => ids.Contains(row.Delivery.DeliveryId)).ToListAsync(ct);
        var eventsByDelivery = eventRows.ToDictionary(row => row.Delivery.DeliveryId);
        var latestAttempts = ids.Count == 0 ? [] : (await db.DeliveryAttempts.AsNoTracking()
                .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.DeliveryId))
                .OrderByDescending(row => row.AttemptedAt).ThenByDescending(row => row.AttemptId).ToListAsync(ct))
            .GroupBy(row => row.DeliveryId).ToDictionary(group => group.Key, group => group.First());
        var page = selected.Select(row => Row(row, eventsByDelivery[row.DeliveryId].EventType,
            eventsByDelivery[row.DeliveryId].Body, latestAttempts.GetValueOrDefault(row.DeliveryId))).ToList();
        return new(page, deliveries.Count > paging.Take && selected.Count > 0
            ? paging.CursorFor(selected[^1]) : null, total, statusCounts);
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
        long? after = null;
        while (true)
        {
            var query = db.Deliveries.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.SubscriptionId == subscriptionId && row.CreatedAt >= request.From
                && row.CreatedAt < request.To && row.ReplayOf == null);
            if (after is { } cursor) query = query.Where(row => row.QueuePosition > cursor);
            // Replays keep the originals' send order: walk them in queue order and give the copies one range.
            var batch = await query.OrderBy(row => row.QueuePosition).Take(500).ToListAsync(ct);
            if (batch.Count == 0) break;
            var position = await WebhookDeliveryQueue.ReserveAsync(db, batch.Count, ct);
            foreach (var source in batch)
            {
                var replay = Replay(source, Guid.CreateVersion7(clock.UtcNow));
                replay.QueuePosition = position++;
                db.Deliveries.Add(replay);
            }
            await db.SaveChangesAsync(ct);
            created += batch.Count;
            after = batch[^1].QueuePosition;
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

}
