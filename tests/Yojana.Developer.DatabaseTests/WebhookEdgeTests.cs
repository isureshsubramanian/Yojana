using System.Diagnostics;
using System.Data.SqlTypes;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Yojana.Developer.Bulk;
using Yojana.Developer.Contracts;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Developer.Delivery;
using Yojana.Developer.Events;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.DatabaseTests;

// P14-T11 edge cases not already covered by WebhookDatabaseTests: an endpoint down past the retry window, a secret
// rotated while a delivery is queued, a duplicate delivery, a burst of events inside one millisecond (and its replay),
// and a 100,000-unit bulk change.
[Collection("developer host")]
public sealed class WebhookEdgeTests(DeveloperHostFixture fixture, ITestOutputHelper output)
{
    [DatabaseFact]
    public async Task Endpoint_down_past_the_retry_window_parks_the_head_then_pauses_and_keeps_later_events()
    {
        var setup = await fixture.NewTenantAsync("edge-down");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(client, "Down endpoint");
        var start = fixture.Clock.UtcNow;
        IReadOnlyList<Guid> events;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            await PointAtAsync(db, created.Subscription.Id, "/test/webhook/fail");
            events = await AddDeliveriesAsync(db, setup.Tenant, created.Subscription.Id, 2);
        }
        await DispatchAsync(setup.Tenant, created.Subscription.Id);

