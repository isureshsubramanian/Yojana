using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connections;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Connectors.CX04;

public interface ICx04Transport
{
    Task<HttpResponseMessage> SendAsync(ConnectionWorkerKey key, HttpRequestMessage request,
        CancellationToken cancellationToken);
}

public sealed class Cx04Transport(ConnectionThrottleProfiles profiles,
    IConnectionThrottleWorkerRegistry workers, IClock clock, IConnectionThrottleDelay delay,
    IConnectionRetryJitter jitter, IConnectionBusyObserver? busy = null) : ICx04Transport, IDisposable
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, HttpMessageInvoker> clients = new();

    public Task<HttpResponseMessage> SendAsync(ConnectionWorkerKey key, HttpRequestMessage request,
        CancellationToken cancellationToken) => Client(key).SendAsync(request, cancellationToken);

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

    public void Dispose()
    {
        foreach (var client in clients.Values) client.Dispose();
        clients.Clear();
    }
}

public sealed class Cx04TokenRegistry
{
    private readonly ConcurrentDictionary<ConnectionWorkerKey, SemaphoreSlim> locks = new();
    internal SemaphoreSlim For(ConnectionWorkerKey key) => locks.GetOrAdd(key, _ => new(1, 1));
}

internal sealed record Cx04TokenSecret(string AccessToken, string RefreshToken, DateTime AccessExpiresAt);
internal sealed record Cx04TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn,
    string? TokenType, string? Scope);

public sealed class Cx04RemoteException(HttpStatusCode statusCode, string operation, string? code = null)
    : Exception($"CX04 {operation} failed with HTTP status {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string? Code { get; } = code;
    public bool IsNotFound => StatusCode == HttpStatusCode.NotFound;
    public bool IsRejected => StatusCode is HttpStatusCode.BadRequest or (HttpStatusCode)422;
}

