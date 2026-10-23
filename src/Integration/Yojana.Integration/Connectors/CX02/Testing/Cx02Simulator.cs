using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yojana.Integration.Connectors.CX02.Testing;

public sealed class Cx02Simulator : IAsyncDisposable
{
    public const string DefaultTenant = "fictional.example";
    public const string DefaultEnvironment = "sandbox";
    public const string Publisher = "yojana";
    public const string Group = "integration";
    public const string Version = "v1.0";
    public static readonly Guid CompanyId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    private static readonly HashSet<string> StandardResources =
        ["items", "customers", "salesInvoices", "salesInvoiceLines", "salesCreditMemos", "salesCreditMemoLines",
            "journals", "journalLines", "attachments", "documentAttachments", "salesShipments", "salesShipmentLines",
            "purchaseReceipts", "purchaseReceiptLines", "itemLedgerEntries"];
    private static readonly HashSet<string> ReadOnlyStandardResources =
        ["salesInvoiceLines", "salesShipments", "salesShipmentLines", "purchaseReceipts", "purchaseReceiptLines", "itemLedgerEntries"];
    private static readonly HashSet<string> CustomResources =
        ["trackedItemLedgerEntries", "itemEntryRelations", "reservationEntries", "trackingSpecifications",
            "serviceItems", "postedTransferShipments", "postedTransferShipmentLines", "postedTransferReceipts",
            "postedTransferReceiptLines", "postedReturnReceipts", "postedReturnReceiptLines"];
    private static readonly HashSet<string> IncomingAttachmentParents =
        ["", "Journal", "Sales Order", "Sales Quote", "Sales Credit Memo", "Sales Invoice", "Purchase Invoice"];

    private readonly object stateLock = new();
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly HttpClient deliveryClient = new();
    private readonly TimeProvider clock;
    private readonly Dictionary<string, TokenState> tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<DateTimeOffset>> principalRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim processingSlots;
    private readonly Dictionary<string, List<Dictionary<string, object?>>> resources =
        new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, SubscriptionState> subscriptions = [];
    private readonly Dictionary<Guid, BusinessSubscriptionState> businessSubscriptions = [];
    private readonly List<PendingChange> pendingChanges = [];
    private int tokenSequence;
    private int queuedRequests;
    private int throttleStormRemaining;
    private HttpStatusCode? timeoutFault;
    private int minimumAcceptedTokenSequence;
    private bool lapseSubscriptions;
    private int serviceUnavailableResponses;
    private bool rejectNextPostAsClosedPeriod;
    private bool expireTokenBeforeNextPost;

