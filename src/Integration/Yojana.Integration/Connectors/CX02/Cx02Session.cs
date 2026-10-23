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

namespace Yojana.Integration.Connectors.CX02;

public interface ICx02Transport
{
    Task<HttpResponseMessage> SendAsync(ConnectionWorkerKey key, HttpRequestMessage request,
        CancellationToken cancellationToken);
}

public sealed class Cx02Transport(
    ConnectionThrottleProfiles profiles,
    IConnectionThrottleWorkerRegistry workers,
    IClock clock,
    IConnectionThrottleDelay delay,
    IConnectionRetryJitter jitter,
    IConnectionBusyObserver? busy = null) : ICx02Transport, IDisposable
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, HttpMessageInvoker> clients = new();

    public Task<HttpResponseMessage> SendAsync(ConnectionWorkerKey key, HttpRequestMessage request,
        CancellationToken cancellationToken) => Client(key).SendAsync(request, cancellationToken);

    public void Dispose()
    {
        foreach (var client in clients.Values) client.Dispose();
        clients.Clear();
    }

    private HttpMessageInvoker Client(ConnectionWorkerKey key) => clients.GetOrAdd(key, workerKey =>
        new HttpMessageInvoker(new ConnectionRateLimitedHandler(workerKey, profiles, workers, clock, delay, jitter,
            busy: busy)
        {
            InnerHandler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(15),
                ConnectTimeout = TimeSpan.FromSeconds(30),
                UseCookies = false,
            },
        }, true));
}

public sealed class Cx02TokenRegistry
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, Cx02TokenState> states = new();
    internal Cx02TokenState For(ConnectionWorkerKey key) => states.GetOrAdd(key, _ => new());
}

internal sealed class Cx02TokenState
{
    public SemaphoreSlim RefreshLock { get; } = new(1, 1);
    public string? AccessToken { get; set; }
    public DateTime ExpiresAtUtc { get; set; }

    public void Invalidate()
    {
        AccessToken = null;
        ExpiresAtUtc = DateTime.MinValue;
    }
}

public sealed class Cx02RemoteException(HttpStatusCode statusCode, string operation,
    string? remoteCode = null, string? remoteMessage = null)
    : Exception(remoteMessage is null
        ? $"CX02 {operation} failed with HTTP status {(int)statusCode}."
        : $"CX02 {operation} failed: {remoteMessage}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? RemoteCode { get; } = remoteCode;
    public string? RemoteMessage { get; } = remoteMessage;
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;
    public bool IsAuthenticationFailure => StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
        || RemoteCode?.StartsWith("Authentication_", StringComparison.Ordinal) == true;
}

