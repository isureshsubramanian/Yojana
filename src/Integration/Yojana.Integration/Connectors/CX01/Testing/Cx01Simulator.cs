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

internal sealed record Cx01SimulatorDraft(int DocEntry, string DocObjectCode, string? Reference,
    string AuthorizationStatus);

internal sealed class Cx01Simulator : IAsyncDisposable
{
    private readonly object draftLock = new();
    private readonly Dictionary<int, Cx01SimulatorDraft> drafts = [];
    private readonly Dictionary<string, int> postedFromDrafts = new(StringComparer.Ordinal);
    private int nextDraft = 7000;
    private readonly Dictionary<string, Dictionary<int, Dictionary<string, object?>>> documents = new(StringComparer.Ordinal);
    private bool enableDocumentPosts;
    public string? LastAttachmentUpload { get; private set; }
    public bool EnableDocumentPosts
    {
        get => enableDocumentPosts;
        set
        {
            enableDocumentPosts = value;
            if (value) documents["Returns"] = new() { [5101] = new() { ["DocEntry"] = 5101,
                ["UpdateDate"] = "2026-09-01", ["UpdateTime"] = "090000", ["DocumentLines"] = Array.Empty<object>() } };
        }
    }
    public bool DropNextCreateReply { get; set; }
    public Func<Task>? BeforeNextDocumentCreate { get; set; }
    public bool FailNextDocumentCreateAfterHook { get; set; }
    public string? RejectNextDocumentCollection { get; set; }
    public string? FailNextDocumentCollection { get; set; }
    public Dictionary<string, int> DocumentCreateCounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonElement> DocumentBodies { get; } = new(StringComparer.Ordinal);
    public List<(string Method, string Path)> OutboundPaths { get; } = [];
    public Dictionary<string, object?>? Document(string collection, int key)
    {
        lock (draftLock) return documents.GetValueOrDefault(collection)?.GetValueOrDefault(key);
    }
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
    public string Release { get; set; } = "1000340";
    public bool WebhooksEnabled { get; set; } = true;
    public int SubscriptionCreateCount { get; private set; }
    public int LoginCount { get; private set; }
    public int ExpireAfterAuthenticatedRequests { get; set; } = int.MaxValue;
    public bool RateLimitNextRequest { get; set; }
    public bool SameWarehouseReturnPosted { get; set; }
    public bool DeliveryCancelled { get; set; }
    public bool SubscriptionStopped { get; set; }
    public List<string> DeletedSubscriptionPaths { get; } = [];
    public bool WebhookSubscriptionCreated { get; private set; }
    public bool WebhookSubscriptionPaused { get; private set; }
    public int SubscriptionResumeCount { get; private set; }
    public bool WebhookSubscriptionDeleted { get; private set; }
    public bool FailedNotificationAvailable { get; set; }
    public List<Cx01SimulatorNotification> EventNotifications { get; } = [];
    public int EventNotificationPageSize { get; set; } = 200;
    public List<string> EventNotificationQueries { get; } = [];
    public string? LastReferenceValue { get; private set; }
    public string? LastReferenceField { get; private set; }
    private readonly Dictionary<int, JsonElement> draftBodies = [];
    public string DocumentRejectionMessage { get; set; } = "Fictional posting period is closed";
    public JsonDocument? LastSubscriptionBody { get; private set; }
    public JsonDocument? LastPatchBody { get; private set; }
    public HttpStatusCode? RejectNextPatchWith { get; set; }
    public int PatchAttempts { get; private set; }
    public int RequestCount { get; private set; }
    public string? LastHandlerFailure { get; private set; }
    public int ServiceUnavailableResponses { get; set; }
    public decimal OnHandQuantity { get; set; } = 2m;
    public bool FailMetadataRead { get; set; }
    public bool FailUnsubscribe { get; set; }
    public bool TrackingTablesAllowed { get; set; } = true;
    public bool SerialQueryCreated => serialQueryCreated;
    public int SerialQueryCreateCount { get; private set; }
    public bool SelectEmptiedSerials { get; private set; }
    public bool RejectNextDeliveryReadWithBusinessError { get; set; }
    public List<string> CaptureReadPaths { get; } = [];
    /// <summary>Remote equipment cards for the coexistence drift scan, keyed rows as the remote returns them.</summary>
    public List<Dictionary<string, object?>> EquipmentCards { get; } = [];
    /// <summary>The remote's own page cap, applied below any requested $top.</summary>
    public int EquipmentCardPageCap { get; set; } = 20;
    public Action<Dictionary<string, object?>>? EditEquipmentCardBeforeNextPatch { get; set; }
    public HttpStatusCode? RejectNextEquipmentCardPatchWith { get; set; }
    public HashSet<string> AcceptedEquipmentCardStatuses { get; } = new(StringComparer.Ordinal)
    {
        "sns_Active", "sns_Terminated", "sns_InRepair", "sns_Returned", "sns_Loaned",
    };
    public void SimulateLaterUserEdit() => returnUpdateTime = "090200";
    public void SimulateLaterDeliveryEdit(string updateTime) => deliveryUpdateTime = updateTime;
    public int PageSize { get; set; } = 1;
    public List<string> ReceivedCookieHeaders { get; } = [];
    public bool ReturnsApprovalTemplateActive { get; set; }
    public int? JournalReferenceLength { get; set; }
    public string? LocalCurrencyCode { get; set; }
    public bool ApprovalTemplatesForbidden { get; set; }
    public int DraftCreateCount { get; private set; }
    public JsonDocument? LastDraftBody { get; private set; }

