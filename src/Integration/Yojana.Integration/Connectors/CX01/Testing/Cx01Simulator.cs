using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Yojana.Integration.Connectors.CX01.Testing;

internal sealed class Cx01Simulator : IAsyncDisposable
{
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Task loop;
    private readonly string createdDelivery = Fixture("delivery-created.json");
    private readonly string returnedDelivery = Fixture("delivery-after-return.json");
    private readonly string item = Fixture("item.json");
    private string session = "session-1";
    private int authenticatedRequests;

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
    public bool WebhookSubscriptionCreated { get; private set; }
    public bool FailedNotificationAvailable { get; set; }
    public string? LastReferenceValue { get; private set; }
    public int PageSize { get; set; } = 1;
    public List<string> ReceivedCookieHeaders { get; } = [];

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        listener.Stop();
        try { await loop; }
        catch (HttpListenerException) { }
        listener.Close();
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
            if (request.Url?.AbsolutePath == Cx01Protocol.Login)
            {
                LoginCount++;
                authenticatedRequests = 0;
                session = "session-" + LoginCount;
                response.Headers.Add("Set-Cookie", Cx01Protocol.SessionCookie + "=" + session + "; HttpOnly");
                response.AppendHeader("Set-Cookie", Cx01Protocol.RouteCookie + "=.node1; HttpOnly");
                await JsonAsync(response, new { SessionId = session, SessionTimeout = 30 });
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

            var path = request.Url?.AbsolutePath ?? "";
            if (path == Cx01Protocol.Root)
            {
                await JsonAsync(response, new { Version = "10.0 FP 2308" });
                return;
            }
            if (path == Cx01Protocol.Root + "Items('HW-EDGE-100')")
            {
                await RawJsonAsync(response, item);
                return;
            }
            if (path is var deliveryPath && (deliveryPath == Cx01Protocol.Root + "DeliveryNotes(4101)"
                || deliveryPath == Cx01Protocol.Root + "DeliveryNotes(4102)"))
            {
                using var source = JsonDocument.Parse(SameWarehouseReturnPosted ? returnedDelivery : createdDelivery);
                var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(source.RootElement.GetRawText())!;
                if (deliveryPath.EndsWith("(4102)", StringComparison.Ordinal))
                {
                    payload["DocEntry"] = 4102;
                    payload["DocNum"] = 810042;
                }
                if (DeliveryCancelled) payload["Cancelled"] = "tYES";
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
            if (path == Cx01Protocol.Root + "SerialNumberDetails")
            {
                await JsonAsync(response, new { value = Array.Empty<object>() });
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
                        : new[] { new { DocEntry = 5101, UpdateDate = "2026-09-01", UpdateTime = "090000" } },
                });
                return;
            }
            if (path == Cx01Protocol.Root + "Returns(5101)" && request.HttpMethod == "PATCH")
            {
                using var body = JsonDocument.Parse(await new StreamReader(request.InputStream).ReadToEndAsync());
                LastReferenceValue = body.RootElement.EnumerateObject().Single().Value.GetString();
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }
            if (path == Cx01Protocol.Root + "DeliveryNotes(4101)/Cancel" && request.HttpMethod == "POST")
            {
                DeliveryCancelled = true;
                response.StatusCode = (int)HttpStatusCode.NoContent;
                response.Close();
                return;
            }
            if (path == Cx01Protocol.Root + "DocumentCancellationRequests")
            {
                await JsonAsync(response, new
                {
                    value = DeliveryCancelled
                        ? new[] { new { DocEntry = 9101, UpdateDate = "2026-09-01", UpdateTime = "084500" } }
                        : Array.Empty<object>(),
                });
                return;
            }
            if (path == Cx01Protocol.Root + "DocumentCancellationRequests(9101)")
            {
                await JsonAsync(response, new
                {
                    DocEntry = 9101,
                    DocNum = 9101,
                    DocDate = "2026-09-01",
                    UpdateDate = "2026-09-01",
                    UpdateTime = "084500",
                    DocObjectCode = "oDeliveryNotes",
                });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.EventSubscriptions && request.HttpMethod == "POST")
            {
                WebhookSubscriptionCreated = true;
                response.StatusCode = (int)HttpStatusCode.Created;
                await JsonAsync(response, new { AbsoluteEntry = 1, State = "Active" });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.EventNotifications)
            {
                await JsonAsync(response, new
                {
                    value = FailedNotificationAvailable
                        ? new[] { new { ObjectType = "DeliveryNotes", ObjectKey = "4101", Status = "Failed" } }
                        : Array.Empty<object>(),
                });
                return;
            }
            response.StatusCode = (int)HttpStatusCode.NotFound;
            response.Close();
        }
        catch
        {
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
