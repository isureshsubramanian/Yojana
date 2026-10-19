using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Yojana.Integration.Connectors.CX01.Testing;

internal sealed record Cx01SimulatorNotification(
    string EventId,
    string Status,
    string BusinessObject,
    string Key,
    string CreateDate,
    string CreateTime,
    bool EmptyDeliveryDetails = false);

internal sealed class Cx01Simulator : IAsyncDisposable
{
    public static IReadOnlyList<string> TrackingTables { get; } = ["OITL", "ITL1", "OSRN"];
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly string createdDelivery = Fixture("delivery-created.json");
    private readonly string returnedDelivery = Fixture("delivery-after-return.json");
    private readonly string item = Fixture("item.json");
    private string session = "session-1";
    private int authenticatedRequests;
    private bool subscriptionActive;
    private bool serialQueryCreated;
    private string returnUpdateTime = "090000";
    private string deliveryUpdateTime = "081500";

    public Cx01Simulator()
    {
        var port = FreePort();
        BaseUri = new Uri($"http://127.0.0.1:{port}/");
        listener.Prefixes.Add(BaseUri.ToString());
        listener.Start();
        loop = RunAsync();
    }

    public Uri BaseUri { get; }
    public int LoginCount { get; private set; }
    public int ExpireAfterAuthenticatedRequests { get; set; } = int.MaxValue;
    public bool RateLimitNextRequest { get; set; }
    public bool SameWarehouseReturnPosted { get; set; }
    public bool DeliveryCancelled { get; set; }
    public bool SubscriptionStopped { get; set; }
    public List<string> DeletedSubscriptionPaths { get; } = [];
    public bool WebhookSubscriptionCreated { get; private set; }
    public bool WebhookSubscriptionPaused { get; private set; }
    public bool WebhookSubscriptionDeleted { get; private set; }
    public bool FailedNotificationAvailable { get; set; }
    public List<Cx01SimulatorNotification> EventNotifications { get; } = [];
    public int EventNotificationPageSize { get; set; } = 200;
    public List<string> EventNotificationQueries { get; } = [];
    public string? LastReferenceValue { get; private set; }
    public JsonDocument? LastSubscriptionBody { get; private set; }
    public JsonDocument? LastPatchBody { get; private set; }
    public HttpStatusCode? RejectNextPatchWith { get; set; }
    public int PatchAttempts { get; private set; }
    public int RequestCount { get; private set; }
    public string? LastHandlerFailure { get; private set; }
    public int ServiceUnavailableResponses { get; set; }
    public bool FailMetadataRead { get; set; }
    public bool FailUnsubscribe { get; set; }
    public bool TrackingTablesAllowed { get; set; } = true;
    public bool SerialQueryCreated => serialQueryCreated;
    public int SerialQueryCreateCount { get; private set; }
    public bool SelectEmptiedSerials { get; private set; }
    public bool RejectNextDeliveryReadWithBusinessError { get; set; }
    public List<string> CaptureReadPaths { get; } = [];
    public void SimulateLaterUserEdit() => returnUpdateTime = "090200";
    public void SimulateLaterDeliveryEdit(string updateTime) => deliveryUpdateTime = updateTime;
    public int PageSize { get; set; } = 1;
    public List<string> ReceivedCookieHeaders { get; } = [];

    public string OwnSubscriptionId(Guid tenantId, Guid connectionId) => Cx01Protocol.WebhookId(tenantId, connectionId);

