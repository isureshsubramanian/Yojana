using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Developer.Contracts;
using Yojana.Developer.Data;
using Yojana.Developer.Delivery;
using Yojana.Developer.Events;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using PublicApiScopes = Yojana.Serialisation.Contracts.PublicApiScopes;

namespace Yojana.Developer.DatabaseTests;

// P14 exit criterion: a sample customer application subscribes, receives a signed unit.shipped event, verifies it,
// reads the unit and its cover through the public API with only the fields its scope allows, and recovers every
// event it missed during a 6-hour outage by replay.
[Collection("developer host")]
public sealed class ExitCriteriaSampleApplicationTests(DeveloperHostFixture fixture)
{
    [DatabaseFact]
    public async Task Sample_customer_application_verifies_reads_within_scope_and_recovers_a_six_hour_outage()
    {
        fixture.ResetWebhookProbe();
        var setup = await fixture.NewTenantAsync("exit-sample");
        var party = Guid.CreateVersion7();
        var first = await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, "SERVER-EXIT-0001");
        await PublicApiTestSupport.SeedEntitlementAsync(fixture, setup.Tenant, first, party);
        var later = new List<Guid>();
        for (var index = 2; index <= 7; index++)
            later.Add(await PublicApiTestSupport.SeedUnitAsync(fixture, setup.Tenant, party, $"SERVER-EXIT-000{index}"));

        // The tenant administrator registers the customer's application: an API client bound to the customer with
        // read scopes for units and cover, and a webhook for shipments.
        var (_, token) = await PublicApiTestSupport.CreateTokenAsync(fixture, setup.Tenant, setup.Administrator,
            [PublicApiScopes.UnitsRead, PublicApiScopes.CoverRead], PublicApiDisclosureTier.Customer, party);
        using var administrator = await fixture.ClientAsync(setup.Tenant, setup.Administrator);
        var subscribed = await administrator.PostAsJsonAsync("/api/developer/webhooks", new CreateWebhookSubscription(
            "Customer shipments", Url("/test/webhook/capture"), [PublicEventTypes.UnitShipped], null));
        Assert.Equal(HttpStatusCode.Created, subscribed.StatusCode);
        var subscription = (await subscribed.Content.ReadFromJsonAsync<WebhookSecretResponse>())!;
        using var api = PublicApiTestSupport.Anonymous(fixture);
        var app = new SampleCustomerApplication(subscription.Secret, token.AccessToken, api);

        // 1. A shipment arrives signed; the application verifies it and follows the link to read the unit and cover.
        await ShipAsync(setup.Tenant, first, party);
        await DispatchAsync(setup.Tenant, subscription.Subscription.Id);
        var shipped = Assert.Single(app.Receive(fixture.CapturedWebhooks, fixture.Clock.UtcNow));
        Assert.Equal(PublicEventTypes.UnitShipped, shipped.Type);
        Assert.Equal(first, shipped.RecordId);
        Assert.Equal("SERVER-EXIT-0001", shipped.Reference);
        Assert.False(app.Accepts(Tampered(fixture.CapturedWebhooks.Single()), fixture.Clock.UtcNow));

        using var unit = await app.GetAsync(shipped.Self);
        Assert.Equal(first, unit.RootElement.GetProperty("id").GetGuid());
        using var cover = await app.GetAsync(shipped.Self + "/cover");
        var line = cover.RootElement.GetProperty("cover")[0];
        Assert.True(line.TryGetProperty("status", out _));
        Assert.False(line.TryGetProperty("ownerPartyId", out _));
        Assert.False(line.TryGetProperty("obligorEntityId", out _));
        Assert.Equal(HttpStatusCode.Forbidden, await app.StatusAsync($"/api/public/v1/claims?unit={first:D}"));

        // 2. The application is down for six hours; one shipment an hour is published meanwhile.
        await PointAtAsync(setup.Tenant, subscription.Subscription.Id, "/test/webhook/fail");
        var outageStart = fixture.Clock.UtcNow;
        var missed = new List<Guid>();
        foreach (var unitId in later)
        {
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddHours(1);
            missed.Add(await ShipAsync(setup.Tenant, unitId, party));
            await DispatchAsync(setup.Tenant, subscription.Subscription.Id);
        }
        Assert.Equal(fixture.Clock.UtcNow, outageStart.AddHours(6));
        Assert.Single(fixture.CapturedWebhooks);

        // 3. It comes back: queued deliveries drain in order, and a replay of the outage window is de-duplicated.
        await PointAtAsync(setup.Tenant, subscription.Subscription.Id, "/test/webhook/capture");
        var recovered = new List<ReceivedEvent>();
        for (var round = 0; round < 40 && fixture.CapturedWebhooks.Count < 1 + missed.Count; round++)
        {
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(5);
            await DispatchAsync(setup.Tenant, subscription.Subscription.Id);
            recovered.AddRange(app.Receive(fixture.CapturedWebhooks, fixture.Clock.UtcNow));
        }
        Assert.Equal(missed, recovered.Select(item => item.EventId));
        Assert.Equal(later, recovered.Select(item => item.RecordId));

