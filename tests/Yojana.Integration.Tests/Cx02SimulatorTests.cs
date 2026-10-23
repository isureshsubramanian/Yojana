using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Yojana.Integration.Connectors.CX02.Testing;

namespace Yojana.Integration.Tests;

public sealed class Cx02SimulatorTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_match_the_recorded_protocol_profile()
    {
        var options = new Cx02SimulatorOptions();

        Assert.Equal(3_599, options.TokenExpiresInSeconds);
        Assert.Equal(20_000, options.PageSizeCap);
        Assert.Equal(6_000, options.RequestLimit);
        Assert.Equal(TimeSpan.FromSeconds(300), options.RequestWindow);
        Assert.Equal(5, options.ConcurrentRequestLimit);
        Assert.Equal(95, options.QueuedRequestLimit);
        Assert.Equal(TimeSpan.FromMinutes(8), options.QueueWaitTimeout);
        Assert.Equal(200, options.SubscriptionCap);
        Assert.Equal(TimeSpan.FromDays(3), options.SubscriptionLifetime);
        Assert.Equal(TimeSpan.FromSeconds(30), options.WebhookCoalescingWindow);
        Assert.Equal(1_000, options.CollectionNotificationThreshold);
        Assert.Equal(TimeSpan.FromHours(36), options.WebhookRetryWindow);
        Assert.False(options.RetryBusinessEvents);
    }

    [Fact]
    public async Task Token_endpoint_issues_configured_opaque_token_and_rejects_bad_secret_or_scope()
    {
        await using var simulator = new Cx02Simulator(options: new() { TokenExpiresInSeconds = 1234 });
        using var client = new HttpClient();

        using var success = await TokenResponseAsync(client, simulator);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        using var issued = JsonDocument.Parse(await success.Content.ReadAsStringAsync());
        Assert.Equal("Bearer", issued.RootElement.GetProperty("token_type").GetString());
        Assert.Equal(1234, issued.RootElement.GetProperty("expires_in").GetInt32());
        Assert.StartsWith("cx02-token-", issued.RootElement.GetProperty("access_token").GetString(), StringComparison.Ordinal);
        Assert.False(issued.RootElement.TryGetProperty("refresh_token", out _));

        using var badSecret = await TokenResponseAsync(client, simulator, secret: "wrong");
        Assert.Equal(HttpStatusCode.BadRequest, badSecret.StatusCode);
        using var badSecretBody = JsonDocument.Parse(await badSecret.Content.ReadAsStringAsync());
        Assert.Equal("invalid_client", badSecretBody.RootElement.GetProperty("error").GetString());
        Assert.True(badSecretBody.RootElement.TryGetProperty("error_codes", out _));

        using var badScope = await TokenResponseAsync(client, simulator, scope: "wrong/.default");
        Assert.Equal(HttpStatusCode.BadRequest, badScope.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Expired_token_status_is_configurable(HttpStatusCode status)
    {
        await using var simulator = new Cx02Simulator(options: new() { ExpiredTokenStatus = status });
        using var client = await AuthenticatedClientAsync(simulator);
        simulator.ExpireCurrentTokens();

        using var response = await client.GetAsync(simulator.StandardApiUri("companies"));

        Assert.Equal(status, response.StatusCode);
        Assert.Contains("Authentication_InvalidCredentials", await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Company_and_environment_routes_accept_both_documented_forms()
    {
        await using var simulator = new Cx02Simulator();
        using var client = await AuthenticatedClientAsync(simulator);

        using var domain = await client.GetAsync(simulator.StandardApiUri("companies"));
        using var environmentOnly = await client.GetAsync(simulator.StandardApiUri("companies", includeTenant: false));
        using var guid = await client.GetAsync(simulator.StandardApiUri("companies",
            tenant: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        Assert.Equal(HttpStatusCode.OK, domain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, environmentOnly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, guid.StatusCode);
        using var body = JsonDocument.Parse(await domain.Content.ReadAsStringAsync());
        Assert.Equal(Cx02Simulator.CompanyId, body.RootElement.GetProperty("value")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Tenant_guid_assumption_can_be_disabled()
    {
        await using var simulator = new Cx02Simulator(options: new() { AcceptTenantGuid = false });
        using var client = await AuthenticatedClientAsync(simulator);

        using var domain = await client.GetAsync(simulator.StandardApiUri("companies"));
        using var guid = await client.GetAsync(simulator.StandardApiUri("companies",
            tenant: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        Assert.Equal(HttpStatusCode.OK, domain.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, guid.StatusCode);
    }

    [Fact]
    public async Task Standard_resources_are_present_and_posted_resources_are_read_only_without_tracking()
    {
        await using var simulator = new Cx02Simulator();
        using var client = await AuthenticatedClientAsync(simulator);
        var resources = new[]
        {
            "items", "customers", "salesInvoices", "salesCreditMemos", "salesShipments", "salesShipmentLines",
            "purchaseReceipts", "purchaseReceiptLines", "itemLedgerEntries",
        };

        foreach (var resource in resources)
        {
            using var response = await client.GetAsync(CompanyUri(simulator, resource));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.NotEmpty(body.RootElement.GetProperty("value").EnumerateArray());
        }

        using var ledgerResponse = await client.GetAsync(CompanyUri(simulator, "itemLedgerEntries"));
        using var ledger = JsonDocument.Parse(await ledgerResponse.Content.ReadAsStringAsync());
        var entry = ledger.RootElement.GetProperty("value")[0];
        Assert.False(entry.TryGetProperty("serialNumber", out _));
        Assert.False(entry.TryGetProperty("lotNumber", out _));
        Assert.False(entry.TryGetProperty("packageNumber", out _));

        using var rejectedWrite = await client.PostAsync(CompanyUri(simulator, "salesShipments"),
            JsonContent(new { number = "SHIP-200" }));
        Assert.Equal(HttpStatusCode.MethodNotAllowed, rejectedWrite.StatusCode);
        var memoId = await FirstIdAsync(client, CompanyUri(simulator, "salesCreditMemos"));
        using var posted = await client.PostAsync(CompanyUri(simulator,
            $"salesCreditMemos({memoId})/Microsoft.NAV.post"), JsonContent(new { }));
        Assert.Equal(HttpStatusCode.NoContent, posted.StatusCode);
    }

    [Fact]
    public async Task Every_custom_page_and_tracked_posting_action_are_reachable()
    {
        await using var simulator = new Cx02Simulator();
        using var client = await AuthenticatedClientAsync(simulator);
        var pages = new[]
        {
            "trackedItemLedgerEntries", "itemEntryRelations", "reservationEntries", "trackingSpecifications",
            "serviceItems", "postedTransferShipments", "postedTransferShipmentLines", "postedTransferReceipts",
            "postedTransferReceiptLines", "postedReturnReceipts", "postedReturnReceiptLines",
            "trackedDocumentRequests",
        };

        foreach (var page in pages)
        {
            using var response = await client.GetAsync(CompanyUri(simulator, page, custom: true));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var documentId = Guid.CreateVersion7();
        using var action = await client.PostAsync(CompanyUri(simulator,
            $"trackedDocumentRequests({documentId})/Microsoft.NAV.postTrackedDocument", custom: true),
            JsonContent(new { documentType = "salesShipment", documentId, logicalReference = "FIC-SHIP-200",
                metadata = new { customerNumber = "FIC-CUSTOMER-200", shipToCode = "FIC-SHIP" },
                tracking = new[] { new { sourceLineNumber = 10, itemNumber = "HW-SERVER-200",
                    quantity = 1m, serialNumber = "FIC-SERIAL-200", lotNumber = "", packageNumber = "" } } }));
        Assert.Equal(HttpStatusCode.Created, action.StatusCode);
        using var result = JsonDocument.Parse(await action.Content.ReadAsStringAsync());
        Assert.Equal("posted", result.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Bound_action_parameter_assumption_can_be_disabled()
    {
        await using var simulator = new Cx02Simulator(options: new() { BoundActionAcceptsParameters = false });
        using var client = await AuthenticatedClientAsync(simulator);

        using var accepted = await client.PostAsync(CompanyUri(simulator,
            $"trackedDocumentRequests({Guid.CreateVersion7()})/Microsoft.NAV.postTrackedDocument", custom: true),
            JsonContent(new { }));
        using var rejected = await client.PostAsync(CompanyUri(simulator,
            $"trackedDocumentRequests({Guid.CreateVersion7()})/Microsoft.NAV.postTrackedDocument", custom: true),
            JsonContent(new { documentType = "salesShipment" }));

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task Tracked_request_exposes_staged_created_and_failed_states_with_codes()
    {
        await using var simulator = new Cx02Simulator();
        using var client = await AuthenticatedClientAsync(simulator);
        var documentId = Guid.CreateVersion7();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        simulator.BeforeNextDocumentCreate = async () =>
        {
            entered.SetResult();
            await release.Task;
        };
        var post = client.PostAsync(CompanyUri(simulator,
            $"trackedDocumentRequests({documentId})/Microsoft.NAV.postTrackedDocument", custom: true),
            JsonContent(new { documentType = "serviceOrder", documentId, logicalReference = "FIC-SERVICE-STATE",
                metadata = new { customerNumber = "FIC-CUSTOMER", itemNumber = "HW-SERVER-100",
                    serialNumber = "FIC-SERIAL-STATE", description = "Replace storage controller" },
                tracking = new[] { new { sourceLineNumber = 10, itemNumber = "HW-SERVER-100", quantity = 1m,
                    serialNumber = "FIC-SERIAL-STATE", lotNumber = "", packageNumber = "" } } }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using (var stagedReply = await client.GetAsync(CompanyUri(simulator,
                   $"trackedDocumentRequests({documentId})", custom: true)))
        using (var staged = JsonDocument.Parse(await stagedReply.Content.ReadAsStringAsync()))
            Assert.Equal("staged", staged.RootElement.GetProperty("status").GetString());
        release.SetResult();
        using (var completedReply = await post)
        using (var completed = JsonDocument.Parse(await completedReply.Content.ReadAsStringAsync()))
            Assert.Equal("created", completed.RootElement.GetProperty("status").GetString());

        var failedId = Guid.CreateVersion7();
        simulator.RejectNextPostAsClosedPeriod = true;
        using var rejected = await client.PostAsync(CompanyUri(simulator,
            $"trackedDocumentRequests({failedId})/Microsoft.NAV.postTrackedDocument", custom: true),
            JsonContent(new { documentType = "itemJournal", documentId = failedId,
                logicalReference = "FIC-FAILED-STATE", metadata = new { locationCode = "WH-A", reasonCode = "inspection" },
                tracking = new[] { new { sourceLineNumber = 10, itemNumber = "HW-SERVER-100", quantity = 1m,
                    serialNumber = "FIC-SERIAL-FAILED", lotNumber = "", packageNumber = "" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var failedReply = await client.GetAsync(CompanyUri(simulator,
            $"trackedDocumentRequests({failedId})", custom: true));
        using var failed = JsonDocument.Parse(await failedReply.Content.ReadAsStringAsync());
        Assert.Equal("failed", failed.RootElement.GetProperty("status").GetString());
        Assert.Equal("postingPeriodClosed", failed.RootElement.GetProperty("failureCode").GetString());
    }

    [Fact]
    public async Task Reservation_page_rejects_direct_creates()
    {
        await using var simulator = new Cx02Simulator();
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await client.PostAsync(CompanyUri(simulator, "reservationEntries", custom: true),
            JsonContent(new { id = Guid.CreateVersion7(), itemNumber = "HW-SERVER-100" }));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Paging_filter_and_next_link_follow_configured_contract(bool includeNextLink)
    {
        var clock = new Cx02TimeProvider(Start);
        await using var simulator = new Cx02Simulator(clock, new() { IncludeNextLink = includeNextLink });
        simulator.Seed("items",
            Cx02SimulatorEntity.From(new { id = Guid.CreateVersion7(), number = "HW-PC-100", lastModifiedDateTime = "2026-08-31T08:00:00Z" }),
            Cx02SimulatorEntity.From(new { id = Guid.CreateVersion7(), number = "HW-PC-101", lastModifiedDateTime = "2026-09-01T09:00:00Z" }),
            Cx02SimulatorEntity.From(new { id = Guid.CreateVersion7(), number = "HW-PC-102", lastModifiedDateTime = "2026-09-01T10:00:00Z" }));
        using var client = await AuthenticatedClientAsync(simulator);
        using var request = new HttpRequestMessage(HttpMethod.Get, CompanyUri(simulator,
            "items?$filter=lastModifiedDateTime%20gt%202026-09-01T08:30:00Z&$top=2"));
        request.Headers.TryAddWithoutValidation("Prefer", "odata.maxpagesize=1");

        using var response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Single(body.RootElement.GetProperty("value").EnumerateArray());
        Assert.Equal(includeNextLink, body.RootElement.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task Page_cap_etags_patch_and_batch_limits_are_enforced()
    {
        await using var simulator = new Cx02Simulator(options: new() { PageSizeCap = 2 });
        using var client = await AuthenticatedClientAsync(simulator);
        using var oversizedRequest = new HttpRequestMessage(HttpMethod.Get, CompanyUri(simulator, "items"));
        oversizedRequest.Headers.TryAddWithoutValidation("Prefer", "odata.maxpagesize=3");
        using var oversized = await client.SendAsync(oversizedRequest);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversized.StatusCode);

        var itemId = await FirstIdAsync(client, CompanyUri(simulator, "items"));
        using var missingMatch = await client.PatchAsync(CompanyUri(simulator, $"items({itemId})"),
            JsonContent(new { displayName = "Fictional workstation" }));
        Assert.Equal(HttpStatusCode.BadRequest, missingMatch.StatusCode);

        using var get = await client.GetAsync(CompanyUri(simulator, $"items({itemId})"));
        using var item = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        var etag = item.RootElement.GetProperty("@odata.etag").GetString();
        using var patch = new HttpRequestMessage(HttpMethod.Patch, CompanyUri(simulator, $"items({itemId})"))
        {
            Content = JsonContent(new { displayName = "Fictional workstation" }),
        };
        patch.Headers.TryAddWithoutValidation("If-Match", etag);
        using var updated = await client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var batch = await client.PostAsync(simulator.StandardApiUri("$batch"), JsonContent(new
        {
            requests = Enumerable.Range(0, 101).Select(index => new { id = index }).ToArray(),
        }));
        using var maximumBatch = await client.PostAsync(simulator.StandardApiUri("$batch"), JsonContent(new
        {
            requests = Enumerable.Range(0, 100).Select(index => new { id = index }).ToArray(),
        }));
        Assert.Equal(HttpStatusCode.BadRequest, batch.StatusCode);
        Assert.Equal(HttpStatusCode.OK, maximumBatch.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task Entity_changed_status_is_configurable(HttpStatusCode expected)
    {
        await using var simulator = new Cx02Simulator(options: new() { EntityChangedStatus = expected });
        using var client = await AuthenticatedClientAsync(simulator);
        var itemId = await FirstIdAsync(client, CompanyUri(simulator, "items"));
        using var patch = new HttpRequestMessage(HttpMethod.Patch, CompanyUri(simulator, $"items({itemId})"))
        {
            Content = JsonContent(new { displayName = "Fictional workstation" }),
        };
        patch.Headers.TryAddWithoutValidation("If-Match", "W/\"stale\"");

        using var response = await client.SendAsync(patch);

        Assert.Equal(expected, response.StatusCode);
        Assert.Contains("Request_EntityChanged", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "error")]
    [InlineData(false, "code")]
    public async Task Error_envelope_assumption_is_switchable(bool envelope, string rootProperty)
    {
        await using var simulator = new Cx02Simulator(options: new() { UseODataErrorEnvelope = envelope });
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await client.GetAsync(simulator.StandardApiUri("missing"));
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(body.RootElement.TryGetProperty(rootProperty, out _));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Throttle_is_per_principal_and_retry_after_is_switchable(bool retryAfter)
    {
        await using var simulator = new Cx02Simulator(options: new()
        {
            RequestLimit = 1, RequestWindow = TimeSpan.FromMinutes(5), IncludeRetryAfter = retryAfter,
        });
        using var first = await AuthenticatedClientAsync(simulator, clientId: "principal-a");
        using var second = await AuthenticatedClientAsync(simulator, clientId: "principal-b");

        using var accepted = await first.GetAsync(simulator.StandardApiUri("companies"));
        using var throttled = await first.GetAsync(simulator.StandardApiUri("companies"));
        using var otherPrincipal = await second.GetAsync(simulator.StandardApiUri("companies"));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal(retryAfter, throttled.Headers.Contains("Retry-After"));
        Assert.Contains("Application_TooManyRequests", await throttled.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, otherPrincipal.StatusCode);
    }

    [Fact]
    public async Task Scripted_throttle_storm_and_both_timeout_faults_are_one_shot()
    {
        await using var simulator = new Cx02Simulator(options: new() { ThrottleErrorCode = "fictional_throttle" });
        using var client = await AuthenticatedClientAsync(simulator);
        simulator.StartThrottleStorm(2);

        using var first = await client.GetAsync(simulator.StandardApiUri("companies"));
        using var second = await client.GetAsync(simulator.StandardApiUri("companies"));
        using var recovered = await client.GetAsync(simulator.StandardApiUri("companies"));
        simulator.TimeoutNextRequestWith408();
        using var timedOut = await client.GetAsync(simulator.StandardApiUri("companies"));
        simulator.TimeoutNextRequestWith504();
        using var gatewayTimedOut = await client.GetAsync(simulator.StandardApiUri("companies"));

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Contains("fictional_throttle", await first.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        Assert.Equal(HttpStatusCode.RequestTimeout, timedOut.StatusCode);
        Assert.Equal(HttpStatusCode.GatewayTimeout, gatewayTimedOut.StatusCode);
    }

    [Fact]
    public async Task Concurrent_limit_queues_then_returns_503_and_queue_overflow_returns_429()
    {
        await using var simulator = new Cx02Simulator(options: new()
        {
            ConcurrentRequestLimit = 1, QueuedRequestLimit = 1, QueueWaitTimeout = TimeSpan.FromMilliseconds(500),
        });
        using var client = await AuthenticatedClientAsync(simulator);
        simulator.RequestGate.Pause();
        var processing = client.GetAsync(simulator.StandardApiUri("companies"));
        await WaitForRequestCountAsync(simulator, 2);
        var queued = client.GetAsync(simulator.StandardApiUri("companies"));
        await WaitForRequestCountAsync(simulator, 3);

        using var overflow = await client.GetAsync(simulator.StandardApiUri("companies"));
        using var queuedReply = await queued;
        simulator.RequestGate.Resume();
        using var processed = await processing;

        Assert.Equal(HttpStatusCode.TooManyRequests, overflow.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, queuedReply.StatusCode);
        Assert.Equal(HttpStatusCode.OK, processed.StatusCode);
    }

    [Fact]
    public async Task Dropped_action_reply_leaves_the_tracked_request_queryable_for_idempotent_retry()
    {
        await using var simulator = new Cx02Simulator();
        using var client = await AuthenticatedClientAsync(simulator);
        simulator.DropNextPostReply = true;
        var id = Guid.CreateVersion7();

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.PostAsync(
            CompanyUri(simulator, $"trackedDocumentRequests({id})/Microsoft.NAV.postTrackedDocument", custom: true),
            JsonContent(new { documentType = "itemJournal", documentId = id,
                logicalReference = "FIC-ADJUST-100", metadata = new { locationCode = "WH-A", reasonCode = "inspection" },
                tracking = new[] { new { sourceLineNumber = 10, itemNumber = "HW-STORAGE-100",
                    quantity = 1m, serialNumber = "FIC-SERIAL-100", lotNumber = "", packageNumber = "" } } })));
        using var read = await client.GetAsync(CompanyUri(simulator, $"trackedDocumentRequests({id})", custom: true));

        Assert.Equal(1, simulator.PostedDocumentCount);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(Cx02Simulator simulator,
        string clientId = "fictional-client")
    {
        var client = new HttpClient();
        using var response = await TokenResponseAsync(client, simulator, clientId);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            body.RootElement.GetProperty("access_token").GetString());
        return client;
    }

    private static Task<HttpResponseMessage> TokenResponseAsync(HttpClient client, Cx02Simulator simulator,
        string clientId = "fictional-client", string secret = "fictional-secret",
        string scope = Cx02SimulatorOptions.RequiredScope) => client.PostAsync(simulator.TokenUri(),
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = clientId,
                ["client_secret"] = secret, ["scope"] = scope,
            }));

    private static Uri CompanyUri(Cx02Simulator simulator, string resource, bool custom = false)
    {
        var suffix = $"companies({Cx02Simulator.CompanyId})/{resource}";
        return custom ? simulator.CustomApiUri(suffix) : simulator.StandardApiUri(suffix);
    }

    private static StringContent JsonContent(object value) =>
        new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<Guid> FirstIdAsync(HttpClient client, Uri collection)
    {
        using var response = await client.GetAsync(collection);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("value")[0].GetProperty("id").GetGuid();
    }

    private static async Task WaitForRequestCountAsync(Cx02Simulator simulator, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (simulator.Requests.Count < count) await Task.Delay(10, timeout.Token);
    }
}

internal sealed class Cx02TimeProvider(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset current = now;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan duration) => current += duration;
}

internal sealed class Cx02NotificationReceiver : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly ConcurrentQueue<HttpStatusCode> notificationReplies = new();

    public Cx02NotificationReceiver()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        Uri = new($"http://127.0.0.1:{port}/notifications/");
        listener.Prefixes.Add(Uri.ToString());
        listener.Start();
        loop = RunAsync();
    }

    public Uri Uri { get; }
    public bool HandshakeSucceeds { get; set; } = true;
    public string HandshakeContentType { get; set; } = "text/plain";
    public TimeSpan ResponseDelay { get; set; }
    public List<string> Methods { get; } = [];
    public List<string> Bodies { get; } = [];
    public void ReplyWith(params HttpStatusCode[] replies)
    {
        foreach (var reply in replies) notificationReplies.Enqueue(reply);
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await loop; }
        catch (HttpListenerException) { }
        listener.Close();
        stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            _ = Task.Run(() => HandleAsync(context), stop.Token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        lock (Methods) Methods.Add(request.HttpMethod);
        if (ResponseDelay > TimeSpan.Zero) await Task.Delay(ResponseDelay, stop.Token);
        var validation = Query(request.Url, "validationToken");
        if (validation is not null)
        {
            context.Response.StatusCode = HandshakeSucceeds ? 200 : 400;
            context.Response.ContentType = HandshakeContentType;
            var bytes = Encoding.UTF8.GetBytes(HandshakeSucceeds ? validation : "rejected");
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
            return;
        }
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var body = await reader.ReadToEndAsync();
        lock (Bodies) Bodies.Add(body);
        context.Response.StatusCode = notificationReplies.TryDequeue(out var reply) ? (int)reply : 200;
        context.Response.Close();
    }

    private static string? Query(Uri? uri, string name)
    {
        if (uri is null) return null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == name)
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
        }
        return null;
    }
}
