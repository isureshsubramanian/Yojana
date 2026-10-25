using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Developer.Contracts;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Developer.Delivery;
using Yojana.Developer.Events;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ports;

namespace Yojana.Developer.DatabaseTests;

[Collection("developer host")]
public sealed class WebhookDatabaseTests(DeveloperHostFixture fixture)
{
    [DatabaseFact]
    public async Task Administrator_creates_verified_subscription_and_secret_is_returned_once()
    {
        var setup = await fixture.NewTenantAsync("webhook-create");
        using var administrator = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var response = await administrator.PostAsJsonAsync("/api/developer/webhooks", Request(
            "Operations feed", "/test/webhook/echo", [PublicEventTypes.UnitCreated]));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<WebhookSecretResponse>();
        Assert.NotNull(created);
        Assert.StartsWith("yjw_", created.Secret);
        Assert.Equal("active", created.Subscription.Status);

        var detail = await administrator.GetFromJsonAsync<WebhookSubscriptionView>(
            $"/api/developer/webhooks/{created.Subscription.Id:D}");
        Assert.NotNull(detail);
        Assert.Equal(created.Subscription.Id, detail.Id);

        using var staff = await fixture.ClientAsync(setup.Tenant, setup.Staff);
        Assert.Equal(HttpStatusCode.Forbidden, (await staff.GetAsync("/api/developer/webhooks")).StatusCode);
    }