        var replay = await administrator.PostAsJsonAsync(
            $"/api/developer/webhooks/{subscription.Subscription.Id:D}/deliveries/redeliver-range",
            new RedeliverRangeRequest(outageStart.AddSeconds(1), fixture.Clock.UtcNow.AddSeconds(1)));
        replay.EnsureSuccessStatusCode();
        Assert.Equal(missed.Count, (await replay.Content.ReadFromJsonAsync<RedeliverRangeResponse>())!.Created);
        var duplicates = new List<ReceivedEvent>();
        for (var round = 0; round < 10 && fixture.CapturedWebhooks.Count < 1 + 2 * missed.Count; round++)
        {
            await DispatchAsync(setup.Tenant, subscription.Subscription.Id);
            duplicates.AddRange(app.Receive(fixture.CapturedWebhooks, fixture.Clock.UtcNow));
        }
        Assert.Equal(1 + 2 * missed.Count, fixture.CapturedWebhooks.Count);
        Assert.Empty(duplicates);
        Assert.Equal([shipped.EventId, .. missed], app.Seen);
    }

    private async Task<Guid> ShipAsync(TenantId tenant, Guid unitId, Guid party)
    {
        var at = fixture.Clock.UtcNow;
        var eventId = Guid.CreateVersion7(at);
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<PublicEventStager>().StageAsync(new CustodyChanged(
            new(CustodyChanged.Schema, CustodyChanged.Version, eventId, unitId, at, at, 2, Guid.CreateVersion7(),
                "system:test", null), Guid.CreateVersion7(), "customer", null, party, "customer", "warehouse",
            Guid.CreateVersion7(), "sale", UnitEventType.Shipped), default);
        return (await scope.ServiceProvider.GetRequiredService<DeveloperDbContext>().PublicEvents
            .Where(row => row.RecordId == unitId && row.Type == PublicEventTypes.UnitShipped)
            .Select(row => row.EventId).SingleAsync());
    }

    private async Task DispatchAsync(TenantId tenant, Guid subscriptionId)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<WebhookDeliveryDispatcher>()
            .DispatchSubscriptionAsync(subscriptionId, default);
    }

    private async Task PointAtAsync(TenantId tenant, Guid subscriptionId, string path)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        (await db.Subscriptions.SingleAsync(row => row.SubscriptionId == subscriptionId)).Url = Url(path);
        await db.SaveChangesAsync();
    }

    private string Url(string path) => new Uri(new Uri(fixture.Host.Urls.Single()), path).ToString();

    private static CapturedWebhook Tampered(CapturedWebhook original) =>
        original with { Body = original.Body.Replace("SERVER-EXIT-0001", "SERVER-EXIT-9999", StringComparison.Ordinal) };

    /// <summary>What a customer's receiver does: verify the signature, drop duplicates by event id, call the API.</summary>
    private sealed class SampleCustomerApplication(string secret, string accessToken, HttpClient api)
    {
        private readonly HashSet<string> processed = [];
        private int read;

        public List<Guid> Seen { get; } = [];

        public List<ReceivedEvent> Receive(IEnumerable<CapturedWebhook> inbox, DateTime now)
        {
            var accepted = new List<ReceivedEvent>();
            foreach (var message in inbox.Skip(read))
            {
                read++;
                if (!Accepts(message, now) || !processed.Add(message.EventId)) continue;
                using var json = JsonDocument.Parse(message.Body);
                var root = json.RootElement;
                var record = root.GetProperty("record");
                var received = new ReceivedEvent(root.GetProperty("id").GetGuid(), root.GetProperty("type").GetString()!,
                    record.GetProperty("id").GetGuid(), record.GetProperty("reference").GetString(),
                    root.GetProperty("links").GetProperty("self").GetString()!);
                Seen.Add(received.EventId);
                accepted.Add(received);
            }
            return accepted;
        }

        // The published verification steps: t within five minutes, HMAC-SHA256 of "<t>.<body>" keyed with the
        // secret string, any v1 value may match (rotation), compared in constant time.
        public bool Accepts(CapturedWebhook message, DateTime now)
        {
            var parts = message.Signature.Split(',').Select(part => part.Split('=', 2)).Where(part => part.Length == 2)
                .ToList();
            var t = parts.FirstOrDefault(part => part[0] == "t")?[1];
            if (t is null || !long.TryParse(t, out var seconds)) return false;
            if (Math.Abs(new DateTimeOffset(now).ToUnixTimeSeconds() - seconds) > 300) return false;
            var expected = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(
                HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{message.Body}"))));
            return parts.Where(part => part[0] == "v1").Any(part =>
                CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(part[1])));
        }

        public async Task<JsonDocument> GetAsync(string path)
        {
            using var response = await api.SendAsync(PublicApiTestSupport.Bearer(HttpMethod.Get, path, accessToken));
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }

        public async Task<HttpStatusCode> StatusAsync(string path)
        {
            using var response = await api.SendAsync(PublicApiTestSupport.Bearer(HttpMethod.Get, path, accessToken));
            return response.StatusCode;
        }
    }

    private sealed record ReceivedEvent(Guid EventId, string Type, Guid RecordId, string? Reference, string Self);
}
