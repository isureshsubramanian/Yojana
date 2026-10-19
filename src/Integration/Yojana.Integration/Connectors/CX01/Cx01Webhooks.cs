using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Quartz;
using Wolverine;
using Wolverine.Attributes;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Connectors.CX01;

public sealed record Cx01WebhookHint(Guid ConnectionId, string Collection, string Operation, string Key,
    string EventId);

[NonTransactional]
public static class Cx01WebhookHintHandler
{
    public static async Task Handle(Cx01WebhookHint hint, Cx01Adapter adapter, InboundMessageStore inbox,
        Cx01WebhookEventTracker events, CancellationToken cancellationToken)
    {
        if (await events.IsProcessedAsync(hint.ConnectionId, hint.EventId, cancellationToken)) return;
        var mapped = await adapter.CaptureKeyAsync(hint.ConnectionId, hint.Collection, hint.Key,
            cancellationToken);
        if (mapped is not null) _ = await inbox.Accept(mapped.Acceptance, cancellationToken);
        await events.MarkProcessedAsync(hint.ConnectionId, hint.EventId, cancellationToken);
    }
}

public static class Cx01WebhookEndpoints
{
    public const string SigningCredentialPurpose = "cx01_webhook";

    public static IEndpointRouteBuilder MapCx01WebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var path = "/api/integration/connections/{connectionId:guid}/notifications/cx01";
        app.MapGet(path, ReceiveAsync).AllowAnonymous();
        app.MapPost(path, ReceiveAsync).AllowAnonymous();
        return app;
    }

    private static async Task<IResult> ReceiveAsync(Guid connectionId, HttpRequest request, HttpResponse response,
        ITenantContextSetter tenantSetter, IntegrationDbContext db, Cx01Settings settings,
        IConnectionCredentialStore credentials, ConnectionCredentialProtector protector, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var tenantValue = request.Headers[IntegrationNotificationEndpoints.TenantHeader].FirstOrDefault()
            ?? request.Query["tenant"].FirstOrDefault();
        if (!TenantId.TryParse(tenantValue,
                out var tenantId)) return Results.BadRequest(new { error = "tenant_header_required" });
        tenantSetter.Set(tenantId);
        var active = await db.Connections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId
            && x.ConnectionId == connectionId && x.ConnectorCode == Cx01Capabilities.ConnectorCode
            && x.Status == ConnectionStatus.Active, cancellationToken);
        if (!active) return Results.NotFound();
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        if (!configured.WebhooksAvailable) return Results.NotFound();

        byte[] bytes;
        var handshake = request.Headers[Cx01Protocol.HandshakeTokenHeader].FirstOrDefault();
        if (HttpMethods.IsGet(request.Method))
        {
            if (string.IsNullOrWhiteSpace(handshake)) return Results.Unauthorized();
            bytes = Encoding.UTF8.GetBytes(handshake);
        }
        else
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, cancellationToken);
            bytes = body.ToArray();
        }
        var credential = await credentials.GetCurrentAsync(tenantId, connectionId, SigningCredentialPurpose,
            cancellationToken);
        if (credential is null) return Results.Unauthorized();
        var secret = await protector.DecryptAsync(credential, cancellationToken);
        try
        {
            if (!TrySignature(request.Headers[Cx01Protocol.SignatureHeader].FirstOrDefault(), out var signature)
                || !VerifySignature(bytes, signature, secret, configured.WebhookHmacAlgorithm))
                return Results.Unauthorized();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }

        if (HttpMethods.IsGet(request.Method))
            return Results.Content(HandshakeResponse(handshake!),
                "application/json", Encoding.UTF8, StatusCodes.Status200OK);

        JsonDocument document;
        try { document = JsonDocument.Parse(bytes); }
        catch (JsonException) { return Results.BadRequest(new { error = "notification_invalid" }); }
        using (document)
        {
            var events = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().ToList() : [document.RootElement];
            if (events.Count is < 1 or > 1_024)
                return Results.BadRequest(new { error = "notification_batch_size_invalid" });
            foreach (var item in events)
            {
                if (!StringComparer.Ordinal.Equals(Text(item, "source"),
                        "/default/sap.b1/" + configured.CompanyDatabase))
                    return Results.Unauthorized();
                var hint = Hint(item, connectionId);
                if (hint is not null)
                    try { await bus.PublishAsync(hint, new DeliveryOptions { TenantId = tenantId.ToString() }); }
                    catch
                    {
                        // The durable delivery log is the recovery source. A busy receiver must still acknowledge
                        // promptly so the remote dispatcher is not turned into a load amplifier.
                    }
            }
        }
        return Results.Accepted();
    }

    internal static Cx01WebhookHint? Hint(JsonElement item, Guid connectionId)
    {
        var id = Text(item, "id");
        if (string.IsNullOrWhiteSpace(id)) return null;
        var type = Text(item, "type") ?? "";
        const string prefix = "sap.b1.";
        const string suffix = ".v1";
        if (!type.StartsWith(prefix, StringComparison.Ordinal)
            || !type.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var name = type[prefix.Length..^suffix.Length];
        var separator = name.LastIndexOf('.');
        if (separator <= 0 || separator == name.Length - 1) return null;
        var businessObject = name[..separator];
        var operation = name[(separator + 1)..];
        var collection = Cx01Protocol.Entities.Values.Select(x => x.Collection)
            .FirstOrDefault(x => StringComparer.Ordinal.Equals(x, businessObject));
        if (item.TryGetProperty("data", out var data))
        {
            var key = Text(data, "DocEntry") ?? Text(data, "ObjectKey") ?? Text(data, "ItemCode")
                ?? Text(data, "CardCode") ?? Text(data, "EquipmentCardNum") ?? Text(item, "subject");
            if (collection is not null && key is not null)
                return new(connectionId, collection, operation, key, id);
        }
        return null;
    }

    internal static bool VerifySignature(ReadOnlySpan<byte> body, ReadOnlySpan<byte> presented,
        ReadOnlySpan<byte> secret, string algorithm)
    {
        byte[] expected = algorithm.ToUpperInvariant() switch
        {
            "HMACSHA384" => HMACSHA384.HashData(secret, body),
            "HMACSHA512" => HMACSHA512.HashData(secret, body),
            _ => HMACSHA256.HashData(secret, body),
        };
        return expected.Length == presented.Length && CryptographicOperations.FixedTimeEquals(expected, presented);
    }

    internal static string HandshakeResponse(string token) =>
        "{\"Challenge\":" + JsonSerializer.Serialize(token) + "}";

    private static bool TrySignature(string? value, out byte[] signature)
    {
        try
        {
            signature = Convert.FromBase64String(value ?? "");
            return signature.Length > 0;
        }
        catch (FormatException)
        {
            signature = [];
            return false;
        }
    }

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;
}

