using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Yojana.Integration.Connectors.CX02.Testing;

namespace Yojana.Integration.Tests;

public sealed class Cx02SimulatorNotificationTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("POST")]
    [InlineData("GET")]
    public async Task Standard_and_custom_subscriptions_use_configured_handshake_and_echo_client_state(string method)
    {
        await using var receiver = new Cx02NotificationReceiver();
        await using var simulator = new Cx02Simulator(new Cx02TimeProvider(Start), new()
        {
            HandshakeMethod = new(method), HandshakeContentType = "text/plain",
        });
        using var client = await AuthenticatedClientAsync(simulator);

        using var standard = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state-standard");
        using var custom = await SubscribeAsync(client, simulator.CustomApiUri("subscriptions"), receiver.Uri,
            $"/api/{Cx02Simulator.Publisher}/{Cx02Simulator.Group}/{Cx02Simulator.Version}/companies({Cx02Simulator.CompanyId})/trackedItemLedgerEntries",
            "state-custom");

        Assert.Equal(HttpStatusCode.Created, standard.StatusCode);
        Assert.Equal(HttpStatusCode.Created, custom.StatusCode);
        Assert.Equal([method, method], receiver.Methods);
        using var body = JsonDocument.Parse(await custom.Content.ReadAsStringAsync());
        Assert.Equal("state-custom", body.RootElement.GetProperty("clientState").GetString());
        Assert.Equal(Start.AddDays(3), body.RootElement.GetProperty("expirationDateTime").GetDateTimeOffset());
    }

    [Fact]
    public async Task Handshake_media_type_assumption_is_enforced()
    {
        await using var receiver = new Cx02NotificationReceiver { HandshakeContentType = "application/json" };
        await using var simulator = new Cx02Simulator();
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, simulator.ActiveSubscriptionCount);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/json")]
    public async Task Configured_handshake_media_type_is_accepted(string mediaType)
    {
        await using var receiver = new Cx02NotificationReceiver { HandshakeContentType = mediaType };
        await using var simulator = new Cx02Simulator(options: new() { HandshakeContentType = mediaType });
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Configured_handshake_timeout_is_enforced()
    {
        await using var receiver = new Cx02NotificationReceiver { ResponseDelay = TimeSpan.FromMilliseconds(100) };
        await using var simulator = new Cx02Simulator(options: new()
        {
            HandshakeTimeout = TimeSpan.FromMilliseconds(10),
        });
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await SubscribeAsync(client,
            simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Renewal_requires_if_match_repeats_handshake_and_expiry_uses_injected_clock()
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        await using var simulator = new Cx02Simulator(clock, new() { TokenExpiresInSeconds = 10 * 24 * 60 * 60 });
        using var client = await AuthenticatedClientAsync(simulator);
        using var created = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var id = createdBody.RootElement.GetProperty("subscriptionId").GetGuid();
        clock.Advance(TimeSpan.FromDays(2));

        using var missingMatch = await client.PatchAsync(simulator.StandardApiUri($"subscriptions({id})"),
            Json(new { clientState = "renewed" }));
        using var renewal = new HttpRequestMessage(HttpMethod.Patch, simulator.StandardApiUri($"subscriptions({id})"))
        {
            Content = Json(new { clientState = "renewed" }),
        };
        renewal.Headers.TryAddWithoutValidation("If-Match", "W/\"1\"");
        using var renewed = await client.SendAsync(renewal);

        Assert.Equal(HttpStatusCode.BadRequest, missingMatch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
        Assert.Equal(2, receiver.Methods.Count);
        using var renewedBody = JsonDocument.Parse(await renewed.Content.ReadAsStringAsync());
        Assert.Equal(Start.AddDays(5), renewedBody.RootElement.GetProperty("expirationDateTime").GetDateTimeOffset());

        clock.Advance(TimeSpan.FromDays(4));
        simulator.QueueWebhookChange("items", Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(31));
        await simulator.DispatchWebhooksAsync();
        Assert.Empty(receiver.Bodies);
    }

    [Fact]
    public async Task Subscription_cap_and_delete_are_enforced()
    {
        await using var receiver = new Cx02NotificationReceiver();
        await using var simulator = new Cx02Simulator(options: new() { SubscriptionCap = 1 });
        using var client = await AuthenticatedClientAsync(simulator);
        using var first = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "one");
        using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("subscriptionId").GetGuid();

        using var capped = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/customers", "two");
        using var deleted = await client.DeleteAsync(simulator.StandardApiUri($"subscriptions({id})"));

        Assert.Equal(HttpStatusCode.TooManyRequests, capped.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(0, simulator.ActiveSubscriptionCount);
    }

    [Theory]
    [InlineData("lastDateTimeModified")]
    [InlineData("fictionalChangeField")]
    public async Task Coalescing_batches_subscriptions_and_collection_url_uses_configured_filter_verbatim(
        string filterField)
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        await using var simulator = new Cx02Simulator(clock, new()
        {
            CollectionFilterField = filterField,
        });
        using var client = await AuthenticatedClientAsync(simulator);
        using var items = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "items-state");
        using var customers = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/customers", "customers-state");
        foreach (var index in Enumerable.Range(0, 1_001))
            simulator.QueueWebhookChange("items", Guid.CreateVersion7(), index == 0 ? "created" : "updated");
        simulator.QueueWebhookChange("customers", Guid.CreateVersion7(), "deleted");

        await simulator.DispatchWebhooksAsync();
        Assert.Empty(receiver.Bodies);
        clock.Advance(TimeSpan.FromSeconds(30));
        await simulator.DispatchWebhooksAsync();

        var delivered = Assert.Single(receiver.Bodies);
        using var body = JsonDocument.Parse(delivered);
        Assert.Equal(2, body.RootElement.GetProperty("value").GetArrayLength());
        var collection = Assert.Single(body.RootElement.GetProperty("value").EnumerateArray(), value =>
            value.GetProperty("changeType").GetString() == "collection");
        Assert.Contains($"$filter={filterField} gt ", collection.GetProperty("resource").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(body.RootElement.GetProperty("value").EnumerateArray(), value =>
            value.GetProperty("clientState").GetString() == "customers-state");
    }

    [Fact]
    public async Task Duplicate_out_of_order_header_and_lapsed_notification_faults_are_scriptable()
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        await using var simulator = new Cx02Simulator(clock);
        using var client = await AuthenticatedClientAsync(simulator);
        using var subscription = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/salesInvoices", "state");
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        simulator.QueueWebhookChange("salesInvoiceLines", first, "created");
        simulator.QueueWebhookChange("salesInvoiceLines", second, "updated");
        simulator.DuplicateNextNotification = true;
        simulator.ReverseNextNotificationBatch = true;
        clock.Advance(TimeSpan.FromSeconds(31));

        await simulator.DispatchWebhooksAsync();

        using var body = JsonDocument.Parse(Assert.Single(receiver.Bodies));
        var values = body.RootElement.GetProperty("value");
        Assert.Equal(3, values.GetArrayLength());
        Assert.Contains(second.ToString(), values[0].GetProperty("resource").GetString(), StringComparison.Ordinal);
        Assert.Equal(values[0].GetRawText(), values[2].GetRawText());
        Assert.Contains("salesInvoices", values[0].GetProperty("resource").GetString(), StringComparison.Ordinal);

        simulator.LapseSubscriptions();
        simulator.QueueWebhookChange("salesInvoices", Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(31));
        await simulator.DispatchWebhooksAsync();
        Assert.Single(receiver.Bodies);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Application_identity_notification_assumption_is_switchable(bool enabled, int expected)
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        await using var simulator = new Cx02Simulator(clock,
            new() { NotifyChangesByApplicationIdentity = enabled });
        using var client = await AuthenticatedClientAsync(simulator);
        using var subscription = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");
        simulator.QueueWebhookChange("items", Guid.CreateVersion7(), applicationIdentity: true);
        clock.Advance(TimeSpan.FromSeconds(31));

        await simulator.DispatchWebhooksAsync();

        Assert.Equal(expected, receiver.Bodies.Count);
    }

    [Fact]
    public async Task Explicit_unlicensed_actor_fault_suppresses_one_notification()
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        await using var simulator = new Cx02Simulator(clock);
        using var client = await AuthenticatedClientAsync(simulator);
        using var subscription = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");
        simulator.SuppressNextNotificationAsUnlicensedActor = true;
        simulator.QueueWebhookChange("items", Guid.CreateVersion7());
        simulator.QueueWebhookChange("items", Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(31));

        await simulator.DispatchWebhooksAsync();

        using var body = JsonDocument.Parse(Assert.Single(receiver.Bodies));
        Assert.Single(body.RootElement.GetProperty("value").EnumerateArray());
    }

    [Fact]
    public async Task Retryable_reply_retries_but_non_retryable_reply_deletes_subscription()
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        receiver.ReplyWith(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK, HttpStatusCode.BadRequest);
        await using var simulator = new Cx02Simulator(clock, new()
        {
            WebhookRetrySchedule = [TimeSpan.FromMinutes(1)],
        });
        using var client = await AuthenticatedClientAsync(simulator);
        using var subscription = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");
        simulator.QueueWebhookChange("items", Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(31));
        await simulator.DispatchWebhooksAsync();

        Assert.Equal(2, simulator.DeliveredNotifications.Count);
        Assert.Equal(1, simulator.ActiveSubscriptionCount);

        simulator.QueueWebhookChange("items", Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(31));
        await simulator.DispatchWebhooksAsync();
        Assert.Equal(0, simulator.ActiveSubscriptionCount);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Webhook_retry_schedule_is_configurable(bool oneRetry, int attempts)
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        receiver.ReplyWith(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        await using var simulator = new Cx02Simulator(clock, new()
        {
            WebhookRetrySchedule = oneRetry ? [TimeSpan.FromMinutes(1)] : [],
        });
        using var client = await AuthenticatedClientAsync(simulator);
        using var subscription = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");
        simulator.QueueWebhookChange("items", Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(31));

        await simulator.DispatchWebhooksAsync();

        Assert.Equal(attempts, simulator.DeliveredNotifications.Count);
        if (oneRetry)
            Assert.Equal(TimeSpan.FromMinutes(1), simulator.DeliveredNotifications[1].SentAt
                - simulator.DeliveredNotifications[0].SentAt);
    }

    [Fact]
    public async Task Webhook_retries_never_cross_the_36_hour_window()
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        receiver.ReplyWith(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.OK);
        await using var simulator = new Cx02Simulator(clock, new()
        {
            WebhookRetrySchedule = [TimeSpan.FromHours(35), TimeSpan.FromHours(2)],
        });
        using var client = await AuthenticatedClientAsync(simulator);
        using var subscription = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");
        simulator.QueueWebhookChange("items", Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(31));

        await simulator.DispatchWebhooksAsync();

        Assert.Equal(2, simulator.DeliveredNotifications.Count);
        Assert.Equal(TimeSpan.FromHours(35), simulator.DeliveredNotifications[1].SentAt
            - simulator.DeliveredNotifications[0].SentAt);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Accepted_webhook_success_codes_are_configurable(bool acceptNoContent, int remaining)
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        receiver.ReplyWith(HttpStatusCode.NoContent);
        var statuses = acceptNoContent
            ? new HashSet<HttpStatusCode> { HttpStatusCode.OK, HttpStatusCode.NoContent }
            : new HashSet<HttpStatusCode> { HttpStatusCode.OK };
        await using var simulator = new Cx02Simulator(clock, new() { DeliveredWebhookStatuses = statuses });
        using var client = await AuthenticatedClientAsync(simulator);
        using var subscription = await SubscribeAsync(client, simulator.StandardApiUri("subscriptions"), receiver.Uri,
            $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items", "state");
        simulator.QueueWebhookChange("items", Guid.CreateVersion7());
        clock.Advance(TimeSpan.FromSeconds(31));

        await simulator.DispatchWebhooksAsync();

        Assert.Equal(remaining, simulator.ActiveSubscriptionCount);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    public async Task Business_events_are_commit_only_and_retry_is_configurable(bool retry, int attempts)
    {
        await using var receiver = new Cx02NotificationReceiver();
        receiver.ReplyWith(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        await using var simulator = new Cx02Simulator(options: new() { RetryBusinessEvents = retry });
        using var client = await AuthenticatedClientAsync(simulator);
        using var catalogue = await client.GetAsync(simulator.RuntimeApiUri("externalbusinesseventdefinitions"));
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);
        using var catalogueBody = JsonDocument.Parse(await catalogue.Content.ReadAsStringAsync());
        Assert.Equal(3, catalogueBody.RootElement.GetProperty("value").GetArrayLength());
        using var subscribed = await SubscribeBusinessEventAsync(client, simulator, receiver.Uri);
        Assert.Equal(HttpStatusCode.Created, subscribed.StatusCode);

        await simulator.PublishBusinessEventAsync("salesShipmentPosted", new { documentId = Guid.CreateVersion7() },
            committed: false);
        await simulator.PublishBusinessEventAsync("salesShipmentPosted", new { documentId = Guid.CreateVersion7() },
            committed: true);

        Assert.Equal(attempts, simulator.DeliveredNotifications.Count);
        Assert.All(simulator.DeliveredNotifications, delivered => Assert.Equal("business-event", delivered.Channel));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Business_event_handshake_assumption_is_switchable(bool required, bool receiverAccepts)
    {
        await using var receiver = new Cx02NotificationReceiver { HandshakeSucceeds = receiverAccepts };
        await using var simulator = new Cx02Simulator(options: new() { BusinessEventRequiresHandshake = required });
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await SubscribeBusinessEventAsync(client, simulator, receiver.Uri);

        Assert.Equal(required ? HttpStatusCode.BadRequest : HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.Created)]
    [InlineData(false, HttpStatusCode.Forbidden)]
    public async Task Application_business_event_subscription_assumption_is_switchable(bool allowed,
        HttpStatusCode expected)
    {
        await using var receiver = new Cx02NotificationReceiver();
        await using var simulator = new Cx02Simulator(options: new()
        {
            AllowApplicationBusinessEventSubscription = allowed,
        });
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await SubscribeBusinessEventAsync(client, simulator, receiver.Uri);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task Configured_business_event_expiry_suppresses_delivery()
    {
        var clock = new Cx02TimeProvider(Start);
        await using var receiver = new Cx02NotificationReceiver();
        await using var simulator = new Cx02Simulator(clock, new()
        {
            BusinessEventSubscriptionLifetime = TimeSpan.FromHours(1),
        });
        using var client = await AuthenticatedClientAsync(simulator);
        using var subscription = await SubscribeBusinessEventAsync(client, simulator, receiver.Uri);
        clock.Advance(TimeSpan.FromHours(2));

        await simulator.PublishBusinessEventAsync("salesShipmentPosted", new { documentId = Guid.CreateVersion7() },
            committed: true);

        Assert.Empty(receiver.Bodies);
    }

    [Theory]
    [InlineData(Cx02AttachmentUploadPattern.InlineContent)]
    [InlineData(Cx02AttachmentUploadPattern.SeparateContent)]
    public async Task Both_configurable_attachment_upload_patterns_accept_stream_content(
        Cx02AttachmentUploadPattern pattern)
    {
        await using var simulator = new Cx02Simulator(options: new() { AttachmentUploadPattern = pattern });
        using var client = await AuthenticatedClientAsync(simulator);
        HttpResponseMessage created;
        if (pattern == Cx02AttachmentUploadPattern.InlineContent)
        {
            created = await client.PostAsync(CompanyUri(simulator, "attachments"), Json(new
            {
                parentId = Guid.CreateVersion7(), fileName = "certificate.pdf", parentType = "Sales Credit Memo",
                attachmentContent = Convert.ToBase64String("fictional certificate"u8),
            }));
        }
        else
        {
            created = await client.PostAsync(CompanyUri(simulator, "attachments"), Json(new
            {
                parentId = Guid.CreateVersion7(), fileName = "certificate.pdf", parentType = "Sales Credit Memo",
            }));
            using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            var id = body.RootElement.GetProperty("id").GetGuid();
            using var content = new ByteArrayContent("fictional certificate"u8.ToArray());
            content.Headers.ContentType = new("application/octet-stream");
            using var uploaded = await client.PatchAsync(CompanyUri(simulator,
                $"attachments({id})/attachmentContent"), content);
            Assert.Equal(HttpStatusCode.NoContent, uploaded.StatusCode);
        }
        using (created) Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Fact]
    public async Task Document_attachment_parent_values_are_configurable()
    {
        await using var defaultSimulator = new Cx02Simulator();
        using var defaultClient = await AuthenticatedClientAsync(defaultSimulator);
        using var accepted = await defaultClient.PostAsync(CompanyUri(defaultSimulator, "documentAttachments"),
            Json(new { parentId = Guid.CreateVersion7(), fileName = "evidence.pdf", parentType = "Item" }));
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        await using var configuredSimulator = new Cx02Simulator(options: new()
        {
            DocumentAttachmentParentTypes = new HashSet<string>(StringComparer.Ordinal) { "Fictional Asset" },
        });
        using var configuredClient = await AuthenticatedClientAsync(configuredSimulator);
        using var rejected = await configuredClient.PostAsync(CompanyUri(configuredSimulator, "documentAttachments"),
            Json(new { parentId = Guid.CreateVersion7(), fileName = "evidence.pdf", parentType = "Item" }));
        using var custom = await configuredClient.PostAsync(CompanyUri(configuredSimulator, "documentAttachments"),
            Json(new { parentId = Guid.CreateVersion7(), fileName = "evidence.pdf", parentType = "Fictional Asset" }));

        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Created, custom.StatusCode);
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(Cx02Simulator simulator)
    {
        var client = new HttpClient();
        using var token = await client.PostAsync(simulator.TokenUri(), new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = "fictional-client",
                ["client_secret"] = "fictional-secret", ["scope"] = Cx02SimulatorOptions.RequiredScope,
            }));
        token.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await token.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            body.RootElement.GetProperty("access_token").GetString());
        return client;
    }

    private static Task<HttpResponseMessage> SubscribeAsync(HttpClient client, Uri endpoint, Uri receiver,
        string resource, string clientState) => client.PostAsync(endpoint,
            Json(new { notificationUrl = receiver, resource, clientState }));

    private static Task<HttpResponseMessage> SubscribeBusinessEventAsync(HttpClient client, Cx02Simulator simulator,
        Uri receiver) => client.PostAsync(simulator.RuntimeApiUri("externaleventsubscriptions"), Json(new
        {
            companyName = "Fictional Hardware Services", companyId = Cx02Simulator.CompanyId,
            eventName = "salesShipmentPosted", appId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            notificationUrl = receiver, clientState = "event-state",
        }));

    private static Uri CompanyUri(Cx02Simulator simulator, string resource) =>
        simulator.StandardApiUri($"companies({Cx02Simulator.CompanyId})/{resource}");

    private static StringContent Json(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");
}
