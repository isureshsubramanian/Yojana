using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yojana.Integration.Connectors.CX04.Testing;

public sealed class Cx04SimulatorTests
{
    [Fact]
    public async Task S1_discovers_the_build_and_pinned_endpoints()
    {
        await using var simulator = new Cx04Simulator();
        using var client = Client(simulator);

        using var response = await client.GetAsync("entity");
        using var body = await JsonAsync(response);

        Assert.Equal("26.210.0042-fictional", body.RootElement.GetProperty("version")
            .GetProperty("acumaticaBuildVersion").GetString());
        Assert.Contains(body.RootElement.GetProperty("endpoints").EnumerateArray(), value =>
            value.GetProperty("name").GetString() == "Default"
            && value.GetProperty("version").GetString() == Cx04Simulator.DefaultEndpointVersion);
    }

    [Fact]
    public async Task S2_password_grant_rotates_refresh_tokens_and_rejects_the_old_token()
    {
        await using var simulator = new Cx04Simulator();
        using var client = Client(simulator);
        var issued = await PasswordGrantAsync(client);

        using var refreshed = await PostFormAsync(client, "identity/connect/token", new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = issued.RefreshToken!,
        });
        using var refreshedBody = await JsonAsync(refreshed);
        var replacement = refreshedBody.RootElement.GetProperty("refresh_token").GetString();
        using var reused = await PostFormAsync(client, "identity/connect/token", new()
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = issued.RefreshToken!,
        });

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.NotEqual(issued.RefreshToken, replacement);
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        using var error = await JsonAsync(reused);
        Assert.Equal("invalid_grant", error.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task S2_authorisation_code_is_single_use_and_client_credentials_are_unsupported()
    {
        await using var simulator = new Cx04Simulator();
        using var client = Client(simulator);
        const string callback = "https://fictional.example/callback";
        using var authorised = await client.GetAsync("identity/connect/authorize?response_type=code&client_id=client-1"
            + "&scope=api%20offline_access&redirect_uri=" + Uri.EscapeDataString(callback) + "&state=s1");
        var query = System.Web.HttpUtility.ParseQueryString(authorised.Headers.Location!.Query);
        var code = query["code"]!;

        using var token = await PostFormAsync(client, "identity/connect/token", new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = "client-1",
            ["redirect_uri"] = callback, ["code"] = code,
        });
        using var reused = await PostFormAsync(client, "identity/connect/token", new()
        {
            ["grant_type"] = "authorization_code", ["client_id"] = "client-1",
            ["redirect_uri"] = callback, ["code"] = code,
        });
        using var unsupported = await PostFormAsync(client, "identity/connect/token", new()
        {
            ["grant_type"] = "client_credentials", ["client_id"] = "client-1",
        });

        Assert.Equal(HttpStatusCode.Redirect, authorised.StatusCode);
        Assert.Equal("s1", query["state"]);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, reused.StatusCode);
        using var unsupportedBody = await JsonAsync(unsupported);
        Assert.Equal("unsupported_grant_type", unsupportedBody.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task S2_absolute_refresh_chain_expires_from_initial_authentication()
    {
        var clock = new MutableClock(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        await using var simulator = new Cx04Simulator(new() { TimeProvider = clock });
        using var client = Client(simulator);
        var initial = await PasswordGrantAsync(client);
        clock.Advance(TimeSpan.FromDays(29));
        var rotated = await RefreshTokenAsync(client, initial.RefreshToken!);
        clock.Advance(TimeSpan.FromDays(2));

        using var expired = await PostFormAsync(client, "identity/connect/token", new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = rotated.RefreshToken!,
        });

        Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
    }

    [Fact]
    public async Task S2_sliding_refresh_chain_moves_the_expiry_on_each_rotation()
    {
        var clock = new MutableClock(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        await using var simulator = new Cx04Simulator(new()
        {
            TimeProvider = clock,
            RefreshExpiryMode = Cx04RefreshExpiryMode.Sliding,
        });
        using var client = Client(simulator);
        var initial = await PasswordGrantAsync(client);
        clock.Advance(TimeSpan.FromDays(89));
        var first = await RefreshTokenAsync(client, initial.RefreshToken!);
        clock.Advance(TimeSpan.FromDays(89));

        var second = await RefreshTokenAsync(client, first.RefreshToken!);

        Assert.NotNull(second.AccessToken);
    }

    [Fact]
    public async Task S2_token_expiry_fault_expires_a_token_mid_run()
    {
        await using var simulator = new Cx04Simulator();
        simulator.Faults.ExpireAccessTokenAfterRequests = 1;
        using var client = await AuthenticatedClientAsync(simulator);

        using var first = await client.GetAsync(EntityPath("Shipment"));
        using var second = await client.GetAsync(EntityPath("Shipment"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("api offline_access")]
    [InlineData("api api:concurrent_access")]
    public async Task S2_serves_each_required_api_scope(string scope)
    {
        await using var simulator = new Cx04Simulator();
        using var client = Client(simulator);

        using var response = await PostFormAsync(client, "identity/connect/token", new()
        {
            ["grant_type"] = "password", ["username"] = "fictional-api-user", ["password"] = "not-logged",
            ["scope"] = scope,
        });
        using var body = await JsonAsync(response);

        Assert.Equal(scope, body.RootElement.GetProperty("scope").GetString());
        Assert.Equal(scope.Contains("offline_access", StringComparison.Ordinal),
            body.RootElement.TryGetProperty("refresh_token", out var refresh)
            && refresh.ValueKind == JsonValueKind.String);
    }

    [Fact]
    public async Task S3_cookie_login_and_logout_count_and_release_sessions()
    {
        await using var simulator = new Cx04Simulator();
        using var client = Client(simulator, useCookies: true);

        using var login = await LoginAsync(client);
        using var read = await client.GetAsync(EntityPath("Shipment"));
        using var logout = await client.PostAsync("entity/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(0, simulator.ActiveSessionCount);
    }

    [Fact]
    public async Task S3_leaked_session_fault_reaches_the_tier_limit_and_returns_429()
    {
        await using var simulator = new Cx04Simulator();
        simulator.FillSessionLimitWithLeakedSessions();
        using var client = Client(simulator, useCookies: true);

        using var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(simulator.LicenceProfile.MaximumSessions, simulator.ActiveSessionCount);
    }

    [Fact]
    public async Task S3_sessions_are_reaped_after_ten_minutes_when_the_limit_is_reached()
    {
        var clock = new MutableClock(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        await using var simulator = new Cx04Simulator(new() { TimeProvider = clock });
        simulator.FillSessionLimitWithLeakedSessions();
        clock.Advance(TimeSpan.FromMinutes(11));
        using var client = Client(simulator, useCookies: true);

        using var response = await LoginAsync(client);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(1, simulator.ActiveSessionCount);
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task S3_idle_sessions_below_the_limit_follow_the_assumed_reaping_option(bool reap, int expected)
    {
        var clock = new MutableClock(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        await using var simulator = new Cx04Simulator(new()
        {
            TimeProvider = clock,
            ReapIdleSessionsBelowLimit = reap,
        });
        using var first = Client(simulator, useCookies: true);
        using (await LoginAsync(first)) { }
        clock.Advance(TimeSpan.FromMinutes(11));
        using var second = Client(simulator, useCookies: true);

        using (await LoginAsync(second)) { }

        Assert.Equal(expected, simulator.ActiveSessionCount);
    }

    [Theory]
    [InlineData(Cx04LicenceTier.Essentials, 50, 5, 1, 5, 1000, 2000)]
    [InlineData(Cx04LicenceTier.Select, 100, 10, 2, 10, 2000, 4000)]
    [InlineData(Cx04LicenceTier.Prime, 150, 20, 3, 20, 2500, 7500)]
    [InlineData(Cx04LicenceTier.Enterprise, 300, 40, 8, 77, 50000, 50000)]
    public void S4_profiles_match_each_licence_tier(Cx04LicenceTier tier, int rpm, int queue,
        int parallel, int sessions, int lines, int serials)
    {
        var profile = Cx04LicenceProfile.For(tier, 77);

        Assert.Equal((rpm, queue, parallel, sessions, lines, serials),
            (profile.RequestsPerMinute, profile.MaximumQueueSize, profile.ParallelRequests,
                profile.MaximumSessions, profile.LinesPerTransaction, profile.SerialNumbersPerDocument));
    }

    [Fact]
    public async Task S4_throttles_at_half_the_cap_with_the_documented_delay_formula()
    {
        var clock = new MutableClock(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        var delay = new RecordingDelay();
        await using var simulator = new Cx04Simulator(new()
        {
            TimeProvider = clock,
            DelayAsync = delay.WaitAsync,
        });
        using var client = await AuthenticatedClientAsync(simulator);
        for (var index = 0; index < 25; index++) using (await client.GetAsync(EntityPath("Shipment"))) { }
        clock.Advance(TimeSpan.FromSeconds(40));

        using (await client.GetAsync(EntityPath("Shipment"))) { }

        Assert.Equal(TimeSpan.FromMilliseconds(800), simulator.LastThrottleDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(800), Assert.Single(delay.Delays));
    }

    [Theory]
    [InlineData(Cx04QueueLimitReading.PerTier, true)]
    [InlineData(Cx04QueueLimitReading.FixedTwenty, false)]
    public async Task S4_queue_refusal_can_follow_either_published_reading(
        Cx04QueueLimitReading reading, bool sixthQueuedRequestIsRefused)
    {
        await using var simulator = new Cx04Simulator(new()
        {
            QueueLimitReading = reading,
            QueuedRequestLifetime = TimeSpan.FromSeconds(3),
        });
        simulator.Faults.HoldProcessedRequests = true;
        using var client = await AuthenticatedClientAsync(simulator);
        var active = client.GetAsync(EntityPath("Shipment"));
        await WaitUntilAsync(() => simulator.ProcessedRequestCount == 1);
        var queued = Enumerable.Range(0, 6).Select(_ => client.GetAsync(EntityPath("Shipment"))).ToArray();
        await WaitUntilAsync(() => simulator.QueuedRequestCount >= (sixthQueuedRequestIsRefused ? 5 : 6));

        var refused = queued.FirstOrDefault(task => task.IsCompleted)?.Result;
        simulator.ReleaseHeldRequests();
        using var activeResponse = await active;
        var responses = await Task.WhenAll(queued);

        Assert.Equal(sixthQueuedRequestIsRefused, responses.Any(value => value.StatusCode == HttpStatusCode.TooManyRequests));
        refused?.Dispose();
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task S4_429_storm_is_scriptable_and_then_clears()
    {
        await using var simulator = new Cx04Simulator();
        simulator.Faults.TooManyRequestsStormRemaining = 2;
        using var client = await AuthenticatedClientAsync(simulator);

        using var first = await client.GetAsync(EntityPath("Shipment"));
        using var second = await client.GetAsync(EntityPath("Shipment"));
        using var third = await client.GetAsync(EntityPath("Shipment"));

        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
    }

    [Fact]
    public async Task S4_queue_waiting_past_the_limit_is_declined_and_not_counted_as_processed()
    {
        await using var simulator = new Cx04Simulator(new()
        {
            QueuedRequestLifetime = TimeSpan.FromMilliseconds(20),
        });
        simulator.Faults.HoldProcessedRequests = true;
        using var client = await AuthenticatedClientAsync(simulator);
        var active = client.GetAsync(EntityPath("Shipment"));
        await WaitUntilAsync(() => simulator.ProcessedRequestCount == 1);

        using var expired = await client.GetAsync(EntityPath("Shipment"));
        simulator.ReleaseHeldRequests();
        using var activeResponse = await active;

        Assert.Equal(HttpStatusCode.TooManyRequests, expired.StatusCode);
        Assert.Equal(1, simulator.ProcessedRequestCount);
        Assert.Equal(1, simulator.DeclinedRequestCount);
    }

    [Fact]
    public async Task S5_reads_by_key_filter_expand_and_offset_page()
    {
        await using var simulator = new Cx04Simulator();
        simulator.SeedEntity("Default", Cx04Simulator.DefaultEndpointVersion, "Shipment", Shipment("S2", "2026-09-02T09:00:00Z"));
        simulator.SeedEntity("Default", Cx04Simulator.DefaultEndpointVersion, "Shipment", Shipment("S1", "2026-09-01T09:00:00Z"));
        using var client = await AuthenticatedClientAsync(simulator);

        using var keyResponse = await client.GetAsync(EntityPath("Shipment") + "/S1?$expand=Details,Details/Allocations");
        using var keyBody = await JsonAsync(keyResponse);
        using var collapsedResponse = await client.GetAsync(EntityPath("Shipment") + "/S1");
        using var collapsedBody = await JsonAsync(collapsedResponse);
        var filter = Uri.EscapeDataString("LastModifiedDateTime gt datetimeoffset'2026-09-01T12:00:00Z'");
        using var pageResponse = await client.GetAsync(EntityPath("Shipment") + "?$filter=" + filter + "&$top=1&$skip=0");
        using var pageBody = await JsonAsync(pageResponse);

        Assert.True(keyBody.RootElement.TryGetProperty("Details", out var details));
        Assert.True(details[0].TryGetProperty("Allocations", out _));
        Assert.True(keyBody.RootElement.GetProperty("_links").TryGetProperty("files:put", out _));
        Assert.True(details[0].GetProperty("_links").TryGetProperty("files:put", out _));
        Assert.False(collapsedBody.RootElement.TryGetProperty("Details", out _));
        Assert.Equal("S2", pageBody.RootElement[0].GetProperty("ShipmentNbr").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData(true, "S1")]
    [InlineData(false, "S2")]
    public async Task S5_paging_order_exposes_both_stability_assumptions(bool stable, string first)
    {
        await using var simulator = new Cx04Simulator(new() { StablePagingOrder = stable });
        simulator.SeedEntity("Default", Cx04Simulator.DefaultEndpointVersion, "Shipment", Shipment("S2", "2026-09-02T09:00:00Z"));
        simulator.SeedEntity("Default", Cx04Simulator.DefaultEndpointVersion, "Shipment", Shipment("S1", "2026-09-01T09:00:00Z"));
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await client.GetAsync(EntityPath("Shipment") + "?$top=1");
        using var body = await JsonAsync(response);

        Assert.Equal(first, body.RootElement[0].GetProperty("ShipmentNbr").GetProperty("value").GetString());
    }

    [Theory]
    [InlineData("LastModifiedDateTime", true)]
    [InlineData("ChangedAt", false)]
    public async Task S5_modified_field_is_configurable_per_entity_and_version(string configured, bool standardMatches)
    {
        await using var simulator = new Cx04Simulator();
        simulator.SetModifiedDateField("TransferOrder", Cx04Simulator.DefaultEndpointVersion, configured);
        simulator.SeedEntity("Default", Cx04Simulator.DefaultEndpointVersion, "TransferOrder",
            """{"OrderType":{"value":"TR"},"OrderNbr":{"value":"T1"},"LastModifiedDateTime":{"value":"2026-09-02T09:00:00Z"},"ChangedAt":{"value":"2026-09-02T09:00:00Z"}}""");
        using var client = await AuthenticatedClientAsync(simulator);
        var filter = Uri.EscapeDataString("LastModifiedDateTime gt datetimeoffset'2026-09-01T09:00:00Z'");

        using var response = await client.GetAsync(EntityPath("TransferOrder") + "?$filter=" + filter);
        using var body = await JsonAsync(response);

        Assert.Equal(standardMatches ? 1 : 0, body.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task S5_pinned_sales_orders_use_the_older_modified_field_name()
    {
        await using var simulator = new Cx04Simulator();
        simulator.SeedEntity("Default", Cx04Simulator.DefaultEndpointVersion, "SalesOrder",
            """{"OrderType":{"value":"RC"},"OrderNbr":{"value":"SO1"},"LastModified":{"value":"2026-09-02T09:00:00Z"}}""");
        using var client = await AuthenticatedClientAsync(simulator);
        var oldField = Uri.EscapeDataString("LastModified gt datetimeoffset'2026-09-01T09:00:00Z'");
        var newField = Uri.EscapeDataString("LastModifiedDateTime gt datetimeoffset'2026-09-01T09:00:00Z'");

        using var oldResponse = await client.GetAsync(EntityPath("SalesOrder") + "?$filter=" + oldField);
        using var oldBody = await JsonAsync(oldResponse);
        using var newResponse = await client.GetAsync(EntityPath("SalesOrder") + "?$filter=" + newField);
        using var newBody = await JsonAsync(newResponse);

        Assert.Single(oldBody.RootElement.EnumerateArray());
        Assert.Empty(newBody.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task S6_put_creates_updates_and_honours_preconditions()
    {
        await using var simulator = new Cx04Simulator();
        using var client = await AuthenticatedClientAsync(simulator);
        var create = JsonNode.Parse(Shipment("S1", "2026-09-01T09:00:00Z"))!.AsObject();
        create.Remove("id");
        using var created = await PutAsync(client, "Shipment", create, ifNoneMatch: true);
        var update = JsonNode.Parse(Shipment("S1", "2026-09-02T09:00:00Z"))!.AsObject();
        update.Remove("id");
        using var refusedCreate = await PutAsync(client, "Shipment", update, ifNoneMatch: true);
        using var updated = await PutAsync(client, "Shipment", update, ifMatch: true);
        var missing = JsonNode.Parse(Shipment("S9", "2026-09-02T09:00:00Z"))!.AsObject();
        missing.Remove("id");
        using var refusedUpdate = await PutAsync(client, "Shipment", missing, ifMatch: true);

        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, refusedCreate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.PreconditionFailed, refusedUpdate.StatusCode);
    }

    [Theory]
    [InlineData(true, "id-target")]
    [InlineData(false, "key-target")]
    public async Task S6_id_and_key_precedence_follow_the_assumed_option(bool idWins, string expectedDescription)
    {
        await using var simulator = new Cx04Simulator(new() { IdWinsWhenBothProvided = idWins });
        simulator.SeedEntity("Default", Cx04Simulator.DefaultEndpointVersion, "Shipment",
            """{"id":"00000000-0000-0000-0000-000000000101","ShipmentNbr":{"value":"S1"},"Description":{"value":"id-target"}}""");
        simulator.SeedEntity("Default", Cx04Simulator.DefaultEndpointVersion, "Shipment",
            """{"id":"00000000-0000-0000-0000-000000000102","ShipmentNbr":{"value":"S2"},"Description":{"value":"key-target"}}""");
        using var client = await AuthenticatedClientAsync(simulator);
        var body = JsonNode.Parse("""{"id":"00000000-0000-0000-0000-000000000101","ShipmentNbr":{"value":"S2"},"Marker":{"value":"updated"}}""")!.AsObject();

        using (await PutAsync(client, "Shipment", body)) { }
        using var response = await client.GetAsync(EntityPath("Shipment") + "?$expand=Details");
        using var records = await JsonAsync(response);
        var updated = records.RootElement.EnumerateArray().Single(value => value.TryGetProperty("Marker", out _));

        Assert.Equal(expectedDescription, updated.GetProperty("Description").GetProperty("value").GetString());
    }

    [Fact]
    public async Task S6_detail_delete_and_dropped_create_reply_leave_the_committed_record_queryable()
    {
        await using var simulator = new Cx04Simulator();
        simulator.Faults.DropNextCreateReply = true;
        using var client = await AuthenticatedClientAsync(simulator);
        var created = JsonNode.Parse(Shipment("S1", "2026-09-01T09:00:00Z"))!.AsObject();
        created.Remove("id");
        await Assert.ThrowsAnyAsync<HttpRequestException>(async () =>
        {
            using var dropped = await PutAsync(client, "Shipment", created);
        });
        var deleting = JsonNode.Parse(Shipment("S1", "2026-09-01T09:00:00Z"))!.AsObject();
        deleting.Remove("id");
        deleting["Details"]![0]!["delete"] = true;

        using (await PutAsync(client, "Shipment", deleting)) { }
        using var read = await client.GetAsync(EntityPath("Shipment") + "/S1?$expand=Details,Details/Allocations");
        using var body = await JsonAsync(read);

        Assert.Empty(body.RootElement.GetProperty("Details").EnumerateArray());
    }

    [Fact]
    public async Task S6_validation_errors_echo_the_entity_with_a_per_field_error()
    {
        await using var simulator = new Cx04Simulator();
        using var client = await AuthenticatedClientAsync(simulator);
        var body = AllocationDocument("PurchaseReceipt", includeLineQuantity: true, lineQuantity: 3, allocationQuantity: 2);

        using var response = await PutAsync(client, "PurchaseReceipt", body);
        using var error = await JsonAsync(response);

        Assert.Equal(Cx04Status.UnprocessableEntity, response.StatusCode);
        Assert.Contains("sum", error.RootElement.GetProperty("Allocations").GetProperty("error").GetString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Cx04AllocationQuantityBehaviour.LineAndAllocations, true, HttpStatusCode.OK, 2)]
    [InlineData(Cx04AllocationQuantityBehaviour.LineAndAllocations, false, Cx04Status.UnprocessableEntity, 0)]
    [InlineData(Cx04AllocationQuantityBehaviour.AllocationsOnly, false, HttpStatusCode.OK, 0)]
    [InlineData(Cx04AllocationQuantityBehaviour.AllocationsOnly, true, HttpStatusCode.OK, 4)]
    public async Task S7_allocation_quantity_behaviour_supports_both_observed_variants(
        Cx04AllocationQuantityBehaviour behaviour, bool includeLine, HttpStatusCode expected, int expectedLine)
    {
        await using var simulator = new Cx04Simulator(new() { AllocationQuantityBehaviour = behaviour });
        using var client = await AuthenticatedClientAsync(simulator);
        var body = AllocationDocument("PurchaseReceipt", includeLine, 2, 2);

        using var response = await PutAsync(client, "PurchaseReceipt", body);

        Assert.Equal(expected, response.StatusCode);
        if (response.IsSuccessStatusCode && includeLine)
        {
            using var result = await JsonAsync(response);
            Assert.Equal(expectedLine, result.RootElement.GetProperty("Details")[0]
                .GetProperty("ReceiptQty").GetProperty("value").GetInt32());
        }
    }

    [Theory]
    [InlineData(true, Cx04Status.UnprocessableEntity)]
    [InlineData(false, HttpStatusCode.OK)]
    public async Task S7_serial_quantity_one_is_an_explicit_assumption(bool requireOne, HttpStatusCode expected)
    {
        await using var simulator = new Cx04Simulator(new() { RequireSerialQuantityOne = requireOne });
        using var client = await AuthenticatedClientAsync(simulator);
        var body = AllocationDocument("Shipment", includeLineQuantity: true, lineQuantity: 2, allocationQuantity: 2);
        body["Details"]![0]!["Allocations"]![0]!["LotSerialNbr"] = Wrapped("SN-FICTIONAL-001");

        using var response = await PutAsync(client, "Shipment", body);

        Assert.Equal(expected, response.StatusCode);
    }

    [Theory]
    [InlineData(true, HttpStatusCode.OK)]
    [InlineData(false, Cx04Status.UnprocessableEntity)]
    public async Task S7_rm_allocations_follow_the_assumed_shape_switch(bool support, HttpStatusCode expected)
    {
        await using var simulator = new Cx04Simulator(new() { SupportRmAllocations = support });
        using var client = await AuthenticatedClientAsync(simulator);
        var body = AllocationDocument("SalesOrder", includeLineQuantity: true, lineQuantity: 1, allocationQuantity: 1);
        body["OrderType"] = Wrapped("RM");

        using var response = await PutAsync(client, "SalesOrder", body);

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task S7_when_used_serials_are_refused_on_receipts_and_allowed_on_shipments()
    {
        await using var simulator = new Cx04Simulator();
        simulator.SetSerialAssignment("HW-SERVER-01", Cx04SerialAssignment.WhenUsed);
        using var client = await AuthenticatedClientAsync(simulator);

        using var receipt = await PutAsync(client, "PurchaseReceipt",
            AllocationDocument("PurchaseReceipt", true, 1, 1));
        using var shipment = await PutAsync(client, "Shipment",
            AllocationDocument("Shipment", true, 1, 1));

        Assert.Equal(Cx04Status.UnprocessableEntity, receipt.StatusCode);
        Assert.Equal(HttpStatusCode.OK, shipment.StatusCode);
    }

    [Fact]
    public async Task S7_transfer_receipt_and_rc_return_use_their_documented_allocation_shapes()
    {
        await using var simulator = new Cx04Simulator();
        using var client = await AuthenticatedClientAsync(simulator);
        var transfer = AllocationDocument("PurchaseReceipt", true, 1, 1);
        transfer["Type"] = Wrapped("Transfer Receipt");
        var creditReturn = AllocationDocument("SalesOrder", true, 1, 1);

        using var transferResponse = await PutAsync(client, "PurchaseReceipt", transfer);
        using var returnResponse = await PutAsync(client, "SalesOrder", creditReturn);

        Assert.Equal(HttpStatusCode.OK, transferResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, returnResponse.StatusCode);
    }

    [Fact]
    public async Task S7_serial_tracked_line_without_allocations_returns_a_clearly_assumed_error()
    {
        await using var simulator = new Cx04Simulator();
        simulator.SetSerialAssignment("HW-SERVER-01", Cx04SerialAssignment.WhenReceived);
        using var client = await AuthenticatedClientAsync(simulator);
        var body = BaseDocument("Shipment");
        body["Details"] = new JsonArray(new JsonObject
        {
            ["InventoryID"] = Wrapped("HW-SERVER-01"), ["Qty"] = Wrapped(1),
        });

        using var response = await PutAsync(client, "Shipment", body);
        using var error = await JsonAsync(response);

        Assert.Equal(Cx04Status.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Simulator assumption", error.RootElement.GetProperty("Allocations")
            .GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task S8_firm_line_and_serial_limits_return_422()
    {
        await using var simulator = new Cx04Simulator();
        using var client = await AuthenticatedClientAsync(simulator);
        var tooManyLines = BaseDocument("PurchaseReceipt");
        tooManyLines["Details"] = new JsonArray(Enumerable.Range(0, 1001)
            .Select(index => (JsonNode?)new JsonObject { ["LineNbr"] = Wrapped(index) }).ToArray());
        var tooManySerials = BaseDocument("PurchaseReceipt");
        var allocations = new JsonArray(Enumerable.Range(0, 2001).Select(index => (JsonNode?)new JsonObject
        {
            ["LotSerialNbr"] = Wrapped("SN-" + index), ["Qty"] = Wrapped(1),
        }).ToArray());
        tooManySerials["Details"] = new JsonArray(new JsonObject
        {
            ["InventoryID"] = Wrapped("HW-STORAGE-01"), ["ReceiptQty"] = Wrapped(2001),
            ["Allocations"] = allocations,
        });

        using var lineResponse = await PutAsync(client, "PurchaseReceipt", tooManyLines);
        using var serialResponse = await PutAsync(client, "PurchaseReceipt", tooManySerials);

        Assert.Equal(Cx04Status.UnprocessableEntity, lineResponse.StatusCode);
        Assert.Equal(Cx04Status.UnprocessableEntity, serialResponse.StatusCode);
    }

    [Fact]
    public async Task S9_long_running_action_locks_its_session_until_location_polling_completes()
    {
        await using var simulator = new Cx04Simulator();
        using var client = await AuthenticatedClientAsync(simulator);

        using var started = await client.PostAsync(EntityPath("Shipment") + "/ConfirmShipment",
            JsonContent("""{"entity":{"ShipmentNbr":{"value":"S1"}},"parameters":{}}"""));
        using var blocked = await client.GetAsync(EntityPath("Shipment"));
        using var running = await client.GetAsync(started.Headers.Location);
        using var complete = await client.GetAsync(started.Headers.Location);
        using var available = await client.GetAsync(EntityPath("Shipment"));

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, running.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);
        Assert.Equal(HttpStatusCode.OK, available.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Accepted)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task S9_running_status_code_is_an_explicit_assumption(HttpStatusCode configured)
    {
        await using var simulator = new Cx04Simulator(new() { RunningActionStatusCode = configured });
        using var client = await AuthenticatedClientAsync(simulator);
        using var started = await client.PostAsync(EntityPath("Shipment") + "/ConfirmShipment",
            JsonContent("{}"));

        using var polled = await client.GetAsync(started.Headers.Location);

        Assert.Equal(configured, polled.StatusCode);
    }

    [Theory]
    [InlineData("ReleasePurchaseReceipt", "ReleasePurchaseReceipt", HttpStatusCode.Accepted)]
    [InlineData("ReleaseReceipt", "ReleasePurchaseReceipt", HttpStatusCode.NotFound)]
    [InlineData("ReleaseReceipt", "ReleaseReceipt", HttpStatusCode.Accepted)]
    public async Task S9_receipt_release_action_name_is_configurable(string configured, string called,
        HttpStatusCode expected)
    {
        await using var simulator = new Cx04Simulator(new() { ReceiptReleaseActionName = configured });
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await client.PostAsync(EntityPath("PurchaseReceipt") + "/" + called,
            JsonContent("{}"));

        Assert.Equal(expected, response.StatusCode);
    }

    [Fact]
    public async Task S9_unknown_and_disabled_actions_return_404_and_the_recorded_500_shape()
    {
        await using var simulator = new Cx04Simulator();
        using var client = await AuthenticatedClientAsync(simulator);
        using var unknown = await client.PostAsync(EntityPath("Shipment") + "/FictionalUnknown", JsonContent("{}"));
        simulator.Faults.DisableNextAction = true;
        using var disabled = await client.PostAsync(EntityPath("Shipment") + "/ConfirmShipment", JsonContent("{}"));
        using var body = await JsonAsync(disabled);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.InternalServerError, disabled.StatusCode);
        Assert.Equal("PX.Data.PXActionDisabledException", body.RootElement.GetProperty("exceptionType").GetString());
    }

    [Fact]
    public async Task S10_record_and_detail_files_store_binary_comment_and_location()
    {
        await using var simulator = new Cx04Simulator(new() { AttachmentSizeLimitBytes = 3 });
        using var client = await AuthenticatedClientAsync(simulator);
        using var record = FileRequest(new byte[] { 1, 2, 3 }, "server evidence");
        using var accepted = await client.SendAsync(record);
        using var detail = FileRequest(new byte[] { 4, 5, 6 }, "line evidence", detail: true);
        using var acceptedDetail = await client.SendAsync(detail);
        using var oversized = FileRequest(new byte[] { 1, 2, 3, 4 }, null);
        using var refused = await client.SendAsync(oversized);

        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        Assert.NotNull(accepted.Headers.Location);
        Assert.Equal(HttpStatusCode.NoContent, acceptedDetail.StatusCode);
        Assert.Equal(Cx04Status.UnprocessableEntity, refused.StatusCode);
        Assert.Equal(["server evidence", "line evidence"], simulator.Files.Select(value => value.Comment));
    }

    [Fact]
    public async Task S11_push_body_and_static_secret_header_reach_the_webhook()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        await using var simulator = new Cx04Simulator(new()
        {
            WebhookSecretHeader = "X-Fictional-Signature",
            WebhookSecret = "fictional-value",
        }, handler);
        var notification = Notification("evt-1", 10);

        var delivery = Assert.Single(await simulator.PublishNotificationsAsync(
            new("https://fictional.example/hooks"), [notification]));

        Assert.True(delivery.Delivered);
        Assert.Equal("fictional-value", Assert.Single(handler.Headers)["X-Fictional-Signature"]);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.True(body.RootElement.TryGetProperty("Inserted", out _));
        Assert.Equal("evt-1", body.RootElement.GetProperty("Id").GetString());
        Assert.True(body.RootElement.TryGetProperty("TimeStamp", out _));
    }

    [Fact]
    public async Task S11_per_document_queries_build_notifications_and_reject_aggregate_setup()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK);
        await using var simulator = new Cx04Simulator(new(), handler);
        simulator.ConfigurePushQuery("Shipment", "FictionalShipmentChanges", "FictionalTenant",
            new("https://fictional.example/hooks"));

        var delivery = Assert.Single(await simulator.PublishDocumentChangesAsync("Shipment",
            [new Dictionary<string, object?> { ["ShipmentNbr"] = "S1" }], [], "evt-query", 70,
            new Dictionary<string, object?>()));
        var aggregate = Assert.Throws<ArgumentException>(() => simulator.ConfigurePushQuery("Summary",
            "FictionalSummary", "FictionalTenant", new("https://fictional.example/hooks"), aggregate: true));

        Assert.True(delivery.Delivered);
        Assert.Equal("aggregate", aggregate.ParamName);
        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal("FictionalShipmentChanges", body.RootElement.GetProperty("Query").GetString());
    }

    [Fact]
    public async Task S11_errors_retry_at_most_five_times_then_park_and_manual_resend()
    {
        var handler = new SequenceHandler(HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError,
            HttpStatusCode.OK);
        var delay = new RecordingDelay();
        await using var simulator = new Cx04Simulator(new()
        {
            PushRetryInterval = TimeSpan.FromSeconds(7),
            DelayAsync = delay.WaitAsync,
        }, handler);

        var delivery = Assert.Single(await simulator.PublishNotificationsAsync(
            new("https://fictional.example/hooks"), [Notification("evt-2", 20)]));
        var resent = await simulator.ResendParkedNotificationAsync("evt-2", new("https://fictional.example/hooks"));

        Assert.Equal(5, delivery.Attempts);
        Assert.True(delivery.Parked);
        Assert.True(resent);
        Assert.Equal(4, delay.Delays.Count);
        Assert.All(delay.Delays, value => Assert.Equal(TimeSpan.FromSeconds(7), value));
        Assert.Empty(simulator.ParkedNotifications);
    }

    [Fact]
    public async Task S11_failed_notifications_purge_after_two_days()
    {
        var clock = new MutableClock(new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero));
        var handler = new SequenceHandler(Enumerable.Repeat(HttpStatusCode.InternalServerError, 5).ToArray());
        await using var simulator = new Cx04Simulator(new()
        {
            TimeProvider = clock,
            DelayAsync = static (_, _) => ValueTask.CompletedTask,
        }, handler);
        await simulator.PublishNotificationsAsync(new("https://fictional.example/hooks"), [Notification("evt-3", 30)]);
        Assert.Single(simulator.ParkedNotifications);

        clock.Advance(TimeSpan.FromDays(2) + TimeSpan.FromTicks(1));

        Assert.Empty(simulator.ParkedNotifications);
    }

    [Fact]
    public async Task S11_truncated_and_pre_send_loss_faults_have_the_documented_resend_boundaries()
    {
        var handler = new SequenceHandler(Enumerable.Repeat(HttpStatusCode.InternalServerError, 5).ToArray());
        await using var simulator = new Cx04Simulator(new()
        {
            DelayAsync = static (_, _) => ValueTask.CompletedTask,
        }, handler);
        simulator.Faults.TruncateNextNotification = true;
        var truncated = Assert.Single(await simulator.PublishNotificationsAsync(
            new("https://fictional.example/hooks"), [Notification("evt-4", 40)]));
        var resent = await simulator.ResendParkedNotificationAsync("evt-4", new("https://fictional.example/hooks"));
        simulator.Faults.LoseNextNotificationBeforeSend = true;
        var lost = Assert.Single(await simulator.PublishNotificationsAsync(
            new("https://fictional.example/hooks"), [Notification("evt-5", 50)]));

        Assert.True(truncated.Parked);
        Assert.False(resent);
        Assert.Equal(0, lost.Attempts);
        Assert.False(lost.Parked);
    }

    [Fact]
    public async Task S11_duplicate_and_out_of_order_notification_faults_are_scriptable()
    {
        var handler = new SequenceHandler(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK);
        await using var simulator = new Cx04Simulator(new(), handler);
        simulator.Faults.ReverseNextNotificationBatch = true;
        simulator.Faults.DuplicateNextNotification = true;

        var deliveries = await simulator.PublishNotificationsAsync(new("https://fictional.example/hooks"),
            [Notification("evt-early", 1), Notification("evt-late", 2)]);

        Assert.Equal(["evt-late", "evt-late", "evt-early"],
            simulator.DeliveredNotifications.Select(value => value.Id));
        Assert.Equal(3, deliveries.Count);
    }

    [Fact]
    public async Task S12_401_403_and_500_use_the_remote_error_shape()
    {
        await using var simulator = new Cx04Simulator();
        using var anonymous = Client(simulator);
        using var unauthorised = await anonymous.GetAsync(EntityPath("Shipment"));
        using var unauthorisedBody = await JsonAsync(unauthorised);
        using var client = await AuthenticatedClientAsync(simulator);
        simulator.Faults.ForbidNextRequest = true;
        using var forbidden = await client.GetAsync(EntityPath("Shipment"));
        using var forbiddenBody = await JsonAsync(forbidden);
        simulator.Faults.DisableNextAction = true;
        using var failed = await client.PostAsync(EntityPath("Shipment") + "/ConfirmShipment", JsonContent("{}"));
        using var failedBody = await JsonAsync(failed);

        foreach (var body in new[] { unauthorisedBody, forbiddenBody, failedBody })
        {
            Assert.True(body.RootElement.TryGetProperty("message", out _));
            Assert.True(body.RootElement.TryGetProperty("exceptionMessage", out _));
            Assert.True(body.RootElement.TryGetProperty("exceptionType", out _));
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task S12_429_body_and_retry_after_follow_both_assumed_variants(bool empty, bool retryAfter)
    {
        await using var simulator = new Cx04Simulator(new()
        {
            EmptyThrottleResponse = empty,
            IncludeRetryAfterOnThrottle = retryAfter,
        });
        simulator.Faults.TooManyRequestsStormRemaining = 1;
        using var client = await AuthenticatedClientAsync(simulator);

        using var response = await client.GetAsync(EntityPath("Shipment"));
        using var body = await JsonAsync(response);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(retryAfter, response.Headers.RetryAfter is not null);
        Assert.Equal(!empty, body.RootElement.TryGetProperty("message", out _));
    }

    [Fact]
    public async Task S13_manufacturing_completions_are_read_only_and_generate_is_disabled()
    {
        await using var simulator = new Cx04Simulator();
        simulator.SeedEntity("MANUFACTURING", Cx04Simulator.ManufacturingEndpointVersion, "MoveEntry",
            """{"ReferenceNbr":{"value":"MOVE-001"},"ProductionNbr":{"value":"PROD-001"},"InventoryID":{"value":"HW-SERVER-01"}}""");
        using var client = await AuthenticatedClientAsync(simulator);

        using var read = await client.GetAsync(ManufacturingPath("MoveEntry") + "/MOVE-001");
        using var body = await JsonAsync(read);
        using var generate = await client.PostAsync(ManufacturingPath("MoveEntry") + "/Generate", JsonContent("{}"));
        using var error = await JsonAsync(generate);

        Assert.Equal("PROD-001", body.RootElement.GetProperty("ProductionNbr").GetProperty("value").GetString());
        Assert.Equal(HttpStatusCode.InternalServerError, generate.StatusCode);
        Assert.Equal("PX.Data.PXActionDisabledException", error.RootElement.GetProperty("exceptionType").GetString());
    }

    private static HttpClient Client(Cx04Simulator simulator, bool useCookies = false)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = useCookies };
        return new(handler) { BaseAddress = simulator.BaseUri };
    }

    private static async Task<HttpClient> AuthenticatedClientAsync(Cx04Simulator simulator)
    {
        var client = Client(simulator);
        var grant = await PasswordGrantAsync(client);
        client.DefaultRequestHeaders.Authorization = new("Bearer", grant.AccessToken);
        return client;
    }

    private static async Task<TokenGrant> PasswordGrantAsync(HttpClient client)
    {
        using var response = await PostFormAsync(client, "identity/connect/token", new()
        {
            ["grant_type"] = "password", ["client_id"] = "client-1", ["client_secret"] = "fictional-secret",
            ["username"] = "fictional-api-user", ["password"] = "not-logged", ["scope"] = "api offline_access",
        });
        response.EnsureSuccessStatusCode();
        using var body = await JsonAsync(response);
        return new(body.RootElement.GetProperty("access_token").GetString()!,
            body.RootElement.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null);
    }

    private static async Task<TokenGrant> RefreshTokenAsync(HttpClient client, string refreshToken)
    {
        using var response = await PostFormAsync(client, "identity/connect/token", new()
        {
            ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken,
        });
        response.EnsureSuccessStatusCode();
        using var body = await JsonAsync(response);
        return new(body.RootElement.GetProperty("access_token").GetString()!,
            body.RootElement.GetProperty("refresh_token").GetString());
    }

    private static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path,
        Dictionary<string, string> values) => client.PostAsync(path, new FormUrlEncodedContent(values));

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client) => client.PostAsync("entity/auth/login",
        JsonContent("""{"name":"fictional-api-user","password":"not-logged","tenant":"FictionalTenant","branch":"MAIN"}"""));

    private static async Task<JsonDocument> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static StringContent JsonContent(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string entity, JsonObject body,
        bool ifNoneMatch = false, bool ifMatch = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, EntityPath(entity))
        {
            Content = JsonContent(body.ToJsonString()),
        };
        if (ifNoneMatch) request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        if (ifMatch) request.Headers.TryAddWithoutValidation("If-Match", "*");
        return await client.SendAsync(request);
    }

    private static HttpRequestMessage FileRequest(byte[] content, string? comment, bool detail = false)
    {
        var view = detail ? "Detail" : "Document";
        var request = new HttpRequestMessage(HttpMethod.Put,
            $"entity/Default/{Cx04Simulator.DefaultEndpointVersion}/files/Fictional.Shipment/{view}/record-1/evidence.bin")
        {
            Content = new ByteArrayContent(content),
        };
        request.Content.Headers.ContentType = new("application/octet-stream");
        if (comment is not null) request.Headers.TryAddWithoutValidation("PX-CbFileComment", comment);
        return request;
    }

    private static JsonObject AllocationDocument(string entity, bool includeLineQuantity, int lineQuantity,
        int allocationQuantity)
    {
        var body = BaseDocument(entity);
        var detail = new JsonObject
        {
            ["InventoryID"] = Wrapped("HW-SERVER-01"),
            ["Allocations"] = new JsonArray(new JsonObject
            {
                ["Location"] = Wrapped("WH-A"),
                ["Qty"] = Wrapped(allocationQuantity),
                ["LotSerialNbr"] = allocationQuantity == 1 ? Wrapped("SN-FICTIONAL-001") : null,
            }),
        };
        if (includeLineQuantity) detail[entity == "PurchaseReceipt" ? "ReceiptQty" : "Qty"] = Wrapped(lineQuantity);
        body["Details"] = new JsonArray(detail);
        return body;
    }

    private static JsonObject BaseDocument(string entity) => entity switch
    {
        "PurchaseReceipt" => new() { ["Type"] = Wrapped("Receipt"), ["ReceiptNbr"] = Wrapped("R1") },
        "Shipment" => new() { ["ShipmentNbr"] = Wrapped("S1") },
        "SalesOrder" => new() { ["OrderType"] = Wrapped("RC"), ["OrderNbr"] = Wrapped("SO1") },
        "TransferOrder" => new() { ["OrderType"] = Wrapped("TR"), ["OrderNbr"] = Wrapped("T1") },
        _ => new(),
    };

    private static JsonObject Wrapped<T>(T value) => new() { ["value"] = JsonValue.Create(value) };

    private static string Shipment(string number, string modified) => new JsonObject
    {
        ["id"] = "00000000-0000-0000-0000-0000000000" + (number == "S1" ? "01" : "02"),
        ["ShipmentNbr"] = Wrapped(number),
        ["LastModifiedDateTime"] = Wrapped(modified),
        ["Details"] = new JsonArray(new JsonObject
        {
            ["InventoryID"] = Wrapped("HW-SERVER-01"),
            ["Qty"] = Wrapped(1),
            ["Allocations"] = new JsonArray(new JsonObject
            {
                ["LotSerialNbr"] = Wrapped("SN-FICTIONAL-001"), ["Qty"] = Wrapped(1),
            }),
        }),
    }.ToJsonString();

    private static Cx04PushNotification Notification(string id, long timestamp) => new(
        [new Dictionary<string, object?> { ["ShipmentNbr"] = "S1" }],
        [], "FictionalShipmentChanges", "FictionalTenant", id, timestamp,
        new Dictionary<string, object?> { ["Operation"] = "Update" });

    private static string EntityPath(string entity) =>
        $"entity/Default/{Cx04Simulator.DefaultEndpointVersion}/{entity}";

    private static string ManufacturingPath(string entity) =>
        $"entity/MANUFACTURING/{Cx04Simulator.ManufacturingEndpointVersion}/{entity}";

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed record TokenGrant(string AccessToken, string? RefreshToken);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class RecordingDelay
    {
        public List<TimeSpan> Delays { get; } = [];
        public ValueTask WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Delays.Add(delay);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class SequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> statuses = new(statuses);
        public List<string> Bodies { get; } = [];
        public List<IReadOnlyDictionary<string, string>> Headers { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Headers.Add(request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(',', pair.Value),
                StringComparer.OrdinalIgnoreCase));
            return new(statuses.TryDequeue(out var status) ? status : HttpStatusCode.OK);
        }
    }
}