    public Cx01SimulatorDraft? Draft(int docEntry)
    {
        lock (draftLock) return drafts.GetValueOrDefault(docEntry);
    }

    /// <summary>A person adds the held draft in the ERP client: the draft leaves and a document carries its reference.</summary>
    public void AddDraftAsDocument(int draft, int docEntry)
    {
        lock (draftLock)
        {
            var held = drafts[draft];
            drafts.Remove(draft);
            postedFromDrafts[held.Reference!] = docEntry;
            if (EnableDocumentPosts)
            {
                var collection = Cx01ApprovalCatalogue.Targets.Single(x => x.DraftObjectCode == held.DocObjectCode).Collection;
                var body = JsonSerializer.Deserialize<Dictionary<string, object?>>(draftBodies[draft].GetRawText())!;
                body.Remove("DocObjectCode");
                body["DocEntry"] = docEntry;
                body["UpdateDate"] = "2026-09-01";
                body["UpdateTime"] = "100000";
                if (!documents.ContainsKey(collection)) documents[collection] = [];
                documents[collection][docEntry] = body;
            }
        }
    }

    public void SetDraftAuthorization(int draft, string status)
    {
        lock (draftLock) drafts[draft] = drafts[draft] with { AuthorizationStatus = status };
    }

    public void DeleteDraft(int draft)
    {
        lock (draftLock) drafts.Remove(draft);
    }

    public string OwnSubscriptionId(Guid tenantId, Guid connectionId) => Cx01Protocol.WebhookId(tenantId, connectionId);

