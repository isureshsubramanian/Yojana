using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yojana.Integration.Connectors.CX04.Testing;

public sealed class Cx04Simulator : IAsyncDisposable
{
    public const string DefaultEndpointVersion = "24.200.001";
    public const string ManufacturingEndpointVersion = "24.200.001";

    private readonly object stateLock = new();
    private readonly Cx04SimulatorOptions options;
    private readonly Cx04LicenceGovernor governor;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly HttpClient webhookClient;
    private readonly Dictionary<(string Endpoint, string Version, string Entity), List<JsonObject>> records = [];
    private readonly Dictionary<(string Entity, string Version), string> modifiedDateFields = [];
    private readonly Dictionary<string, Cx04SerialAssignment> serialAssignments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AccessGrant> accessGrants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RefreshGrant> refreshGrants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AuthorisationCode> authorisationCodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SimulatorSession> sessions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunningAction> actions = new(StringComparer.Ordinal);
    private readonly List<Cx04StoredFile> files = [];
    private readonly Dictionary<string, PushQuery> pushQueries = new(StringComparer.Ordinal);
    private readonly List<Cx04ParkedNotification> parkedNotifications = [];
    private readonly List<Cx04PushNotification> deliveredNotifications = [];
    private TaskCompletionSource<bool> heldRequests = NewHold();
    private long nextIdentifier;

    public Cx04Simulator(Cx04SimulatorOptions? options = null, HttpMessageHandler? webhookHandler = null)
    {
        this.options = options ?? new();
        governor = new(this.options);
        webhookClient = webhookHandler is null ? new() : new(webhookHandler, disposeHandler: true);
        var port = FreePort();
        BaseUri = new($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(BaseUri.ToString());
        listener.Start();
        loop = RunAsync();
    }

    public Uri BaseUri { get; }
    public string BuildVersion { get; set; } = "26.210.0042-fictional";
    public string DatabaseVersion { get; set; } = "26.210.0042-fictional";
    public Cx04Faults Faults { get; } = new();
    public Cx04LicenceProfile LicenceProfile => options.LicenceProfile;
    public int ActiveSessionCount { get { lock (stateLock) return sessions.Count; } }
    public int ProcessedRequestCount => governor.ProcessedRequests;
    public int DelayedRequestCount => governor.DelayedRequests;
    public int DeclinedRequestCount => governor.DeclinedRequests;
    public int QueuedRequestCount => governor.QueuedRequests;
    public TimeSpan LastThrottleDelay => governor.LastThrottleDelay;
    public IReadOnlyList<Cx04StoredFile> Files { get { lock (stateLock) return files.ToArray(); } }
    public IReadOnlyList<Cx04ParkedNotification> ParkedNotifications
    {
        get
        {
            lock (stateLock)
            {
                PurgeParkedNotifications();
                return parkedNotifications.ToArray();
            }
        }
    }
    public IReadOnlyList<Cx04PushNotification> DeliveredNotifications
    {
        get { lock (stateLock) return deliveredNotifications.ToArray(); }
    }
    public string? LastHandlerFailure { get; private set; }

    public void SeedEntity(string endpoint, string version, string entity, string json)
    {
        var record = JsonNode.Parse(json) as JsonObject
            ?? throw new ArgumentException("The seeded entity must be a JSON object.", nameof(json));
        EnsureIdentity(record);
        AddLinks(record, version, entity);
        lock (stateLock)
        {
            var key = (endpoint, version, entity);
            if (!records.TryGetValue(key, out var values)) records[key] = values = [];
            values.Add(record);
        }
    }

    public void SetModifiedDateField(string entity, string version, string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        lock (stateLock) modifiedDateFields[(entity, version)] = fieldName;
    }

    public void SetSerialAssignment(string inventoryId, Cx04SerialAssignment assignment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inventoryId);
        lock (stateLock) serialAssignments[inventoryId] = assignment;
    }

    public void FillSessionLimitWithLeakedSessions()
    {
        lock (stateLock)
        {
            var maximum = options.LicenceProfile.MaximumSessions;
            if (maximum == int.MaxValue) throw new InvalidOperationException("Set a finite EnterpriseSessionLimit first.");
            while (sessions.Count < maximum)
            {
                var id = Next("leaked-session");
                sessions[id] = new(id, options.TimeProvider.GetUtcNow(), IsCookie: true);
            }
        }
    }

    public void ReleaseHeldRequests()
    {
        Faults.HoldProcessedRequests = false;
        heldRequests.TrySetResult(true);
        heldRequests = NewHold();
    }

    public void ConfigurePushQuery(string documentType, string query, string companyId, Uri webhook,
        bool aggregate = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(companyId);
        ArgumentNullException.ThrowIfNull(webhook);
        if (aggregate)
            throw new ArgumentException("Aggregate push queries are rejected by the simulator protocol profile.",
                nameof(aggregate));
        lock (stateLock) pushQueries[documentType] = new(query, companyId, webhook);
    }

