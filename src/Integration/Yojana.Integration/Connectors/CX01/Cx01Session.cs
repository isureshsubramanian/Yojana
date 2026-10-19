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
    IConnectionRetryJitter jitter) : ICx01Transport, IDisposable
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
        var rateLimited = new ConnectionRateLimitedHandler(workerKey, profiles, workers, clock, delay, jitter)
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

    public void Invalidate()
    {
        SessionCookie = null;
        RouteCookie = null;
        ExpiresAtUtc = DateTime.MinValue;
    }
}

public sealed class Cx01RemoteException(HttpStatusCode statusCode, string operation)
    : Exception($"CX01 {operation} failed with HTTP status {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
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
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        using var metadata = await GetAsync(connectionId, Cx01Protocol.Root, cancellationToken);
        var release = metadata.RootElement.TryGetProperty("Version", out var value)
            ? value.GetString() ?? configured.Release : configured.Release;
        var webhooks = false;
        if (Cx01Capabilities.ReleaseFeaturePack(release) >= 2602)
        {
            try
            {
                using var probe = await GetAsync(connectionId,
                    Cx01Protocol.Root + Cx01Protocol.EventSubscriptions + "?$top=1", cancellationToken);
                webhooks = probe.RootElement.ValueKind == JsonValueKind.Object;
            }
            catch (Cx01RemoteException exception) when (exception.StatusCode is HttpStatusCode.NotFound
                or HttpStatusCode.Forbidden)
            {
                webhooks = false;
            }
        }
        return new(release, webhooks);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(Guid connectionId, HttpMethod method,
        string relativePath, object? body, bool idempotent, CancellationToken cancellationToken)
    {
        var key = new ConnectionWorkerKey(tenant.TenantId, connectionId);
        var configured = await settings.GetAsync(connectionId, cancellationToken);
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
        request.Headers.TryAddWithoutValidation("Cookie",
            $"{Cx01Protocol.SessionCookie}={state.SessionCookie}; {Cx01Protocol.RouteCookie}={state.RouteCookie}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Options.Set(ConnectionRateLimitedHandler.IdempotentRequest, idempotent);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, IntegrationContracts.Json),
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
                    }), Encoding.UTF8, "application/json"),
                };
                using var response = await transport.SendAsync(key, request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    throw new Cx01RemoteException(response.StatusCode, "login");
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
        _ = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        throw new Cx01RemoteException(response.StatusCode, operation);
    }
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