    public Yojana.Integration.Connections.RegisterConnection RegistrationRequest(Guid? requestId = null,
        Cx01NativeWarrantyStatusCodes? nativeWarrantyStatuses = null) =>
        new(requestId ?? Guid.CreateVersion7(), Cx01Capabilities.ConnectorCode, "Fictional main connection", Guid.CreateVersion7(),
            JsonSerializer.SerializeToElement(new Cx01ConnectionSettings(BaseUri, "FICTIONAL_DEMO", "test",
                    NativeWarrantyStatuses: nativeWarrantyStatuses),
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
        LastDraftBody?.Dispose();
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
                await JsonAsync(response, new { SessionId = session, SessionTimeout = 30, Version = Release });
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
            OutboundPaths.Add((request.HttpMethod, path));
            if (EnableDocumentPosts && await HandleDocumentAsync(request, response, path)) return;
            if (path == Cx01Protocol.Root + "SerialNumberDetails" && request.HttpMethod == "GET")
            {
                await JsonAsync(response, new { value = new[] { new { SystemNumber = 17 } } });
                return;
            }
            if (path == Cx01Protocol.Metadata)
            {
                if (FailMetadataRead)
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    await ErrorAsync(response, "403", "Fictional permission denial");
                    return;
                }
                await TextAsync(response,
                    JournalReferenceLength is { } size
                        ? $"<Schema><EntityType Name=\"JournalEntry\"><Property Name=\"Memo\" MaxLength=\"{size}\" /></EntityType><EntitySet Name=\"JournalEntries\" EntityType=\"F.JournalEntry\" /><EntitySet Name=\"EventSubscriptions\" /></Schema>"
                        : "<edmx:Edmx><EntitySet Name=\"EventSubscriptions\"/><EntitySet Name=\"EventNotifications\"/></edmx:Edmx>",
                    "application/xml");
                return;
            }
            if (path == Cx01Protocol.Root)
            {
                await JsonAsync(response, new { Version = Release });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.AdminInfo && request.HttpMethod == "POST")
            {
                await JsonAsync(response, new
                {
                    LocalCurrency = LocalCurrencyCode,
                    EnableWebhook = WebhooksEnabled ? "tYES" : "tNO",
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
                        new { ItemCode = "HW-EDGE-100", WarehouseCode = "WH-A", InStock = OnHandQuantity },
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
                    // The live service links the next page relative to its root, not as an absolute URL.
                    ? "DeliveryNotes?$skiptoken=" + (start + page.Count) : null;
                await JsonAsync(response, new Dictionary<string, object?>
                {
                    ["value"] = page,
                    ["@odata.nextLink"] = next,
                });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.ApprovalTemplates && request.HttpMethod == "GET")
            {
                if (ApprovalTemplatesForbidden)
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    await ErrorAsync(response, "403", "Fictional permission denial");
                    return;
                }
                await JsonAsync(response, new
                {
                    value = new object[]
                    {
                        new { Code = 1, Name = "FICTIONAL_GOODS_ISSUE", IsActive = "tNO",
                            ApprovalTemplateDocuments = new[] { new { DocumentType = "atdtGoodsIssue" } } },
                        new { Code = 2, Name = "FICTIONAL_RETURNS", IsActive = ReturnsApprovalTemplateActive ? "tYES" : "tNO",
                            ApprovalTemplateDocuments = new[] { new { DocumentType = "atdtReturns" } } },
                    },
                });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.Drafts && request.HttpMethod == "POST")
            {
                using var body = JsonDocument.Parse(await new StreamReader(request.InputStream).ReadToEndAsync());
                LastDraftBody?.Dispose();
                LastDraftBody = JsonDocument.Parse(body.RootElement.GetRawText());
                var reference = body.RootElement.EnumerateObject().Select(x => x.Value)
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()).FirstOrDefault(x => x?.StartsWith("YOJANA:", StringComparison.Ordinal) == true);
                Cx01SimulatorDraft draft;
                lock (draftLock)
                {
                    draft = new(++nextDraft, body.RootElement.GetProperty("DocObjectCode").GetString()!, reference,
                        "dasWithout");
                    drafts[draft.DocEntry] = draft;
                    draftBodies[draft.DocEntry] = body.RootElement.Clone();
                }
                DraftCreateCount++;
                if (DropNextCreateReply) { DropNextCreateReply = false; response.Abort(); return; }
                response.StatusCode = (int)HttpStatusCode.Created;
                await JsonAsync(response, new { draft.DocEntry, draft.DocObjectCode });
                return;
            }
            if (path == Cx01Protocol.Root + Cx01Protocol.Drafts && request.HttpMethod == "GET")
            {
                var wanted = FilterValue(request.Url);
                Cx01SimulatorDraft[] found;
                lock (draftLock) found = drafts.Values.Where(x => x.Reference == wanted && draftBodies[x.DocEntry].TryGetProperty(FilterField(request.Url), out var value) && value.GetString() == wanted).ToArray();
                await JsonAsync(response, new { value = found.Select(x => new { x.DocEntry, x.DocObjectCode }).ToArray() });
                return;
            }
            if (path.StartsWith(Cx01Protocol.Root + Cx01Protocol.Drafts + "(", StringComparison.Ordinal)
                && request.HttpMethod == "GET")
            {
                var key = int.Parse(path[(Cx01Protocol.Root.Length + Cx01Protocol.Drafts.Length + 1)..^1],
                    System.Globalization.CultureInfo.InvariantCulture);
                var draft = Draft(key);
                if (draft is null)
                {
                    await NotFoundAsync(response);
                    return;
                }
                await JsonAsync(response, new { draft.DocEntry, draft.AuthorizationStatus });
                return;
            }
            if (path == Cx01Protocol.Root + "Returns" && request.HttpMethod == "GET"
                && FilterValue(request.Url) is { } returnReference && IsDraftReference(returnReference))
            {
                int? posted;
                lock (draftLock) posted = postedFromDrafts.TryGetValue(returnReference, out var entry) ? entry : null;
                await JsonAsync(response, new
                {
                    value = posted is null ? Array.Empty<object>() : [new { DocEntry = posted.Value }],
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
                    value = LastReferenceValue is null || Query(request.Url, "$filter")?.Contains(" eq ", StringComparison.Ordinal) == true
                        && (LastReferenceValue != FilterValue(request.Url) || LastReferenceField != FilterField(request.Url))
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
                LastReferenceField = body.RootElement.EnumerateObject().First(property => property.Name.StartsWith("u_", StringComparison.OrdinalIgnoreCase)).Name;
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
            if (path == Cx01Protocol.Root + "CustomerEquipmentCards" && request.HttpMethod == "GET")
            {
                var query = System.Web.HttpUtility.ParseQueryString(request.Url?.Query ?? "");
                var after = long.Parse(query["$filter"]!.Split(" gt ")[1], System.Globalization.CultureInfo.InvariantCulture);
                var top = Math.Min(int.Parse(query["$top"] ?? "20", System.Globalization.CultureInfo.InvariantCulture),
                    EquipmentCardPageCap);
                var selected = query["$select"]!.Split(',');
                var matching = EquipmentCards.Where(card => Convert.ToInt64(card["EquipmentCardNum"],
                        System.Globalization.CultureInfo.InvariantCulture) > after)
                    .OrderBy(card => Convert.ToInt64(card["EquipmentCardNum"], System.Globalization.CultureInfo.InvariantCulture))
                    .ToList();
                var page = matching.Take(top).Select(card => selected.Where(card.ContainsKey)
                    .ToDictionary(field => field, field => card[field])).ToList();
                object body = matching.Count > top
                    ? new Dictionary<string, object?> { ["value"] = page, ["odata.nextLink"] = "CustomerEquipmentCards?$skip=" + top }
                    : new Dictionary<string, object?> { ["value"] = page };
                await JsonAsync(response, body);
                return;
            }
            if (path.StartsWith(Cx01Protocol.Root + "CustomerEquipmentCards(", StringComparison.Ordinal)
                && path.EndsWith(')'))
            {
                var raw = path[(Cx01Protocol.Root.Length + "CustomerEquipmentCards(".Length)..^1].Trim('\'');
                var card = EquipmentCards.SingleOrDefault(row => string.Equals(
                    Convert.ToString(row["EquipmentCardNum"], System.Globalization.CultureInfo.InvariantCulture),
                    raw, StringComparison.Ordinal));
                if (card is null) { await NotFoundAsync(response); return; }
                if (request.HttpMethod == "GET")
                {
                    var snapshot = card.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                    await JsonAsync(response, snapshot);
                    if (EditEquipmentCardBeforeNextPatch is { } edit)
                    {
                        EditEquipmentCardBeforeNextPatch = null;
                        edit(card);
                    }
                    return;
                }
                if (request.HttpMethod == "PATCH")
                {
                    PatchAttempts++;
                    if (RejectNextEquipmentCardPatchWith is { } rejection)
                    {
                        RejectNextEquipmentCardPatchWith = null;
                        response.StatusCode = (int)rejection;
                        await ErrorAsync(response, "-5002", "Fictional installed-base update was rejected");
                        return;
                    }
                    using var body = JsonDocument.Parse(await new StreamReader(request.InputStream).ReadToEndAsync());
                    if (body.RootElement.TryGetProperty("StatusOfSerialNumber", out var requestedStatus)
                        && requestedStatus.GetString() is { } status
                        && !AcceptedEquipmentCardStatuses.Contains(status))
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        await ErrorAsync(response, "-5002",
                            "Fictional installed-base status code was rejected");
                        return;
                    }
                    LastPatchBody?.Dispose();
                    LastPatchBody = JsonDocument.Parse(body.RootElement.GetRawText());
                    foreach (var property in body.RootElement.EnumerateObject())
                        card[property.Name] = property.Value.ValueKind == JsonValueKind.Null
                            ? null : property.Value.ValueKind == JsonValueKind.String
                                ? property.Value.GetString() : property.Value.Clone();
                    response.StatusCode = (int)HttpStatusCode.NoContent;
                    response.Close();
                    return;
                }
            }
            // The live stock-transfer object has no change time; the service refuses a query that names one.
            if (path == Cx01Protocol.Root + "StockTransfers"
                && request.Url?.Query.Contains("UpdateTime", StringComparison.Ordinal) == true)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
                await JsonAsync(response, new { error = new { code = "-1000",
                    message = "Property 'UpdateTime' of 'StockTransfer'is invalid" } });
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
                SubscriptionCreateCount++;
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
                var state = JsonSerializer.Deserialize<Dictionary<string, object?>>(LastSubscriptionBody.RootElement.GetRawText())!;
                state["State"] = SubscriptionStopped ? "Stopped" : "Active";
                await JsonAsync(response, state);
                return;
            }
            if (path.Contains(Cx01Protocol.EventSubscriptions + "('", StringComparison.Ordinal)
                && path.EndsWith("/Resume", StringComparison.Ordinal) && request.HttpMethod == "POST")
            {
                SubscriptionStopped = false;
                subscriptionActive = true;
                SubscriptionResumeCount++;
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
                    ? Cx01Protocol.EventNotifications + "?$skip=" + (skip + page.Count) : null;
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

    private async Task<bool> HandleDocumentAsync(HttpListenerRequest request, HttpListenerResponse response, string path)
    {
        var collection = Cx01DocumentCatalogue.Collections.FirstOrDefault(x => path == Cx01Protocol.Root + x
            || path.StartsWith(Cx01Protocol.Root + x + "(", StringComparison.Ordinal));
        if (collection is null) return false;
        var byKey = path.EndsWith(')');
        var entry = byKey ? int.Parse(path[(path.LastIndexOf('(') + 1)..^1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (request.HttpMethod == "POST" && !byKey)
        {
            if (BeforeNextDocumentCreate is { } beforeCreate)
            {
                BeforeNextDocumentCreate = null;
                await beforeCreate();
                if (FailNextDocumentCreateAfterHook)
                {
                    FailNextDocumentCreateAfterHook = false;
                    response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                    await ErrorAsync(response, "-5002", "Fictional remote unavailable");
                    return true;
                }
            }
            if (RejectNextDocumentCollection == collection || FailNextDocumentCollection == collection)
            {
                var transient = FailNextDocumentCollection == collection;
                RejectNextDocumentCollection = null;
                FailNextDocumentCollection = null;
                response.StatusCode = transient ? 503 : 400;
                await ErrorAsync(response, "-5002", DocumentRejectionMessage);
                return true;
            }
            Dictionary<string, object?> body;
            if (collection == "Attachments2")
            {
                var multipart = await new StreamReader(request.InputStream).ReadToEndAsync();
                LastAttachmentUpload = multipart;
                var match = System.Text.RegularExpressions.Regex.Match(multipart, "filename=\"?([^\";\r\n]+)");
                var name = match.Groups[1].Value;
                body = new() { ["Attachments2_Lines"] = new[] { new { FileName = Path.GetFileNameWithoutExtension(name), FileExtension = Path.GetExtension(name).TrimStart('.') } } };
            }
            else
            {
                body = JsonSerializer.Deserialize<Dictionary<string, object?>>(await new StreamReader(request.InputStream).ReadToEndAsync())!;
                var required = collection switch
                {
                    "ServiceCalls" => "Subject", "JournalEntries" => "JournalEntryLines", "StockTransfers" => "StockTransferLines", _ => "DocumentLines",
                };
                if (!body.ContainsKey(required))
                {
                    response.StatusCode = 400;
                    await ErrorAsync(response, "-5002", "Fictional document is missing required lines or subject");
                    return true;
                }
            }
            lock (draftLock)
            {
                if (!documents.ContainsKey(collection)) documents[collection] = [];
                entry = 8001 + documents[collection].Count;
                body[Cx01DocumentCatalogue.KeyField(collection)] = entry;
                body["UpdateDate"] = "2026-09-01";
                body["UpdateTime"] = "100000";
                documents[collection][entry] = body;
                DocumentCreateCounts[collection] = DocumentCreateCounts.GetValueOrDefault(collection) + 1;
                DocumentBodies[collection] = JsonSerializer.SerializeToElement(body);
            }
            if (DropNextCreateReply) { DropNextCreateReply = false; response.Abort(); return true; }
            response.StatusCode = 201;
            await JsonAsync(response, body);
            return true;
        }
        if (request.HttpMethod == "GET")
        {
            if (byKey)
            {
                var found = Document(collection, entry);
                if (found is null) return false;
                await JsonAsync(response, found);
            }
            else
            {
                var reference = Query(request.Url, "$filter")?.Contains(" eq ", StringComparison.Ordinal) == true ? FilterValue(request.Url) : null;
                object[] found;
                lock (draftLock) found = (documents.GetValueOrDefault(collection)?.Values.AsEnumerable() ?? Enumerable.Empty<Dictionary<string, object?>>())
                    .Where(x => reference is null || x.GetValueOrDefault(FilterField(request.Url))?.ToString() == reference).Cast<object>().ToArray();
                await JsonAsync(response, new { value = found });
            }
            return true;
        }
        if (request.HttpMethod == "PATCH" && byKey && Document(collection, entry) is { } target)
        {
            using var body = JsonDocument.Parse(await new StreamReader(request.InputStream).ReadToEndAsync());
            foreach (var property in body.RootElement.EnumerateObject()) target[property.Name] = property.Value.Clone();
            response.StatusCode = 204;
            response.Close();
            return true;
        }
        return false;
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

    private bool IsDraftReference(string reference)
    {
        lock (draftLock)
            return postedFromDrafts.ContainsKey(reference) || drafts.Values.Any(x => x.Reference == reference);
    }

    private static string FilterField(Uri? uri) => Query(uri, "$filter")?.Split(" eq ", 2)[0] ?? "";

    private static string? FilterValue(Uri? uri)
    {
        var filter = Query(uri, "$filter");
        if (filter is null) return null;
        var start = filter.IndexOf('\'');
        var end = filter.IndexOf("\' and", start + 1, StringComparison.Ordinal);
        if (end < 0) end = filter.LastIndexOf('\'');
        return start >= 0 && end > start ? filter[(start + 1)..end].Replace("''", "'", StringComparison.Ordinal) : null;
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
