using Yojana.Integration.Connections;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Connectors.CX01;

public interface ICx01Transport
{
    Task<HttpResponseMessage> SendAsync(ConnectionWorkerKey workerKey, HttpRequestMessage request,
        CancellationToken cancellationToken);
}

/// <summary>Composes the existing rate-limit handler with a pooled BCL HTTP transport for each connection.</summary>
public sealed class Cx01Transport(
    ConnectionThrottleProfiles profiles,
    IConnectionThrottleWorkerRegistry workers,
    IClock clock,
    IConnectionThrottleDelay delay,
    IConnectionRetryJitter jitter,
    IConnectionBusyObserver? busy = null) : ICx01Transport, IDisposable
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, HttpMessageInvoker> clients = new();

    public Task<HttpResponseMessage> SendAsync(ConnectionWorkerKey workerKey, HttpRequestMessage request,
        CancellationToken cancellationToken) => Client(workerKey).SendAsync(request, cancellationToken);

    public void Dispose()
    {
        foreach (var client in clients.Values) client.Dispose();
        clients.Clear();
    }

    private HttpMessageInvoker Client(ConnectionWorkerKey key) => clients.GetOrAdd(key, workerKey =>
    {
        var rateLimited = new ConnectionRateLimitedHandler(workerKey, profiles, workers, clock, delay, jitter, busy: busy)
        {
            InnerHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                ConnectTimeout = TimeSpan.FromSeconds(30),
                UseCookies = false,
            },
        };
        return new HttpMessageInvoker(rateLimited, true);
    });
}

public sealed class Cx01SessionRegistry
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, Cx01SessionState> states = new();
    internal Cx01SessionState For(ConnectionWorkerKey key) => states.GetOrAdd(key, _ => new());
}

internal sealed class Cx01SessionState
{
    public SemaphoreSlim LoginLock { get; } = new(1, 1);
    public string? SessionCookie { get; set; }
    public string? RouteCookie { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public string? Release { get; set; }

    public void Invalidate()
    {
        SessionCookie = null;
        RouteCookie = null;
        ExpiresAtUtc = DateTime.MinValue;
    }
}

public sealed class Cx01RemoteException(
    HttpStatusCode statusCode,
    string operation,
    string? remoteCode = null,
    string? remoteMessage = null,
    IReadOnlyList<string>? details = null)
    : Exception(remoteMessage is null
        ? $"CX01 {operation} failed with HTTP status {(int)statusCode}."
        : $"CX01 {operation} failed: {remoteMessage}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? RemoteCode { get; } = remoteCode;
    public string? RemoteMessage { get; } = remoteMessage;
    public IReadOnlyList<string> Details { get; } = details ?? [];
    public bool IsRecordGone => StatusCode == HttpStatusCode.NotFound;
    public bool IsBusinessRejection => (int)StatusCode is >= 400 and < 500
        && StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests);
    public bool IsTransient => StatusCode == HttpStatusCode.RequestTimeout
        || StatusCode == HttpStatusCode.TooManyRequests || (int)StatusCode >= 500;

    public string BusinessMessage(int maximumLength = 2_000)
    {
        var value = RemoteMessage ?? Message;
        return value.Length <= maximumLength ? value : value[..maximumLength];
    }
}

