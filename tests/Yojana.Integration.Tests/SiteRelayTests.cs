using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Security;
using System.Net;
using System.Security.Cryptography;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Relays;
using Yojana.Relay;
using Yojana.Relay.Contracts;

namespace Yojana.Integration.Tests;

public sealed class SiteRelayTests
{
    private static readonly Cx01ConnectionSettings Settings = new(new("http://127.0.0.1:9000"), "Fictional", "test", Transport: "site_relay");

    [Fact]
    public void Signatures_bind_every_field_and_body_using_the_secret()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var verifier = RelaySigning.KeyBytes(secret);
        var relay = Guid.NewGuid();
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var timestamp = new DateTimeOffset(now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = RelaySigning.Sign(verifier, relay, timestamp, "nonce", "POST", "/api/relay/v1/poll", [1, 2]);
        Assert.True(RelayAuthentication.Verify(verifier, relay, timestamp, "nonce", "POST", "/api/relay/v1/poll", [1, 2], signature));
        Assert.False(RelayAuthentication.Verify(verifier, Guid.NewGuid(), timestamp, "nonce", "POST", "/api/relay/v1/poll", [1, 2], signature));
        Assert.False(RelayAuthentication.Verify(verifier, relay, timestamp, "other", "POST", "/api/relay/v1/poll", [1, 2], signature));
        Assert.False(RelayAuthentication.Verify(verifier, relay, timestamp, "nonce", "GET", "/api/relay/v1/poll", [1, 2], signature));
        Assert.False(RelayAuthentication.Verify(verifier, relay, timestamp, "nonce", "POST", "/api/relay/v1/results", [1, 2], signature));
        Assert.False(RelayAuthentication.Verify(verifier, relay, timestamp, "nonce", "POST", "/api/relay/v1/poll", [2, 1], signature));
        Assert.False(RelayAuthentication.Verify(verifier, relay, timestamp, "nonce", "POST", "/api/relay/v1/poll", [1, 2], "invalid"));
    }