    public Cx02Simulator(TimeProvider? clock = null, Cx02SimulatorOptions? options = null)
    {
        this.clock = clock ?? TimeProvider.System;
        Options = options ?? new();
        processingSlots = new(Options.ConcurrentRequestLimit, Options.ConcurrentRequestLimit);
        SeedDefaults();
        var port = FreePort();
        BaseUri = new($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(BaseUri.ToString());
        listener.Start();
        loop = RunAsync();
    }

    public Uri BaseUri { get; }
    public Cx02SimulatorOptions Options { get; }
    public Cx02SimulatorRequestGate RequestGate { get; } = new();
    public List<(string Method, string Path)> Requests { get; } = [];
    public List<Cx02DeliveredNotification> DeliveredNotifications { get; } = [];
    public string? LastHandlerFailure { get; private set; }
    public int CreateCount { get; private set; }
    public int PostedDocumentCount { get; private set; }
    public int AttachmentUploadCount { get; private set; }
    public bool DropNextCreateReply { get; set; }
    public bool DropNextPostReply { get; set; }
    public bool DuplicateNextNotification { get; set; }
    public bool ReverseNextNotificationBatch { get; set; }
    public bool SuppressNextNotificationAsUnlicensedActor { get; set; }
    public int ActiveSubscriptionCount { get { lock (stateLock) return subscriptions.Count; } }
    public int ActiveBusinessSubscriptionCount { get { lock (stateLock) return businessSubscriptions.Count; } }
    public int ServiceUnavailableResponses
    {
        get => Volatile.Read(ref serviceUnavailableResponses);
        set => Interlocked.Exchange(ref serviceUnavailableResponses, value);
    }
    public bool RejectNextPostAsClosedPeriod
    {
        get => Volatile.Read(ref rejectNextPostAsClosedPeriod);
        set => Volatile.Write(ref rejectNextPostAsClosedPeriod, value);
    }
    public bool ExpireTokenBeforeNextPost
    {
        get => Volatile.Read(ref expireTokenBeforeNextPost);
        set => Volatile.Write(ref expireTokenBeforeNextPost, value);
    }
    public Func<Task>? BeforeNextDocumentCreate { get; set; }
    public bool FailNextDocumentCreateAfterHook { get; set; }

    public Uri TokenUri(string tenant = DefaultTenant) => new(BaseUri,
        $"{Uri.EscapeDataString(tenant)}/oauth2/v2.0/token");

    public Uri StandardApiUri(string suffix = "", string tenant = DefaultTenant,
        string environment = DefaultEnvironment, bool includeTenant = true) => new(BaseUri,
            $"v2.0/{(includeTenant ? Uri.EscapeDataString(tenant) + "/" : "")}{Uri.EscapeDataString(environment)}/api/v2.0/{suffix}");

    public Uri CustomApiUri(string suffix = "", string tenant = DefaultTenant,
        string environment = DefaultEnvironment, bool includeTenant = true) => new(BaseUri,
            $"v2.0/{(includeTenant ? Uri.EscapeDataString(tenant) + "/" : "")}{Uri.EscapeDataString(environment)}/api/{Publisher}/{Group}/{Version}/{suffix}");

    public Uri RuntimeApiUri(string suffix = "", string tenant = DefaultTenant,
        string environment = DefaultEnvironment, bool includeTenant = true) => new(BaseUri,
            $"v2.0/{(includeTenant ? Uri.EscapeDataString(tenant) + "/" : "")}{Uri.EscapeDataString(environment)}/api/microsoft/runtime/v1.0/{suffix}");

    public void Seed(string resource, params Cx02SimulatorEntity[] entities)
    {
        if (!StandardResources.Contains(resource) && !CustomResources.Contains(resource))
            throw new ArgumentOutOfRangeException(nameof(resource), resource, "Unknown simulator resource.");
        lock (stateLock)
            resources[resource] = entities.Select(entity => NormaliseEntity(entity.Fields)).ToList();
    }

    public void QueueWebhookChange(string resource, Guid id, string changeType = "updated",
        bool applicationIdentity = false, string? headerResource = null)
    {
        if (changeType is not ("created" or "updated" or "deleted"))
            throw new ArgumentOutOfRangeException(nameof(changeType));
        var suppressed = SuppressNextNotificationAsUnlicensedActor
            || applicationIdentity && !Options.NotifyChangesByApplicationIdentity;
        SuppressNextNotificationAsUnlicensedActor = false;
        if (suppressed) return;
        lock (stateLock)
            pendingChanges.Add(new(headerResource ?? HeaderResource(resource), id, changeType, clock.GetUtcNow()));
    }

    public void StartThrottleStorm(int requestCount) =>
        Interlocked.Exchange(ref throttleStormRemaining, requestCount);

    public void ExpireCurrentTokens() => minimumAcceptedTokenSequence = Volatile.Read(ref tokenSequence) + 1;
    public void LapseSubscriptions() => lapseSubscriptions = true;
    public void TimeoutNextRequestWith408() => timeoutFault = HttpStatusCode.RequestTimeout;
    public void TimeoutNextRequestWith504() => timeoutFault = HttpStatusCode.GatewayTimeout;

    public async Task DispatchWebhooksAsync(CancellationToken cancellationToken = default)
    {
        List<PendingChange> ready;
        List<SubscriptionState> current;
        lock (stateLock)
        {
            ready = pendingChanges.Where(change => clock.GetUtcNow() - change.RecordedAt
                >= Options.WebhookCoalescingWindow).ToList();
            pendingChanges.RemoveAll(ready.Contains);
            current = subscriptions.Values.Where(subscription => subscription.ExpiresAt > clock.GetUtcNow()).ToList();
        }
        if (lapseSubscriptions) current = [];
        foreach (var byDestination in current.GroupBy(subscription => subscription.NotificationUrl))
        {
            var notifications = new List<object>();
            foreach (var subscription in byDestination)
            {
                var matching = ready.Where(change => ResourceMatches(subscription.Resource, change.Resource)).ToList();
                if (matching.Count == 0) continue;
                if (matching.Count > Options.CollectionNotificationThreshold)
                {
                    var changedAt = matching.Min(change => change.RecordedAt).UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
                    notifications.Add(Notification(subscription, "collection",
                        subscription.Resource + "?$filter=" + Options.CollectionFilterField + " gt " + changedAt,
                        matching.Max(change => change.RecordedAt)));
                }
                else
                {
                    notifications.AddRange(matching.Select(change => Notification(subscription, change.ChangeType,
                        subscription.Resource.TrimEnd('/') + "(" + change.Id + ")", change.RecordedAt)));
                }
            }
            if (ReverseNextNotificationBatch)
            {
                notifications.Reverse();
                ReverseNextNotificationBatch = false;
            }
            if (DuplicateNextNotification && notifications.Count > 0)
            {
                notifications.Add(notifications[0]);
                DuplicateNextNotification = false;
            }
            if (notifications.Count == 0) continue;
            await DeliverWebhookAsync(byDestination.Key, JsonSerializer.Serialize(new { value = notifications }),
                byDestination.Select(item => item.Id).ToArray(), cancellationToken);
        }
    }

    public async Task PublishBusinessEventAsync(string eventName, object payload, bool committed,
        CancellationToken cancellationToken = default)
    {
        if (!committed) return;
        List<BusinessSubscriptionState> targets;
        lock (stateLock) targets = businessSubscriptions.Values.Where(item => item.EventName == eventName
            && (item.ExpiresAt is null || item.ExpiresAt > clock.GetUtcNow())).ToList();
        foreach (var target in targets)
        {
            var body = JsonSerializer.Serialize(new
            {
                initiatingUserAADObjectId = "00000000-0000-0000-0000-000000000001",
                timestamp = clock.GetUtcNow(),
                companyName = "Fictional Hardware Services",
                companyId = CompanyId,
                eventName,
                payload,
                appId = "11111111-1111-1111-1111-111111111111",
                target.ClientState,
            });
            await DeliverBusinessEventAsync(target.NotificationUrl, body, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await loop; }
        catch (HttpListenerException) { }
        listener.Close();
        deliveryClient.Dispose();
        processingSlots.Dispose();
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
            lock (Requests) Requests.Add((request.HttpMethod, path));
            if (Regex.IsMatch(path, "^/[^/]+/oauth2/v2\\.0/token$", RegexOptions.CultureInvariant))
            {
                await HandleTokenAsync(request, response);
                return;
            }

            var route = ParseRoute(path);
            if (route is null)
            {
                await ErrorAsync(response, HttpStatusCode.NotFound, "BadRequest_NotFound", "The route was not found.");
                return;
            }
            if (route.Value.Tenant is { } tenant && Guid.TryParse(tenant, out _) && !Options.AcceptTenantGuid)
            {
                await ErrorAsync(response, HttpStatusCode.NotFound, "BadRequest_NotFound", "The tenant route was not found.");
                return;
            }
            var token = Authenticate(request);
            if (token is null)
            {
                await ErrorAsync(response, Options.ExpiredTokenStatus, "Authentication_InvalidCredentials",
                    "The access token is invalid or expired.");
                return;
            }
            await using var lease = await EnterThrottleAsync(token.Principal, response, stop.Token);
            if (lease is null) return;
            await RequestGate.WaitAsync(stop.Token);
            if (TakeServiceUnavailable())
            {
                await ErrorAsync(response, HttpStatusCode.ServiceUnavailable, "Internal_ServerBusy",
                    "The fictional remote service is unavailable.");
                return;
            }
            if (TakeTimeoutFault() is { } timeout)
            {
                await ErrorAsync(response, timeout, "Internal_Timeout", "The operation timed out.");
                return;
            }

            await HandleApiAsync(request, response, route.Value, token.Principal);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { context.Response.Abort(); }
        catch (Exception exception)
        {
            LastHandlerFailure = exception.ToString();
            if (context.Response.OutputStream.CanWrite) context.Response.StatusCode = 500;
            context.Response.Close();
        }
    }

    private async Task HandleTokenAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.Close();
            return;
        }
        var form = ParseForm(await ReadBodyAsync(request));
        if (form.GetValueOrDefault("grant_type") != "client_credentials"
            || form.GetValueOrDefault("client_secret") != Options.ClientSecret
            || form.GetValueOrDefault("scope") != Cx02SimulatorOptions.RequiredScope
            || string.IsNullOrWhiteSpace(form.GetValueOrDefault("client_id")))
        {
            response.StatusCode = (int)HttpStatusCode.BadRequest;
            await JsonAsync(response, new
            {
                error = "invalid_client",
                error_description = "The client credentials or scope are invalid.",
                error_codes = new[] { 7000215 },
                timestamp = clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
                trace_id = "00000000-0000-0000-0000-000000000001",
                correlation_id = "00000000-0000-0000-0000-000000000002",
            });
            return;
        }
        var value = "cx02-token-" + Interlocked.Increment(ref tokenSequence).ToString(CultureInfo.InvariantCulture);
        lock (stateLock)
            tokens[value] = new(form["client_id"], clock.GetUtcNow().AddSeconds(Options.TokenExpiresInSeconds), tokenSequence);
        await JsonAsync(response, new { token_type = "Bearer", expires_in = Options.TokenExpiresInSeconds, access_token = value });
    }

