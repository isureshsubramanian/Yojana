using System.Net;
using System.Text.Json;
using Yojana.Relay.Contracts;

namespace Yojana.Relay;

/// <summary>Local HTTP and session ownership only; no document mapping or business decisions.</summary>
public sealed class RelayLocalClient : IDisposable
{
    private readonly RelayAgentConfiguration configuration;
    private readonly RelaySecrets secrets;
    private readonly HttpClient client;
    private readonly RelayAllowList allowList;
    private readonly Func<string, string, Uri> resolveTarget;
    private readonly SemaphoreSlim sessionLock = new(1, 1);
    public string? ErpVersion { get; private set; }
    private DateTime sessionExpires;
    private DateTime keepAliveAt;
    private TimeSpan sessionTimeout = TimeSpan.FromMinutes(30);

    public RelayLocalClient(RelayAgentConfiguration configuration, RelaySecrets secrets, HttpMessageHandler? handler = null,
        Func<string, string, Uri>? resolveTarget = null)
    {
        this.configuration = configuration;
        this.secrets = secrets;
        this.resolveTarget = resolveTarget ?? ((path, query) => new Uri(configuration.ServiceUrl.GetLeftPart(UriPartial.Authority) + path + query));
        allowList = new(configuration.Declaration);
        client = new(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = configuration.Credentials == "site", CookieContainer = new(),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<RelayResult> ExecuteAsync(RelayWork work, CancellationToken ct)
    {
        try
        {
            if (work.Body.Length > RelayBounds.BodyLimit) return Result(413);
            if (!allowList.Allows(work.Method, work.RelativePath, work.Query)
                || configuration.Credentials == "site" && work.RelativePath == configuration.Declaration.Session.LoginPath)
                return Result(403, true);
            // Session ownership is declared by the CX01 client in the cloud and this agent's local configuration.
            // In site mode the cloud must skip login and cookies: forwarding either would expose site credentials
            // or replace the agent-owned session. Changing one side alone breaks authentication on every call.
            if (configuration.Credentials == "site") await EnsureSessionAsync(false, ct);
            using var first = await SendAsync(work, ct);
            if (first.StatusCode == HttpStatusCode.Unauthorized && configuration.Credentials == "site")
            {
                await EnsureSessionAsync(true, ct);
                using var retried = await SendAsync(work, ct);
                return await ResultAsync(retried, ct);
            }
            return await ResultAsync(first, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Failure(work); }

        RelayResult Result(int status, bool denied = false) => new(work.RequestId, work.LeaseId, status,
            new Dictionary<string, string[]>(), [], denied);
        async Task<RelayResult> ResultAsync(HttpResponseMessage response, CancellationToken token) =>
            new(work.RequestId, work.LeaseId, (int)response.StatusCode,
                response.Headers.Concat(response.Content.Headers)
                    .Where(pair => configuration.Declaration.ResponseHeaders.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)
                        && (configuration.Credentials == "cloud" || !pair.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.OrdinalIgnoreCase),
                await RelayBounds.ReadAsync(await response.Content.ReadAsStreamAsync(token), RelayBounds.BodyLimit, token));
    }

    private Task<HttpResponseMessage> SendAsync(RelayWork work, CancellationToken ct)
    {
        var target = resolveTarget(work.RelativePath, work.Query);
        var request = new HttpRequestMessage(new(work.Method), target) { Content = new ByteArrayContent(work.Body) };
        foreach (var pair in work.Headers.Where(pair => configuration.Declaration.RequestHeaders.Contains(pair.Key,
                     StringComparer.OrdinalIgnoreCase) && (configuration.Credentials == "cloud"
                     || !pair.Key.Equals("Cookie", StringComparison.OrdinalIgnoreCase))))
            if (!request.Headers.TryAddWithoutValidation(pair.Key, pair.Value))
                request.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
        return SendAndDisposeRequestAsync(request, ct);
    }

    private async Task<HttpResponseMessage> SendAndDisposeRequestAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request) return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task EnsureSessionAsync(bool force, CancellationToken ct)
    {
        await sessionLock.WaitAsync(ct);
        try
        {
            if (!force && sessionExpires > DateTime.UtcNow.AddSeconds(15)) return;
            var declaration = configuration.Declaration.Session;
            using var request = new HttpRequestMessage(HttpMethod.Post,
                new Uri(configuration.ServiceUrl.GetLeftPart(UriPartial.Authority) + declaration.LoginPath))
            {
                Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [declaration.CompanyField] = secrets.Company,
                    [declaration.UserField] = secrets.UserName,
                    [declaration.PasswordField] = secrets.Password,
                }), System.Text.Encoding.UTF8, "application/json"),
            };
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await RelayBounds.ReadAsync(
                await response.Content.ReadAsStreamAsync(ct), RelayBounds.BodyLimit, ct));
            if (declaration.VersionField is { } field && body.RootElement.TryGetProperty(field, out var version)
                && version.ValueKind == JsonValueKind.String) ErpVersion = version.GetString();
            if (body.RootElement.TryGetProperty(declaration.TimeoutField, out var timeout)
                && timeout.TryGetInt32(out var minutes)) sessionTimeout = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 120));
            sessionExpires = DateTime.UtcNow + sessionTimeout;
            keepAliveAt = DateTime.UtcNow + sessionTimeout / 2;
        }
        finally { sessionLock.Release(); }
    }

    public async Task KeepAliveAsync(CancellationToken ct)
    {
        if (configuration.Credentials != "site") return;
        if (sessionExpires == default) { await EnsureSessionAsync(false, ct); return; }
        if (keepAliveAt > DateTime.UtcNow) return;
        var declaration = configuration.Declaration.Session;
        await EnsureSessionAsync(false, ct);
        using var response = await client.GetAsync(new Uri(configuration.ServiceUrl.GetLeftPart(UriPartial.Authority)
            + declaration.KeepAlivePath), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) await EnsureSessionAsync(true, ct);
        else response.EnsureSuccessStatusCode();
        sessionExpires = DateTime.UtcNow + sessionTimeout;
        keepAliveAt = DateTime.UtcNow + sessionTimeout / 2;
    }

    public static RelayResult Failure(RelayWork work) => new(work.RequestId, work.LeaseId, 503,
        new Dictionary<string, string[]> { ["Content-Type"] = ["application/json"] },
        System.Text.Encoding.UTF8.GetBytes("{\"error\":\"relay_local_fault\"}"));

    public void Dispose() { client.Dispose(); sessionLock.Dispose(); }
}
