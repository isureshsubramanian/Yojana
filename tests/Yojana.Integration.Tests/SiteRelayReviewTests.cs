using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Relays;
using Yojana.Relay;
using Yojana.Relay.Contracts;

namespace Yojana.Integration.Tests;

public sealed class SiteRelayReviewTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeProvider TestTime = new FixedTime();
    private static RelayAgentConfiguration Configuration() => new(Guid.NewGuid(), Guid.NewGuid(),
        new("https://product.example/customer%20one/product/"), new("http://127.0.0.1:9000"),
        new([new("GET", "/local/.*")], new("/session", "/local/", "company", "user", "password", "timeout", [], "release"),
            [], ["Content-Type"], []), Credentials: "cloud", Concurrency: 1);
    private static RelaySecrets Secrets() => new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), "Fictional", "user", "password");
    private static RelayWork Work() => new(Guid.NewGuid(), Guid.NewGuid(), "GET", "/local/read", "",
        new Dictionary<string, string[]>(), [], TestNow.UtcDateTime.AddMinutes(1));

    [Theory]
    [InlineData("regex")]
    [InlineData("uri")]
    [InlineData("oversize")]
    public async Task Work_faults_return_a_stable_server_error_and_the_next_work_can_run(string fault)
    {
        var config = Configuration();
        using var local = new RelayLocalClient(config, Secrets(), new DelegateHandler((_, _) => throw fault switch
        {
            "regex" => new RegexMatchTimeoutException(), "uri" => new UriFormatException(), _ => new InvalidDataException(),
        }));
        var result = await local.ExecuteAsync(Work(), CancellationToken.None);
        Assert.Equal(503, result.Status);
        Assert.Contains("relay_local_fault", System.Text.Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_real_allow_list_timeout_is_contained_per_work_item()
    {
        var config = Configuration();
        config = config with { Declaration = config.Declaration with { Paths = [new("GET", "/(a+)+")] } };
        using var local = new RelayLocalClient(config, Secrets(), new DelegateHandler((_, _) => throw new InvalidOperationException()));
        var result = await local.ExecuteAsync(Work() with { RelativePath = "/" + new string('a', 10000) + "!" }, CancellationToken.None);
        Assert.Equal(503, result.Status);
    }

    [Fact]
    public async Task Invalid_local_url_is_contained_before_http_is_called()
    {
        var config = Configuration();
        var calls = 0;
        using var local = new RelayLocalClient(config, Secrets(), new DelegateHandler((_, _) =>
        { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }),
            resolveTarget: (_, _) => throw new UriFormatException());
        Assert.Equal(503, (await local.ExecuteAsync(Work(), CancellationToken.None)).Status);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Null_poll_items_and_upload_faults_do_not_stop_the_worker_or_repeat_local_work()
    {
        var config = Configuration();
        var secrets = Secrets();
        var work = Work();
        var polls = 0;
        var uploads = 0;
        var localCalls = 0;
        var uploaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new DelegateHandler(async (request, ct) =>
        {
            Assert.StartsWith("/customer%20one/product/api/relay/v1/", request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            if (request.RequestUri.AbsolutePath.EndsWith("/poll", StringComparison.Ordinal))
            {
                var count = Interlocked.Increment(ref polls);
                if (count == 1) return Json(new { items = (object?)null });
                if (count > 2) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Json(new RelayPollReply([work]));
            }
            if (Interlocked.Increment(ref uploads) == 1) throw new InvalidDataException("oversize upload response");
            var result = await JsonSerializer.DeserializeAsync<RelayResult>(await request.Content!.ReadAsStreamAsync(ct), JsonSerializerOptions.Web, ct);
            Assert.Equal(503, result!.Status);
            Assert.Contains("relay_local_fault", System.Text.Encoding.UTF8.GetString(result.Body), StringComparison.Ordinal);
            uploaded.TrySetResult();
            return Json(new { accepted = true });
        }));
        using var local = new RelayLocalClient(config, secrets, new DelegateHandler((_, _) =>
        { Interlocked.Increment(ref localCalls); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
        using var agent = new RelayAgent(config, new(http, config, secrets, TestTime), local, new RecordingLogger<RelayAgent>(), TestTime);
        await agent.StartAsync(CancellationToken.None);
        try
        {
            await uploaded.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(1, localCalls);
            Assert.Equal(2, uploads);
            Assert.Null(agent.ExecuteTask!.Exception);
        }
        finally { await agent.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Malformed_login_during_keep_alive_is_logged_and_retried()
    {
        var config = Configuration() with { Credentials = "site" };
        var logins = 0;
        var loggedIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var local = new RelayLocalClient(config, Secrets(), new DelegateHandler((_, _) =>
        {
            if (Interlocked.Increment(ref logins) == 1) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("[invalid") });
            loggedIn.TrySetResult();
            return Task.FromResult(Json(new { timeout = 30, release = "fictional-release" }));
        }));
        using var http = new HttpClient(new DelegateHandler(async (_, ct) =>
        { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(new RelayPollReply([])); }));
        var logger = new RecordingLogger<RelayAgent>();
        using var agent = new RelayAgent(config, new(http, config, Secrets(), TestTime), local, logger, TestTime);
        await agent.StartAsync(CancellationToken.None);
        try
        {
            await logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await loggedIn.Task.WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Null(agent.ExecuteTask!.Exception);
        }
        finally { await agent.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Listener_bind_failure_is_logged_and_retried_without_stopping()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var bind = $"http://127.0.0.1:{((IPEndPoint)socket.LocalEndpoint).Port}/";
        socket.Stop();
        using var occupied = new HttpListener();
        occupied.Prefixes.Add(bind);
        occupied.Start();
        var config = Configuration() with { WebhookBind = bind };
        using var http = new HttpClient(new DelegateHandler((_, _) => Task.FromResult(Json(
            new RelayWebhookReply(200, new Dictionary<string, string[]>(), [])))));
        var logger = new RecordingLogger<RelayWebhookListener>();
        using var listener = new RelayWebhookListener(config, new(http, config, Secrets(), TestTime), logger);
        await listener.StartAsync(CancellationToken.None);
        try
        {
            await logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(listener.ExecuteTask!.IsCompleted);
            occupied.Close();
            await Task.Delay(TimeSpan.FromSeconds(6));
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync(bind);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally { await listener.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task Expired_leases_are_logged_as_possible_clock_skew_and_never_execute()
    {
        var config = Configuration();
        var first = true;
        using var http = new HttpClient(new DelegateHandler(async (_, ct) =>
        {
            if (!first) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            first = false;
            return Json(new RelayPollReply([Work() with { ExpiresAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) }]));
        }));
        using var local = new RelayLocalClient(config, Secrets(), new DelegateHandler((_, _) => throw new InvalidOperationException()));
        var logger = new RecordingLogger<RelayAgent>();
        using var agent = new RelayAgent(config, new(http, config, Secrets(), TestTime), local, logger, TestTime);
        await agent.StartAsync(CancellationToken.None);
        try { await logger.Warning.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Null(agent.ExecuteTask!.Exception); }
        finally { await agent.StopAsync(CancellationToken.None); }
    }

    [WindowsFact]
    public void Windows_secret_acl_is_explicit_and_has_only_the_three_required_principals()
    {
        if (!OperatingSystem.IsWindows()) return;
        var security = RelaySecretFile.WindowsSecurity();
        Assert.True(security.AreAccessRulesProtected);
        var rules = security.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier))
            .Cast<System.Security.AccessControl.FileSystemAccessRule>().ToList();
        Assert.Equal(3, rules.Count);
        var principals = new List<string>();
        foreach (var rule in rules)
        {
            Assert.False(rule.IsInherited);
            Assert.Equal(System.Security.AccessControl.AccessControlType.Allow, rule.AccessControlType);
            principals.Add(rule.IdentityReference.Value);
        }
        Assert.Contains("S-1-5-18", principals);
        Assert.Contains("S-1-5-32-544", principals);
        Assert.Contains(principals, value => value.StartsWith("S-1-5-80-", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("http://127.0.0.1:9001/", true)]
    [InlineData("http://10.1.2.3:9001/hints/", true)]
    [InlineData("https://203.0.113.4:9001/", true)]
    [InlineData("http://[::1]:9001/", true)]
    [InlineData("http://localhost:9001/", false)]
    [InlineData("http://127.0.0.1:9001", false)]
    [InlineData("http://0.0.0.0:9001/", false)]
    [InlineData("http://203.0.113.4:9001/", false)]
    [InlineData("https://user@127.0.0.1:9001/", false)]
    [InlineData("http://127.0.0.1:9001/?x=/", false)]
    public void Cloud_and_agent_use_the_same_webhook_validation(string url, bool valid)
    {
        var settings = new Cx01ConnectionSettings(Configuration().ServiceUrl, "Fictional", "test", RelayWebhookUrl: url);
        if (valid) { settings.Validate(); RelayWebhookListener.ValidateBind(url); }
        else
        {
            Assert.Throws<InvalidOperationException>(() => settings.Validate());
            Assert.Throws<InvalidOperationException>(() => RelayWebhookListener.ValidateBind(url));
        }
    }

    [Fact]
    public async Task Signing_preserves_the_escaped_prefix_and_binds_the_tenant_header()
    {
        var config = Configuration();
        var secret = Secrets().RelaySecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, RelayProductUrl.Resolve(config.ProductUrl, "/api/relay/v1/poll"));
        RelaySigning.Stamp(request, config, secret, [], TestNow);
        Assert.Equal("/customer%20one/product/api/relay/v1/poll", request.RequestUri!.AbsolutePath);
        var body = await new ByteArrayContent([]).ReadAsByteArrayAsync();
        Assert.True(RelayAuthentication.Verify(RelaySigning.KeyBytes(secret), config.RelayId,
            request.Headers.GetValues("X-Relay-Timestamp").Single(), request.Headers.GetValues("X-Relay-Nonce").Single(),
            "POST", request.RequestUri.AbsolutePath, body, request.Headers.GetValues("X-Relay-Signature").Single(), config.TenantId.ToString("D")));
        Assert.False(RelayAuthentication.Verify(RelaySigning.KeyBytes(secret), config.RelayId,
            request.Headers.GetValues("X-Relay-Timestamp").Single(), request.Headers.GetValues("X-Relay-Nonce").Single(),
            "POST", request.RequestUri.AbsolutePath, body, request.Headers.GetValues("X-Relay-Signature").Single(), Guid.NewGuid().ToString("D")));
    }

    [Fact]
    public void Secret_provisioning_requires_an_explicit_configuration_path()
    {
        Assert.Throws<ArgumentException>(() => RelayCommandLine.ConfigurationPath(["--protect-secrets"]));
        Assert.Equal(Path.GetFullPath("relay.json"), RelayCommandLine.ConfigurationPath(["--protect-secrets", "relay.json"]));
    }

    private sealed class FixedTime : TimeProvider
    { public override DateTimeOffset GetUtcNow() => TestNow; }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web)) };
    private sealed class DelegateHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public TaskCompletionSource Warning { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (logLevel >= LogLevel.Warning) Warning.TrySetResult(); }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows ACL and DPAPI assertions require a Windows host.";
    }
}