    private TokenState? Authenticate(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (header is null || !header.StartsWith("Bearer ", StringComparison.Ordinal)) return null;
        lock (stateLock)
        {
            if (!tokens.TryGetValue(header[7..], out var token)
                || token.Sequence < Volatile.Read(ref minimumAcceptedTokenSequence)
                || token.ExpiresAt <= clock.GetUtcNow())
                return null;
            return token;
        }
    }

    private async ValueTask<ThrottleLease?> EnterThrottleAsync(string principal, HttpListenerResponse response,
        CancellationToken cancellationToken)
    {
        if (TakeStormRequest())
        {
            await ThrottledAsync(response);
            return null;
        }
        var overWindowLimit = false;
        lock (stateLock)
        {
            if (!principalRequests.TryGetValue(principal, out var requests))
                principalRequests[principal] = requests = new();
            while (requests.TryPeek(out var oldest) && clock.GetUtcNow() - oldest >= Options.RequestWindow)
                requests.Dequeue();
            if (requests.Count >= Options.RequestLimit)
            {
                overWindowLimit = true;
            }
            else requests.Enqueue(clock.GetUtcNow());
        }
        if (overWindowLimit)
        {
            await ThrottledAsync(response);
            return null;
        }
        if (processingSlots.Wait(0, cancellationToken)) return new(processingSlots, false);
        if (Interlocked.Increment(ref queuedRequests) > Options.QueuedRequestLimit)
        {
            Interlocked.Decrement(ref queuedRequests);
            await ThrottledAsync(response);
            return null;
        }
        try
        {
            if (!await processingSlots.WaitAsync(Options.QueueWaitTimeout, cancellationToken))
            {
                await ErrorAsync(response, HttpStatusCode.ServiceUnavailable, "Internal_ServerBusy",
                    "The queued request exceeded its wait time.");
                return null;
            }
            return new(processingSlots, true);
        }
        finally
        {
            Interlocked.Decrement(ref queuedRequests);
        }
    }

    private async Task HandleApiAsync(HttpListenerRequest request, HttpListenerResponse response, ApiRoute route,
        string principal)
    {
        if (route.Remainder is "$batch")
        {
            await HandleBatchAsync(request, response);
            return;
        }
        if (route.Remainder is "companies" && request.HttpMethod == "GET")
        {
            await CollectionAsync(request, response, "companies",
                [NormaliseEntity(new Dictionary<string, object?>
                {
                    ["id"] = CompanyId, ["systemVersion"] = "1.0", ["name"] = "Fictional Hardware Services",
                    ["displayName"] = "Fictional Hardware Services", ["businessProfileId"] = "",
                    ["lastModifiedDateTime"] = "2026-09-01T08:00:00Z",
                })]);
            return;
        }
        if (route.Remainder is "subscriptions" || route.Remainder.StartsWith("subscriptions(", StringComparison.Ordinal))
        {
            await HandleSubscriptionsAsync(request, response, route);
            return;
        }
        if (route.Remainder is "microsoft/runtime/v1.0/externalbusinesseventdefinitions")
        {
            await JsonAsync(response, new { value = BusinessEventCatalogue() });
            return;
        }
        if (route.Remainder is "microsoft/runtime/v1.0/externaleventsubscriptions")
        {
            await HandleBusinessSubscriptionAsync(request, response, principal);
            return;
        }
        if (!TryCompanyResource(route.Remainder, out var resourcePath))
        {
            await ErrorAsync(response, HttpStatusCode.NotFound, "BadRequest_NotFound", "The resource was not found.");
            return;
        }
        if (await HandleActionAsync(request, response, resourcePath)) return;
        if (await HandleAttachmentAsync(request, response, resourcePath)) return;

        var resource = ResourceName(resourcePath);
        if (route.Custom ? !CustomResources.Contains(resource) : !StandardResources.Contains(resource))
        {
            await ErrorAsync(response, HttpStatusCode.NotFound, "BadRequest_NotFound", "The resource was not found.");
            return;
        }
        var key = ResourceKey(resourcePath);
        if (request.HttpMethod == "GET" && key is null)
        {
            List<Dictionary<string, object?>> values;
            lock (stateLock) values = resources.GetValueOrDefault(resource, []).Select(CloneEntity).ToList();
            await CollectionAsync(request, response, resource, values);
            return;
        }
        if (request.HttpMethod == "GET" && key is not null)
        {
            var found = Find(resource, key.Value);
            if (found is null) await ErrorAsync(response, HttpStatusCode.NotFound, "Internal_RecordNotFound", "The entity was not found.");
            else await JsonAsync(response, found);
            return;
        }
        if (request.HttpMethod == "PATCH" && key is not null)
        {
            await PatchAsync(request, response, resource, key.Value);
            return;
        }
        if (request.HttpMethod == "POST" && key is null && !ReadOnlyStandardResources.Contains(resource))
        {
            await CreateAsync(request, response, resource);
            return;
        }
        response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
        response.Close();
    }