public sealed class Cx01Client(
    ITenantContext tenant,
    IClock clock,
    Cx01Settings settings,
    IConnectionCredentialStore credentials,
    ConnectionCredentialProtector protector,
    Cx01SessionRegistry sessions,
    ICx01Transport transport)
{
    public const string CredentialPurpose = "cx01_login";

    public async Task<JsonDocument> GetAsync(Guid connectionId, string relativePath,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(connectionId, HttpMethod.Get, relativePath, null,
            true, cancellationToken);
        await EnsureSuccessAsync(response, "read", cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    public async Task<string> GetTextAsync(Guid connectionId, string relativePath,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(connectionId, HttpMethod.Get, relativePath, null,
            true, cancellationToken);
        await EnsureSuccessAsync(response, "read", cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public async Task<JsonDocument?> SendJsonAsync(Guid connectionId, HttpMethod method, string relativePath,
        object? body, bool idempotent, CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(connectionId, method, relativePath, body, idempotent,
            cancellationToken);
        await EnsureSuccessAsync(response, "write", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    public async Task KeepAliveAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        if (SiteSession(configured)) return;
        var state = sessions.For(new(tenant.TenantId, connectionId));
        if (state.SessionCookie is null || state.ExpiresAtUtc - clock.UtcNow >
            TimeSpan.FromMinutes(configured.KeepAliveMinutes)) return;
        using var response = await SendAuthenticatedAsync(connectionId, HttpMethod.Get,
            Cx01Protocol.Root, null, true, cancellationToken);
        await EnsureSuccessAsync(response, "keep-alive", cancellationToken);
    }

    public async Task<Cx01ReleaseProbe> ProbeReleaseAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var release = await ReleaseAsync(connectionId, cancellationToken);
        var metadata = await GetTextAsync(connectionId, Cx01Protocol.Metadata, cancellationToken);
        var supported = metadata.Contains(Cx01Protocol.EventSubscriptions, StringComparison.Ordinal);
        var webhooks = false;
        if (supported)
            try
            {
                using var admin = await SendJsonAsync(connectionId, HttpMethod.Post,
                    Cx01Protocol.Root + Cx01Protocol.AdminInfo, new { }, true, cancellationToken);
                webhooks = admin is not null && Text(admin.RootElement, "EnableWebhook") is "tYES" or "Y";
            }
            catch (Cx01RemoteException exception) when (exception.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.Forbidden)
            {
                webhooks = false;
            }
        return new(release, webhooks);
    }

    public async Task<string> ReleaseAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var key = new ConnectionWorkerKey(tenant.TenantId, connectionId);
        if (SiteSession(configured)) return configured.Release;
        await EnsureSessionAsync(key, configured, cancellationToken);
        return sessions.For(key).Release ?? configured.Release;
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(Guid connectionId, HttpMethod method,
        string relativePath, object? body, bool idempotent, CancellationToken cancellationToken)
    {
        var key = new ConnectionWorkerKey(tenant.TenantId, connectionId);
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        if (SiteSession(configured))
            return await SendOnceAsync(key, configured, method, relativePath, body, idempotent, cancellationToken);
        await EnsureSessionAsync(key, configured, cancellationToken);
        var response = await SendOnceAsync(key, configured, method, relativePath, body, idempotent,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        sessions.For(key).Invalidate();
        await EnsureSessionAsync(key, configured, cancellationToken);
        return await SendOnceAsync(key, configured, method, relativePath, body, idempotent, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(ConnectionWorkerKey key,
        Cx01ConnectionSettings configured, HttpMethod method, string relativePath, object? body,
        bool idempotent, CancellationToken cancellationToken)
    {
        var state = sessions.For(key);
        using var request = new HttpRequestMessage(method, Resolve(configured.ServiceUri, relativePath));
        if (!SiteSession(configured)) request.Headers.TryAddWithoutValidation("Cookie",
            $"{Cx01Protocol.SessionCookie}={state.SessionCookie}; {Cx01Protocol.RouteCookie}={state.RouteCookie}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Options.Set(ConnectionRateLimitedHandler.IdempotentRequest, idempotent);
        if (body is Cx01MultipartUpload upload) request.Content = upload.CreateContent();
        else if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Cx01Protocol.Json),
                Encoding.UTF8, "application/json");
        var response = await transport.SendAsync(key, request, cancellationToken);
        if (response.IsSuccessStatusCode)
            state.ExpiresAtUtc = clock.UtcNow.AddMinutes(30);
        return response;
    }

    private async Task EnsureSessionAsync(ConnectionWorkerKey key, Cx01ConnectionSettings configured,
        CancellationToken cancellationToken)
    {
        var state = sessions.For(key);
        if (state.SessionCookie is not null && state.RouteCookie is not null
            && state.ExpiresAtUtc > clock.UtcNow.AddSeconds(15)) return;
        await state.LoginLock.WaitAsync(cancellationToken);
        try
        {
            if (state.SessionCookie is not null && state.RouteCookie is not null
                && state.ExpiresAtUtc > clock.UtcNow.AddSeconds(15)) return;
            var stored = await credentials.GetCurrentAsync(key.TenantId, key.ConnectionId, CredentialPurpose,
                cancellationToken) ?? throw new InvalidOperationException("CX01 login credentials are not configured.");
            var plaintext = await protector.DecryptAsync(stored, cancellationToken);
            try
            {
                var secret = JsonSerializer.Deserialize<Cx01Credential>(plaintext, IntegrationContracts.Json)
                    ?? throw new InvalidOperationException("CX01 login credentials are invalid.");
                using var request = new HttpRequestMessage(HttpMethod.Post, Resolve(configured.ServiceUri,
                    Cx01Protocol.Login))
                {
                    Content = new StringContent(JsonSerializer.Serialize(new
                    {
                        CompanyDB = configured.CompanyDatabase,
                        secret.UserName,
                        secret.Password,
                    }, Cx01Protocol.Json), Encoding.UTF8, "application/json"),
                };
                using var response = await transport.SendAsync(key, request, cancellationToken);
                await EnsureSuccessAsync(response, "login", cancellationToken);
                state.SessionCookie = Cookie(response, Cx01Protocol.SessionCookie);
                state.RouteCookie = Cookie(response, Cx01Protocol.RouteCookie);
                if (state.SessionCookie is null || state.RouteCookie is null)
                {
                    state.Invalidate();
                    throw new InvalidOperationException("CX01 login did not return both required session cookies.");
                }
                var timeout = 30;
                using var document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                if (document.RootElement.TryGetProperty("SessionTimeout", out var timeoutValue)
                    && timeoutValue.TryGetInt32(out var readTimeout)) timeout = readTimeout;
                state.Release = Text(document.RootElement, "Version");
                state.ExpiresAtUtc = clock.UtcNow.AddMinutes(Math.Max(1, timeout));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
        finally
        {
            state.LoginLock.Release();
        }
    }

    // Session ownership is declared here and in RelayLocalClient through the provisioning contract.
    // Site credentials cannot cross the customer boundary, so the cloud must skip login and cookies.
    // Changing one side alone replaces the agent-owned session and breaks all relay authentication.
    private static bool SiteSession(Cx01ConnectionSettings configured) =>
        configured.Transport == "site_relay" && configured.RelayCredentials == "site";

    private static Uri Resolve(Uri serviceUri, string relativePath) =>
        new(new Uri(serviceUri.GetLeftPart(UriPartial.Authority)), relativePath);

    private static string? Cookie(HttpResponseMessage response, string name)
    {
        foreach (var header in response.Headers.TryGetValues("Set-Cookie", out var values) ? values : [])
        {
            var prefix = name + "=";
            var part = header.Split(';', 2)[0];
            if (part.StartsWith(prefix, StringComparison.Ordinal)) return part[prefix.Length..];
        }
        return null;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        string? code = null;
        string? message = null;
        var details = new List<string>();
        try
        {
            using var body = JsonDocument.Parse(bytes);
            if (body.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                // A site relay reports its own faults as {"error":"<code>","kind":"<fault kind>"}.
                code = error.GetString();
                message = body.RootElement.TryGetProperty("kind", out var kind) ? kind.ToString() : null;
            }
            else if (body.RootElement.TryGetProperty("error", out error) && error.ValueKind == JsonValueKind.Object)
            {
                code = Text(error, "code");
                message = Text(error, "message");
                if (error.TryGetProperty("details", out var remoteDetails)
                    && remoteDetails.ValueKind == JsonValueKind.Array)
                    foreach (var detail in remoteDetails.EnumerateArray())
                        if (Text(detail, "message") is { Length: > 0 } value)
                            details.Add(value.Length <= 1_000 ? value : value[..1_000]);
            }
        }
        catch (JsonException) { }
        throw new Cx01RemoteException(response.StatusCode, operation, Cap(code, 120), Cap(message, 2_000), details);
    }

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;

    private static string? Cap(string? value, int length) => value is null || value.Length <= length
        ? value : value[..length];
}

public sealed class Cx01SessionMaintenance(
    ITenantContext tenant,
    IntegrationDbContext db,
    Cx01Settings settings,
    Cx01Client client)
{
    public async Task KeepAliveTenantAsync(CancellationToken cancellationToken)
    {
        var connections = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectorCode == Cx01Capabilities.ConnectorCode && x.Status == ConnectionStatus.Active)
            .Select(x => x.ConnectionId).ToListAsync(cancellationToken);
        foreach (var connection in connections)
        {
            try
            {
                await using var activity = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, connection, cancellationToken);
                if (!await db.Connections.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
                    && x.ConnectionId == connection && x.Status == ConnectionStatus.Active, cancellationToken)) continue;
                _ = await settings.GetAsync(connection, cancellationToken);
                await client.KeepAliveAsync(connection, cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // A connection being onboarded may not have settings or credentials yet.
            }
        }
    }
}

[DisallowConcurrentExecution]
public sealed class Cx01SessionKeepAliveJob(ITenantContextSetter setter, Cx01SessionMaintenance maintenance)
    : TenantJob(setter)
{
    public const string JobName = "integration.cx01-session-keep-alive";
    public const string Cron = "0 0/10 * * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        maintenance.KeepAliveTenantAsync(cancellationToken);
}