        fixture.Clock.UtcNow = start.AddHours(25);
        await DispatchAsync(setup.Tenant, created.Subscription.Id);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            var head = await DeliveryAsync(db, events[0]);
            var next = await DeliveryAsync(db, events[1]);
            Assert.Equal(WebhookDeliveryStatus.Parked, head.Status);
            Assert.Equal(2, head.AttemptCount);
            Assert.Equal(WebhookDeliveryStatus.Pending, next.Status);
            Assert.Equal(0, next.AttemptCount);
            Assert.Equal(WebhookSubscriptionStatus.Active, (await SubscriptionAsync(db, created.Subscription.Id)).Status);
        }

        fixture.Clock.UtcNow = start.AddHours(73);
        await DispatchAsync(setup.Tenant, created.Subscription.Id);
        using var check = fixture.ScopeFor(setup.Tenant);
        var developer = check.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        var subscription = await SubscriptionAsync(developer, created.Subscription.Id);
        Assert.Equal(WebhookSubscriptionStatus.Paused, subscription.Status);
        Assert.Equal("continuous_failure", subscription.PausedReason);
        var retained = await DeliveryAsync(developer, events[1]);
        Assert.Equal(WebhookDeliveryStatus.Pending, retained.Status);
        Assert.Null(retained.ParkedAt);
        Assert.Equal(fixture.Clock.UtcNow, retained.RetryWindowStartedAt);
    }

    [DatabaseFact]
    public async Task Secret_rotated_while_a_delivery_is_queued_signs_with_both_secrets_until_the_old_one_expires()
    {
        fixture.ResetWebhookProbe();
        var setup = await fixture.NewTenantAsync("edge-rotate");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(client, "Rotating endpoint");
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            await PointAtAsync(db, created.Subscription.Id, "/test/webhook/capture");
            await AddDeliveriesAsync(db, setup.Tenant, created.Subscription.Id, 1);
        }
        var rotation = await client.PostAsync($"/api/developer/webhooks/{created.Subscription.Id:D}/rotate-secret", null);
        rotation.EnsureSuccessStatusCode();
        var rotated = (await rotation.Content.ReadFromJsonAsync<WebhookSecretResponse>())!;
        Assert.NotEqual(created.Secret, rotated.Secret);
        await DispatchAsync(setup.Tenant, created.Subscription.Id);

        var during = Assert.Single(fixture.CapturedWebhooks);
        var (timestamp, signatures) = Parse(during.Signature);
        Assert.Equal([Sign(rotated.Secret, timestamp, during.Body), Sign(created.Secret, timestamp, during.Body)],
            signatures);

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(25);
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await AddDeliveriesAsync(scope.ServiceProvider.GetRequiredService<DeveloperDbContext>(), setup.Tenant,
                created.Subscription.Id, 1);
        await DispatchAsync(setup.Tenant, created.Subscription.Id);
        var after = fixture.CapturedWebhooks.Last();
        var (afterTimestamp, afterSignatures) = Parse(after.Signature);
        Assert.Equal([Sign(rotated.Secret, afterTimestamp, after.Body)], afterSignatures);
    }

    [DatabaseFact]
    public async Task Redelivery_repeats_the_event_id_and_body_so_a_receiver_can_drop_the_duplicate()
    {
        fixture.ResetWebhookProbe();
        var setup = await fixture.NewTenantAsync("edge-duplicate");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(client, "Duplicate endpoint");
        IReadOnlyList<Guid> events;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            await PointAtAsync(db, created.Subscription.Id, "/test/webhook/capture");
            events = await AddDeliveriesAsync(db, setup.Tenant, created.Subscription.Id, 1);
        }
        await DispatchAsync(setup.Tenant, created.Subscription.Id);
        Guid original;
        using (var scope = fixture.ScopeFor(setup.Tenant))
            original = (await DeliveryAsync(scope.ServiceProvider.GetRequiredService<DeveloperDbContext>(), events[0]))
                .DeliveryId;
        (await client.PostAsync(
            $"/api/developer/webhooks/{created.Subscription.Id:D}/deliveries/{original:D}/redeliver", null))
            .EnsureSuccessStatusCode();
        await DispatchAsync(setup.Tenant, created.Subscription.Id);

        var received = fixture.CapturedWebhooks.ToArray();
        Assert.Equal(2, received.Length);
        Assert.Equal(events[0].ToString("D"), received[0].EventId);
        Assert.Equal(received[0].EventId, received[1].EventId);
        Assert.Equal(received[0].Body, received[1].Body);
        using var check = fixture.ScopeFor(setup.Tenant);
        var deliveries = await check.ServiceProvider.GetRequiredService<DeveloperDbContext>().Deliveries
            .Where(row => row.SubscriptionId == created.Subscription.Id).ToListAsync();
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, row => Assert.Equal(WebhookDeliveryStatus.Succeeded, row.Status));
    }

    [DatabaseFact]
    public async Task Burst_staged_in_one_millisecond_and_its_replayed_period_arrive_in_event_order()
    {
        fixture.ResetWebhookProbe();
        var setup = await fixture.NewTenantAsync("edge-burst");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(client, "Burst endpoint", [PublicEventTypes.UnitCreated]);
        var at = fixture.Clock.UtcNow;
        var staged = new List<string>();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            await PointAtAsync(scope.ServiceProvider.GetRequiredService<DeveloperDbContext>(), created.Subscription.Id,
                "/test/webhook/capture");
            var stager = scope.ServiceProvider.GetRequiredService<PublicEventStager>();
            // The clock does not move: every delivery shares one created_at, as a fast burst does in production.
            for (var index = 0; index < 20; index++)
            {
                var message = new UnitCommissioned(new(UnitCommissioned.Schema, UnitCommissioned.Version,
                    Guid.CreateVersion7(at), Guid.CreateVersion7(at), at, at, 1, Guid.CreateVersion7(),
                    "system:test", null), Guid.CreateVersion7(), "receipt", "active", [], null, null, null);
                await stager.StageAsync(message, default);
            }
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            staged.AddRange((await db.Deliveries.Where(row => row.SubscriptionId == created.Subscription.Id)
                .OrderBy(row => row.QueuePosition).Select(row => row.EventId).ToListAsync()).Select(id => id.ToString("D")));
        }
        Assert.Equal(20, staged.Count);
        await DrainAsync(setup.Tenant, created.Subscription.Id, 20);
        Assert.Equal(staged, fixture.CapturedWebhooks.Select(row => row.EventId));

        var replay = await client.PostAsJsonAsync(
            $"/api/developer/webhooks/{created.Subscription.Id:D}/deliveries/redeliver-range",
            new RedeliverRangeRequest(at, at.AddSeconds(1)));
        replay.EnsureSuccessStatusCode();
        Assert.Equal(20, (await replay.Content.ReadFromJsonAsync<RedeliverRangeResponse>())!.Created);
        await DrainAsync(setup.Tenant, created.Subscription.Id, 40);
        Assert.Equal(staged.Concat(staged), fixture.CapturedWebhooks.Select(row => row.EventId));
    }

    private async Task DrainAsync(TenantId tenant, Guid subscriptionId, int expected)
    {
        for (var round = 0; round < 50 && fixture.CapturedWebhooks.Count < expected; round++)
            await DispatchAsync(tenant, subscriptionId);
        Assert.Equal(expected, fixture.CapturedWebhooks.Count);
    }

    [DatabaseFact]
    public async Task Hundred_thousand_unit_bulk_change_publishes_one_summary_and_pages_every_unit()
    {
        const int size = 100_000;
        var setup = await fixture.NewTenantAsync("edge-bulk");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var subscription = await CreateAsync(client, "Bulk endpoint",
            [PublicEventTypes.BulkCompleted, PublicEventTypes.UnitCreated]);
        var party = Guid.CreateVersion7();
        var seeded = new[]
        {
            await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "SERVER-BULK-0001"),
            await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "SERVER-BULK-0002"),
        };
        var at = fixture.Clock.UtcNow;
        var units = seeded.Concat(Enumerable.Range(0, size - seeded.Length).Select(_ => Guid.CreateVersion7(at)))
            .ToList();
        var operationId = Guid.CreateVersion7(at);
        var watch = Stopwatch.StartNew();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var stager = scope.ServiceProvider.GetRequiredService<PublicEventStager>();
            // A sample of the per-unit events the bulk executor appends inside the operation; each is suppressed.
            foreach (var unit in units.Take(200))
                await stager.StageAsync(new UnitCommissioned(new(UnitCommissioned.Schema, UnitCommissioned.Version,
                    Guid.CreateVersion7(at), unit, at, at, 1, Guid.CreateVersion7(), "system:test", null,
                    operationId, size, "cover_change"), Guid.CreateVersion7(), "receipt", "active", [], null, null,
                    null), default);
            await stager.StageAsync(new BulkOperationCompleted(new(BulkOperationCompleted.Schema,
                BulkOperationCompleted.Version, Guid.CreateVersion7(at), at, at, Guid.CreateVersion7(),
                "system:test", null), operationId, "cover_change", size, units), default);
        }
        output.WriteLine($"Staged {size:N0} bulk members in {watch.ElapsedMilliseconds:N0} ms");

        using var check = fixture.ScopeFor(setup.Tenant);
        var db = check.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        Assert.Equal(1, await db.PublicEvents.CountAsync(row => row.Type == PublicEventTypes.BulkCompleted));
        Assert.Equal(0, await db.PublicEvents.CountAsync(row => row.Type == PublicEventTypes.UnitCreated));
        Assert.Equal(1, await db.Deliveries.CountAsync(row => row.SubscriptionId == subscription.Subscription.Id));
        Assert.Equal(size, await db.BulkOperationUnits.CountAsync(row => row.OperationId == operationId));

        watch.Restart();
        var query = check.ServiceProvider.GetRequiredService<BulkOperationUnitsQuery>();
        var paged = new List<BulkOperationUnitRow>(size);
        Guid? after = null;
        var pages = 0;
        do
        {
            var page = await query.PageAsync(operationId, after, 200, default);
            Assert.NotNull(page);
            paged.AddRange(page.Items);
            pages++;
            after = page.NextCursor is { } next ? Guid.Parse(next) : null;
        } while (after is not null);
        output.WriteLine($"Paged {paged.Count:N0} members in {pages} pages in {watch.ElapsedMilliseconds:N0} ms");

        Assert.Equal(size, paged.Select(row => row.UnitId).Distinct().Count());
        Assert.Equal(paged.Select(row => new SqlGuid(row.UnitId)).Order(), paged.Select(row => new SqlGuid(row.UnitId)));
        Assert.Equal("SERVER-BULK-0001", paged.Single(row => row.UnitId == seeded[0]).Reference);
        Assert.Equal("SERVER-BULK-0002", paged.Single(row => row.UnitId == seeded[1]).Reference);
        Assert.Equal(size - seeded.Length, paged.Count(row => row.Reference is null));
    }

    private async Task<WebhookSecretResponse> CreateAsync(HttpClient client, string name,
        IReadOnlyList<string>? types = null)
    {
        var response = await client.PostAsJsonAsync("/api/developer/webhooks", new CreateWebhookSubscription(name,
            new Uri(new Uri(fixture.Host.Urls.Single()), "/test/webhook/echo").ToString(),
            types ?? [PublicEventTypes.ImportCompleted], null));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WebhookSecretResponse>())!;
    }

    private async Task PointAtAsync(DeveloperDbContext db, Guid subscriptionId, string path)
    {
        var subscription = await SubscriptionAsync(db, subscriptionId);
        subscription.Url = new Uri(new Uri(fixture.Host.Urls.Single()), path).ToString();
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<Guid>> AddDeliveriesAsync(DeveloperDbContext db, TenantId tenant,
        Guid subscriptionId, int count)
    {
        var ids = new List<Guid>();
        var position = await WebhookDeliveryQueue.ReserveAsync(db, count, default);
        for (var index = 0; index < count; index++)
        {
            var at = fixture.Clock.UtcNow.AddMilliseconds(index);
            var eventId = Guid.CreateVersion7(at);
            ids.Add(eventId);
            db.PublicEvents.Add(new PublicEvent
            {
                TenantId = tenant, EventId = eventId, Type = PublicEventTypes.ImportCompleted, Version = "1",
                RecordKind = "import", RecordId = Guid.CreateVersion7(at), Sequence = 1, OccurredAt = at,
                RecordedAt = at, Body = $$"""{"id":"{{eventId:D}}","type":"import.completed","version":"1"}""",
            });
            db.Deliveries.Add(new WebhookDelivery
            {
                TenantId = tenant, DeliveryId = Guid.CreateVersion7(at.AddTicks(1)), SubscriptionId = subscriptionId,
                EventId = eventId, Status = WebhookDeliveryStatus.Pending, CreatedAt = at,
                NextAttemptAt = at.AddSeconds(-1), QueuePosition = position++,
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private async Task DispatchAsync(TenantId tenant, Guid subscriptionId)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<WebhookDeliveryDispatcher>()
            .DispatchSubscriptionAsync(subscriptionId, default);
    }

    private static Task<WebhookSubscription> SubscriptionAsync(DeveloperDbContext db, Guid id) =>
        db.Subscriptions.SingleAsync(row => row.SubscriptionId == id);

    private static Task<WebhookDelivery> DeliveryAsync(DeveloperDbContext db, Guid eventId) =>
        db.Deliveries.OrderBy(row => row.CreatedAt).FirstAsync(row => row.EventId == eventId);

    private static (string Timestamp, IReadOnlyList<string> Signatures) Parse(string header)
    {
        var parts = header.Split(',').Select(part => part.Split('=', 2)).ToList();
        return (parts.Single(part => part[0] == "t")[1], parts.Where(part => part[0] == "v1").Select(part => part[1]).ToList());
    }

    // Receiver-side check, written independently of WebhookSigner: HMAC-SHA256 of "<t>.<body>" keyed with the
    // displayed secret string's UTF-8 bytes (the published signature format).
    private static string Sign(string secret, string timestamp, string body) => Convert.ToHexStringLower(
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp}.{body}")));
}