    private async Task CollectionAsync(HttpListenerRequest request, HttpListenerResponse response, string resource,
        List<Dictionary<string, object?>> values)
    {
        var requested = RequestedPageSize(request);
        if (requested > Options.PageSizeCap)
        {
            await ErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "BadRequest_RequestTooLarge",
                "The requested page exceeds the maximum size.");
            return;
        }
        var modifiedAfter = ModifiedAfter(request.Url);
        if (modifiedAfter is not null)
            values = values.Where(value => DateTimeOffset.TryParse(value.GetValueOrDefault("lastModifiedDateTime")?.ToString(),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified) && modified > modifiedAfter).ToList();
        if (EqualityFilter(request.Url) is { } equality)
            values = values.Where(value => StringComparer.Ordinal.Equals(
                JsonValue(value.GetValueOrDefault(equality.Field)), equality.Value)).ToList();
        var skip = int.TryParse(Query(request.Url, "$skiptoken"), NumberStyles.None, CultureInfo.InvariantCulture,
            out var parsed) ? parsed : 0;
        var top = int.TryParse(Query(request.Url, "$top"), NumberStyles.None, CultureInfo.InvariantCulture,
            out parsed) ? parsed : int.MaxValue;
        var take = Math.Min(requested, top);
        var page = values.Skip(skip).Take(take).ToList();
        var body = new Dictionary<string, object?> { ["value"] = page };
        if (Options.IncludeNextLink && skip + page.Count < Math.Min(values.Count, skip + top))
        {
            var next = new UriBuilder(request.Url!) { Query = "$skiptoken=" + (skip + page.Count).ToString(CultureInfo.InvariantCulture) };
            body["@odata.nextLink"] = next.Uri.ToString();
        }
        await JsonAsync(response, body);
    }

    private async Task CreateAsync(HttpListenerRequest request, HttpListenerResponse response, string resource)
    {
        var body = await ReadJsonObjectAsync(request);
        body["id"] = body.TryGetValue("id", out var supplied) && Guid.TryParse(supplied?.ToString(), out var id)
            ? id : Guid.CreateVersion7();
        body["lastModifiedDateTime"] = clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        body["@odata.etag"] = "W/\"1\"";
        lock (stateLock) resources.GetValueOrDefault(resource)!.Add(body);
        CreateCount++;
        if (DropNextCreateReply)
        {
            DropNextCreateReply = false;
            response.StatusCode = (int)HttpStatusCode.Created;
            response.ContentType = "application/json";
            response.ContentLength64 = 128;
            await response.OutputStream.WriteAsync("{"u8.ToArray());
            response.Abort();
            return;
        }
        response.StatusCode = (int)HttpStatusCode.Created;
        await JsonAsync(response, body);
    }

    private async Task PatchAsync(HttpListenerRequest request, HttpListenerResponse response, string resource, Guid id)
    {
        var found = Find(resource, id);
        if (found is null)
        {
            await ErrorAsync(response, HttpStatusCode.NotFound, "Internal_RecordNotFound", "The entity was not found.");
            return;
        }
        var match = request.Headers["If-Match"];
        if (string.IsNullOrWhiteSpace(match))
        {
            await ErrorAsync(response, HttpStatusCode.BadRequest, "BadRequest_InvalidToken",
                "The concurrency token could not be validated.");
            return;
        }
        if (match != "*" && match != found["@odata.etag"]?.ToString())
        {
            await ErrorAsync(response, Options.EntityChangedStatus, "Request_EntityChanged",
                "Another user has already changed the record.");
            return;
        }
        var changes = await ReadJsonObjectAsync(request);
        lock (stateLock)
        {
            foreach (var change in changes) found[change.Key] = change.Value;
            found["@odata.etag"] = NextEtag(found["@odata.etag"]?.ToString());
            found["lastModifiedDateTime"] = clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }
        await JsonAsync(response, found);
    }

    private async Task<bool> HandleActionAsync(HttpListenerRequest request, HttpListenerResponse response,
        string resourcePath)
    {
        if (request.HttpMethod != "POST" || !resourcePath.Contains("/Microsoft.NAV.", StringComparison.Ordinal))
            return false;
        if (resourcePath.EndsWith("/Microsoft.NAV.post", StringComparison.Ordinal)
            && (resourcePath.StartsWith("salesCreditMemos(", StringComparison.Ordinal)
                || resourcePath.StartsWith("journals(", StringComparison.Ordinal)))
        {
            if (TakeClosedPeriodRejection())
            {
                await ErrorAsync(response, HttpStatusCode.BadRequest, "Application_DialogException",
                    "The posting period is closed.");
                return true;
            }
            PostedDocumentCount++;
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.Close();
            return true;
        }
        if (!resourcePath.EndsWith("/Microsoft.NAV.postTrackedDocument", StringComparison.Ordinal)) return false;
        if (TakeTokenExpiryBeforePost())
        {
            ExpireCurrentTokens();
            await ErrorAsync(response, Options.ExpiredTokenStatus, "Authentication_InvalidCredentials",
                "The access token expired before posting.");
            return true;
        }
        var body = await ReadBodyAsync(request);
        if (!Options.BoundActionAcceptsParameters && !string.IsNullOrWhiteSpace(body) && body != "{}")
        {
            await ErrorAsync(response, HttpStatusCode.BadRequest, "BadRequest_InvalidRequestBody",
                "This bound action does not accept parameters.");
            return true;
        }
        if (TakeClosedPeriodRejection())
        {
            await ErrorAsync(response, HttpStatusCode.BadRequest, "Application_DialogException",
                "The posting period is closed.");
            return true;
        }
        if (BeforeNextDocumentCreate is { } hook)
        {
            BeforeNextDocumentCreate = null;
            await hook();
            if (FailNextDocumentCreateAfterHook)
            {
                FailNextDocumentCreateAfterHook = false;
                await ErrorAsync(response, HttpStatusCode.ServiceUnavailable, "Internal_ServerBusy",
                    "The fictional create failed after entering its test gate.");
                return true;
            }
        }
        var values = string.IsNullOrWhiteSpace(body)
            ? new Dictionary<string, JsonElement>()
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(body) ?? [];
        var logicalReference = BodyText(values, "logicalReference") ?? "FICTIONAL-POST-1001";
        var documentType = BodyText(values, "documentType") ?? "trackedDocument";
        var documentId = values.TryGetValue("documentId", out var requestedId) && requestedId.TryGetGuid(out var parsedId)
            ? parsedId : Guid.CreateVersion7();
        if (documentType == "serviceOrder")
        {
            lock (stateLock) resources["serviceItems"].Add(NormaliseEntity(new Dictionary<string, object?>
            {
                ["id"] = documentId, ["number"] = logicalReference,
                ["serialNumber"] = MetadataText(values, "serialNumber") ?? FirstTrackingValue(values, "serialNumber"),
                ["itemNumber"] = MetadataText(values, "itemNumber"), ["status"] = "Open",
            }));
        }
        else
        {
            lock (stateLock) resources["trackedItemLedgerEntries"].Add(NormaliseEntity(new Dictionary<string, object?>
            {
                ["id"] = documentId, ["entryNumber"] = 9000 + PostedDocumentCount,
                ["itemNumber"] = FirstTrackingValue(values, "itemNumber"),
                ["postingDate"] = clock.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["entryType"] = documentType, ["documentNumber"] = logicalReference,
                ["documentType"] = documentType, ["quantity"] = -1m,
                ["serialNumber"] = FirstTrackingValue(values, "serialNumber"),
                ["lotNumber"] = "", ["packageNumber"] = "",
            }));
        }
        PostedDocumentCount++;
        if (DropNextPostReply)
        {
            DropNextPostReply = false;
            response.StatusCode = (int)HttpStatusCode.Created;
            response.ContentType = "application/json";
            response.ContentLength64 = 128;
            await response.OutputStream.WriteAsync("{"u8.ToArray());
            response.Abort();
            return true;
        }
        response.StatusCode = (int)HttpStatusCode.Created;
        await JsonAsync(response, new
        {
            id = documentId, documentNumber = logicalReference, status = "posted",
            postedAt = clock.GetUtcNow(),
        });
        return true;
    }

    private async Task<bool> HandleAttachmentAsync(HttpListenerRequest request, HttpListenerResponse response,
        string resourcePath)
    {
        var resource = ResourceName(resourcePath);
        if (resource is not ("attachments" or "documentAttachments")) return false;
        if (request.HttpMethod == "PATCH" && resourcePath.EndsWith("/attachmentContent", StringComparison.Ordinal)
            && Options.AttachmentUploadPattern == Cx02AttachmentUploadPattern.SeparateContent)
        {
            var bytes = await ReadBytesAsync(request);
            if (bytes.LongLength > Options.AttachmentSizeLimit)
            {
                await ErrorAsync(response, HttpStatusCode.RequestEntityTooLarge, "BadRequest_RequestTooLarge",
                    "The attachment exceeds the configured size limit.");
                return true;
            }
            AttachmentUploadCount++;
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.Close();
            return true;
        }
        if (request.HttpMethod != "POST") return false;
        Dictionary<string, object?> body;
        if (Options.AttachmentUploadPattern == Cx02AttachmentUploadPattern.InlineContent)
        {
            body = await ReadJsonObjectAsync(request);
            if (!body.ContainsKey("attachmentContent"))
            {
                await ErrorAsync(response, HttpStatusCode.BadRequest, "Application_FieldValidationException",
                    "attachmentContent is required.");
                return true;
            }
        }
        else
        {
            body = request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true
                ? await ReadJsonObjectAsync(request)
                : new Dictionary<string, object?> { ["attachmentContent"] = Convert.ToBase64String(await ReadBytesAsync(request)) };
        }
        var parentType = body.GetValueOrDefault("parentType")?.ToString() ?? "";
        var allowed = resource == "attachments" ? IncomingAttachmentParents : Options.DocumentAttachmentParentTypes;
        if (!allowed.Contains(parentType))
        {
            await ErrorAsync(response, HttpStatusCode.BadRequest, "Application_FieldValidationException",
                "The attachment parentType is not supported.");
            return true;
        }
        body["id"] = Guid.CreateVersion7();
        body["lastModifiedDateTime"] = clock.GetUtcNow();
        body["@odata.etag"] = "W/\"1\"";
        lock (stateLock) resources[resource].Add(NormaliseEntity(body));
        response.StatusCode = (int)HttpStatusCode.Created;
        await JsonAsync(response, body);
        return true;
    }

    private async Task HandleSubscriptionsAsync(HttpListenerRequest request, HttpListenerResponse response,
        ApiRoute route)
    {
        var id = ResourceKey(route.Remainder);
        if (request.HttpMethod == "POST" && id is null)
        {
            if (ActiveSubscriptionCount >= Options.SubscriptionCap)
            {
                await ThrottledAsync(response);
                return;
            }
            var body = await ReadJsonObjectAsync(request);
            var notificationUrl = new Uri(body["notificationUrl"]!.ToString()!, UriKind.Absolute);
            if (!await HandshakeAsync(notificationUrl, cancellationToken: stop.Token))
            {
                await ErrorAsync(response, HttpStatusCode.BadRequest, "Application_DialogException",
                    "The notification endpoint did not complete validation.");
                return;
            }
            var subscription = new SubscriptionState(Guid.CreateVersion7(), notificationUrl,
                body["resource"]!.ToString()!, body.GetValueOrDefault("clientState")?.ToString(),
                clock.GetUtcNow(), clock.GetUtcNow().Add(Options.SubscriptionLifetime), "W/\"1\"");
            lock (stateLock) subscriptions[subscription.Id] = subscription;
            response.StatusCode = (int)HttpStatusCode.Created;
            await JsonAsync(response, SubscriptionBody(subscription));
            return;
        }
        if (id is not { } subscriptionId || !subscriptions.TryGetValue(subscriptionId, out var existing))
        {
            await ErrorAsync(response, HttpStatusCode.NotFound, "Internal_RecordNotFound", "The subscription was not found.");
            return;
        }
        if (request.HttpMethod == "PATCH")
        {
            var etag = request.Headers["If-Match"];
            if (etag != "*" && etag != existing.Etag)
            {
                await ErrorAsync(response, HttpStatusCode.BadRequest, "BadRequest_InvalidToken",
                    "A matching concurrency token is required.");
                return;
            }
            var changes = await ReadJsonObjectAsync(request);
            var destination = changes.TryGetValue("notificationUrl", out var changedUrl)
                ? new Uri(changedUrl!.ToString()!, UriKind.Absolute) : existing.NotificationUrl;
            if (!await HandshakeAsync(destination, cancellationToken: stop.Token))
            {
                await ErrorAsync(response, HttpStatusCode.BadRequest, "Application_DialogException",
                    "The notification endpoint did not complete validation.");
                return;
            }
            existing = existing with
            {
                NotificationUrl = destination,
                ClientState = changes.GetValueOrDefault("clientState")?.ToString() ?? existing.ClientState,
                ExpiresAt = clock.GetUtcNow().Add(Options.SubscriptionLifetime), Etag = NextEtag(existing.Etag),
            };
            lock (stateLock) subscriptions[subscriptionId] = existing;
            lapseSubscriptions = false;
            await JsonAsync(response, SubscriptionBody(existing));
            return;
        }
        if (request.HttpMethod == "DELETE")
        {
            lock (stateLock) subscriptions.Remove(subscriptionId);
            response.StatusCode = (int)HttpStatusCode.NoContent;
            response.Close();
            return;
        }
        if (request.HttpMethod == "GET")
        {
            await JsonAsync(response, SubscriptionBody(existing));
            return;
        }
        response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
        response.Close();
    }

    private async Task HandleBusinessSubscriptionAsync(HttpListenerRequest request, HttpListenerResponse response,
        string principal)
    {
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
            response.Close();
            return;
        }
        if (!Options.AllowApplicationBusinessEventSubscription && !string.IsNullOrEmpty(principal))
        {
            await ErrorAsync(response, HttpStatusCode.Forbidden, "Authorization_PermissionDenied",
                "Application identities cannot create this subscription.");
            return;
        }
        var body = await ReadJsonObjectAsync(request);
        if (!body.TryGetValue("appId", out var appId) || !Guid.TryParse(appId?.ToString(), out _))
        {
            await ErrorAsync(response, HttpStatusCode.BadRequest, "Application_FieldValidationException",
                "A business-event application identifier is required.");
            return;
        }
        var destination = new Uri(body["notificationUrl"]!.ToString()!, UriKind.Absolute);
        if (Options.BusinessEventRequiresHandshake && !await HandshakeAsync(destination, cancellationToken: stop.Token))
        {
            await ErrorAsync(response, HttpStatusCode.BadRequest, "Application_DialogException",
                "The notification endpoint did not complete validation.");
            return;
        }
        var item = new BusinessSubscriptionState(Guid.CreateVersion7(), body["eventName"]!.ToString()!, destination,
            body.GetValueOrDefault("clientState")?.ToString(), Options.BusinessEventSubscriptionLifetime is { } lifetime
                ? clock.GetUtcNow().Add(lifetime) : null);
        lock (stateLock) businessSubscriptions[item.Id] = item;
        response.StatusCode = (int)HttpStatusCode.Created;
        response.Close();
    }

    private async Task HandleBatchAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        var body = await ReadBodyAsync(request);
        int count;
        if (request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            using var json = JsonDocument.Parse(body);
            count = json.RootElement.GetProperty("requests").GetArrayLength();
        }
        else count = Regex.Count(body, "^Content-Type: application/http", RegexOptions.Multiline);
        if (count > 100)
        {
            await ErrorAsync(response, HttpStatusCode.BadRequest, "BadRequest_TooManyOperations",
                "A batch can contain at most 100 operations.");
            return;
        }
        await JsonAsync(response, new { responses = Enumerable.Range(0, count).Select(index => new { id = index, status = 204 }) });
    }

    private async Task<bool> HandshakeAsync(Uri notificationUrl, CancellationToken cancellationToken)
    {
        var token = "validation-" + Guid.CreateVersion7().ToString("N", CultureInfo.InvariantCulture);
        var separator = string.IsNullOrEmpty(notificationUrl.Query) ? "?" : "&";
        var target = new Uri(notificationUrl + separator + "validationToken=" + Uri.EscapeDataString(token));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Options.HandshakeTimeout);
        using var message = new HttpRequestMessage(Options.HandshakeMethod, target);
        try
        {
            using var reply = await deliveryClient.SendAsync(message, timeout.Token);
            return reply.StatusCode == HttpStatusCode.OK && await reply.Content.ReadAsStringAsync(timeout.Token) == token
                && reply.Content.Headers.ContentType?.MediaType == Options.HandshakeContentType;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task DeliverWebhookAsync(Uri destination, string body, Guid[] subscriptionIds,
        CancellationToken cancellationToken)
    {
        var intervals = new[] { TimeSpan.Zero }.Concat(Options.WebhookRetrySchedule).ToArray();
        var elapsed = TimeSpan.Zero;
        for (var attempt = 0; attempt < intervals.Length && elapsed <= Options.WebhookRetryWindow; attempt++)
        {
            elapsed += intervals[attempt];
            if (elapsed > Options.WebhookRetryWindow) break;
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var reply = await deliveryClient.PostAsync(destination, content, cancellationToken);
            DeliveredNotifications.Add(new("webhook", destination, body, attempt + 1, clock.GetUtcNow().Add(elapsed)));
            if (Options.DeliveredWebhookStatuses.Contains(reply.StatusCode)) return;
            if (reply.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                || (int)reply.StatusCode >= 500) continue;
            lock (stateLock)
                foreach (var id in subscriptionIds) subscriptions.Remove(id);
            return;
        }
    }

    private async Task DeliverBusinessEventAsync(Uri destination, string body, CancellationToken cancellationToken)
    {
        var attempts = Options.RetryBusinessEvents ? 2 : 1;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var reply = await deliveryClient.PostAsync(destination, content, cancellationToken);
            DeliveredNotifications.Add(new("business-event", destination, body, attempt, clock.GetUtcNow()));
            if (reply.IsSuccessStatusCode) return;
        }
    }

    private async Task ThrottledAsync(HttpListenerResponse response)
    {
        if (Options.IncludeRetryAfter) response.Headers["Retry-After"] = "1";
        await ErrorAsync(response, HttpStatusCode.TooManyRequests, Options.ThrottleErrorCode,
            "The principal exceeded its request limit.");
    }

    private async Task ErrorAsync(HttpListenerResponse response, HttpStatusCode status, string code, string message)
    {
        response.StatusCode = (int)status;
        if (Options.UseODataErrorEnvelope) await JsonAsync(response, new { error = new { code, message } });
        else await JsonAsync(response, new { code, message });
    }

    private static async Task<JsonElement> JsonAsync(HttpListenerResponse response, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
        return JsonSerializer.SerializeToElement(value);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest request) =>
        await new StreamReader(request.InputStream, request.ContentEncoding).ReadToEndAsync();

    private static async Task<byte[]> ReadBytesAsync(HttpListenerRequest request)
    {
        using var stream = new MemoryStream();
        await request.InputStream.CopyToAsync(stream);
        return stream.ToArray();
    }

    private static async Task<Dictionary<string, object?>> ReadJsonObjectAsync(HttpListenerRequest request) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(await ReadBodyAsync(request)) ?? [];

    private static Dictionary<string, string> ParseForm(string body) => body.Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(part => part.Split('=', 2)).ToDictionary(part => Uri.UnescapeDataString(part[0].Replace('+', ' ')),
            part => part.Length == 1 ? "" : Uri.UnescapeDataString(part[1].Replace('+', ' ')), StringComparer.Ordinal);

    private ApiRoute? ParseRoute(string path)
    {
        var segments = path.Trim('/').Split('/');
        if (segments.Length < 5 || segments[0] != "v2.0") return null;
        var apiIndex = Array.IndexOf(segments, "api");
        if (apiIndex is not (2 or 3)) return null;
        var tenant = apiIndex == 3 ? segments[1] : null;
        var environment = segments[apiIndex - 1];
        if (segments.Length > apiIndex + 1 && segments[apiIndex + 1] == "v2.0")
            return new(tenant, environment, false, string.Join('/', segments.Skip(apiIndex + 2)));
        if (segments.Length > apiIndex + 3 && segments[apiIndex + 1] == Publisher
            && segments[apiIndex + 2] == Group && segments[apiIndex + 3] == Version)
            return new(tenant, environment, true, string.Join('/', segments.Skip(apiIndex + 4)));
        if (segments.Length > apiIndex + 3 && segments[apiIndex + 1] == "microsoft"
            && segments[apiIndex + 2] == "runtime")
            return new(tenant, environment, false, string.Join('/', segments.Skip(apiIndex + 1)));
        return null;
    }

    private static bool TryCompanyResource(string remainder, out string resource)
    {
        var match = Regex.Match(remainder, "^companies\\([^)]+\\)/(.+)$", RegexOptions.CultureInvariant);
        resource = match.Success ? match.Groups[1].Value : "";
        return match.Success;
    }

    private static string ResourceName(string path)
    {
        var part = path.Split('/').Last(segment => !segment.StartsWith("Microsoft.NAV.", StringComparison.Ordinal)
            && segment != "attachmentContent");
        var parenthesis = part.IndexOf('(');
        return parenthesis < 0 ? part : part[..parenthesis];
    }

    private static Guid? ResourceKey(string path)
    {
        var match = Regex.Match(path, "\\((?<id>[0-9a-fA-F-]{36})\\)", RegexOptions.CultureInvariant);
        return match.Success && Guid.TryParse(match.Groups["id"].Value, out var id) ? id : null;
    }

    private Dictionary<string, object?>? Find(string resource, Guid id)
    {
        lock (stateLock) return resources.GetValueOrDefault(resource)?.FirstOrDefault(entity =>
            Guid.TryParse(entity.GetValueOrDefault("id")?.ToString(), out var found) && found == id);
    }

    private int RequestedPageSize(HttpListenerRequest request)
    {
        var prefer = request.Headers["Prefer"];
        var match = Regex.Match(prefer ?? "", "(?:^|;)\\s*odata\\.maxpagesize=(?<size>\\d+)",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        return match.Success ? int.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture) : Options.PageSizeCap;
    }

    private static DateTimeOffset? ModifiedAfter(Uri? uri)
    {
        var filter = Query(uri, "$filter");
        if (filter is null) return null;
        var match = Regex.Match(filter, "^lastModifiedDateTime gt (?<value>.+)$", RegexOptions.CultureInvariant);
        return match.Success && DateTimeOffset.TryParse(match.Groups["value"].Value.Trim('\''), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    }

    private static (string Field, string Value)? EqualityFilter(Uri? uri)
    {
        var filter = Query(uri, "$filter");
        if (filter is null || filter.StartsWith("lastModifiedDateTime gt ", StringComparison.Ordinal)) return null;
        var match = Regex.Match(filter, "^(?<field>[A-Za-z0-9_@.]+) eq '(?<value>(?:''|[^'])*)'$",
            RegexOptions.CultureInvariant);
        return match.Success
            ? (match.Groups["field"].Value, match.Groups["value"].Value.Replace("''", "'", StringComparison.Ordinal))
            : null;
    }

    private static string? JsonValue(object? value) => value switch
    {
        null => null,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
        JsonElement element => element.ToString(),
        _ => value.ToString(),
    };

    private static string FirstTrackingValue(Dictionary<string, JsonElement> body, string field)
    {
        if (!body.TryGetValue("tracking", out var tracking) || tracking.ValueKind != JsonValueKind.Array
            || tracking.GetArrayLength() == 0) return "";
        var row = tracking[0];
        return row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? "" : "";
    }

    private static string? MetadataText(Dictionary<string, JsonElement> body, string field) =>
        body.TryGetValue("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            && metadata.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;

    private static string? BodyText(Dictionary<string, JsonElement> body, string field) =>
        body.TryGetValue(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Query(Uri? uri, string name)
    {
        if (uri is null) return null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == name)
                return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
        }
        return null;
    }

    private static Dictionary<string, object?> NormaliseEntity(IReadOnlyDictionary<string, object?> fields)
    {
        var value = fields.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
        if (!value.ContainsKey("id")) value["id"] = Guid.CreateVersion7();
        if (!value.ContainsKey("lastModifiedDateTime")) value["lastModifiedDateTime"] = "2026-09-01T08:00:00Z";
        if (!value.ContainsKey("@odata.etag")) value["@odata.etag"] = "W/\"1\"";
        return value;
    }

    private static Dictionary<string, object?> CloneEntity(Dictionary<string, object?> entity) =>
        JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(entity))!;

    private static string NextEtag(string? current)
    {
        var digits = new string((current ?? "").Where(char.IsDigit).ToArray());
        var value = int.TryParse(digits, out var parsed) ? parsed + 1 : 1;
        return $"W/\"{value}\"";
    }

    private static string HeaderResource(string resource) => resource switch
    {
        "salesInvoiceLines" => "salesInvoices",
        "salesCreditMemoLines" => "salesCreditMemos",
        "postedTransferShipmentLines" => "postedTransferShipments",
        "postedTransferReceiptLines" => "postedTransferReceipts",
        "postedReturnReceiptLines" => "postedReturnReceipts",
        _ => resource,
    };

    private static bool ResourceMatches(string subscribed, string changed) =>
        ResourceName(subscribed) == changed;

    private static object Notification(SubscriptionState subscription, string changeType, string resource,
        DateTimeOffset changedAt) => new
    {
        subscriptionId = subscription.Id,
        clientState = subscription.ClientState,
        expirationDateTime = subscription.ExpiresAt,
        resource,
        changeType,
        lastModifiedDateTime = changedAt,
    };

    private static Dictionary<string, object?> SubscriptionBody(SubscriptionState item) => new()
    {
        ["subscriptionId"] = item.Id,
        ["notificationUrl"] = item.NotificationUrl,
        ["resource"] = item.Resource,
        ["timestamp"] = item.CreatedAt,
        ["userId"] = Guid.Empty,
        ["lastModifiedDateTime"] = item.CreatedAt,
        ["clientState"] = item.ClientState,
        ["expirationDateTime"] = item.ExpiresAt,
        ["systemCreatedAt"] = item.CreatedAt,
        ["systemCreatedBy"] = Guid.Empty,
        ["systemModifiedAt"] = item.CreatedAt,
        ["systemModifiedBy"] = Guid.Empty,
        ["@odata.etag"] = item.Etag,
    };

    private static object[] BusinessEventCatalogue() =>
    [
        new { category = "Sales", name = "salesShipmentPosted", displayName = "Sales shipment posted",
            description = "A sales shipment was posted.", payload = "[{\"Index\":0,\"Name\":\"documentId\",\"Type\":\"Guid\"}]",
            appId = "00000000-0000-0000-0000-000000000001", appName = "Base Application", appVersion = "1.0",
            appPublisher = "Platform publisher" },
        new { category = "Sales", name = "salesCreditMemoPosted", displayName = "Sales credit memo posted",
            description = "A sales credit memo was posted.", payload = "[{\"Index\":0,\"Name\":\"documentId\",\"Type\":\"Guid\"}]",
            appId = "00000000-0000-0000-0000-000000000001", appName = "Base Application", appVersion = "1.0",
            appPublisher = "Platform publisher" },
        new { category = "Purchase", name = "purchaseReceiptPosted", displayName = "Purchase receipt posted",
            description = "A purchase receipt was posted.", payload = "[{\"Index\":0,\"Name\":\"documentId\",\"Type\":\"Guid\"}]",
            appId = "00000000-0000-0000-0000-000000000001", appName = "Base Application", appVersion = "1.0",
            appPublisher = "Platform publisher" },
    ];

    private void SeedDefaults()
    {
        var now = "2026-09-01T08:00:00Z";
        resources["items"] = [NormaliseEntity(new Dictionary<string, object?> { ["number"] = "HW-SERVER-100", ["displayName"] = "Fictional rack server", ["lastModifiedDateTime"] = now })];
        resources["customers"] = [NormaliseEntity(new Dictionary<string, object?> { ["number"] = "C-100", ["displayName"] = "Fictional Systems Ltd", ["lastModifiedDateTime"] = now })];
        resources["salesInvoices"] = [NormaliseEntity(new Dictionary<string, object?> { ["number"] = "INV-100", ["lastModifiedDateTime"] = now })];
        resources["salesInvoiceLines"] = [];
        resources["salesCreditMemos"] = [NormaliseEntity(new Dictionary<string, object?> { ["number"] = "CRM-100", ["lastModifiedDateTime"] = now })];
        resources["salesCreditMemoLines"] = [];
        resources["journals"] = [];
        resources["journalLines"] = [];
        resources["attachments"] = [];
        resources["documentAttachments"] = [];
        resources["salesShipments"] = [NormaliseEntity(new Dictionary<string, object?> { ["number"] = "SHIP-100", ["lastModifiedDateTime"] = now })];
        resources["salesShipmentLines"] = [NormaliseEntity(new Dictionary<string, object?> { ["lineObjectNumber"] = "HW-SERVER-100", ["quantity"] = 1m, ["shipmentDate"] = "2026-09-01", ["lastModifiedDateTime"] = now })];
        resources["purchaseReceipts"] = [NormaliseEntity(new Dictionary<string, object?> { ["number"] = "REC-100", ["lastModifiedDateTime"] = now })];
        resources["purchaseReceiptLines"] = [NormaliseEntity(new Dictionary<string, object?> { ["lineObjectNumber"] = "HW-SERVER-100", ["quantity"] = 1m, ["lastModifiedDateTime"] = now })];
        resources["itemLedgerEntries"] = [NormaliseEntity(new Dictionary<string, object?> { ["entryNumber"] = 1001, ["itemNumber"] = "HW-SERVER-100", ["postingDate"] = "2026-09-01", ["entryType"] = "Sale", ["sourceNumber"] = "C-100", ["sourceType"] = "Customer", ["documentNumber"] = "SHIP-100", ["documentType"] = "Sales Shipment", ["description"] = "Fictional rack server", ["quantity"] = -1m, ["salesAmountActual"] = 1000m, ["costAmountActual"] = 700m, ["lastModifiedDateTime"] = now })];
        foreach (var resource in CustomResources) resources[resource] = [];
        resources["trackedItemLedgerEntries"] = [NormaliseEntity(new Dictionary<string, object?> { ["entryNumber"] = 1001, ["itemNumber"] = "HW-SERVER-100", ["postingDate"] = "2026-09-01", ["entryType"] = "Sale", ["documentNumber"] = "SHIP-100", ["documentType"] = "Sales Shipment", ["quantity"] = -1m, ["serialNumber"] = "SN-FICTIONAL-001", ["lotNumber"] = "", ["packageNumber"] = "", ["lastModifiedDateTime"] = now })];
    }

    private bool TakeStormRequest()
    {
        while (true)
        {
            var current = Volatile.Read(ref throttleStormRemaining);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref throttleStormRemaining, current - 1, current) == current) return true;
        }
    }

    private bool TakeServiceUnavailable()
    {
        while (true)
        {
            var current = Volatile.Read(ref serviceUnavailableResponses);
            if (current <= 0) return false;
            if (Interlocked.CompareExchange(ref serviceUnavailableResponses, current - 1, current) == current)
                return true;
        }
    }

    private bool TakeClosedPeriodRejection() => Interlocked.Exchange(ref rejectNextPostAsClosedPeriod, false);
    private bool TakeTokenExpiryBeforePost() => Interlocked.Exchange(ref expireTokenBeforeNextPost, false);

    private HttpStatusCode? TakeTimeoutFault()
    {
        lock (stateLock)
        {
            var value = timeoutFault;
            timeoutFault = null;
            return value;
        }
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private readonly record struct ApiRoute(string? Tenant, string Environment, bool Custom, string Remainder);
    private sealed record TokenState(string Principal, DateTimeOffset ExpiresAt, int Sequence);
    private sealed record SubscriptionState(Guid Id, Uri NotificationUrl, string Resource, string? ClientState,
        DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Etag);
    private sealed record BusinessSubscriptionState(Guid Id, string EventName, Uri NotificationUrl,
        string? ClientState, DateTimeOffset? ExpiresAt);
    private sealed record PendingChange(string Resource, Guid Id, string ChangeType, DateTimeOffset RecordedAt);

    private sealed class ThrottleLease(SemaphoreSlim semaphore, bool wasQueued) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            semaphore.Release();
            _ = wasQueued;
            return ValueTask.CompletedTask;
        }
    }
}