    public Task<IReadOnlyList<Cx04NotificationDelivery>> PublishDocumentChangesAsync(
        string documentType,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> inserted,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> deleted,
        string id,
        long timestamp,
        IReadOnlyDictionary<string, object?> additionalInfo,
        CancellationToken cancellationToken = default)
    {
        PushQuery configured;
        lock (stateLock)
            configured = pushQueries.GetValueOrDefault(documentType)
                ?? throw new InvalidOperationException("No push query is configured for document type " + documentType + '.');
        var notification = new Cx04PushNotification(inserted, deleted, configured.Query, configured.CompanyId,
            id, timestamp, additionalInfo);
        return PublishNotificationsAsync(configured.Webhook, [notification], cancellationToken);
    }

    public async Task<IReadOnlyList<Cx04NotificationDelivery>> PublishNotificationsAsync(
        Uri webhook,
        IReadOnlyList<Cx04PushNotification> notifications,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webhook);
        var ordered = Faults.ReverseNextNotificationBatch ? notifications.Reverse().ToArray() : notifications.ToArray();
        Faults.ReverseNextNotificationBatch = false;
        var deliveries = new List<Cx04NotificationDelivery>();
        foreach (var notification in ordered)
        {
            deliveries.Add(await PublishOneAsync(webhook, notification, cancellationToken));
            if (Faults.DuplicateNextNotification)
            {
                Faults.DuplicateNextNotification = false;
                deliveries.Add(await PublishOneAsync(webhook, notification, cancellationToken));
            }
        }
        return deliveries;
    }

    public async Task<bool> ResendParkedNotificationAsync(
        string notificationId,
        Uri webhook,
        CancellationToken cancellationToken = default)
    {
        Cx04ParkedNotification? parked;
        lock (stateLock)
        {
            PurgeParkedNotifications();
            parked = parkedNotifications.FirstOrDefault(value => value.Notification.Id == notificationId);
            if (parked is null || parked.Truncated) return false;
            parkedNotifications.Remove(parked);
        }

        var delivery = await PublishOneAsync(webhook, parked.Notification, cancellationToken);
        return delivery.Delivered;
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        ReleaseHeldRequests();
        try { await loop; }
        catch (HttpListenerException) { }
        listener.Close();
        webhookClient.Dispose();
        governor.Dispose();
        stop.Dispose();
    }

    private async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (Exception) when (stop.IsCancellationRequested) { break; }
            _ = Task.Run(() => HandleAsync(context), stop.Token);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;
            var path = request.Url?.AbsolutePath ?? "";

            if (path == "/identity/connect/authorize" && request.HttpMethod == "GET")
            {
                await AuthoriseAsync(request, response);
                return;
            }
            if (path == "/identity/connect/token" && request.HttpMethod == "POST")
            {
                await TokenAsync(request, response);
                return;
            }
            if (path == "/entity/auth/logout" && request.HttpMethod == "POST")
            {
                Logout(request, response);
                return;
            }
            if (path == "/entity" && request.HttpMethod == "GET")
            {
                await DiscoveryAsync(request, response);
                return;
            }

            if (Faults.TooManyRequestsStormRemaining > 0)
            {
                Faults.TooManyRequestsStormRemaining--;
                await TooManyRequestsAsync(response);
                return;
            }
            if (Faults.ForbidNextRequest)
            {
                Faults.ForbidNextRequest = false;
                await StandardErrorAsync(response, HttpStatusCode.Forbidden,
                    "The request is forbidden.", "Fictional rights do not include this form.", "PX.Data.PXException");
                return;
            }

            if (path == "/entity/auth/login" && request.HttpMethod == "POST")
            {
                using var loginLease = await governor.EnterAsync(stop.Token);
                if (loginLease is null) { await TooManyRequestsAsync(response); return; }
                await LoginAsync(response);
                return;
            }

            var authentication = Authenticate(request);
            if (authentication is null)
            {
                await StandardErrorAsync(response, HttpStatusCode.Unauthorized,
                    "The request is not authorised.", "The access token or session is missing or expired.",
                    "IdentityModel.InvalidTokenException");
                return;
            }

            if (ActionBlocks(authentication.SessionId, path))
            {
                await StandardErrorAsync(response, HttpStatusCode.Conflict,
                    "A long-running action is in progress.",
                    "Simulator assumption: only the action status request can use this session while it is running.",
                    "SimulatorAssumedConcurrentActionException");
                return;
            }

            using var lease = await governor.EnterAsync(stop.Token);
            if (lease is null) { await TooManyRequestsAsync(response); return; }
            if (Faults.HoldProcessedRequests) await heldRequests.Task.WaitAsync(stop.Token);

            if (path.StartsWith("/entity/action-status/", StringComparison.Ordinal))
            {
                ActionStatus(response, path);
                return;
            }
            if (path.StartsWith("/entity/Default/", StringComparison.Ordinal))
            {
                await DefaultEndpointAsync(request, response, authentication.SessionId);
                return;
            }
            if (path.StartsWith("/entity/MANUFACTURING/", StringComparison.Ordinal))
            {
                await ManufacturingEndpointAsync(request, response);
                return;
            }

            await StandardErrorAsync(response, HttpStatusCode.NotFound,
                "The requested resource was not found.", "Unknown simulator endpoint.", "PX.Data.PXException");
        }
        catch (Exception exception)
        {
            LastHandlerFailure = exception.ToString();
            if (context.Response.OutputStream.CanWrite)
                await StandardErrorAsync(context.Response, HttpStatusCode.InternalServerError,
                    "An error has occurred.", exception.Message, exception.GetType().FullName ?? exception.GetType().Name);
        }
    }

    private async Task AuthoriseAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.Url?.Query ?? "");
        if (!string.Equals(query["response_type"], "code", StringComparison.Ordinal))
        {
            await OAuthErrorAsync(response, "unsupported_response_type");
            return;
        }

        var redirect = query["redirect_uri"];
        if (!Uri.TryCreate(redirect, UriKind.Absolute, out var redirectUri))
        {
            await OAuthErrorAsync(response, "invalid_request");
            return;
        }

        var code = Next("code");
        lock (stateLock)
            authorisationCodes[code] = new(code, query["client_id"] ?? "", query["scope"] ?? "api", redirectUri);
        var separator = string.IsNullOrEmpty(redirectUri.Query) ? "?" : "&";
        response.StatusCode = (int)HttpStatusCode.Redirect;
        response.RedirectLocation = redirectUri + separator + "code=" + Uri.EscapeDataString(code)
            + (query["state"] is { } state ? "&state=" + Uri.EscapeDataString(state) : "");
        response.Close();
    }

    private async Task TokenAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var form = System.Web.HttpUtility.ParseQueryString(await new StreamReader(request.InputStream).ReadToEndAsync());
        switch (form["grant_type"])
        {
            case "password":
                if (string.IsNullOrWhiteSpace(form["username"]) || string.IsNullOrWhiteSpace(form["password"]))
                {
                    await OAuthErrorAsync(response, "invalid_grant");
                    return;
                }
                await IssueGrantAsync(response, form["scope"] ?? "api");
                return;
            case "authorization_code":
                AuthorisationCode? code;
                lock (stateLock)
                {
                    code = authorisationCodes.GetValueOrDefault(form["code"] ?? "");
                    if (code is not null) authorisationCodes.Remove(code.Value);
                }
                if (code is null || !string.Equals(code.ClientId, form["client_id"], StringComparison.Ordinal)
                    || form["redirect_uri"] is { } redirect
                    && !string.Equals(code.RedirectUri.ToString(), redirect, StringComparison.Ordinal))
                {
                    await OAuthErrorAsync(response, "invalid_grant");
                    return;
                }
                await IssueGrantAsync(response, code.Scope);
                return;
            case "refresh_token":
                await RefreshAsync(response, form["refresh_token"] ?? "");
                return;
            case "client_credentials":
            default:
                await OAuthErrorAsync(response, "unsupported_grant_type");
                return;
        }
    }

    private async Task IssueGrantAsync(HttpListenerResponse response, string scope)
    {
        var now = options.TimeProvider.GetUtcNow();
        if (!TryOpenSession(IsCookie: false, out var sessionId))
        {
            await TooManyRequestsAsync(response);
            return;
        }

        var access = Next("access");
        var chain = Next("chain");
        var refresh = scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("offline_access", StringComparer.Ordinal)
            && options.RefreshExpiryMode != Cx04RefreshExpiryMode.Disabled ? Next("refresh") : null;
        lock (stateLock)
        {
            accessGrants[access] = new(access, sessionId, now + options.AccessTokenLifetime, scope, 0);
            if (refresh is not null)
                refreshGrants[refresh] = new(refresh, chain, sessionId, now,
                    RefreshExpiry(now), scope);
        }
        await JsonAsync(response, TokenBody(access, refresh, scope));
    }

    private async Task RefreshAsync(HttpListenerResponse response, string presented)
    {
        RefreshGrant? refresh;
        var now = options.TimeProvider.GetUtcNow();
        lock (stateLock)
        {
            refresh = refreshGrants.GetValueOrDefault(presented);
            if (refresh is not null) refreshGrants.Remove(presented);
        }
        if (refresh is null || refresh.ExpiresAt <= now)
        {
            await OAuthErrorAsync(response, "invalid_grant");
            return;
        }

        var access = Next("access");
        var replacement = Next("refresh");
        var expiry = options.RefreshExpiryMode == Cx04RefreshExpiryMode.Sliding
            ? now + options.SlidingRefreshChainLifetime : refresh.ExpiresAt;
        lock (stateLock)
        {
            accessGrants[access] = new(access, refresh.SessionId, now + options.AccessTokenLifetime, refresh.Scope, 0);
            refreshGrants[replacement] = refresh with { Value = replacement, ExpiresAt = expiry };
        }
        await JsonAsync(response, TokenBody(access, replacement, refresh.Scope));
    }

    private Dictionary<string, object?> TokenBody(string access, string? refresh, string scope)
    {
        var body = new Dictionary<string, object?>
        {
            ["access_token"] = access,
            ["token_type"] = "Bearer",
            ["expires_in"] = (int)options.AccessTokenLifetime.TotalSeconds,
            ["scope"] = scope,
        };
        if (refresh is not null) body["refresh_token"] = refresh;
        return body;
    }

    private async Task LoginAsync(HttpListenerResponse response)
    {
        if (!TryOpenSession(IsCookie: true, out var session))
        {
            await TooManyRequestsAsync(response);
            return;
        }
        response.AppendCookie(new("ASP.NET_SessionId", session) { HttpOnly = true, Path = "/" });
        response.StatusCode = (int)HttpStatusCode.NoContent;
        response.Close();
    }

    private bool TryOpenSession(bool IsCookie, out string sessionId)
    {
        lock (stateLock)
        {
            ReapIdleSessions(sessions.Count >= options.LicenceProfile.MaximumSessions
                || options.ReapIdleSessionsBelowLimit);
            if (sessions.Count >= options.LicenceProfile.MaximumSessions)
            {
                sessionId = "";
                return false;
            }
            sessionId = Next(IsCookie ? "cookie" : "oauth-session");
            sessions[sessionId] = new(sessionId, options.TimeProvider.GetUtcNow(), IsCookie);
            return true;
        }
    }

    private void Logout(HttpListenerRequest request, HttpListenerResponse response)
    {
        var authentication = Authenticate(request, countRequest: false);
        if (authentication is not null)
        {
            lock (stateLock)
            {
                sessions.Remove(authentication.SessionId);
                foreach (var token in accessGrants.Where(pair => pair.Value.SessionId == authentication.SessionId)
                             .Select(pair => pair.Key).ToArray()) accessGrants.Remove(token);
            }
        }
        response.StatusCode = (int)HttpStatusCode.NoContent;
        response.Close();
    }

    private Authentication? Authenticate(HttpListenerRequest request, bool countRequest = true)
    {
        var now = options.TimeProvider.GetUtcNow();
        var bearer = request.Headers["Authorization"];
        if (bearer?.StartsWith("Bearer ", StringComparison.Ordinal) == true)
        {
            var value = bearer["Bearer ".Length..];
            lock (stateLock)
            {
                var grant = accessGrants.GetValueOrDefault(value);
                if (grant is null || grant.ExpiresAt <= now) return null;
                if (countRequest && grant.AuthenticatedRequests >= Faults.ExpireAccessTokenAfterRequests)
                {
                    accessGrants.Remove(value);
                    return null;
                }
                if (countRequest) accessGrants[value] = grant with { AuthenticatedRequests = grant.AuthenticatedRequests + 1 };
                TouchSession(grant.SessionId, now);
                return new(grant.SessionId);
            }
        }

        var cookie = request.Cookies["ASP.NET_SessionId"]?.Value;
        lock (stateLock)
        {
            if (cookie is null || !sessions.ContainsKey(cookie)) return null;
            TouchSession(cookie, now);
            return new(cookie);
        }
    }

    private void ReapIdleSessions(bool enabled)
    {
        if (!enabled) return;
        var cutOff = options.TimeProvider.GetUtcNow() - TimeSpan.FromMinutes(10);
        foreach (var session in sessions.Values.Where(value => value.LastUsedAt <= cutOff).ToArray())
            sessions.Remove(session.Id);
    }

    private void TouchSession(string sessionId, DateTimeOffset now)
    {
        if (sessions.TryGetValue(sessionId, out var session)) sessions[sessionId] = session with { LastUsedAt = now };
    }

    private async Task DiscoveryAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var authenticated = Authenticate(request, countRequest: false) is not null;
        await JsonAsync(response, new
        {
            version = new { acumaticaBuildVersion = BuildVersion, databaseVersion = DatabaseVersion },
            endpoints = new object[]
            {
                new { name = "Default", version = DefaultEndpointVersion,
                    href = new Uri(BaseUri, $"entity/Default/{DefaultEndpointVersion}/") },
                new { name = "MANUFACTURING", version = ManufacturingEndpointVersion,
                    href = new Uri(BaseUri, $"entity/MANUFACTURING/{ManufacturingEndpointVersion}/") },
            },
            features = authenticated ? new[] { "FictionalDistribution", "FictionalManufacturing" } : [],
        });
    }

    private async Task DefaultEndpointAsync(HttpListenerRequest request, HttpListenerResponse response, string sessionId)
    {
        var segments = request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length >= 5 && segments[3] == "files")
        {
            await PutFileAsync(request, response);
            return;
        }
        if (segments.Length < 4)
        {
            await NotFoundAsync(response);
            return;
        }
        var version = segments[2];
        var entity = segments[3];
        var key = segments.Length > 4 ? Uri.UnescapeDataString(string.Join('/', segments.Skip(4))) : null;

        if (request.HttpMethod == "GET")
        {
            await ReadEntitiesAsync(request, response, "Default", version, entity, key);
            return;
        }
        if (request.HttpMethod == "PUT" && key is null)
        {
            await PutEntityAsync(request, response, version, entity);
            return;
        }
        if (request.HttpMethod == "POST" && key is not null)
        {
            await StartActionAsync(response, sessionId, entity, key);
            return;
        }
        await NotFoundAsync(response);
    }

    private async Task ManufacturingEndpointAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var segments = request.Url!.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 4) { await NotFoundAsync(response); return; }
        var version = segments[2];
        var entity = segments[3];
        var key = segments.Length > 4 ? string.Join('/', segments.Skip(4)) : null;
        if (request.HttpMethod == "GET")
        {
            await ReadEntitiesAsync(request, response, "MANUFACTURING", version, entity, key);
            return;
        }
        if (request.HttpMethod == "POST" && string.Equals(key, "Generate", StringComparison.Ordinal))
        {
            await DisabledActionAsync(response);
            return;
        }
        await NotFoundAsync(response);
    }

    private async Task ReadEntitiesAsync(HttpListenerRequest request, HttpListenerResponse response,
        string endpoint, string version, string entity, string? key)
    {
        JsonObject[] source;
        lock (stateLock)
            source = records.GetValueOrDefault((endpoint, version, entity))?.Select(value => (JsonObject)value.DeepClone()).ToArray() ?? [];
        if (key is not null)
        {
            var found = source.FirstOrDefault(value => MatchesKey(value, entity, key));
            if (found is null) { await NotFoundAsync(response); return; }
            ShapeExpansion(found, request.Url);
            await JsonNodeAsync(response, found);
            return;
        }

        var query = System.Web.HttpUtility.ParseQueryString(request.Url?.Query ?? "");
        var filter = query["$filter"];
        var values = source.AsEnumerable();
        if (!string.IsNullOrWhiteSpace(filter)) values = ApplyFilter(values, entity, version, filter);
        if (options.StablePagingOrder)
            values = values.OrderBy(value => EntityKey(value, entity), StringComparer.Ordinal);
        var skip = int.TryParse(query["$skip"], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedSkip)
            ? parsedSkip : 0;
        var top = int.TryParse(query["$top"], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedTop)
            ? parsedTop : int.MaxValue;
        var page = values.Skip(skip).Take(top).ToArray();
        foreach (var value in page) ShapeExpansion(value, request.Url);
        await JsonNodeAsync(response, new JsonArray(page.Cast<JsonNode?>().ToArray()));
    }

    private IEnumerable<JsonObject> ApplyFilter(IEnumerable<JsonObject> source, string entity, string version, string filter)
    {
        if (filter.Contains(" gt datetimeoffset'", StringComparison.Ordinal))
        {
            var parts = filter.Split(" gt datetimeoffset'", 2, StringSplitOptions.None);
            var field = ModifiedDateField(entity, version);
            if (!string.Equals(parts[0], field, StringComparison.Ordinal)) return [];
            var raw = parts[1].TrimEnd('\'');
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var after)) return [];
            return source.Where(value => DateTimeOffset.TryParse(Scalar(value[field]), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var modified) && modified > after);
        }
        if (filter.Contains(" eq ", StringComparison.Ordinal))
        {
            var parts = filter.Split(" eq ", 2, StringSplitOptions.None);
            var wanted = parts[1].Trim().Trim('\'').Replace("''", "'", StringComparison.Ordinal);
            return source.Where(value => string.Equals(Scalar(value[parts[0].Trim()]), wanted, StringComparison.Ordinal));
        }
        return source;
    }

    private async Task PutEntityAsync(HttpListenerRequest request, HttpListenerResponse response, string version, string entity)
    {
        var body = JsonNode.Parse(await new StreamReader(request.InputStream).ReadToEndAsync()) as JsonObject;
        if (body is null)
        {
            await StandardErrorAsync(response, HttpStatusCode.BadRequest, "The request is invalid.",
                "An entity object is required.", "PX.Data.PXException");
            return;
        }

        if (!ValidateEntity(body, entity, out var field, out var error))
        {
            await ValidationErrorAsync(response, body, field, error);
            return;
        }

        JsonObject? existing;
        var created = false;
        lock (stateLock)
        {
            var recordKey = ("Default", version, entity);
            if (!records.TryGetValue(recordKey, out var values)) records[recordKey] = values = [];
            existing = FindExisting(values, body, entity);
            if (request.Headers["If-None-Match"] == "*" && existing is not null
                || request.Headers["If-Match"] == "*" && existing is null)
            {
                response.StatusCode = (int)HttpStatusCode.PreconditionFailed;
                response.Close();
                return;
            }

            if (existing is null)
            {
                existing = (JsonObject)body.DeepClone();
                EnsureIdentity(existing);
                values.Add(existing);
                created = true;
            }
            else
            {
                Merge(existing, body);
            }
            ApplyDetailDeletes(existing);
            ApplyReportedDoubleQuantity(existing, entity);
            AddLinks(existing, version, entity);
        }

        if (created && Faults.DropNextCreateReply)
        {
            Faults.DropNextCreateReply = false;
            response.ContentLength64 = 100;
            await response.OutputStream.WriteAsync(new byte[] { (byte)'{' });
            response.Abort();
            return;
        }
        await JsonNodeAsync(response, existing.DeepClone());
    }

    private bool ValidateEntity(JsonObject body, string entity, out string field, out string error)
    {
        field = "Details";
        error = "";
        var details = body["Details"] as JsonArray;
        if (details is null) return true;
        if (details.Count > options.LicenceProfile.LinesPerTransaction)
        {
            error = "The firm line limit for this licence tier was exceeded.";
            return false;
        }
        var serials = details.OfType<JsonObject>().SelectMany(Allocations)
            .Count(allocation => !string.IsNullOrWhiteSpace(Scalar(allocation["LotSerialNbr"])));
        if (serials > options.LicenceProfile.SerialNumbersPerDocument)
        {
            error = "The firm serial-number limit for this licence tier was exceeded.";
            return false;
        }

        foreach (var detail in details.OfType<JsonObject>())
        {
            var allocations = Allocations(detail).ToArray();
            var inventory = Scalar(detail["InventoryID"]);
            var assignment = inventory is not null && serialAssignments.TryGetValue(inventory, out var configured)
                ? configured : Cx04SerialAssignment.WhenReceived;
            if (allocations.Length == 0)
            {
                if (inventory is not null && serialAssignments.ContainsKey(inventory))
                {
                    field = "Allocations";
                    error = "Simulator assumption: a serial-tracked line requires allocations.";
                    return false;
                }
                continue;
            }
            if (assignment == Cx04SerialAssignment.WhenUsed && entity == "PurchaseReceipt"
                && allocations.Any(value => !string.IsNullOrWhiteSpace(Scalar(value["LotSerialNbr"]))))
            {
                field = "Allocations";
                error = "Simulator assumption: a When Used item cannot carry serials on a receipt.";
                return false;
            }
            if (options.RequireSerialQuantityOne && allocations.Any(value =>
                    !string.IsNullOrWhiteSpace(Scalar(value["LotSerialNbr"])) && Decimal(value["Qty"]) != 1m))
            {
                field = "Qty";
                error = "Simulator assumption F67: each serial allocation quantity must be one.";
                return false;
            }
            var type = Scalar(body["OrderType"]);
            if (entity == "SalesOrder" && type == "RM" && !options.SupportRmAllocations)
            {
                field = "Allocations";
                error = "Simulator assumption F68 disabled: RM allocation shape is not accepted.";
                return false;
            }

            var lineQuantityField = entity == "PurchaseReceipt" ? "ReceiptQty" : "Qty";
            var lineQuantity = Decimal(detail[lineQuantityField]);
            var allocated = allocations.Sum(value => Decimal(value["Qty"]) ?? 0m);
            if (options.AllocationQuantityBehaviour == Cx04AllocationQuantityBehaviour.LineAndAllocations)
            {
                if (lineQuantity is null)
                {
                    field = lineQuantityField;
                    error = "Ext. Cost must not be negative";
                    return false;
                }
                if (lineQuantity != allocated)
                {
                    field = "Allocations";
                    error = "Simulator assumption: allocation quantities must sum to the line quantity.";
                    return false;
                }
            }
            else if (lineQuantity is null)
            {
                continue;
            }
        }
        return true;
    }

    private void ApplyReportedDoubleQuantity(JsonObject record, string entity)
    {
        if (options.AllocationQuantityBehaviour != Cx04AllocationQuantityBehaviour.AllocationsOnly) return;
        if (record["Details"] is not JsonArray details) return;
        foreach (var detail in details.OfType<JsonObject>())
        {
            var field = entity == "PurchaseReceipt" ? "ReceiptQty" : "Qty";
            if (Decimal(detail[field]) is not { } line || !Allocations(detail).Any()) continue;
            SetScalar(detail, field, line + Allocations(detail).Sum(value => Decimal(value["Qty"]) ?? 0m));
        }
    }

    private async Task PutFileAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "PUT") { await NotFoundAsync(response); return; }
        if (request.ContentLength64 > options.AttachmentSizeLimitBytes)
        {
            await StandardErrorAsync(response, Cx04Status.UnprocessableEntity, "The file is invalid.",
                "Simulator assumption F77: the configured attachment size limit was exceeded.", "PX.Data.PXException");
            return;
        }
        var comment = request.Headers["PX-CbFileComment"];
        if (comment?.Length > 500)
        {
            await StandardErrorAsync(response, HttpStatusCode.BadRequest, "The file comment is invalid.",
                "PX-CbFileComment cannot exceed 500 characters.", "PX.Data.PXException");
            return;
        }
        using var memory = new MemoryStream();
        await request.InputStream.CopyToAsync(memory, stop.Token);
        var location = new Uri(BaseUri, "entity/file-content/" + Next("file"));
        lock (stateLock) files.Add(new(location, comment, memory.ToArray()));
        response.StatusCode = (int)HttpStatusCode.NoContent;
        response.Headers[HttpResponseHeader.Location] = location.ToString();
        response.Close();
    }

    private async Task StartActionAsync(HttpListenerResponse response, string sessionId, string entity, string action)
    {
        var known = entity == "Shipment" && action == "ConfirmShipment"
            || entity == "PurchaseReceipt" && action == options.ReceiptReleaseActionName;
        if (!known) { await NotFoundAsync(response); return; }
        if (Faults.DisableNextAction)
        {
            Faults.DisableNextAction = false;
            await DisabledActionAsync(response);
            return;
        }
        var id = Next("action");
        lock (stateLock) actions[id] = new(id, sessionId, options.ActionPollsBeforeCompletion);
        response.StatusCode = (int)HttpStatusCode.Accepted;
        response.Headers[HttpResponseHeader.Location] = new Uri(BaseUri, "entity/action-status/" + id).ToString();
        response.Close();
    }

    private void ActionStatus(HttpListenerResponse response, string path)
    {
        var id = path[(path.LastIndexOf('/') + 1)..];
        lock (stateLock)
        {
            if (!actions.TryGetValue(id, out var action))
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                response.Close();
                return;
            }
            if (action.PollsRemaining > 0)
            {
                actions[id] = action with { PollsRemaining = action.PollsRemaining - 1 };
                response.StatusCode = (int)options.RunningActionStatusCode;
            }
            else
            {
                actions.Remove(id);
                response.StatusCode = (int)HttpStatusCode.NoContent;
            }
        }
        response.Close();
    }

    private bool ActionBlocks(string sessionId, string path)
    {
        lock (stateLock)
            return actions.Values.Any(value => value.SessionId == sessionId)
                && !path.StartsWith("/entity/action-status/", StringComparison.Ordinal);
    }

    private static Task DisabledActionAsync(HttpListenerResponse response) =>
        StandardErrorAsync(response, HttpStatusCode.InternalServerError, "An error has occurred.",
            "The action is disabled.", "PX.Data.PXActionDisabledException");

    private async Task<Cx04NotificationDelivery> PublishOneAsync(
        Uri webhook,
        Cx04PushNotification notification,
        CancellationToken cancellationToken)
    {
        if (Faults.LoseNextNotificationBeforeSend)
        {
            Faults.LoseNextNotificationBeforeSend = false;
            return new(notification.Id, 0, Delivered: false, Parked: false);
        }
        var truncated = Faults.TruncateNextNotification;
        Faults.TruncateNextNotification = false;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, webhook)
            {
                Content = JsonContent.Create(notification, options: PushJson),
            };
            message.Headers.TryAddWithoutValidation(options.WebhookSecretHeader, options.WebhookSecret);
            using var result = await webhookClient.SendAsync(message, cancellationToken);
            if (result.IsSuccessStatusCode)
            {
                lock (stateLock) deliveredNotifications.Add(notification);
                return new(notification.Id, attempt, Delivered: true, Parked: false);
            }
            if (attempt < 5) await options.DelayAsync(options.PushRetryInterval, cancellationToken);
        }
        lock (stateLock)
            parkedNotifications.Add(new(notification, 5, options.TimeProvider.GetUtcNow(), truncated));
        return new(notification.Id, 5, Delivered: false, Parked: true);
    }

    private void PurgeParkedNotifications()
    {
        var cutOff = options.TimeProvider.GetUtcNow() - options.FailedNotificationRetention;
        parkedNotifications.RemoveAll(value => value.ParkedAt <= cutOff);
    }

    private DateTimeOffset RefreshExpiry(DateTimeOffset now) => options.RefreshExpiryMode switch
    {
        Cx04RefreshExpiryMode.Absolute => now + options.AbsoluteRefreshChainLifetime,
        Cx04RefreshExpiryMode.Sliding => now + options.SlidingRefreshChainLifetime,
        _ => now,
    };

    private JsonObject? FindExisting(List<JsonObject> values, JsonObject body, string entity)
    {
        var byId = Scalar(body["id"]);
        var byKey = EntityKey(body, entity);
        if (byId is not null && options.IdWinsWhenBothProvided)
            return values.FirstOrDefault(value => string.Equals(Scalar(value["id"]), byId, StringComparison.Ordinal));
        if (byKey is not null)
            return values.FirstOrDefault(value => string.Equals(EntityKey(value, entity), byKey, StringComparison.Ordinal));
        return byId is null ? null : values.FirstOrDefault(value => string.Equals(Scalar(value["id"]), byId, StringComparison.Ordinal));
    }

    private static bool MatchesKey(JsonObject value, string entity, string key) =>
        string.Equals(EntityKey(value, entity), key.Trim('(', ')', '\''), StringComparison.Ordinal)
        || string.Equals(Scalar(value["id"]), key.Trim('(', ')', '\''), StringComparison.Ordinal);

    private static string? EntityKey(JsonObject value, string entity)
    {
        var fields = entity switch
        {
            "PurchaseReceipt" => new[] { "Type", "ReceiptNbr" },
            "Shipment" => ["ShipmentNbr"],
            "SalesOrder" => ["OrderType", "OrderNbr"],
            "TransferOrder" => ["OrderType", "OrderNbr"],
            "MoveEntry" => ["ReferenceNbr"],
            _ => ["id"],
        };
        var values = fields.Select(field => Scalar(value[field])).ToArray();
        return values.Any(string.IsNullOrWhiteSpace) ? null : string.Join('|', values!);
    }

    private string ModifiedDateField(string entity, string version)
    {
        lock (stateLock)
        {
            if (modifiedDateFields.TryGetValue((entity, version), out var field)) return field;
            return entity == "SalesOrder" && version == DefaultEndpointVersion
                ? "LastModified" : "LastModifiedDateTime";
        }
    }

    private static void ShapeExpansion(JsonObject value, Uri? uri)
    {
        var query = System.Web.HttpUtility.ParseQueryString(uri?.Query ?? "");
        var expand = query["$expand"] ?? "";
        if (!expand.Split(',', StringSplitOptions.TrimEntries).Contains("Details", StringComparer.Ordinal))
        {
            value.Remove("Details");
            return;
        }
        if (!expand.Split(',', StringSplitOptions.TrimEntries).Contains("Details/Allocations", StringComparer.Ordinal)
            && value["Details"] is JsonArray details)
            foreach (var detail in details.OfType<JsonObject>()) detail.Remove("Allocations");
    }

    private static IEnumerable<JsonObject> Allocations(JsonObject detail) =>
        (detail["Allocations"] as JsonArray)?.OfType<JsonObject>() ?? [];

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var pair in patch) target[pair.Key] = pair.Value?.DeepClone();
    }

    private static void ApplyDetailDeletes(JsonObject record)
    {
        if (record["Details"] is not JsonArray details) return;
        for (var index = details.Count - 1; index >= 0; index--)
            if (details[index] is JsonObject detail && string.Equals(Scalar(detail["delete"]), "true", StringComparison.OrdinalIgnoreCase))
                details.RemoveAt(index);
    }

    private void AddLinks(JsonObject value, string version, string entity)
    {
        var id = Scalar(value["id"])!;
        value["_links"] = new JsonObject
        {
            ["files:put"] = new JsonObject
            {
                ["href"] = new Uri(BaseUri, $"entity/Default/{version}/files/Fictional.{entity}/Document/{id}/{{filename}}")
                    .ToString(),
            },
        };
        if (value["Details"] is not JsonArray details) return;
        foreach (var detail in details.OfType<JsonObject>())
        {
            EnsureIdentity(detail);
            var detailId = Scalar(detail["id"])!;
            detail["_links"] = new JsonObject
            {
                ["files:put"] = new JsonObject
                {
                    ["href"] = new Uri(BaseUri,
                        $"entity/Default/{version}/files/Fictional.{entity}/Detail/{detailId}/{{filename}}").ToString(),
                },
            };
        }
    }

    private void EnsureIdentity(JsonObject value)
    {
        if (value["id"] is null) value["id"] = DeterministicGuid(++nextIdentifier).ToString();
        if (value["rowNumber"] is null) value["rowNumber"] = nextIdentifier;
    }

    private string Next(string prefix) => prefix + '-' + Interlocked.Increment(ref nextIdentifier).ToString("D6", CultureInfo.InvariantCulture);

    private static Guid DeterministicGuid(long value) => Guid.ParseExact($"00000000000000000000{value:D12}", "N");

    private static decimal? Decimal(JsonNode? node)
    {
        node = Unwrap(node);
        return node is JsonValue value && value.TryGetValue<decimal>(out var number) ? number
            : decimal.TryParse(node?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static string? Scalar(JsonNode? node)
    {
        node = Unwrap(node);
        if (node is null) return null;
        if (node is JsonValue value && value.TryGetValue<string>(out var text)) return text;
        return node.ToJsonString().Trim('"');
    }

    private static JsonNode? Unwrap(JsonNode? node) => node is JsonObject wrapper && wrapper["value"] is { } value
        ? value : node;

    private static void SetScalar(JsonObject value, string field, decimal number)
    {
        if (value[field] is JsonObject wrapper && wrapper.ContainsKey("value")) wrapper["value"] = number;
        else value[field] = number;
    }

    private static async Task ValidationErrorAsync(HttpListenerResponse response, JsonObject entity,
        string field, string error)
    {
        var result = (JsonObject)entity.DeepClone();
        var failing = result[field] as JsonObject ?? new JsonObject { ["value"] = result[field]?.DeepClone() };
        failing["error"] = error;
        result[field] = failing;
        response.StatusCode = (int)Cx04Status.UnprocessableEntity;
        await JsonNodeAsync(response, result);
    }

    private async Task TooManyRequestsAsync(HttpListenerResponse response)
    {
        response.StatusCode = (int)HttpStatusCode.TooManyRequests;
        if (options.IncludeRetryAfterOnThrottle) response.Headers[HttpResponseHeader.RetryAfter] = "1";
        if (options.EmptyThrottleResponse) await JsonAsync(response, new { });
        else await JsonAsync(response, new Cx04ErrorBody("The licence limit was exceeded.",
            "Too many requests are active or queued.", "PX.Data.PXException"));
    }

    private static Task NotFoundAsync(HttpListenerResponse response) => StandardErrorAsync(response,
        HttpStatusCode.NotFound, "The requested resource was not found.", "The entity or action does not exist.",
        "PX.Data.PXException");

    private static Task StandardErrorAsync(HttpListenerResponse response, HttpStatusCode status,
        string message, string exceptionMessage, string exceptionType)
    {
        response.StatusCode = (int)status;
        return JsonAsync(response, new Cx04ErrorBody(message, exceptionMessage, exceptionType));
    }

    private static Task OAuthErrorAsync(HttpListenerResponse response, string error)
    {
        response.StatusCode = (int)HttpStatusCode.BadRequest;
        return JsonAsync(response, new { error });
    }

    private static Task JsonAsync(HttpListenerResponse response, object value) =>
        RawJsonAsync(response, JsonSerializer.Serialize(value, PushJson));

    private static Task JsonNodeAsync(HttpListenerResponse response, JsonNode value) =>
        RawJsonAsync(response, value.ToJsonString(PushJson));

    private static async Task RawJsonAsync(HttpListenerResponse response, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static TaskCompletionSource<bool> NewHold() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static readonly JsonSerializerOptions PushJson = new(JsonSerializerDefaults.Web);

    private sealed record AccessGrant(string Value, string SessionId, DateTimeOffset ExpiresAt, string Scope,
        int AuthenticatedRequests);
    private sealed record RefreshGrant(string Value, string ChainId, string SessionId, DateTimeOffset StartedAt,
        DateTimeOffset ExpiresAt, string Scope);
    private sealed record AuthorisationCode(string Value, string ClientId, string Scope, Uri RedirectUri);
    private sealed record SimulatorSession(string Id, DateTimeOffset LastUsedAt, bool IsCookie);
    private sealed record Authentication(string SessionId);
    private sealed record RunningAction(string Id, string SessionId, int PollsRemaining);
    private sealed record PushQuery(string Query, string CompanyId, Uri Webhook);
}