    [Theory]
    [InlineData(-301, false)]
    [InlineData(-300, true)]
    [InlineData(300, true)]
    [InlineData(301, false)]
    [InlineData(long.MaxValue, false)]
    [InlineData(long.MinValue, false)]
    public void Signatures_have_a_five_minute_skew_window(long offset, bool expected)
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var timestamp = offset is long.MaxValue or long.MinValue ? offset : new DateTimeOffset(now).ToUnixTimeSeconds() + offset;
        Assert.Equal(expected, RelayAuthentication.IsTimely(timestamp, now));
    }

    [Theory]
    [InlineData("GET", "/admin", "")]
    [InlineData("POST", "//external.example/", "")]
    [InlineData("GET", "/../admin", "")]
    [InlineData("GET", "/%2fadmin", "")]
    [InlineData("GET", "/\\admin", "")]
    public void Undeclared_and_ambiguous_paths_are_denied(string method, string path, string query) =>
        Assert.False(new RelayAllowList(Cx01RelayProfile.Declaration(Settings)).Allows(method, path, query));

    [Fact]
    public async Task Denied_paths_and_oversize_bodies_never_touch_the_local_service()
    {
        var handler = new CountingHandler();
        var configuration = new RelayAgentConfiguration(Guid.NewGuid(), Guid.NewGuid(), new("https://product.example"),
            Settings.ServiceUri, Cx01RelayProfile.Declaration(Settings));
        using var local = new RelayLocalClient(configuration, new("", "Fictional", "user", "password"), handler);
        var work = new RelayWork(Guid.NewGuid(), Guid.NewGuid(), "GET", "/admin", "", new Dictionary<string, string[]>(), [], new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(403, (await local.ExecuteAsync(work, CancellationToken.None)).Status);
        Assert.Equal(413, (await local.ExecuteAsync(work with { Body = new byte[RelayBounds.BodyLimit + 1] }, CancellationToken.None)).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Bounded_stream_read_stops_before_accepting_an_oversize_body()
    {
        using var input = new MemoryStream(new byte[1025]);
        await Assert.ThrowsAsync<InvalidDataException>(() => RelayBounds.ReadAsync(input, 1024, CancellationToken.None));
    }

    [Fact]
    public void Secret_store_uses_protection_and_restricts_file_permissions()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "secret-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "relay.secrets");
        try
        {
            IRelaySecretProtection protection = OperatingSystem.IsWindows() ? new DpapiRelaySecretProtection() : new FileRelaySecretProtection();
            var store = new RelaySecretFile(protection);
            var secrets = new RelaySecrets("relay", "Fictional", "user", "secret");
            store.Write(path, secrets);
            Assert.Equal(secrets, store.Read(path));
            Assert.Throws<IOException>(() => store.Write(path, secrets));
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead);
                Assert.Throws<InvalidOperationException>(() => store.Read(path));
            }
            else Assert.DoesNotContain("secret", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("http://0.0.0.0:9001/")]
    [InlineData("http://203.0.113.4:9001/")]
    [InlineData("http://localhost:9001/")]
    public void Plaintext_webhooks_require_an_explicit_lan_bind(string bind) =>
        Assert.Throws<InvalidOperationException>(() => RelayWebhookListener.ValidateBind(bind));

    [Fact]
    public void Relay_endpoints_use_signatures_and_administration_keeps_the_existing_role_policies()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<RelayRegistry>();
        var app = builder.Build();
        app.MapRelayEndpoints();
        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToList();
        var agent = endpoints.Where(endpoint => endpoint.RoutePattern.RawText!.StartsWith("/api/relay/", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, agent.Count);
        Assert.All(agent, endpoint =>
        {
            Assert.NotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Empty(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
        });
        var admin = endpoints.Except(agent).ToList();
        Assert.Equal(4, admin.Count);
        Assert.All(admin, endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            policy => policy.Policy == YojanaPolicy.IntegrationOperations));
        Assert.All(admin.Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("POST")),
            endpoint => Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
                policy => policy.Policy == YojanaPolicy.TenantAdministrator));
    }

    [Fact]
    public async Task Failed_result_upload_is_retried_without_repeating_the_local_request()
    {
        var now = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var configuration = new RelayAgentConfiguration(Guid.NewGuid(), Guid.NewGuid(), new("https://product.example"),
            Settings.ServiceUri, Cx01RelayProfile.Declaration(Settings), Credentials: "cloud", Concurrency: 1);
        var secrets = new RelaySecrets(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "", "", "");
        var work = new RelayWork(Guid.NewGuid(), Guid.NewGuid(), "GET", configuration.Declaration.Session.KeepAlivePath,
            "", new Dictionary<string, string[]>(), [], now.UtcDateTime.AddMinutes(1));
        var cloudHandler = new RetryingResultHandler(work);
        using var http = new HttpClient(cloudHandler);
        var localHandler = new CountingHandler();
        using var local = new RelayLocalClient(configuration, secrets, localHandler);
        using var agent = new RelayAgent(configuration, new RelayCloudClient(http, configuration, secrets, new FixedTime(now)),
            local, Microsoft.Extensions.Logging.Abstractions.NullLogger<RelayAgent>.Instance, new FixedTime(now));
        await agent.StartAsync(CancellationToken.None);
        try
        {
            await cloudHandler.Uploaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(1, localHandler.Calls);
            Assert.Equal(2, cloudHandler.ResultAttempts);
        }
        finally { await agent.StopAsync(CancellationToken.None); }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class RetryingResultHandler(RelayWork work) : HttpMessageHandler
    {
        private int polls;
        public int ResultAttempts { get; private set; }
        public TaskCompletionSource Uploaded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/poll", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref polls) > 1) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new(HttpStatusCode.OK) { Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(
                    new RelayPollReply([work]), System.Text.Json.JsonSerializerOptions.Web)) };
            }
            ResultAttempts++;
            if (ResultAttempts == 1) return new(HttpStatusCode.ServiceUnavailable);
            Uploaded.TrySetResult();
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"accepted\":true}") };
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }
    }
}