public sealed class Cx02Client(
    ITenantContext tenant,
    IClock clock,
    Cx02Settings settings,
    IConnectionCredentialStore credentials,
    ConnectionCredentialProtector protector,
    Cx02TokenRegistry tokens,
    ICx02Transport transport)
{
    public const string CredentialPurpose = "cx02_client";

    public async Task<JsonDocument> GetAsync(Guid connectionId, Uri uri,
        CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(connectionId, HttpMethod.Get, uri, null,
            idempotent: true, cancellationToken);
        await EnsureSuccessAsync(response, "read", cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
            cancellationToken: cancellationToken);
    }

    public async Task<JsonDocument?> SendJsonAsync(Guid connectionId, HttpMethod method, Uri uri, object? body,
        bool idempotent, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        using var response = await SendAuthenticatedAsync(connectionId, method, uri, body, idempotent,
            cancellationToken, headers);
        await EnsureSuccessAsync(response, "request", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return bytes.Length == 0 ? null : JsonDocument.Parse(bytes);
    }

    public async Task<IReadOnlyList<JsonElement>> ReadCollectionAsync(Guid connectionId, Uri first,
        int pageSize, int pageCap, CancellationToken cancellationToken)
    {
        if (pageSize is < 1 || pageSize > pageCap || pageCap > 20_000)
            throw new InvalidOperationException("The CX02 collection page size is outside its configured cap.");
        var result = new List<JsonElement>();
        Uri? next = first;
        var pages = 0;
        while (next is not null)
        {
            if (++pages > pageCap)
                throw new InvalidOperationException("The CX02 collection exceeded its configured page cap.");
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.TryAddWithoutValidation("Prefer", "odata.maxpagesize="
                + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var response = await SendAuthenticatedRequestAsync(connectionId, request, true, cancellationToken);
            await EnsureSuccessAsync(response, "page read", cancellationToken);
            using var page = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken),
                cancellationToken: cancellationToken);
            if (page.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
                result.AddRange(values.EnumerateArray().Select(item => item.Clone()));
            if (page.RootElement.TryGetProperty("@odata.nextLink", out var link)
                && !string.IsNullOrWhiteSpace(link.GetString()))
                next = Uri.TryCreate(link.GetString(), UriKind.Absolute, out var absolute)
                    ? absolute : new Uri(next, link.GetString());
            else next = null;
        }
        return result;
    }

    public Task<HttpResponseMessage> SendAuthenticatedRequestAsync(Guid connectionId, HttpRequestMessage request,
        bool idempotent, CancellationToken cancellationToken) =>
        SendAuthenticatedAsync(connectionId, request.Method, request.RequestUri!, null, idempotent,
            cancellationToken, request.Headers.ToDictionary(item => item.Key, item => string.Join(',', item.Value),
                StringComparer.OrdinalIgnoreCase));

    public async Task<string> NotificationClientStateAsync(Guid connectionId,
        CancellationToken cancellationToken)
    {
        var stored = await credentials.GetCurrentAsync(tenant.TenantId, connectionId,
            CredentialPurpose, cancellationToken)
            ?? throw new InvalidOperationException("CX02 notification credentials are not configured.");
        var plaintext = await protector.DecryptAsync(stored, cancellationToken);
        try
        {
            var secret = JsonSerializer.Deserialize<Cx02ClientCredential>(plaintext, IntegrationContracts.Json)
                ?? throw new InvalidOperationException("CX02 client credentials are invalid.");
            return string.IsNullOrWhiteSpace(secret.ClientState)
                ? throw new InvalidOperationException("CX02 notification client state is not configured.")
                : secret.ClientState;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(Guid connectionId, HttpMethod method, Uri uri,
        object? body, bool idempotent, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var key = new ConnectionWorkerKey(tenant.TenantId, connectionId);
        await EnsureTokenAsync(key, configured, cancellationToken);
        var response = await SendOnceAsync(key, method, uri, body, idempotent, headers, cancellationToken);
        if (response.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)) return response;
        response.Dispose();
        tokens.For(key).Invalidate();
        await EnsureTokenAsync(key, configured, cancellationToken);
        return await SendOnceAsync(key, method, uri, body, idempotent, headers, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(ConnectionWorkerKey key, HttpMethod method, Uri uri,
        object? body, bool idempotent, IReadOnlyDictionary<string, string>? headers,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.For(key).AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Options.Set(ConnectionRateLimitedHandler.IdempotentRequest, idempotent);
        if (headers is not null)
            foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Cx02Protocol.Json),
                Encoding.UTF8, "application/json");
        return await transport.SendAsync(key, request, cancellationToken);
    }

    private async Task EnsureTokenAsync(ConnectionWorkerKey key, Cx02ConnectionSettings configured,
        CancellationToken cancellationToken)
    {
        var state = tokens.For(key);
        var refreshAt = clock.UtcNow.AddSeconds(configured.RefreshTokenEarlySeconds);
        if (state.AccessToken is not null && state.ExpiresAtUtc > refreshAt) return;
        await state.RefreshLock.WaitAsync(cancellationToken);
        try
        {
            refreshAt = clock.UtcNow.AddSeconds(configured.RefreshTokenEarlySeconds);
            if (state.AccessToken is not null && state.ExpiresAtUtc > refreshAt) return;
            var stored = await credentials.GetCurrentAsync(key.TenantId, key.ConnectionId, CredentialPurpose,
                cancellationToken) ?? throw new InvalidOperationException("CX02 client credentials are not configured.");
            var plaintext = await protector.DecryptAsync(stored, cancellationToken);
            try
            {
                var secret = JsonSerializer.Deserialize<Cx02ClientCredential>(plaintext, IntegrationContracts.Json)
                    ?? throw new InvalidOperationException("CX02 client credentials are invalid.");
                using var request = new HttpRequestMessage(HttpMethod.Post, Cx02Protocol.TokenUri(configured))
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["grant_type"] = "client_credentials",
                        ["client_id"] = secret.ClientId,
                        ["client_secret"] = secret.ClientSecret,
                        ["scope"] = Cx02Protocol.Scope,
                    }),
                };
                request.Options.Set(ConnectionRateLimitedHandler.IdempotentRequest, true);
                using var response = await transport.SendAsync(key, request, cancellationToken);
                await EnsureSuccessAsync(response, "token request", cancellationToken);
                using var token = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
                state.AccessToken = token.RootElement.GetProperty("access_token").GetString()
                    ?? throw new InvalidOperationException("CX02 token response omitted the access token.");
                var seconds = token.RootElement.GetProperty("expires_in").GetInt32();
                state.ExpiresAtUtc = clock.UtcNow.AddSeconds(Math.Max(1, seconds));
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally { state.RefreshLock.Release(); }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        string? code = null;
        string? message = null;
        try
        {
            using var body = JsonDocument.Parse(bytes);
            var root = body.RootElement;
            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                code = Text(error, "code");
                message = Text(error, "message");
            }
            else if (error.ValueKind == JsonValueKind.String)
            {
                code = error.GetString();
                message = Text(root, "error_description");
            }
            else
            {
                code = Text(root, "code");
                message = Text(root, "message");
            }
        }
        catch (JsonException) { }
        throw new Cx02RemoteException(response.StatusCode, operation, Cap(code, 120), Cap(message, 2_000));
    }

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;

    private static string? Cap(string? value, int length) => value is null || value.Length <= length
        ? value : value[..length];
}