    [DatabaseFact]
    public async Task Handshake_failure_and_redirect_are_refused()
    {
        var setup = await fixture.NewTenantAsync("webhook-handshake");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var failed = await client.PostAsJsonAsync("/api/developer/webhooks", Request(
            "Failing", "/test/webhook/fail", [PublicEventTypes.UnitCreated]));
        Assert.False(failed.IsSuccessStatusCode);
        var redirected = await client.PostAsJsonAsync("/api/developer/webhooks", Request(
            "Redirected", "/test/webhook/redirect", [PublicEventTypes.UnitCreated]));
        Assert.False(redirected.IsSuccessStatusCode);
        using var scope = fixture.ScopeFor(setup.Tenant);
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<DeveloperDbContext>().Subscriptions
            .Where(row => row.Name == "Failing" || row.Name == "Redirected").ToListAsync());
    }

    [DatabaseFact]
    public async Task Staging_is_idempotent_and_matches_legal_entity_filter()
    {
        var setup = await fixture.NewTenantAsync("webhook-stage");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var legalEntity = Guid.CreateVersion7();
        var created = await CreateAsync(client, "Imports", [PublicEventTypes.ImportCompleted],
            new WebhookFilters(LegalEntityIds: [legalEntity]));
        var at = fixture.Clock.UtcNow;
        var message = new ImportCompleted(new(ImportCompleted.Schema, ImportCompleted.Version,
            Guid.CreateVersion7(at), at, at, legalEntity, "system:test", null), Guid.CreateVersion7(),
            "user_upload", "hash", null, "applied", 2, 0, 0);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var stager = scope.ServiceProvider.GetRequiredService<PublicEventStager>();
            await stager.StageAsync(message, default);
            await stager.StageAsync(message, default);
        }
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            Assert.Equal(1, await db.PublicEvents.CountAsync(row => row.EventId == message.Header.EventId));
            Assert.Equal(1, await db.Deliveries.CountAsync(row => row.SubscriptionId == created.Subscription.Id));
        }
    }

    [DatabaseFact]
    public async Task Gone_pauses_subscription_and_retains_pending_delivery()
    {
        var setup = await fixture.NewTenantAsync("webhook-gone");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(client, "Gone", [PublicEventTypes.ImportCompleted]);
        var at = fixture.Clock.UtcNow;
        var message = new ImportCompleted(new(ImportCompleted.Schema, ImportCompleted.Version,
            Guid.CreateVersion7(at), at, at, Guid.CreateVersion7(), "system:test", null),
            Guid.CreateVersion7(), "user_upload", "hash", null, "applied", 1, 0, 0);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            var subscription = await db.Subscriptions.SingleAsync(row => row.SubscriptionId == created.Subscription.Id);
            subscription.Url = Target("/test/webhook/gone");
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<PublicEventStager>().StageAsync(message, default);
            await scope.ServiceProvider.GetRequiredService<WebhookDeliveryDispatcher>()
                .DispatchSubscriptionAsync(subscription.SubscriptionId, default);
        }
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            var subscription = await db.Subscriptions.SingleAsync(row => row.SubscriptionId == created.Subscription.Id);
            Assert.Equal(WebhookSubscriptionStatus.Paused, subscription.Status);
            Assert.Equal("endpoint_gone", subscription.PausedReason);
            Assert.Equal(WebhookDeliveryStatus.Pending, (await db.Deliveries.SingleAsync()).Status);
            Assert.Equal(1, await db.DeliveryAttempts.CountAsync());
            var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Contains(await platform.SecurityEvents.Where(row => row.TenantId == setup.Tenant).ToListAsync(),
                row => row.Reason == "webhook_automatically_paused");
        }
    }

    [DatabaseFact]
    public async Task Slow_subscription_does_not_hold_other_subscriptions_ticks()
    {
        fixture.ResetWebhookProbe();
        var setup = await fixture.NewTenantAsync("webhook-isolate");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var slow = await CreateAsync(client, "Slow endpoint", [PublicEventTypes.ImportCompleted]);
        var quick = await CreateAsync(client, "Quick endpoint", [PublicEventTypes.ImportCompleted]);
        IReadOnlyList<Guid> slowEvents;
        IReadOnlyList<Guid> quickEvents;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            var slowRow = await db.Subscriptions.SingleAsync(row => row.SubscriptionId == slow.Subscription.Id);
            slowRow.Url = Target("/test/webhook/slow-ten-seconds");
            slowRow.RatePerSecond = 1;
            var quickRow = await db.Subscriptions.SingleAsync(row => row.SubscriptionId == quick.Subscription.Id);
            quickRow.Url = Target("/test/webhook/record");
            quickRow.RatePerSecond = 1;
            await db.SaveChangesAsync();
            slowEvents = await AddDeliveriesAsync(db, setup.Tenant, slow.Subscription.Id, 1,
                fixture.Clock.UtcNow);
            quickEvents = await AddDeliveriesAsync(db, setup.Tenant, quick.Subscription.Id, 3,
                fixture.Clock.UtcNow.AddMilliseconds(1));
        }

        var coordinator = fixture.Services.GetRequiredService<WebhookDeliveryCoordinator>();
        Assert.True(coordinator.TryStart(setup.Tenant, slow.Subscription.Id));
        await WaitForObservedAsync(slowEvents[0], TimeSpan.FromSeconds(2));
        for (var tick = 1; tick <= 3; tick++)
        {
            Assert.True(coordinator.TryStart(setup.Tenant, quick.Subscription.Id));
            await WaitForObservedAsync(quickEvents[tick - 1], TimeSpan.FromSeconds(2));
            if (tick < 3) await Task.Delay(TimeSpan.FromSeconds(1));
        }

        await Task.Delay(250);
        using (var check = fixture.ScopeFor(setup.Tenant))
        {
            var db = check.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            Assert.Equal(0, await AttemptsAsync(db, slow.Subscription.Id));
            Assert.Equal(3, await AttemptsAsync(db, quick.Subscription.Id));
            Assert.Equal(3, await db.Deliveries.CountAsync(row => row.SubscriptionId == quick.Subscription.Id
                && row.Status == WebhookDeliveryStatus.Succeeded));
        }
        Assert.Equal(quickEvents, fixture.DeliveredWebhookEvents.Where(quickEvents.Contains).ToArray());
        await Task.Delay(TimeSpan.FromSeconds(9));
        using var slowCheck = fixture.ScopeFor(setup.Tenant);
        Assert.Equal(1, await AttemptsAsync(slowCheck.ServiceProvider.GetRequiredService<DeveloperDbContext>(),
            slow.Subscription.Id));
    }

    [DatabaseFact]
    public async Task Failed_head_blocks_later_events_until_it_recovers()
    {
        fixture.ResetWebhookProbe();
        var setup = await fixture.NewTenantAsync("webhook-order");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(client, "Ordered", [PublicEventTypes.ImportCompleted]);
        IReadOnlyList<Guid> events;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            var subscription = await db.Subscriptions.SingleAsync(row => row.SubscriptionId == created.Subscription.Id);
            subscription.Url = Target("/test/webhook/fail-twice");
            await db.SaveChangesAsync();
            events = await AddDeliveriesAsync(db, setup.Tenant, subscription.SubscriptionId, 3,
                fixture.Clock.UtcNow);
            var dispatcher = scope.ServiceProvider.GetRequiredService<WebhookDeliveryDispatcher>();
            await dispatcher.DispatchSubscriptionAsync(subscription.SubscriptionId, default);
        }
        Assert.Equal([events[0]], fixture.DeliveredWebhookEvents.ToArray());

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(2);
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await scope.ServiceProvider.GetRequiredService<WebhookDeliveryDispatcher>()
                .DispatchSubscriptionAsync(created.Subscription.Id, default);
        Assert.Equal([events[0], events[0]], fixture.DeliveredWebhookEvents.ToArray());

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(2);
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await scope.ServiceProvider.GetRequiredService<WebhookDeliveryDispatcher>()
                .DispatchSubscriptionAsync(created.Subscription.Id, default);
        Assert.Equal([events[0], events[0], events[0], events[1], events[2]],
            fixture.DeliveredWebhookEvents.ToArray());
        using var check = fixture.ScopeFor(setup.Tenant);
        var deliveries = await check.ServiceProvider.GetRequiredService<DeveloperDbContext>().Deliveries
            .Where(row => row.SubscriptionId == created.Subscription.Id).OrderBy(row => row.CreatedAt).ToListAsync();
        Assert.Equal([3, 1, 1], deliveries.Select(row => row.AttemptCount).ToArray());
        Assert.All(deliveries, row => Assert.Equal(WebhookDeliveryStatus.Succeeded, row.Status));
    }

    [DatabaseFact]
    public async Task Resume_starts_a_fresh_retry_window_for_old_queued_delivery()
    {
        var setup = await fixture.NewTenantAsync("webhook-resume");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var created = await CreateAsync(client, "Paused queue", [PublicEventTypes.ImportCompleted]);
        (await client.PostAsync($"/api/developer/webhooks/{created.Subscription.Id:D}/pause", null))
            .EnsureSuccessStatusCode();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            var subscription = await db.Subscriptions.SingleAsync(row => row.SubscriptionId == created.Subscription.Id);
            subscription.Url = Target("/test/webhook/fail");
            await db.SaveChangesAsync();
            await AddDeliveriesAsync(db, setup.Tenant, subscription.SubscriptionId, 1, fixture.Clock.UtcNow);
        }
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddDays(3);
        (await client.PostAsync($"/api/developer/webhooks/{created.Subscription.Id:D}/resume", null))
            .EnsureSuccessStatusCode();
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await scope.ServiceProvider.GetRequiredService<WebhookDeliveryDispatcher>()
                .DispatchSubscriptionAsync(created.Subscription.Id, default);

        using var check = fixture.ScopeFor(setup.Tenant);
        var delivery = await check.ServiceProvider.GetRequiredService<DeveloperDbContext>().Deliveries
            .SingleAsync(row => row.SubscriptionId == created.Subscription.Id);
        Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(fixture.Clock.UtcNow, delivery.RetryWindowStartedAt);
        Assert.Null(delivery.ParkedAt);
    }

    [DatabaseFact]
    public async Task Item_family_filter_matches_the_stable_string_code()
    {
        var setup = await fixture.NewTenantAsync("webhook-family");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var matching = await CreateAsync(client, "Rack servers", [PublicEventTypes.UnitCreated],
            new WebhookFilters(ItemFamilies: ["rack_servers"]));
        var other = await CreateAsync(client, "Network", [PublicEventTypes.UnitCreated],
            new WebhookFilters(ItemFamilies: ["network"]));
        var itemId = Guid.CreateVersion7(fixture.Clock.UtcNow);
        var unitId = Guid.CreateVersion7(fixture.Clock.UtcNow);
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(
            InMemoryItemPorts.DefaultProfile(itemId) with { ItemClass = "rack_servers" });
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            serialisation.Current.Add(new UnitCurrent
            {
                TenantId = setup.Tenant, UnitId = unitId, ItemId = itemId,
                LifecycleStatus = LifecycleStatus.Released, Disposition = UnitDisposition.InTransit,
                LastEventSeq = 1, LastEventTime = fixture.Clock.UtcNow,
            });
            await serialisation.SaveChangesAsync();
            var message = new UnitCommissioned(new(UnitCommissioned.Schema, UnitCommissioned.Version,
                Guid.CreateVersion7(fixture.Clock.UtcNow), unitId, fixture.Clock.UtcNow, fixture.Clock.UtcNow,
                1, Guid.CreateVersion7(), "system:test", null), itemId, "receipt", "active", [], null, null, null);
            await scope.ServiceProvider.GetRequiredService<PublicEventStager>().StageAsync(message, default);
        }
        using var check = fixture.ScopeFor(setup.Tenant);
        var db = check.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        Assert.Equal("rack_servers", (await db.PublicEvents.SingleAsync()).ItemFamily);
        Assert.True(await db.Deliveries.AnyAsync(row => row.SubscriptionId == matching.Subscription.Id));
        Assert.False(await db.Deliveries.AnyAsync(row => row.SubscriptionId == other.Subscription.Id));
    }

    [DatabaseFact]
    public async Task Database_lock_skips_a_subscription_already_owned_by_another_host()
    {
        var setup = await fixture.NewTenantAsync("webhook-lock");
        var subscriptionId = Guid.CreateVersion7(fixture.Clock.UtcNow);
        using var firstScope = fixture.ScopeFor(setup.Tenant);
        await using var first = await firstScope.ServiceProvider.GetRequiredService<WebhookSubscriptionDispatchLock>()
            .TryAcquireAsync(subscriptionId, default);
        Assert.NotNull(first);
        using var secondScope = fixture.ScopeFor(setup.Tenant);
        await using var second = await secondScope.ServiceProvider.GetRequiredService<WebhookSubscriptionDispatchLock>()
            .TryAcquireAsync(subscriptionId, default);
        Assert.Null(second);
        await using var different = await secondScope.ServiceProvider.GetRequiredService<WebhookSubscriptionDispatchLock>()
            .TryAcquireAsync(Guid.CreateVersion7(fixture.Clock.UtcNow), default);
        Assert.NotNull(different);
    }

    [DatabaseFact]
    public async Task Replay_discard_and_tenant_isolation_keep_history()
    {
        var first = await fixture.NewTenantAsync("webhook-first");
        var second = await fixture.NewTenantAsync("webhook-second");
        using var firstClient = await fixture.ClientAsync(first.Tenant, first.Administrator);
        var created = await CreateAsync(firstClient, "History", [PublicEventTypes.ImportCompleted]);
        Guid deliveryId;
        using (var scope = fixture.ScopeFor(first.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
            var eventId = Guid.CreateVersion7(fixture.Clock.UtcNow);
            db.PublicEvents.Add(new() { TenantId = first.Tenant, EventId = eventId,
                Type = PublicEventTypes.ImportCompleted, Version = "1", RecordKind = "import",
                RecordId = Guid.CreateVersion7(), Sequence = 1, OccurredAt = fixture.Clock.UtcNow,
                RecordedAt = fixture.Clock.UtcNow, Body = "{}" });
            deliveryId = Guid.CreateVersion7(fixture.Clock.UtcNow);
            db.Deliveries.Add(new() { TenantId = first.Tenant, DeliveryId = deliveryId,
                SubscriptionId = created.Subscription.Id, EventId = eventId,
                Status = WebhookDeliveryStatus.Parked, CreatedAt = fixture.Clock.UtcNow,
                NextAttemptAt = fixture.Clock.UtcNow, ParkedAt = fixture.Clock.UtcNow });
            await db.SaveChangesAsync();
            var queries = scope.ServiceProvider.GetRequiredService<WebhookDeliveryQueries>();
            var replay = await queries.RedeliverAsync(created.Subscription.Id, deliveryId, default);
            Assert.Equal(deliveryId, replay.ReplayOf);
            var range = await queries.RedeliverRangeAsync(created.Subscription.Id,
                new(fixture.Clock.UtcNow.AddMinutes(-1), fixture.Clock.UtcNow.AddMinutes(1)), default);
            Assert.Equal(1, range.Created);
            Assert.Equal(WebhookDeliveryStatus.Discarded,
                (await queries.DiscardAsync(created.Subscription.Id, deliveryId, first.Administrator, default)).Status);
        }
        using var otherScope = fixture.ScopeFor(second.Tenant);
        var other = otherScope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        Assert.Empty(await other.Subscriptions.ToListAsync());
        Assert.Empty(await other.Deliveries.ToListAsync());
    }

    [DatabaseFact]
    public async Task Bulk_above_threshold_yields_one_summary_and_members_while_below_keeps_unit_event()
    {
        var setup = await fixture.NewTenantAsync("webhook-bulk");
        using var client = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var subscription = await CreateAsync(client, "Bulk", [PublicEventTypes.BulkCompleted,
            PublicEventTypes.UnitCreated]);
        var at = fixture.Clock.UtcNow;
        var operationId = Guid.CreateVersion7(at);
        var firstUnit = Guid.CreateVersion7(at);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var stager = scope.ServiceProvider.GetRequiredService<PublicEventStager>();
            await stager.StageAsync(new UnitCommissioned(new(UnitCommissioned.Schema,
                UnitCommissioned.Version, Guid.CreateVersion7(at), firstUnit, at, at, 1,
                Guid.CreateVersion7(), "system:test", null, operationId, 501, "cover_change"),
                Guid.CreateVersion7(), "receipt", "active", [], null, null, null), default);
            await stager.StageAsync(new BulkOperationCompleted(new(BulkOperationCompleted.Schema,
                BulkOperationCompleted.Version, Guid.CreateVersion7(at), at, at, Guid.CreateVersion7(),
                "system:test", null), operationId, "cover_change", 501, [firstUnit]), default);

            var secondUnit = Guid.CreateVersion7(at);
            await stager.StageAsync(new UnitCommissioned(new(UnitCommissioned.Schema,
                UnitCommissioned.Version, Guid.CreateVersion7(at), secondUnit, at, at, 1,
                Guid.CreateVersion7(), "system:test", null, Guid.CreateVersion7(at), 2, "cover_change"),
                Guid.CreateVersion7(), "receipt", "active", [], null, null, null), default);
        }
        using var check = fixture.ScopeFor(setup.Tenant);
        var db = check.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        Assert.Equal(1, await db.PublicEvents.CountAsync(row => row.Type == PublicEventTypes.BulkCompleted));
        Assert.Equal(1, await db.PublicEvents.CountAsync(row => row.Type == PublicEventTypes.UnitCreated));
        Assert.Equal(1, await db.BulkOperationUnits.CountAsync(row => row.OperationId == operationId));
        Assert.Equal(2, await db.Deliveries.CountAsync(row => row.SubscriptionId == subscription.Subscription.Id));
    }

    [DatabaseFact]
    public async Task Retention_removes_finished_history_but_keeps_an_event_referenced_by_pending_delivery()
    {
        var setup = await fixture.NewTenantAsync("webhook-retain");
        var old = fixture.Clock.UtcNow.AddDays(-31);
        var subscriptionId = Guid.CreateVersion7(old);
        var pendingEventId = Guid.CreateVersion7(old);
        var finishedEventId = Guid.CreateVersion7(old);
        var orphanEventId = Guid.CreateVersion7(old);
        var finishedDeliveryId = Guid.CreateVersion7(old);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        db.PublicEvents.AddRange(
            Event(setup.Tenant, pendingEventId, old), Event(setup.Tenant, finishedEventId, old),
            Event(setup.Tenant, orphanEventId, old));
        db.Deliveries.Add(new WebhookDelivery { TenantId = setup.Tenant,
            DeliveryId = Guid.CreateVersion7(old), SubscriptionId = subscriptionId,
            EventId = pendingEventId, Status = WebhookDeliveryStatus.Pending,
            CreatedAt = old, NextAttemptAt = old });
        db.Deliveries.Add(new WebhookDelivery { TenantId = setup.Tenant,
            DeliveryId = finishedDeliveryId, SubscriptionId = subscriptionId,
            EventId = finishedEventId, Status = WebhookDeliveryStatus.Succeeded,
            CreatedAt = old, NextAttemptAt = old, SucceededAt = old });
        db.DeliveryAttempts.Add(new WebhookDeliveryAttempt { TenantId = setup.Tenant,
            AttemptId = Guid.CreateVersion7(old), DeliveryId = finishedDeliveryId,
            AttemptedAt = old, Outcome = "succeeded", StatusCode = 204 });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<WebhookRetentionService>().RunAsync(default);

        Assert.True(await db.PublicEvents.AnyAsync(row => row.EventId == pendingEventId));
        Assert.False(await db.PublicEvents.AnyAsync(row => row.EventId == finishedEventId));
        Assert.False(await db.PublicEvents.AnyAsync(row => row.EventId == orphanEventId));
        Assert.False(await db.Deliveries.AnyAsync(row => row.DeliveryId == finishedDeliveryId));
        Assert.False(await db.DeliveryAttempts.AnyAsync(row => row.DeliveryId == finishedDeliveryId));
    }

    private static PublicEvent Event(TenantId tenant, Guid id, DateTime at) => new()
    {
        TenantId = tenant, EventId = id, Type = PublicEventTypes.UnitCreated,
        Version = "1", RecordKind = "unit", RecordId = Guid.CreateVersion7(at),
        Sequence = 1, OccurredAt = at, RecordedAt = at, Body = "{}",
    };

    private static async Task<IReadOnlyList<Guid>> AddDeliveriesAsync(DeveloperDbContext db,
        TenantId tenant, Guid subscriptionId, int count, DateTime createdAt)
    {
        var ids = new List<Guid>();
        // Several rows in one save: reserve their queue positions so the send order is the insertion order.
        var position = await WebhookDeliveryQueue.ReserveAsync(db, count, default);
        for (var index = 0; index < count; index++)
        {
            var at = createdAt.AddMilliseconds(index);
            var eventId = Guid.CreateVersion7(at);
            ids.Add(eventId);
            db.PublicEvents.Add(Event(tenant, eventId, at));
            db.Deliveries.Add(new WebhookDelivery
            {
                TenantId = tenant, DeliveryId = Guid.CreateVersion7(at.AddTicks(1)),
                SubscriptionId = subscriptionId, EventId = eventId,
                Status = WebhookDeliveryStatus.Pending, CreatedAt = at,
                NextAttemptAt = createdAt.AddSeconds(-1), QueuePosition = position++,
            });
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private async Task WaitForObservedAsync(Guid eventId, TimeSpan timeout)
    {
        var expires = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < expires)
        {
            if (fixture.DeliveredWebhookEvents.Contains(eventId)) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Webhook endpoint did not observe {eventId:D} within {timeout}; observed "
            + string.Join(",", fixture.DeliveredWebhookEvents.Select(id => id.ToString("D"))));
    }

    private static Task<int> AttemptsAsync(DeveloperDbContext db, Guid subscriptionId) =>
        db.DeliveryAttempts.CountAsync(attempt => db.Deliveries.Any(delivery =>
            delivery.TenantId == attempt.TenantId && delivery.DeliveryId == attempt.DeliveryId
            && delivery.SubscriptionId == subscriptionId));

    private CreateWebhookSubscription Request(string name, string path,
        IReadOnlyList<string> types, WebhookFilters? filters = null) =>
        new(name, Target(path), types, filters);

    private async Task<WebhookSecretResponse> CreateAsync(HttpClient client, string name,
        IReadOnlyList<string> types, WebhookFilters? filters = null)
    {
        var response = await client.PostAsJsonAsync("/api/developer/webhooks",
            Request(name, "/test/webhook/echo", types, filters));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<WebhookSecretResponse>())!;
    }

    private string Target(string path) => new Uri(new Uri(fixture.Host.Urls.Single()), path).ToString();
}