public sealed class Cx01WebhookEventTracker(
    ITenantContext tenant,
    IntegrationDbContext db,
    Yojana.Platform.Contracts.Time.IClock clock)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(31);

    public Task<bool> IsProcessedAsync(Guid connectionId, string eventId, CancellationToken cancellationToken) =>
        db.ConnectionNotificationReceipts.AsNoTracking().AnyAsync(x => x.TenantId == tenant.TenantId
            && x.ConnectionId == connectionId && x.EventId == eventId && x.ProcessedAt != null,
            cancellationToken);

    public async Task<IReadOnlyDictionary<string, ConnectionNotificationReceipt>> ReadAsync(Guid connectionId,
        IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0) return new Dictionary<string, ConnectionNotificationReceipt>();
        return await db.ConnectionNotificationReceipts.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && eventIds.Contains(x.EventId))
            .ToDictionaryAsync(x => x.EventId, StringComparer.Ordinal, cancellationToken);
    }

    public async Task RememberPendingAsync(Guid connectionId, IEnumerable<string> eventIds, Guid runId,
        DateTime observedAt, CancellationToken cancellationToken)
    {
        foreach (var eventId in eventIds.Distinct(StringComparer.Ordinal))
            db.ConnectionNotificationReceipts.Add(new ConnectionNotificationReceipt
            {
                TenantId = tenant.TenantId,
                ConnectionId = connectionId,
                EventId = eventId,
                FirstSeenPendingAt = observedAt,
                FirstSeenPendingRunId = runId,
                LastObservedAt = observedAt,
            });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkProcessedAsync(Guid connectionId, string eventId,
        CancellationToken cancellationToken)
    {
        var row = await db.ConnectionNotificationReceipts.SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId && x.EventId == eventId,
            cancellationToken);
        if (row is null)
        {
            row = new ConnectionNotificationReceipt
            {
                TenantId = tenant.TenantId,
                ConnectionId = connectionId,
                EventId = eventId,
                LastObservedAt = clock.UtcNow,
            };
            db.ConnectionNotificationReceipts.Add(row);
        }
        row.LastObservedAt = clock.UtcNow;
        row.ProcessedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PurgeAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var before = clock.UtcNow - Retention;
        await db.ConnectionNotificationReceipts.Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId && x.LastObservedAt < before)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public Task<DateOnly?> WatermarkAsync(Guid connectionId, CancellationToken cancellationToken) =>
        db.ConnectionNotificationWatchdogs.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
                && x.ConnectionId == connectionId).Select(x => x.WatermarkDate)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task SetWatermarkAsync(Guid connectionId, DateOnly watermark,
        CancellationToken cancellationToken)
    {
        var row = await db.ConnectionNotificationWatchdogs.SingleOrDefaultAsync(x =>
            x.TenantId == tenant.TenantId && x.ConnectionId == connectionId, cancellationToken);
        if (row is null)
        {
            row = new ConnectionNotificationWatchdog
            {
                TenantId = tenant.TenantId,
                ConnectionId = connectionId,
                UpdatedAt = clock.UtcNow,
            };
            db.ConnectionNotificationWatchdogs.Add(row);
        }
        if (row.WatermarkDate is null || watermark > row.WatermarkDate) row.WatermarkDate = watermark;
        row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class Cx01WebhookMaintenance(
    ITenantContext tenant,
    IntegrationDbContext db,
    Cx01Settings settings,
    Cx01Client client,
    Cx01Adapter adapter,
    InboundMessageStore inbox,
    Cx01WebhookEventTracker events,
    IConnectionCredentialStore credentials,
    ConnectionCredentialProtector protector,
    Yojana.Platform.Contracts.Time.IClock clock)
{
    internal const int WatchdogPageSize = 200;
    internal const int WatchdogRunCap = 2_000;

    public async Task SubscribeAsync(Guid connectionId, Uri callback, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        if (!configured.WebhooksAvailable)
            throw new InvalidOperationException("CX01 webhooks are unavailable for this connection release.");
        var credential = await credentials.GetCurrentAsync(tenant.TenantId, connectionId,
            Cx01WebhookEndpoints.SigningCredentialPurpose, cancellationToken)
            ?? throw new InvalidOperationException("CX01 webhook credentials are not configured.");
        var secret = await protector.DecryptAsync(credential, cancellationToken);
        try
        {
            await UnsubscribeAsync(connectionId, cancellationToken);
            var callbackBuilder = new UriBuilder(callback);
            var tenantParameter = "tenant=" + Uri.EscapeDataString(tenant.TenantId.ToString());
            callbackBuilder.Query = string.IsNullOrWhiteSpace(callbackBuilder.Query)
                ? tenantParameter : callbackBuilder.Query.TrimStart('?') + "&" + tenantParameter;
            var authentication = JsonSerializer.Serialize(new
            {
                Algorithm = Cx01Protocol.WebhookAlgorithm(configured.WebhookHmacAlgorithm),
                Key = Encoding.UTF8.GetString(secret),
            });
            var body = new
            {
                WebhookID = Cx01Protocol.WebhookId(tenant.TenantId.Value, connectionId),
                WebhookURL = callbackBuilder.Uri.ToString(),
                AuthenticationType = "HMAC",
                AuthenticationCred = authentication,
                Handshake = "tYES",
                VerifyCertificate = configured.WebhookVerifyCertificate ? "tYES" : "tNO",
                State = "Active",
                EventCollection = configured.EffectiveWebhookEvents().Select(value =>
                {
                    var item = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["BusinessObject"] = value.BusinessObject,
                        ["TransactionType"] = value.TransactionType,
                    };
                    if (value.BizObjProps is { Count: > 0 }) item["BizObjProps"] = value.BizObjProps;
                    return item;
                }).ToList(),
            };
            using var ignored = await client.SendJsonAsync(connectionId, HttpMethod.Post,
                Cx01Protocol.Root + Cx01Protocol.EventSubscriptions, body, false, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task UnsubscribeAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var id = Cx01Protocol.WebhookId(tenant.TenantId.Value, connectionId);
        var resource = Cx01Protocol.Root + Cx01Protocol.EventSubscriptions + "("
            + Cx01Protocol.KeyLiteral(id) + ")";
        try
        {
            using var paused = await client.SendJsonAsync(connectionId, HttpMethod.Post, resource + "/Pause",
                new { }, true, cancellationToken);
            using var deleted = await client.SendJsonAsync(connectionId, HttpMethod.Delete, resource,
                null, true, cancellationToken);
        }
        catch (Cx01RemoteException exception) when (exception.IsRecordGone)
        {
            // Idempotent replacement: a missing prior subscription is already in the desired state.
        }
    }

    public async Task<int> WatchdogConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        if (!configured.WebhooksAvailable) return 0;
        await events.PurgeAsync(connectionId, cancellationToken);
        var runId = Guid.CreateVersion7(clock.UtcNow);
        var watermark = await events.WatermarkAsync(connectionId, cancellationToken);
        var filter = "(Status eq 'Failed' or Status eq 'New')";
        if (watermark is { } date)
            filter += " and CreateDate ge '" + date.AddDays(-1).ToString("yyyy-MM-dd") + "'";
        var path = Cx01Protocol.Root + Cx01Protocol.EventNotifications
            + "?$filter=" + Uri.EscapeDataString(filter)
            + "&$orderby=CreateDate desc,CreateTime desc,EventID desc&$top=" + WatchdogPageSize;
        var accepted = 0;
        var examined = 0;
        DateOnly? newestCreateDate = watermark;
        while (examined < WatchdogRunCap)
        {
            using var log = await client.GetAsync(connectionId, path, cancellationToken);
            var rows = log.RootElement.TryGetProperty("value", out var values)
                ? values.EnumerateArray().Take(WatchdogRunCap - examined).ToList() : [];
            examined += rows.Count;
            var eventIds = rows.Select(row => Text(row, "EventID")).Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>().Distinct(StringComparer.Ordinal).ToList();
            var known = await events.ReadAsync(connectionId, eventIds, cancellationToken);
            var newlyPending = rows.Where(row => Text(row, "Status") == "New")
                .Select(row => Text(row, "EventID")).Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>().Where(eventId => !known.ContainsKey(eventId)).Distinct(StringComparer.Ordinal).ToList();
            if (newlyPending.Count > 0)
                await events.RememberPendingAsync(connectionId, newlyPending, runId, clock.UtcNow, cancellationToken);

            foreach (var row in rows)
            {
                var eventId = Text(row, "EventID");
                if (string.IsNullOrWhiteSpace(eventId)) continue;
                var status = Text(row, "Status");
                known.TryGetValue(eventId, out var state);
                if (state?.ProcessedAt is not null) continue;
                if (status == "New" && (state?.FirstSeenPendingRunId is null
                    || state.FirstSeenPendingRunId == runId)) continue;
                if (status is not ("New" or "Failed")) continue;
                var collection = Text(row, "BusinessObject");
                var key = Key(row);
                if (collection is null || key is null) continue;
                var mapped = await adapter.CaptureKeyAsync(connectionId, collection, key, cancellationToken);
                if (mapped is not null)
                {
                    var outcome = await inbox.Accept(mapped.Acceptance, cancellationToken);
                    if (outcome.Outcome == InboundAcceptanceResult.Accepted) accepted++;
                }
                await events.MarkProcessedAsync(connectionId, eventId, cancellationToken);
            }
            foreach (var row in rows)
                if (Date(row, "CreateDate") is { } created
                    && (newestCreateDate is null || created > newestCreateDate)) newestCreateDate = created;
            if (examined >= WatchdogRunCap || rows.Count == 0
                || !log.RootElement.TryGetProperty("@odata.nextLink", out var next)
                || string.IsNullOrWhiteSpace(next.GetString())) break;
            path = new Uri(next.GetString()!, UriKind.RelativeOrAbsolute).IsAbsoluteUri
                ? new Uri(next.GetString()!).PathAndQuery : next.GetString()!;
        }
        if (newestCreateDate is { } observedDate)
            await events.SetWatermarkAsync(connectionId, observedDate, cancellationToken);
        return accepted;
    }

    private static string? Key(JsonElement row)
    {
        var keyData = Text(row, "KeyData");
        if (string.IsNullOrWhiteSpace(keyData)) return null;
        try
        {
            using var parsed = JsonDocument.Parse(keyData);
            return Text(parsed.RootElement, "DocEntry") ?? Text(parsed.RootElement, "ItemCode")
                ?? Text(parsed.RootElement, "CardCode") ?? Text(parsed.RootElement, "ObjectKey");
        }
        catch (JsonException) { return null; }
    }

    private static DateOnly? Date(JsonElement row, string property)
    {
        var value = Text(row, property);
        return value is not null && value.Length >= 10 && DateOnly.TryParse(value[..10], out var date) ? date : null;
    }

    public async Task<int> WatchdogTenantAsync(CancellationToken cancellationToken)
    {
        var connections = await db.Connections.AsNoTracking().Where(x => x.TenantId == tenant.TenantId
            && x.ConnectorCode == Cx01Capabilities.ConnectorCode && x.Status == ConnectionStatus.Active)
            .Select(x => x.ConnectionId).ToListAsync(cancellationToken);
        var accepted = 0;
        foreach (var connection in connections)
        {
            try { accepted += await WatchdogConnectionAsync(connection, cancellationToken); }
            catch (InvalidOperationException) { }
        }
        return accepted;
    }

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;
}

[DisallowConcurrentExecution]
public sealed class Cx01WebhookWatchdogJob(ITenantContextSetter setter, Cx01WebhookMaintenance maintenance)
    : TenantJob(setter)
{
    public const string JobName = "integration.cx01-webhook-watchdog";
    public const string Cron = "30 * * * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        maintenance.WatchdogTenantAsync(cancellationToken);
}