public sealed class Cx04Client(ITenantContext tenant, IClock clock, IntegrationDbContext db,
    Cx04Settings settings, IConnectionCredentialStore credentials, ConnectionCredentialProtector protector,
    Cx04TokenRegistry tokens, ICx04Transport transport, IConnectionAuditSink audit,
    Cx04SignInNotifications notifications)
{
    public const string TokenPurpose = "cx04_oauth_tokens";
    public const string PkcePurpose = "cx04_pkce_verifier";

    public async Task<JsonDocument> GetAsync(Guid connectionId, Uri uri, CancellationToken ct) =>
        await SendJsonAsync(connectionId, HttpMethod.Get, uri, null, true, ct)
        ?? throw new InvalidOperationException("CX04 read returned no response body.");

    public async Task<IReadOnlyList<JsonElement>> ReadCollectionAsync(Guid connectionId, Uri uri,
        int pageSize, int pageCap, CancellationToken ct)
    {
        var result = new List<JsonElement>();
        for (var skip = 0; skip < pageCap; skip += pageSize)
        {
            var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            var pageUri = new Uri(uri + separator + "$top=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "&$skip=" + skip.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using var document = await GetAsync(connectionId, pageUri, ct);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new Cx04RemoteException(HttpStatusCode.OK, "collection read");
            var rows = document.RootElement.EnumerateArray().Select(value => value.Clone()).ToList();
            result.AddRange(rows);
            if (rows.Count < pageSize) break;
            if (result.Count >= pageCap)
                throw new InvalidOperationException("The CX04 collection exceeded its configured page cap.");
        }
        return result;
    }

    public Task<JsonDocument?> PutAsync(Guid connectionId, Uri uri, object payload,
        bool createOnly, CancellationToken ct) => SendJsonAsync(connectionId, HttpMethod.Put, uri, payload,
            true, ct, createOnly ? ("If-None-Match", "*") : null);

    public async Task<Uri?> PutFileAsync(Guid connectionId, Uri uri, byte[] bytes, string? comment,
        CancellationToken ct)
    {
        using var response = await SendAuthenticatedAsync(connectionId, token =>
        {
            var request = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new ByteArrayContent(bytes),
            };
            request.Content.Headers.ContentType = new("application/octet-stream");
            request.Headers.Authorization = new("Bearer", token);
            if (!string.IsNullOrWhiteSpace(comment)) request.Headers.TryAddWithoutValidation("PX-CbFileComment", comment);
            return request;
        }, ct);
        await EnsureSuccessAsync(response, "file upload", ct);
        return response.Headers.Location;
    }

    public async Task SignOutCurrentAsync(Guid connectionId, CancellationToken ct)
    {
        var configured = await settings.GetAsync(connectionId, ct);
        var secret = await ReadTokenAsync(connectionId, ct);
        if (secret is null) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, Cx04Protocol.Logout(configured));
        request.Headers.Authorization = new("Bearer", secret.AccessToken);
        using var response = await transport.SendAsync(new(tenant.TenantId, connectionId), request, ct);
        await RecordCallAsync(connectionId, ct);
        if (!response.IsSuccessStatusCode)
            throw new Cx04RemoteException(response.StatusCode, "sign-out");
    }

    private async Task<JsonDocument?> SendJsonAsync(Guid connectionId, HttpMethod method, Uri uri,
        object? payload, bool idempotent, CancellationToken ct, (string Name, string Value)? header = null)
    {
        using var response = await SendAuthenticatedAsync(connectionId, token =>
        {
            var request = new HttpRequestMessage(method, uri);
            request.Headers.Authorization = new("Bearer", token);
            if (header is { } value) request.Headers.TryAddWithoutValidation(value.Name, value.Value);
            if (payload is not null)
                request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload,
                    Cx04Protocol.Json)) { Headers = { ContentType = new("application/json") } };
            return request;
        }, ct);
        await EnsureSuccessAsync(response, method == HttpMethod.Get ? "read" : "write", ct);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(Guid connectionId,
        Func<string, HttpRequestMessage> request, CancellationToken ct)
    {
        var token = await AccessTokenAsync(connectionId, forceRefresh: false, ct);
        var response = await transport.SendAsync(new(tenant.TenantId, connectionId), request(token), ct);
        await RecordCallAsync(connectionId, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized) return response;
        response.Dispose();
        token = await AccessTokenAsync(connectionId, forceRefresh: true, ct);
        var retried = await transport.SendAsync(new(tenant.TenantId, connectionId), request(token), ct);
        await RecordCallAsync(connectionId, ct);
        return retried;
    }

    private async Task<string> AccessTokenAsync(Guid connectionId, bool forceRefresh, CancellationToken ct)
    {
        var key = new ConnectionWorkerKey(tenant.TenantId, connectionId);
        var gate = tokens.For(key);
        await gate.WaitAsync(ct);
        try
        {
            await using var operation = await ConnectionOperationLock.AcquireAsync(db, tenant.TenantId,
                $"cx04-refresh:{connectionId:N}", ct);
            var current = await ReadTokenAsync(connectionId, ct)
                ?? throw new Cx04AuthorisationException("consent_required", "CX04 consent is required.");
            var configured = await settings.GetAsync(connectionId, ct);
            if (!forceRefresh && current.AccessExpiresAt > clock.UtcNow.AddSeconds(configured.RefreshEarlySeconds))
                return current.AccessToken;
            var state = await db.Cx04AuthorisationStates.SingleAsync(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId, ct);
            if (state.RefreshChainExpiresAt <= clock.UtcNow)
            {
                await RequireConsentAsync(connectionId, state, "refresh_chain_expired", ct);
                throw new Cx04AuthorisationException("reconsent_required", "CX04 consent has expired.");
            }
            Cx04TokenResponse refreshed;
            try
            {
                refreshed = await TokenRequestAsync(connectionId, configured,
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["client_id"] = configured.ClientId,
                        ["refresh_token"] = current.RefreshToken,
                    }, ct);
            }
            catch (Cx04RemoteException exception) when (exception.Code == "invalid_grant")
            {
                await RequireConsentAsync(connectionId, state, "invalid_grant", ct);
                throw new Cx04AuthorisationException("reconsent_required", "CX04 consent is no longer valid.");
            }
            if (string.IsNullOrWhiteSpace(refreshed.RefreshToken))
                throw new Cx04RemoteException(HttpStatusCode.OK, "token refresh");
            var expiry = state.RefreshExpiryMode == Cx04RefreshExpiryModes.Sliding
                ? clock.UtcNow.AddDays(state.RefreshLifetimeDays) : state.RefreshChainExpiresAt!.Value;
            var replacement = new Cx04TokenSecret(refreshed.AccessToken, refreshed.RefreshToken,
                clock.UtcNow.AddSeconds(refreshed.ExpiresIn));
            // TIGHT COUPLING (rotating token use): this awaited credential replacement and the returned access token
            // are ordered deliberately. Returning before SaveAsync commits could use an access token whose matching
            // one-time refresh token was lost after a crash. EfConnectionCredentialStore documents the other side.
            await SaveTokenAsync(connectionId, replacement, expiry, ct);
            state.RefreshChainExpiresAt = expiry;
            state.UpdatedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);
            await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), connectionId,
                ConnectionAuditEventType.ConsentRenewed, "system:cx04-authorisation", clock.UtcNow,
                JsonSerializer.Serialize(new { refresh_chain_expires_at = expiry }, IntegrationContracts.Json)), ct);
            return replacement.AccessToken;
        }
        finally { gate.Release(); }
    }

    internal async Task<Cx04TokenResponse> TokenRequestAsync(Guid connectionId,
        Cx04ConnectionSettings configured, IReadOnlyDictionary<string, string> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Cx04Protocol.Token(configured))
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await transport.SendAsync(new(tenant.TenantId, connectionId), request, ct);
        await RecordCallAsync(connectionId, ct);
        if (!response.IsSuccessStatusCode)
        {
            string? code = null;
            try
            {
                using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
                    cancellationToken: ct);
                code = error.RootElement.TryGetProperty("error", out var value) ? value.GetString() : null;
            }
            catch (JsonException) { }
            throw new Cx04RemoteException(response.StatusCode, "token exchange", code);
        }
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct),
            cancellationToken: ct);
        var root = body.RootElement;
        return new(root.GetProperty("access_token").GetString()
                ?? throw new Cx04RemoteException(HttpStatusCode.OK, "token exchange"),
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            root.GetProperty("expires_in").GetInt32(),
            root.TryGetProperty("token_type", out var tokenType) ? tokenType.GetString() : null,
            root.TryGetProperty("scope", out var scope) ? scope.GetString() : null);
    }

    internal async Task SaveTokenAsync(Guid connectionId, Cx04TokenSecret secret, DateTime expiry,
        CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(secret, IntegrationContracts.Json);
        try
        {
            await credentials.SaveAsync(await protector.EncryptAsync(tenant.TenantId, connectionId, TokenPurpose,
                bytes, expiry, expiry, ct), ct);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    internal async Task<Cx04TokenSecret?> ReadTokenAsync(Guid connectionId, CancellationToken ct)
    {
        var record = await credentials.GetCurrentAsync(tenant.TenantId, connectionId, TokenPurpose, ct);
        if (record is null) return null;
        var bytes = await protector.DecryptAsync(record, ct);
        try { return JsonSerializer.Deserialize<Cx04TokenSecret>(bytes, IntegrationContracts.Json); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task RequireConsentAsync(Guid connectionId, Cx04AuthorisationState state, string reason,
        CancellationToken ct)
    {
        if (state.Status == Cx04AuthorisationStatus.ConsentRequired) return;
        state.Status = Cx04AuthorisationStatus.ConsentRequired;
        state.PendingStateHash = null;
        state.PendingStateExpiresAt = null;
        state.UpdatedAt = clock.UtcNow;
        var connection = await db.Connections.SingleAsync(value => value.TenantId == tenant.TenantId
            && value.ConnectionId == connectionId, ct);
        connection.Status = Data.Entities.ConnectionStatus.Paused;
        await db.SaveChangesAsync(ct);
        await audit.AppendAsync(new(tenant.TenantId, Guid.CreateVersion7(clock.UtcNow), connectionId,
            ConnectionAuditEventType.ConsentRequired, "system:cx04-authorisation", clock.UtcNow,
            JsonSerializer.Serialize(new { reason }, IntegrationContracts.Json)), ct);
        var noticeReason = reason == "invalid_grant" ? "refused_by_erp" : "expired";
        var detail = noticeReason == "refused_by_erp" ? "the source application refused the refresh grant"
            : $"the sign-in expired at {state.RefreshChainExpiresAt:yyyy-MM-dd HH:mm} UTC";
        await notifications.NotifyPauseAsync(state, noticeReason, detail, ct);
    }

    private Task<int> RecordCallAsync(Guid connectionId, CancellationToken ct) =>
        db.Cx04AuthorisationStates.Where(value => value.TenantId == tenant.TenantId
                && value.ConnectionId == connectionId)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.LastCallAt, clock.UtcNow), ct);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        await response.Content.LoadIntoBufferAsync(ct);
        throw new Cx04RemoteException(response.StatusCode, operation);
    }
}
