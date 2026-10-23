using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Notifications;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Webhooks;
using Yojana.Platform.Contracts.Tenancy;
using WebHost = Yojana.Web.WebHost;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class NotificationEndpointTests(IntegrationHostFixture fixture, ITestOutputHelper output)
{
    [DatabaseFact]
    public async Task Signed_batch_is_acknowledged_without_inline_translation_and_meets_the_dev_latency_target()
    {
        var tenant = await fixture.NewTenantAsync("hook");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var source = "source-" + Guid.NewGuid().ToString("N")[..8];
        var secret = RandomNumberGenerator.GetBytes(32);
        await ConfigureWebhookAsync(tenant, connection, source, secret);

        await using var host = await WebProcess.StartAsync();
        using var client = new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(30) };
        var warmupBody = Batch(tenant, connection, source, 1);
        using (var warmup = Request(connection, tenant, secret, warmupBody))
        using (var warmupResponse = await client.SendAsync(warmup))
            Assert.Equal(HttpStatusCode.Accepted, warmupResponse.StatusCode);
        var body = Batch(tenant, connection, source, 1_024);
        using var request = Request(connection, tenant, secret, body);
        var clientTimer = Stopwatch.StartNew();
        using var response = await client.SendAsync(request);
        clientTimer.Stop();
        var result = await response.Content.ReadFromJsonAsync<WebhookNotificationResponse>(IntegrationContracts.Json);
        var serverAcknowledgement = ServerAcknowledgement(response);
        output.WriteLine("Fresh 1,024-event notification batch: server {0:F1} ms; client {1:F1} ms",
            serverAcknowledgement.TotalMilliseconds, clientTimer.Elapsed.TotalMilliseconds);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(result);
        Assert.Equal((1_024, 0, 0), (result.Accepted, result.AlreadyProcessed, result.Superseded));
        Assert.True(serverAcknowledgement < TimeSpan.FromMilliseconds(200),
            $"The server acknowledged the measured 1,024-event batch in {serverAcknowledgement.TotalMilliseconds:F1} ms; the development target is below 200 ms.");

        var observedCount = 0;
        for (var attempt = 0; attempt < 200; attempt++)
        {
            try
            {
                using var check = fixture.ScopeFor(tenant);
                var db = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
                observedCount = await db.InboundMessages.AsNoTracking()
                    .CountAsync(x => x.ConnectionId == connection);
                if (observedCount == 1_025) break;
            }
            catch (SqlException exception) when (exception.Number == 1205)
            {
                // The shared Express test host may be releasing old durable envelopes while this eventual assertion
                // polls. A deadlock victim here is a transient read, not a failed notification acceptance.
            }
            await Task.Delay(25);
        }
        Assert.True(observedCount == 1_025,
            $"Expected the warm-up plus 1,024 accepted rows, observed {observedCount}.{Environment.NewLine}{host.Details()}");

        var unfinishedCount = observedCount;
        for (var attempt = 0; attempt < 1_200; attempt++)
        {
            try
            {
                using var check = fixture.ScopeFor(tenant);
                var db = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
                unfinishedCount = await db.InboundMessages.AsNoTracking().CountAsync(x => x.ConnectionId == connection
                    && (x.TranslationStatus == InboundTranslationStatus.Pending
                        || x.TranslationStatus == InboundTranslationStatus.Processing));
                if (unfinishedCount == 0) break;
            }
            catch (SqlException exception) when (exception.Number == 1205)
            {
                // Translation and the shared test host can briefly deadlock this diagnostic read. The translation
                // owns the work; allow it to finish so this test never leaks load into the next suite scenario.
            }
            await Task.Delay(25);
        }
        Assert.True(unfinishedCount == 0,
            $"Expected the notification batch to settle before test teardown, observed {unfinishedCount} unfinished rows.{Environment.NewLine}{host.Details()}");
    }

    private static TimeSpan ServerAcknowledgement(HttpResponseMessage response)
    {
        var value = Assert.Single(response.Headers.GetValues("Server-Timing"));
        var prefix = IntegrationNotificationEndpoints.AcknowledgementTimingMetric + ";dur=";
        Assert.StartsWith(prefix, value, StringComparison.Ordinal);
        return TimeSpan.FromMilliseconds(double.Parse(value[prefix.Length..], CultureInfo.InvariantCulture));
    }

    private async Task ConfigureWebhookAsync(TenantId tenant, Guid connection, string source, byte[] secret)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ConnectionNotificationSources.Add(new()
        {
            TenantId = tenant, ConnectionId = connection, Source = source, Enabled = true,
            UpdatedAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var credential = await protector.EncryptAsync(tenant, connection, IntegrationNotificationEndpoints.SigningCredentialPurpose,
            secret, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>()
            .SaveAsync(credential, CancellationToken.None);
    }

    private static byte[] Batch(TenantId tenant, Guid connection, string source, int count)
    {
        var at = new DateTimeOffset(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);
        var events = new List<WebhookNotification>(count);
        for (var index = 0; index < count; index++)
        {
            var external = $"HOOK-{index:D4}-{Guid.NewGuid():N}";
            var envelope = new CanonicalEnvelope("1.0", Guid.CreateVersion7(), tenant.Value, connection, "CX01",
                "item", external, new("1", OpenEnum.Known("sequence")), at, at.AddSeconds(1),
                OpenEnum.Known("webhook"), OpenEnum.Known("complete"), $"raw/item/{external}/1");
            var canonical = new CanonicalMessage<ItemUpserted>(envelope, nameof(ItemUpserted),
                new(external, "Fictional webhook item", OpenEnum.Known("serial"), "EA", false));
            events.Add(new(JsonSerializer.SerializeToElement(canonical, IntegrationContracts.Json),
                JsonSerializer.SerializeToElement(new { index })));
        }
        return JsonSerializer.SerializeToUtf8Bytes(new WebhookNotificationBatch(source, events), IntegrationContracts.Json);
    }

    private static HttpRequestMessage Request(Guid connection, TenantId tenant, byte[] secret, byte[] body)
    {
        var timestamp = DateTime.UtcNow;
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/integration/connections/{connection:D}/notifications")
        {
            Content = new ByteArrayContent(body),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(IntegrationNotificationEndpoints.TenantHeader, tenant.ToString());
        request.Headers.Add(IntegrationNotificationEndpoints.TimestampHeader, timestamp.ToString("O"));
        request.Headers.Add(IntegrationNotificationEndpoints.SignatureHeader, Convert.ToHexString(
            HmacSha256WebhookSignatureVerifier.Sign(IntegrationNotificationEndpoints.SignedContent(timestamp, body), secret)));
        return request;
    }

    internal sealed class WebProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly List<string> lines;
        public Uri BaseAddress { get; }

        public string Details()
        {
            lock (lines) return string.Join(Environment.NewLine, lines.TakeLast(120));
        }

        private WebProcess(Process process, List<string> lines, Uri baseAddress)
        {
            this.process = process;
            this.lines = lines;
            BaseAddress = baseAddress;
        }

        public static async Task<WebProcess> StartAsync()
        {
            var port = FreePort();
            var lines = new List<string>();
            var start = new ProcessStartInfo("dotnet", $"\"{typeof(WebHost).Assembly.Location}\"")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            start.Environment["Platform__ConnectionString"] = IntegrationHostFixture.ConnectionString;
            start.Environment["Serialisation__ImportFiles__DatabaseConnectionString"] = IntegrationHostFixture.ImportFilesConnectionString;
            start.Environment["Serialisation__ImportFiles__FileRoot"] = Path.Combine(Path.GetTempPath(), "yojana-integration-http-imports");
            start.Environment["Serialisation__ImportFiles__ArchiveStores__0__Name"] = "folder-archive";
            start.Environment["Serialisation__ImportFiles__ArchiveStores__0__Kind"] = "folder";
            start.Environment["Serialisation__ImportFiles__ArchiveStores__0__Path"] = Path.Combine(Path.GetTempPath(), "yojana-integration-http-archive");
            start.Environment["Serialisation__ImportFiles__ArchiveStores__1__Name"] = "database-archive";
            start.Environment["Serialisation__ImportFiles__ArchiveStores__1__Kind"] = "database";
            var archiveConnection = new SqlConnectionStringBuilder(IntegrationHostFixture.ConnectionString)
            {
                InitialCatalog = new SqlConnectionStringBuilder(IntegrationHostFixture.ConnectionString).InitialCatalog + "_archive",
            };
            start.Environment["Serialisation__ImportFiles__ArchiveStores__1__ConnectionString"] = archiveConnection.ConnectionString;
            start.Environment["Platform__MigrateOnStartup"] = "false";
            start.Environment["Platform__AutoBuildMessageStorage"] = "false";
            start.Environment["Platform__StartScheduler"] = "false";
            start.Environment["Platform__ServiceName"] = "yojana-integration-notification-http-test";
            start.Environment["Platform__SignIn__AuditHmacKey"] = "notification-http-tests-only-hmac-key";
            start.Environment["Integration__Security__KeyEncryption__Provider"] = "Local";
            start.Environment["Integration__Security__LocalKeyEncryption__Current__KeyId"] = "integration-tests";
            start.Environment["Integration__Security__LocalKeyEncryption__Current__KeyVersion"] = "1";
            start.Environment["Integration__Security__LocalKeyEncryption__Current__KeyBase64"] =
                Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray());
            var process = new Process { StartInfo = start };
            process.OutputDataReceived += (_, args) => { if (args.Data is { } line) lock (lines) lines.Add(line); };
            process.ErrorDataReceived += (_, args) => { if (args.Data is { } line) lock (lines) lines.Add(line); };
            Assert.True(process.Start());
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var host = new WebProcess(process, lines, new($"http://127.0.0.1:{port}"));
            using var probe = new HttpClient { BaseAddress = host.BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline && !process.HasExited)
            {
                try
                {
                    using var response = await probe.GetAsync("/health");
                    if (response.StatusCode == HttpStatusCode.OK) return host;
                }
                catch (HttpRequestException) { }
                catch (TaskCanceledException) { }
                await Task.Delay(100);
            }
            await host.DisposeAsync();
            string details;
            lock (lines) details = string.Join(Environment.NewLine, lines.TakeLast(80));
            throw new Xunit.Sdk.XunitException("The notification HTTP test host did not start." + Environment.NewLine + details);
        }

        public async ValueTask DisposeAsync()
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            process.Dispose();
        }

        private static int FreePort()
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
    }
}