    public Yojana.Integration.Connections.RegisterConnection RegistrationRequest(Guid? requestId = null) =>
        new(requestId ?? Guid.CreateVersion7(), Cx01Capabilities.ConnectorCode, "Fictional main connection", Guid.CreateVersion7(),
            JsonSerializer.SerializeToElement(new Cx01ConnectionSettings(BaseUri, "FICTIONAL_DEMO", "test"),
                Yojana.Integration.Contracts.IntegrationContracts.Json),
            [new(Cx01Client.CredentialPurpose, JsonSerializer.Serialize(new Cx01Credential("fictional-user", "not-logged"),
                Yojana.Integration.Contracts.IntegrationContracts.Json))]);

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await loop; }
        catch (HttpListenerException) { }
        listener.Close();
        LastSubscriptionBody?.Dispose();
        LastPatchBody?.Dispose();
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
            RequestCount++;
            var response = context.Response;
            if (request.Url?.AbsolutePath == Cx01Protocol.Login)
            {
                LoginCount++;
                authenticatedRequests = 0;
                session = "session-" + LoginCount;
                response.Headers.Add("Set-Cookie", Cx01Protocol.SessionCookie + "=" + session
                    + "; Path=/b1s/v2; HttpOnly");
                response.AppendHeader("Set-Cookie", Cx01Protocol.RouteCookie + "=.node1; Path=/b1s/v2; HttpOnly");
                await JsonAsync(response, new { SessionId = session, SessionTimeout = 30, Version = "1000340" });
                return;
            }

            var cookies = request.Headers["Cookie"] ?? "";
            ReceivedCookieHeaders.Add(cookies);
            if (!cookies.Contains(Cx01Protocol.SessionCookie + "=" + session, StringComparison.Ordinal)
                || !cookies.Contains(Cx01Protocol.RouteCookie + "=.node1", StringComparison.Ordinal)
                || authenticatedRequests++ >= ExpireAfterAuthenticatedRequests)
            {
                response.StatusCode = (int)HttpStatusCode.Unauthorized;
                response.Close();
                return;
            }
            if (RateLimitNextRequest)
            {
                RateLimitNextRequest = false;
                response.StatusCode = (int)HttpStatusCode.TooManyRequests;
                response.Headers["Retry-After"] = "0";
                response.Close();
                return;
            }
            if (ServiceUnavailableResponses > 0)
            {
                ServiceUnavailableResponses--;
                response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                response.Headers["Retry-After"] = "0";
                response.Close();
                return;
            }

            var path = request.Url?.AbsolutePath ?? "";
            if (path == Cx01Protocol.Metadata)
            {
                if (FailMetadataRead)
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    await ErrorAsync(response, "403", "Fictional permission denial");
                    return;
                }
                await TextAsync(response,
                    "<edmx:Edmx><EntitySet Name=\"EventSubscriptions\"/><EntitySet Name=\"EventNotifications\"/></edmx:Edmx>",
                    "application/xml");
                return;
            }
            if (path == Cx01Protocol.Root)
            {
                await JsonAsync(response, new { Version = "1000340" });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.AdminInfo && request.HttpMethod == "POST")
            {
                await JsonAsync(response, new
                {
                    EnableWebhook = "tYES",
                    WebhookRetryTimes = 3,
                    WebhookRetryInterval = 2,
                    WebhookRequestTimeout = 10,
                    MaxNumberOfWebHooks = 16,
                });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.UserFields)
            {
                await JsonAsync(response, new
                {
                    value = new object[]
                    {
                        new { TableName = "ORDN", Name = "RETURN_REASON", Type = "db_Alpha", Mandatory = "tYES", DefaultValue = (string?)null },
                        new { TableName = "OITM", Name = "FICTIONAL_TAG", Type = "db_Alpha", Mandatory = "tNO", DefaultValue = (string?)null },
                    },
                });
                return;
            }
            if (path == Cx01Protocol.Root + "Items('HW-EDGE-100')")
            {
                await RawJsonAsync(response, item);
                return;
            }
            if (path == Cx01Protocol.Root + "ItemWarehouseInfoCollection")
            {
                await JsonAsync(response, new
                {
                    value = new[]
                    {
                        new { ItemCode = "HW-EDGE-100", WarehouseCode = "WH-A", InStock = 2m },
                    },
                });
                return;
            }
            if (path is var deliveryPath && (deliveryPath == Cx01Protocol.Root + "DeliveryNotes(4101)"
                || deliveryPath == Cx01Protocol.Root + "DeliveryNotes(4102)"
                || deliveryPath == Cx01Protocol.Root + "DeliveryNotes(4103)"))
            {
                if (RejectNextDeliveryReadWithBusinessError)
                {
                    RejectNextDeliveryReadWithBusinessError = false;
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await ErrorAsync(response, "24001", "(custom) Read blocked by a fictional validation");
                    return;
                }
                using var source = JsonDocument.Parse(SameWarehouseReturnPosted ? returnedDelivery : createdDelivery);
                var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(source.RootElement.GetRawText())!;
                payload["UpdateTime"] = deliveryUpdateTime;
                if (deliveryPath.EndsWith("(4102)", StringComparison.Ordinal))
                {
                    payload["DocEntry"] = 4102;
                    payload["DocNum"] = 810042;
                }
                if (DeliveryCancelled && deliveryPath.EndsWith("(4101)", StringComparison.Ordinal))
                {
                    payload["Cancelled"] = "tYES";
                    payload["CancelStatus"] = "csYes";
                }
                else if (DeliveryCancelled && deliveryPath.EndsWith("(4103)", StringComparison.Ordinal))
                {
                    payload["DocEntry"] = 4103;
                    payload["DocNum"] = 810043;
                    payload["CancelStatus"] = "csCancellation";
                    payload["DocumentLines"] = new[]
                    {
                        new
                        {
                            LineNum = 0, ItemCode = "HW-EDGE-100", Quantity = 2m, WarehouseCode = "WH-A",
                            BaseType = 15, BaseEntry = 4101, BaseLine = 0,
                            SerialNumbers = Array.Empty<object>(),
                        },
                    };
                }
                if (request.Url?.Query.Contains("$select", StringComparison.Ordinal) == true)
                {
                    SelectEmptiedSerials = true;
                    payload["DocumentLines"] = new[]
                    {
                        new { LineNum = 0, ItemCode = "HW-EDGE-100", Quantity = 2m,
                            WarehouseCode = "WH-A", SerialNumbers = Array.Empty<object>() },
                    };
                }
                CaptureReadPaths.Add(request.RawUrl ?? path);
                await JsonAsync(response, payload);
                return;
            }
            if (path == Cx01Protocol.Root + "DeliveryNotes")
            {
                var skip = Query(request.Url, "$skiptoken");
                var rows = new List<object>
                {
                    new { DocEntry = 4101, UpdateDate = "2026-09-01", UpdateTime = "081500" },
                    new { DocEntry = 4102, UpdateDate = "2026-09-01", UpdateTime = "081500" },
                };
                if (DeliveryCancelled)
                    rows.Add(new { DocEntry = 4103, UpdateDate = "2026-09-01", UpdateTime = "084500" });
                var start = int.TryParse(skip, out var parsed) ? parsed : 0;
                var page = rows.Skip(start).Take(PageSize).ToList();
                var next = start + page.Count < rows.Count
                    ? new Uri(BaseUri, Cx01Protocol.Root.TrimStart('/') + "DeliveryNotes?$skiptoken="
                        + (start + page.Count)).ToString() : null;
                await JsonAsync(response, new Dictionary<string, object?>
                {
                    ["value"] = page,
                    ["@odata.nextLink"] = next,
                });
                return;
            }
            if (path == Cx01Protocol.Root + "Returns" && request.HttpMethod == "POST")
            {
                SameWarehouseReturnPosted = true;
                response.StatusCode = (int)HttpStatusCode.Created;
                await JsonAsync(response, new { DocEntry = 5101 });
                return;
            }
            if (path == Cx01Protocol.Root + "Returns" && request.HttpMethod == "GET")
            {
                await JsonAsync(response, new
                {
                    value = LastReferenceValue is null
                        ? Array.Empty<object>()
                        : new[] { new { DocEntry = 5101, UpdateDate = "2026-09-01", UpdateTime = returnUpdateTime } },
                });
                return;
            }
            if (path == Cx01Protocol.Root + "Returns(5101)" && request.HttpMethod == "PATCH")
            {
                PatchAttempts++;
                if (RejectNextPatchWith is { } rejection)
                {
                    RejectNextPatchWith = null;
                    response.StatusCode = (int)rejection;
                    await ErrorAsync(response, "-5002", "(custom) Return needs a reason code");
                    return;
                }
                using var body = JsonDocument.Parse(await new StreamReader(request.InputStream).ReadToEndAsync());
                LastPatchBody?.Dispose();
                LastPatchBody = JsonDocument.Parse(body.RootElement.GetRawText());
                LastReferenceValue = body.RootElement.EnumerateObject()
                    .First(property => property.Name.StartsWith("u_", StringComparison.OrdinalIgnoreCase))
                    .Value.GetString();
                returnUpdateTime = "090001";
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }
            if (path == Cx01Protocol.Root + "Returns(5101)" && request.HttpMethod == "GET")
            {
                await JsonAsync(response, new
                {
                    DocEntry = 5101, DocNum = 5101, DocDate = "2026-09-01", UpdateDate = "2026-09-01",
                    UpdateTime = returnUpdateTime, U_YojanaRma = LastReferenceValue,
                    U_FICTIONAL_POST_WRITE = "changed by procedure", DocumentLines = Array.Empty<object>(),
                });
                return;
            }
            if (path == Cx01Protocol.Root + "DeliveryNotes(4101)/Cancel" && request.HttpMethod == "POST")
            {
                DeliveryCancelled = true;
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }
            if (new[]
                {
                    "Items", "BusinessPartners", "Invoices", "PurchaseDeliveryNotes",
                    "InventoryGenEntries", "StockTransfers", "InventoryGenExits",
                }.Any(collection => path == Cx01Protocol.Root + collection))
            {
                await JsonAsync(response, new { value = Array.Empty<object>() });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.EventSubscriptions && request.HttpMethod == "POST")
            {
                var body = await new StreamReader(request.InputStream).ReadToEndAsync();
                LastSubscriptionBody?.Dispose();
                LastSubscriptionBody = JsonDocument.Parse(body);
                WebhookSubscriptionCreated = true;
                subscriptionActive = true;
                response.StatusCode = (int)HttpStatusCode.Created;
                await JsonAsync(response, new
                {
                    WebhookID = LastSubscriptionBody.RootElement.GetProperty("WebhookID").GetString(),
                    State = "Active",
                });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.EventSubscriptions && request.HttpMethod == "GET")
            {
                await JsonAsync(response, new
                {
                    value = subscriptionActive && LastSubscriptionBody is not null
                        ? new[] { new { WebhookID = LastSubscriptionBody.RootElement.GetProperty("WebhookID").GetString(), State = "Active" } }
                        : Array.Empty<object>(),
                });
                return;
            }
            if (path.Contains(Cx01Protocol.EventSubscriptions + "('", StringComparison.Ordinal)
                && request.HttpMethod == "GET")
            {
                if (LastSubscriptionBody is null) { await NotFoundAsync(response); return; }
                await JsonAsync(response, new { State = SubscriptionStopped ? "Stopped" : "Active" });
                return;
            }
            if (path.Contains(Cx01Protocol.EventSubscriptions + "('", StringComparison.Ordinal)
                && path.EndsWith("/Resume", StringComparison.Ordinal) && request.HttpMethod == "POST")
            {
                SubscriptionStopped = false;
                subscriptionActive = true;
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }
            if (path.Contains(Cx01Protocol.EventSubscriptions + "('", StringComparison.Ordinal)
                && path.EndsWith("/Pause", StringComparison.Ordinal) && request.HttpMethod == "POST")
            {
                if (FailUnsubscribe)
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    await ErrorAsync(response, "fictional_denial", "Fictional remote detail must not reach audit or tasks");
                    return;
                }
                if (!subscriptionActive) { await NotFoundAsync(response); return; }
                subscriptionActive = false;
                WebhookSubscriptionPaused = true;
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }
            if (path.Contains(Cx01Protocol.EventSubscriptions + "('", StringComparison.Ordinal)
                && request.HttpMethod == "DELETE")
            {
                if (subscriptionActive)
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await ErrorAsync(response, "-5002", "Not allowed to delete one active webhook subscription");
                    return;
                }
                DeletedSubscriptionPaths.Add(path);
                WebhookSubscriptionDeleted = true;
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.EventNotifications)
            {
                EventNotificationQueries.Add(request.RawUrl ?? path);
                var source = EventNotifications.Count > 0 ? EventNotifications : FailedNotificationAvailable
                    ? [new Cx01SimulatorNotification("evt-failed-0001", "Failed", "DeliveryNotes", "4101",
                        "2026-09-01T00:00:00Z", "08:45:00.000")]
                    : [];
                var filter = Query(request.Url, "$filter");
                var watermarkText = filter is null ? null : filter.Split("CreateDate ge '", 2).ElementAtOrDefault(1)
                    ?.Split('\'', 2)[0];
                var filtered = source.Where(item => watermarkText is null
                        || StringComparer.Ordinal.Compare(item.CreateDate[..10], watermarkText) >= 0)
                    .OrderByDescending(item => item.CreateDate, StringComparer.Ordinal)
                    .ThenByDescending(item => item.CreateTime, StringComparer.Ordinal)
                    .ThenByDescending(item => item.EventId, StringComparer.Ordinal).ToList();
                var skip = int.TryParse(Query(request.Url, "$skip"), out var parsedSkip) ? parsedSkip : 0;
                var page = filtered.Skip(skip).Take(EventNotificationPageSize).Select(item => new
                {
                    EventID = item.EventId,
                    item.Status,
                    item.BusinessObject,
                    Operation = "Created",
                    KeyData = "{\"DocEntry\":" + item.Key + "}",
                    item.CreateDate,
                    item.CreateTime,
                    DeliveryDetails = item.EmptyDeliveryDetails ? Array.Empty<object>() :
                        [new { Status = item.Status, RetryNum = 3, ResponseCode = 503,
                            LastSentDate = item.CreateDate, LastSentTime = item.CreateTime }],
                }).ToList();
                var next = skip + page.Count < filtered.Count
                    ? new Uri(BaseUri, Cx01Protocol.Root.TrimStart('/') + Cx01Protocol.EventNotifications
                        + "?$skip=" + (skip + page.Count)).ToString() : null;
                await JsonAsync(response, new Dictionary<string, object?>
                {
                    ["value"] = page,
                    ["@odata.nextLink"] = next,
                });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.SqlQueries && request.HttpMethod == "POST")
            {
                if (!TrackingTablesAllowed)
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await ErrorAsync(response, "702", "Table 'OITL' not accessible");
                    return;
                }
                serialQueryCreated = true;
                SerialQueryCreateCount++;
                response.StatusCode = (int)HttpStatusCode.Created;
                await JsonAsync(response, new { SqlCode = Cx01Protocol.SerialLinkQueryCode });
                return;
            }
            if (path.Contains(Cx01Protocol.SqlQueries + "('", StringComparison.Ordinal)
                && path.EndsWith("')", StringComparison.Ordinal) && request.HttpMethod == "GET")
            {
                if (!serialQueryCreated) { await NotFoundAsync(response); return; }
                await JsonAsync(response, new { SqlCode = Cx01Protocol.SerialLinkQueryCode });
                return;
            }
            if (path.Contains(Cx01Protocol.SqlQueries + "('", StringComparison.Ordinal)
                && path.EndsWith("/List", StringComparison.Ordinal))
            {
                if (!TrackingTablesAllowed)
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    await ErrorAsync(response, "702", "Table 'OITL' not accessible");
                    return;
                }
                await JsonAsync(response, new
                {
                    value = new[]
                    {
                        new { DocType = 15, DocEntry = 4101, DocLine = 0, ItemCode = "HW-EDGE-100",
                            DistNumber = "SN-FICTIONAL-001", Quantity = -1m },
                        new { DocType = 16, DocEntry = 5101, DocLine = 0, ItemCode = "HW-EDGE-100",
                            DistNumber = "SN-FICTIONAL-001", Quantity = 1m },
                    },
                });
                return;
            }
            response.StatusCode = (int)HttpStatusCode.NotFound;
            await ErrorAsync(response, "-2028", "Entity with value(999999) does not exist");
        }
        catch (Exception exception)
        {
            LastHandlerFailure = exception.ToString();
            if (context.Response.OutputStream.CanWrite) context.Response.StatusCode = 500;
            context.Response.Close();
        }
    }

    private static async Task JsonAsync(HttpListenerResponse response, object value) =>
        await RawJsonAsync(response, JsonSerializer.Serialize(value));

    private static async Task RawJsonAsync(HttpListenerResponse response, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static async Task TextAsync(HttpListenerResponse response, string value, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
        response.Close();
    }

    private static Task ErrorAsync(HttpListenerResponse response, string code, string message) =>
        JsonAsync(response, new { error = new { code, message, details = Array.Empty<object>() } });

    private static async Task NotFoundAsync(HttpListenerResponse response)
    {
        response.StatusCode = (int)HttpStatusCode.NotFound;
        await ErrorAsync(response, "-2028", "Entity does not exist");
    }

    private static string? Query(Uri? uri, string name)
    {
        if (uri is null) return null;
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (Uri.UnescapeDataString(pair[0]) == name) return pair.Length == 2 ? Uri.UnescapeDataString(pair[1]) : "";
        }
        return null;
    }

    private static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
        "Connectors", "CX01", "Fixtures", name));
}
