using System.Net;
using System.Diagnostics;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Yojana.Integration.Connections;
using Yojana.Integration.Contracts;
using Yojana.Integration.Gateway;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Data;
using Yojana.Integration.Notifications;
using Yojana.Integration.Relays;
using Yojana.Platform;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Relay;
using Yojana.Relay.Contracts;
using Yojana.Serialisation;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class SiteRelayDatabaseTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Real_agent_captures_poll_executes_write_and_forwards_verified_webhooks()
    {
        await using var simulator = new Cx01Simulator();
        await using var cloud = await StartCloudAsync();
        var (tenant, connection, issued) = await RegisterAsync(simulator.BaseUri, cloud.BaseAddress);
        var configuration = issued.Configuration with { WebhookBind = $"http://127.0.0.1:{FreePort()}/" };
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        var cloudClient = new RelayCloudClient(http, configuration, new(issued.Secret, "FICTIONAL_DEMO", "integration-user", "not-logged"), new TestTime(fixture.Clock));
        using var local = new RelayLocalClient(configuration, new(issued.Secret, "FICTIONAL_DEMO", "integration-user", "not-logged"));
        using var agent = new RelayAgent(configuration, cloudClient, local, NullLogger<RelayAgent>.Instance, new TestTime(fixture.Clock));
        using var listener = new RelayWebhookListener(configuration, cloudClient);
        await agent.StartAsync(CancellationToken.None);
        await listener.StartAsync(CancellationToken.None);
        try
        {
            using var scope = fixture.ScopeFor(tenant);
            var client = scope.ServiceProvider.GetRequiredService<Cx01Client>();
            var read = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>().PollAsync(
                new(tenant, connection, "deliveries", null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);
            Assert.NotEmpty(read.Events);
            Assert.Equal(1, simulator.LoginCount);
            await Cx01RelayAcceptance.AssertOutboundAsync(scope.ServiceProvider, connection, client, simulator);
            await Cx01RelayAcceptance.AssertWebhooksAsync(scope.ServiceProvider, tenant, connection, configuration, http);
            Assert.Null(await scope.ServiceProvider.GetRequiredService<Yojana.Integration.Security.Cryptography.IConnectionCredentialStore>()
                .GetCurrentAsync(tenant, connection, Cx01Client.CredentialPurpose, CancellationToken.None));
            var page = await scope.ServiceProvider.GetRequiredService<RelayRegistry>().ListAsync(connection, null, 10, CancellationToken.None);
            Assert.Equal(1, page.RelayConnected);
            Assert.NotNull(Assert.Single(page.Items).LastSeenAt);
            var calls = simulator.RequestCount;
            using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, new Uri(simulator.BaseUri, "/undeclared"));
            using var denied = await scope.ServiceProvider.GetRequiredService<RelayRequestQueue>().SendAsync(connection,
                deniedRequest, 15, 128, configuration.Declaration, CancellationToken.None);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal(calls, simulator.RequestCount);
            Assert.True(await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ConnectionEvents
                .AnyAsync(row => row.ConnectionId == connection && row.EventType == "RelayPathDenied"));
            await agent.StopAsync(CancellationToken.None);
            await cloud.App.StopAsync();
            using var unavailable = await http.GetAsync(configuration.WebhookBind);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        }
        finally { await listener.StopAsync(CancellationToken.None); await agent.StopAsync(CancellationToken.None); }
    }

    [DatabaseFact]
    public async Task Relay_signatures_reject_replay_wrong_tenant_revocation_and_expired_rotation_secrets()
    {
        await using var simulator = new Cx01Simulator();
        await using var cloud = await StartCloudAsync();
        var (tenant, connection, issued) = await RegisterAsync(simulator.BaseUri, cloud.BaseAddress);
        using var http = new HttpClient();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new RelayResult(Guid.NewGuid(), Guid.NewGuid(), 200, new Dictionary<string, string[]>(), []), JsonSerializerOptions.Web);
        using var signed = Signed(issued, bytes, "/api/relay/v1/results");
        var signatureHeaders = signed.Headers.ToDictionary(x => x.Key, x => x.Value.ToArray());
        using (var first = await http.SendAsync(signed)) Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        using var replay = new HttpRequestMessage(HttpMethod.Post, new Uri(cloud.BaseAddress, "/api/relay/v1/results")) { Content = new ByteArrayContent(bytes) };
        foreach (var pair in signatureHeaders) replay.Headers.Add(pair.Key, pair.Value);
        using (var response = await http.SendAsync(replay)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var wrongTenant = Signed(issued, bytes, "/api/relay/v1/results");
        wrongTenant.Headers.Remove("X-Relay-Tenant");
        wrongTenant.Headers.Add("X-Relay-Tenant", Guid.NewGuid().ToString());
        using (var response = await http.SendAsync(wrongTenant)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        SiteRelayRotated rotated;
        using (var scope = fixture.ScopeFor(tenant))
            rotated = await scope.ServiceProvider.GetRequiredService<RelayRegistry>().RotateAsync(connection, issued.RelayId, new(1), "operator", CancellationToken.None);
        using (var overlap = Signed(issued, bytes, "/api/relay/v1/results"))
        using (var response = await http.SendAsync(overlap)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var scope = fixture.ScopeFor(tenant))
            await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().SiteRelays.Where(x => x.RelayId == issued.RelayId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PreviousSecretExpiresAt, fixture.Clock.UtcNow.AddSeconds(-1)));
        using (var expired = Signed(issued, bytes, "/api/relay/v1/results"))
        using (var response = await http.SendAsync(expired)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var current = issued with { Secret = rotated.Secret };
        using (var fresh = Signed(current, bytes, "/api/relay/v1/results"))
        using (var response = await http.SendAsync(fresh)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var scope = fixture.ScopeFor(tenant))
            await scope.ServiceProvider.GetRequiredService<RelayRegistry>().RevokeAsync(connection, issued.RelayId, "operator", CancellationToken.None);
        using (var revoked = Signed(current, bytes, "/api/relay/v1/results"))
        using (var response = await http.SendAsync(revoked)) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [DatabaseFact]
    public async Task Offline_agent_is_down_and_failed_poll_does_not_advance_the_watermark()
    {
        await using var simulator = new Cx01Simulator();
        var (tenant, connection, _) = await RegisterAsync(simulator.BaseUri, new("https://product.example"), timeout: 1);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var before = await db.ConnectionPollWatermarks.SingleOrDefaultAsync(x => x.ConnectionId == connection && x.EntityType == "deliveries");
        if (before is null)
        {
            db.ConnectionPollWatermarks.Add(new() { TenantId = tenant, ConnectionId = connection, EntityType = "deliveries",
                CanonicalMessage = "DeliveryPosted", IntervalSeconds = 60, OverlapSeconds = 120, NextPollAt = fixture.Clock.UtcNow });
            await db.SaveChangesAsync();
        }
        await scope.ServiceProvider.GetRequiredService<ConnectionPollingService>().PollDueAsync(CancellationToken.None);
        var row = await db.ConnectionPollWatermarks.AsNoTracking().SingleAsync(x => x.ConnectionId == connection && x.EntityType == "deliveries");
        Assert.Null(row.Watermark);
        Assert.NotNull(row.LastError);
        await db.SiteRelays.Where(x => x.ConnectionId == connection).ExecuteUpdateAsync(setters =>
            setters.SetProperty(x => x.LastSeenAt, fixture.Clock.UtcNow.AddSeconds(-121)));
        var health = await scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>().RefreshAsync(connection, CancellationToken.None);
        Assert.Equal("down", health.Status);
        Assert.Equal("relay_offline", health.LastErrorCode);
        Assert.False(health.RelayConnected);
    }

    [DatabaseFact]
    public async Task Two_agents_share_a_relay_without_executing_a_request_twice()
    {
        await using var simulator = new Cx01Simulator();
        await using var cloud = await StartCloudAsync();
        var (tenant, connection, issued) = await RegisterAsync(simulator.BaseUri, cloud.BaseAddress);
        var config = issued.Configuration with { Concurrency = 1 };
        var secrets = new RelaySecrets(issued.Secret, "FICTIONAL_DEMO", "integration-user", "not-logged");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        var client = new RelayCloudClient(http, config, secrets, new TestTime(fixture.Clock));
        using var handler1 = new CountingLocalHandler(config);
        using var handler2 = new CountingLocalHandler(config);
        using var local1 = new RelayLocalClient(config, secrets, handler1);
        using var local2 = new RelayLocalClient(config, secrets, handler2);
        using var agent1 = new RelayAgent(config, client, local1, NullLogger<RelayAgent>.Instance, new TestTime(fixture.Clock));
        using var agent2 = new RelayAgent(config, client, local2, NullLogger<RelayAgent>.Instance, new TestTime(fixture.Clock));
        await agent1.StartAsync(CancellationToken.None);
        await agent2.StartAsync(CancellationToken.None);
        try
        {
            var tasks = Enumerable.Range(0, 8).Select(async _ =>
            {
                using var scope = fixture.ScopeFor(tenant);
                await Cx01RelayAcceptance.AssertOutboundAsync(scope.ServiceProvider, connection, scope.ServiceProvider.GetRequiredService<Cx01Client>(), simulator);
            });
            await Task.WhenAll(tasks);
            Assert.Equal(8, simulator.PatchAttempts);
            // Exactly-once is PatchAttempts above and the completed rows below. The handlers also see the agents' other
            // local traffic, so they only bound it. Leasing is not fair scheduling: one agent may legitimately take all
            // eight, so requiring both to take part made this test fail at random.
            Assert.True(handler1.Calls + handler2.Calls >= 8);
            using var check = fixture.ScopeFor(tenant);
            var rows = await check.ServiceProvider.GetRequiredService<IntegrationDbContext>().RelayRequests
                .AsNoTracking().Where(x => x.ConnectionId == connection).ToListAsync();
            Assert.Equal(8, rows.Count);
            Assert.All(rows, row => { Assert.NotNull(row.LeaseId); Assert.NotNull(row.CompletedAt); });
        }
        finally { await agent1.StopAsync(CancellationToken.None); await agent2.StopAsync(CancellationToken.None); }
    }

    [DatabaseFact]
    public async Task Wire_body_limit_is_enforced_before_parsing_or_authentication()
    {
        await using var cloud = await StartCloudAsync();
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(cloud.BaseAddress, "/api/relay/v1/results"))
            { Content = new ByteArrayContent(new byte[RelayRequestQueue.WireLimit + 1]) };
        request.Headers.ExpectContinue = true;
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [DatabaseFact]
    public async Task Site_mode_registration_requires_no_cloud_login_and_does_not_probe_an_unprovisioned_agent()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("relayreg");
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>().SeedGenericEntriesAsync(CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.ConnectorCatalogue.ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, true));
        var configured = new Cx01ConnectionSettings(simulator.BaseUri, "FICTIONAL_DEMO", "test", Transport: "site_relay");
        var request = new RegisterConnection(Guid.NewGuid(), "CX01", "Fictional site", Guid.NewGuid(),
            JsonSerializer.SerializeToElement(configured, IntegrationContracts.Json), []);
        var result = await scope.ServiceProvider.GetRequiredService<ConnectionCommands>().RegisterAsync(request, "operator", CancellationToken.None);
        Assert.Equal(0, simulator.RequestCount);
        Assert.Empty(await db.ConnectionCredentials.ToListAsync());
        var issued = await scope.ServiceProvider.GetRequiredService<RelayRegistry>().RegisterAsync(result.ConnectionId,
            new("Fictional relay", new("https://product.example")), "operator", CancellationToken.None);
        Assert.NotEqual(Guid.Empty, issued.RelayId);
    }

    [DatabaseFact]
    public async Task Optional_cloud_credentials_keep_the_cloud_session_owner()
    {
        await using var simulator = new Cx01Simulator();
        await using var cloud = await StartCloudAsync();
        var (tenant, connection, issued) = await RegisterAsync(simulator.BaseUri, cloud.BaseAddress, mode: "cloud");
        using var scope = fixture.ScopeFor(tenant);
        var encrypted = await scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>().EncryptAsync(tenant,
            connection, Cx01Client.CredentialPurpose, JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
                IntegrationContracts.Json), null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>().SaveAsync(encrypted, CancellationToken.None);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        var secrets = new RelaySecrets(issued.Secret, "", "", "");
        using var local = new RelayLocalClient(issued.Configuration, secrets);
        using var agent = new RelayAgent(issued.Configuration,
            new RelayCloudClient(http, issued.Configuration, secrets, new TestTime(fixture.Clock)), local,
            NullLogger<RelayAgent>.Instance, new TestTime(fixture.Clock));
        await agent.StartAsync(CancellationToken.None);
        try
        {
            _ = await scope.ServiceProvider.GetRequiredService<Cx01Client>().ProbeReleaseAsync(connection, CancellationToken.None);
            Assert.Equal(1, simulator.LoginCount);
        }
        finally { await agent.StopAsync(CancellationToken.None); }
    }

    [DatabaseFact]
    public async Task Queue_envelopes_are_encrypted_bound_to_the_request_and_accept_the_maximum_binary_body()
    {
        var tenant = await fixture.NewTenantAsync("relayenc");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var protection = scope.ServiceProvider.GetRequiredService<RelayPayloadProtection>();
        var id = Guid.NewGuid();
        var request = new RelayWork(id, Guid.Empty, "POST", "/declared", "",
            new Dictionary<string, string[]> { ["Cookie"] = ["fictional-session-secret"] },
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(RelayBounds.BodyLimit), fixture.Clock.UtcNow.AddMinutes(1));
        var encrypted = await protection.ProtectAsync(tenant, connection, id, "request", request, CancellationToken.None);
        Assert.DoesNotContain("fictional-session-secret", encrypted, StringComparison.Ordinal);
        Assert.True(encrypted.Length <= RelayBounds.WireLimit);
        var restored = await protection.UnprotectAsync<RelayWork>(tenant, connection, id, "request", encrypted, CancellationToken.None);
        Assert.Equal(request.Body, restored.Body);
        Assert.Equal(request.Headers["Cookie"], restored.Headers["Cookie"]);
        await Assert.ThrowsAsync<InvalidDataException>(() => protection.UnprotectAsync<RelayWork>(tenant, connection,
            Guid.NewGuid(), "request", encrypted, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => protection.UnprotectAsync<RelayWork>(tenant, connection,
            id, "result", encrypted, CancellationToken.None));
    }

    [RelayLiveFact]
    [Trait("Category", "RelayLive")]
    public async Task Live_site_credentials_survey_and_one_read_use_the_in_process_agent()
    {
        var input = Cx01RelayLiveInput.Read();
        await using var cloud = await StartCloudAsync();
        var (tenant, connection, issued) = await RegisterAsync(input.Service, cloud.BaseAddress);
        using (var configure = fixture.ScopeFor(tenant))
        {
            var settings = configure.ServiceProvider.GetRequiredService<Cx01Settings>();
            await settings.SetAsync(connection, (await settings.GetAsync(connection, CancellationToken.None)) with
                { CompanyDatabase = input.Secrets.Company }, CancellationToken.None);
        }
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        var secrets = input.Secrets with { RelaySecret = issued.Secret };
        // A self-signed service certificate is trusted only when pinned, exactly as an installed agent would be.
        var configuration = issued.Configuration with
        {
            ServiceCertificateSha256 = Environment.GetEnvironmentVariable("YOJANA_CX01_CERT_SHA256"),
        };
        using var local = new RelayLocalClient(configuration, secrets);
        using var agent = new RelayAgent(configuration,
            new RelayCloudClient(http, configuration, secrets, new TestTime(fixture.Clock)), local,
            NullLogger<RelayAgent>.Instance, new TestTime(fixture.Clock));
        await agent.StartAsync(CancellationToken.None);
        try
        {
            using var scope = fixture.ScopeFor(tenant);
            _ = await scope.ServiceProvider.GetRequiredService<Cx01SurveyService>().RunAsync(connection, new(), CancellationToken.None);
            await Cx01RelayLiveInput.ReadOneAsync(scope.ServiceProvider.GetRequiredService<Cx01Client>(), connection);
        }
        finally { await agent.StopAsync(CancellationToken.None); }
    }

    [DatabaseFact]
    public async Task Encrypted_signing_columns_cannot_authenticate_and_forced_rotation_drops_both_old_keys()
    {
        await using var simulator = new Cx01Simulator();
        await using var cloud = await StartCloudAsync("/customer%20one/product");
        var (tenant, connection, issued) = await RegisterAsync(simulator.BaseUri, cloud.BaseAddress);
        using var http = new HttpClient();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new RelayResult(Guid.NewGuid(), Guid.NewGuid(), 200,
            new Dictionary<string, string[]>(), []), JsonSerializerOptions.Web);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = await db.SiteRelays.SingleAsync(x => x.RelayId == issued.RelayId);
        Assert.DoesNotContain(issued.Secret, row.SigningKeyEnvelope, StringComparison.Ordinal);
        await db.Database.OpenConnectionAsync();
        var resource = $"integration:{tenant}:relay-auth:{issued.RelayId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive', @LockOwner='Session';");
        try
        {
            using (var forged = Signed(issued, bytes, "/api/relay/v1/results"))
            {
                var signature = RelaySigning.Sign(System.Text.Encoding.UTF8.GetBytes(row.SigningKeyEnvelope), issued.RelayId,
                    forged.Headers.GetValues("X-Relay-Timestamp").Single(), forged.Headers.GetValues("X-Relay-Nonce").Single(),
                    "POST", forged.RequestUri!.AbsolutePath, bytes, tenant.ToString());
                forged.Headers.Remove("X-Relay-Signature");
                forged.Headers.Add("X-Relay-Signature", signature);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                using var response = await http.SendAsync(forged, timeout.Token);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
        finally
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"EXEC sys.sp_releaseapplock @Resource={resource}, @LockOwner='Session';");
            await db.Database.CloseConnectionAsync();
        }
        using (var valid = Signed(issued, bytes, "/api/relay/v1/results"))
        using (var response = await http.SendAsync(valid)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var registry = scope.ServiceProvider.GetRequiredService<RelayRegistry>();
        var rotated = await registry.RotateAsync(connection, issued.RelayId, new(), "operator", CancellationToken.None);
        var missingReason = await Assert.ThrowsAsync<ConnectionRuleException>(() => registry.RotateAsync(connection,
            issued.RelayId, new(Force: true), "operator", CancellationToken.None));
        Assert.Equal("relay_rotation_reason_required", missingReason.Rule);
        var forced = await registry.RotateAsync(connection, issued.RelayId, new(Force: true, Reason: "Suspected disclosure"),
            "operator", CancellationToken.None);
        foreach (var old in new[] { issued, issued with { Secret = rotated.Secret } })
        {
            using var request = Signed(old, bytes, "/api/relay/v1/results");
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        using (var current = Signed(issued with { Secret = forced.Secret }, bytes, "/api/relay/v1/results"))
        using (var response = await http.SendAsync(current)) Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(row.PreviousSigningKeyEnvelope);
        Assert.True(await db.ConnectionEvents.AnyAsync(x => x.ConnectionId == connection && x.EventType == "RelayRotated"
            && x.DetailsJson.Contains("Suspected disclosure")));
    }

    [DatabaseFact]
    public async Task Direct_transport_rejects_authentication_but_keeps_relays_visible_and_revocable()
    {
        await using var simulator = new Cx01Simulator();
        await using var cloud = await StartCloudAsync();
        var (tenant, connection, issued) = await RegisterAsync(simulator.BaseUri, cloud.BaseAddress);
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, (await settings.GetAsync(connection, CancellationToken.None)) with { Transport = "direct" }, CancellationToken.None);
        using var http = new HttpClient();
        using var request = Signed(issued, JsonSerializer.SerializeToUtf8Bytes(new RelayPoll(1, "test", "fictional-host"),
            JsonSerializerOptions.Web), "/api/relay/v1/poll");
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("relay_transport_required", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        var registry = scope.ServiceProvider.GetRequiredService<RelayRegistry>();
        Assert.Single((await registry.ListAsync(connection, null, 10, CancellationToken.None)).Items);
        Assert.True((await registry.RevokeAsync(connection, issued.RelayId, "operator", CancellationToken.None)).Accepted);
    }

    [DatabaseFact]
    public async Task Poll_capacity_leases_only_the_requested_bounded_number()
    {
        await using var simulator = new Cx01Simulator();
        var (tenant, connection, issued) = await RegisterAsync(simulator.BaseUri, new("https://product.example"));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var protection = scope.ServiceProvider.GetRequiredService<RelayPayloadProtection>();
        for (var i = 0; i < 5; i++)
        {
            var id = Guid.NewGuid();
            var work = new RelayWork(id, Guid.Empty, "GET", "/local", "", new Dictionary<string, string[]>(), [], fixture.Clock.UtcNow.AddMinutes(1));
            db.RelayRequests.Add(new() { TenantId = tenant, ConnectionId = connection, RequestId = id,
                CreatedAt = fixture.Clock.UtcNow, ExpiresAt = work.ExpiresAtUtc,
                PayloadJson = await protection.ProtectAsync(tenant, connection, id, "request", work, CancellationToken.None) });
        }
        await db.SaveChangesAsync();
        var relay = await db.SiteRelays.SingleAsync(x => x.RelayId == issued.RelayId);
        var queue = scope.ServiceProvider.GetRequiredService<RelayRequestQueue>();
        Assert.Equal(3, (await queue.PollAsync(relay, new(3, "test", "fictional-host"), CancellationToken.None)).Items.Count);
        Assert.Equal(2, (await queue.PollAsync(relay, new(4, "test", "fictional-host"), CancellationToken.None)).Items.Count);
    }

    [DatabaseFact]
    public async Task First_report_and_changed_release_probe_webhook_availability_through_the_agent()
    {
        await using var simulator = new Cx01Simulator();
        await using var cloud = await StartCloudAsync();
        var (tenant, connection, issued) = await RegisterAsync(simulator.BaseUri, cloud.BaseAddress);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        foreach (var version in new[] { "1000340", "1000341" })
        {
            simulator.Release = version;
            simulator.WebhooksEnabled = version == "1000340";
            var secrets = new RelaySecrets(issued.Secret, "FICTIONAL_DEMO", "integration-user", "not-logged");
            using var local = new RelayLocalClient(issued.Configuration, secrets);
            using var agent = new RelayAgent(issued.Configuration, new(http, issued.Configuration, secrets, new TestTime(fixture.Clock)),
                local, NullLogger<RelayAgent>.Instance, new TestTime(fixture.Clock));
            await agent.StartAsync(CancellationToken.None);
            try
            {
                using var scope = fixture.ScopeFor(tenant);
                var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                while (!await db.SiteRelays.AnyAsync(x => x.RelayId == issued.RelayId && x.ErpVersion == version, timeout.Token))
                    await Task.Delay(100, timeout.Token);
                await scope.ServiceProvider.GetRequiredService<RelayMaintenance>().ProbeAsync(timeout.Token);
                var configured = await scope.ServiceProvider.GetRequiredService<Cx01Settings>().GetAsync(connection, timeout.Token);
                Assert.Equal(version, configured.Release);
                Assert.Equal(simulator.WebhooksEnabled, configured.WebhooksAvailable);
                Assert.Equal(version, await db.SiteRelays.Where(x => x.RelayId == issued.RelayId).Select(x => x.ProbedErpVersion).SingleAsync(timeout.Token));
            }
            finally { await agent.StopAsync(CancellationToken.None); }
        }
    }

    [DatabaseFact]
    public async Task Request_waits_and_empty_poll_holds_back_off_database_reads_and_locks()
    {
        await using var simulator = new Cx01Simulator();
        var (tenant, connection, issued) = await RegisterAsync(simulator.BaseUri, new("https://product.example"));
        using var scope = fixture.ScopeFor(tenant);
        using var activity = new QueueCommandObserver();
        var queue = scope.ServiceProvider.GetRequiredService<RelayRequestQueue>();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(simulator.BaseUri, "/local"));
        using var response = await queue.SendAsync(connection, request, 3, 128, issued.Configuration.Declaration, CancellationToken.None);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.InRange(activity.ResultReads, 1, 7);
        var relay = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().SiteRelays.SingleAsync(x => x.RelayId == issued.RelayId);
        activity.ResetLocks();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queue.PollAsync(relay,
            new(1, "test", "fictional-host"), timeout.Token));
        Assert.InRange(activity.QueueLocks, 1, 7);
    }

    [DatabaseFact]
    public async Task Poll_has_a_tiny_pre_auth_cap_and_unauthenticated_attempts_are_rate_limited()
    {
        await using var cloud = await StartCloudAsync();
        using var http = new HttpClient();
        var url = new Uri(cloud.BaseAddress, "/api/relay/v1/poll");
        using (var response = await http.PostAsync(url, new ByteArrayContent(new byte[16385])))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ChunkedContent(new byte[16385]) })
        {
            request.Headers.ExpectContinue = true;
            using var response = await http.SendAsync(request);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        }
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 121; i++)
        {
            using var response = await http.PostAsync(url, new ByteArrayContent([]));
            statuses.Add(response.StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    private async Task<(TenantId Tenant, Guid Connection, SiteRelayIssued Issued)> RegisterAsync(Uri service, Uri product, int timeout = 15, string mode = "site")
    {
        var tenant = await fixture.NewTenantAsync("relay");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(service, "FICTIONAL_DEMO", "10.0 FP 2602", WebhooksAvailable: true, Transport: "site_relay", RelayCredentials: mode, RelayRequestTimeoutSeconds: timeout), CancellationToken.None);
        var issued = await scope.ServiceProvider.GetRequiredService<RelayRegistry>().RegisterAsync(connection,
            new("Fictional site relay", product), "operator", CancellationToken.None);
        return (tenant, connection, issued);
    }

    private HttpRequestMessage Signed(SiteRelayIssued relay, byte[] bytes, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, RelayProductUrl.Resolve(relay.Configuration.ProductUrl, path)) { Content = new ByteArrayContent(bytes) };
        RelaySigning.Stamp(request, relay.Configuration, relay.Secret, bytes, new DateTimeOffset(fixture.Clock.UtcNow));
        return request;
    }

    private async Task<RelayCloudHost> StartCloudAsync(string prefix = "")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(fixture.Services.GetRequiredService<IConfiguration>().AsEnumerable());
        builder.Configuration["Platform:MigrateOnStartup"] = "false";
        builder.Configuration["Platform:AutoBuildMessageStorage"] = "false";
        builder.Configuration["Platform:ServiceName"] = "relay-acceptance-" + Guid.NewGuid().ToString("N")[..8];
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.AddYojanaPlatform();
        builder.AddSerialisationModule();
        builder.AddIntegrationModule();
        builder.Services.AddSingleton<IClock>(fixture.Clock);
        var app = builder.Build();
        if (prefix.Length > 0) app.UsePathBase(Uri.UnescapeDataString(prefix));
        app.UseRateLimiter();
        app.MapRelayEndpoints();
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(app, new(address.TrimEnd('/') + prefix + "/"));
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private sealed class QueueCommandObserver : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly ConcurrentBag<IDisposable> subscriptions = [];
        private int reads;
        private int locks;
        public int ResultReads => Volatile.Read(ref reads);
        public int QueueLocks => Volatile.Read(ref locks);
        public QueueCommandObserver() => subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        public void ResetLocks() => Interlocked.Exchange(ref locks, 0);
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore") subscriptions.Add(listener.Subscribe(this));
        }
        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key != RelationalEventId.CommandExecuted.Name || value.Value is not CommandExecutedEventData data) return;
            var command = data.Command;
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && command.CommandText.Contains("[r].[result_json]", StringComparison.Ordinal))
                Interlocked.Increment(ref reads);
            if (command.CommandText.Contains("sp_getapplock", StringComparison.Ordinal) && command.Parameters.Cast<System.Data.Common.DbParameter>()
                .Any(parameter => parameter.Value is string text && text.Contains("relay-queue:", StringComparison.Ordinal)))
                Interlocked.Increment(ref locks);
        }
        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { foreach (var subscription in subscriptions) subscription.Dispose(); }
    }

    private sealed class ChunkedContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
    }

    private sealed class CountingLocalHandler(RelayAgentConfiguration configuration) : DelegatingHandler(new SocketsHttpHandler())
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath != configuration.Declaration.Session.LoginPath) Interlocked.Increment(ref calls);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed record RelayCloudHost(WebApplication App, Uri BaseAddress) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }
    private sealed class TestTime(MutableTestClock clock) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(clock.UtcNow); }
}

public sealed class RelayLiveFactAttribute : FactAttribute
{
    public RelayLiveFactAttribute()
    {
        if (new[] { "YOJANA_TEST_SQL", "YOJANA_CX01_URL", "YOJANA_CX01_USER", "YOJANA_CX01_PASSWORD" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))))
            Skip = "RelayLive requires YOJANA_TEST_SQL and the CX01 URL, user and password environment variables.";
    }
}
