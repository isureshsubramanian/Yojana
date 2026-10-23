using System.Globalization;
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
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Connectors.CX02;

public sealed record Cx02NotificationHint(Guid ConnectionId, string EventId, string Resource,
    string ChangeType);

[NonTransactional]
public static class Cx02NotificationHintHandler
{
    public static async Task Handle(Cx02NotificationHint hint, Cx02Adapter adapter, InboundMessageStore inbox,
        Cx02NotificationTracker tracker, CancellationToken cancellationToken)
    {
        await using var operation = await tracker.EnterAsync(hint.ConnectionId, cancellationToken);
        if (!await tracker.IsActiveAsync(hint.ConnectionId, cancellationToken)
            || await tracker.IsProcessedAsync(hint.ConnectionId, hint.EventId, cancellationToken)) return;
        var events = await adapter.CaptureHintAsync(hint.ConnectionId, hint.Resource, hint.ChangeType,
            cancellationToken);
        await inbox.AcceptMany(events, cancellationToken);
        await tracker.MarkProcessedAsync(hint.ConnectionId, hint.EventId, cancellationToken);
    }
}

public static class Cx02WebhookEndpoints
{
    public static IEndpointRouteBuilder MapCx02WebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var path = "/api/integration/connections/{connectionId:guid}/notifications/cx02";
        app.MapGet(path, ReceiveAsync).AllowAnonymous();
        app.MapPost(path, ReceiveAsync).AllowAnonymous();
        return app;
    }

    internal static async Task<IResult> ReceiveAsync(Guid connectionId, HttpRequest request,
        ITenantContextSetter tenantSetter, IntegrationDbContext db, Cx02Client client, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        var tenantValue = request.Headers[IntegrationNotificationEndpoints.TenantHeader].FirstOrDefault()
            ?? request.Query["tenant"].FirstOrDefault();
        if (!TenantId.TryParse(tenantValue, out var tenantId))
            return Results.BadRequest(new { error = "tenant_header_required" });
        tenantSetter.Set(tenantId);
        var active = await db.Connections.AsNoTracking().AnyAsync(connection => connection.TenantId == tenantId
            && connection.ConnectionId == connectionId && connection.ConnectorCode == Cx02Capabilities.ConnectorCode
            && connection.Status == ConnectionStatus.Active, cancellationToken);
        if (!active) return Results.NotFound();

        var validation = request.Query["validationToken"].FirstOrDefault();
        if (!string.IsNullOrEmpty(validation))
            return Results.Text(validation, "text/plain", Encoding.UTF8, StatusCodes.Status200OK);
        if (HttpMethods.IsGet(request.Method)) return Results.Ok();

        JsonDocument body;
        try { body = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken); }
        catch (JsonException)
        {
            // A malformed delivery is not useful, but a rejection would delete the valid subscription. Polling and
            // reconciliation remain the recovery path, so acknowledge without enqueueing.
            return Results.Ok();
        }
        using (body)
        {
            var expected = await client.NotificationClientStateAsync(connectionId, cancellationToken);
            foreach (var hint in Hints(connectionId, body.RootElement, expected).Take(1_024))
                try
                {
                    await bus.PublishAsync(hint, new DeliveryOptions { TenantId = tenantId.ToString() });
                }
                catch
                {
                    // Modified-since polling is the durable recovery source. The notification endpoint must stay a
                    // fast acknowledgement path even while the local message transport is unavailable.
                }
        }
        return Results.Ok();
    }

    internal static IReadOnlyList<Cx02NotificationHint> Hints(Guid connectionId, JsonElement root,
        string expectedClientState)
    {
        var result = new List<Cx02NotificationHint>();
        if (root.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
        {
            foreach (var value in values.EnumerateArray())
            {
                if (!FixedEquals(Text(value, "clientState"), expectedClientState)) continue;
                var resource = Text(value, "resource");
                if (string.IsNullOrWhiteSpace(resource)) continue;
                var change = Text(value, "changeType") ?? "collection";
                var identity = string.Join('|', Text(value, "subscriptionId"), resource, change,
                    Text(value, "lastModifiedDateTime"));
                result.Add(new(connectionId, StableEventId(identity), resource, change));
            }
            return result;
        }
        if (!FixedEquals(Text(root, "clientState"), expectedClientState)) return result;
        var eventName = Text(root, "eventName");
        var eventResource = eventName switch
        {
            "salesShipmentPosted" => "trackedItemLedgerEntries",
            "purchaseReceiptPosted" => "trackedItemLedgerEntries",
            "salesCreditMemoPosted" => "salesCreditMemos",
            _ => null,
        };
        if (eventResource is null) return result;
        var businessIdentity = string.Join('|', eventName, Text(root, "timestamp"),
            root.TryGetProperty("payload", out var payload) ? payload.GetRawText() : "");
        result.Add(new(connectionId, StableEventId(businessIdentity), eventResource, "collection"));
        return result;
    }

    private static bool FixedEquals(string? presented, string expected)
    {
        if (presented is null) return false;
        var left = Encoding.UTF8.GetBytes(presented);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private static string StableEventId(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string? Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var found) && found.ValueKind != JsonValueKind.Null
            ? found.ValueKind == JsonValueKind.String ? found.GetString() : found.ToString() : null;
}

public sealed class Cx02NotificationTracker(IntegrationDbContext db, ITenantContext tenant, IClock clock)
{
    public async Task<IAsyncDisposable> EnterAsync(Guid connectionId, CancellationToken cancellationToken) =>
        await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId, connectionId, cancellationToken);

    public Task<bool> IsActiveAsync(Guid connectionId, CancellationToken cancellationToken) =>
        db.Connections.AsNoTracking().AnyAsync(connection => connection.TenantId == tenant.TenantId
            && connection.ConnectionId == connectionId && connection.Status == ConnectionStatus.Active,
            cancellationToken);

    public Task<bool> IsProcessedAsync(Guid connectionId, string eventId, CancellationToken cancellationToken) =>
        db.ConnectionNotificationReceipts.AsNoTracking().AnyAsync(receipt => receipt.TenantId == tenant.TenantId
            && receipt.ConnectionId == connectionId && receipt.EventId == eventId && receipt.ProcessedAt != null,
            cancellationToken);

    public async Task MarkProcessedAsync(Guid connectionId, string eventId,
        CancellationToken cancellationToken)
    {
        var receipt = await db.ConnectionNotificationReceipts.SingleOrDefaultAsync(value =>
            value.TenantId == tenant.TenantId && value.ConnectionId == connectionId && value.EventId == eventId,
            cancellationToken);
        if (receipt is null)
        {
            receipt = new ConnectionNotificationReceipt
            {
                TenantId = tenant.TenantId, ConnectionId = connectionId, EventId = eventId,
                FirstSeenPendingAt = clock.UtcNow, LastObservedAt = clock.UtcNow,
            };
            db.ConnectionNotificationReceipts.Add(receipt);
        }
        receipt.LastObservedAt = clock.UtcNow;
        receipt.ProcessedAt ??= clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed class Cx02WebhookMaintenance(
    IntegrationDbContext db,
    ITenantContext tenant,
    IClock clock,
    Cx02Settings settings,
    Cx02Client client)
{
    private static readonly string[] BusinessEvents =
        ["salesShipmentPosted", "purchaseReceiptPosted", "salesCreditMemoPosted"];

    public async Task EnsureAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        if (configured.NotificationBaseUri is null) return;
        var callback = Callback(configured.NotificationBaseUri, connectionId, tenant.TenantId);
        var clientState = await client.NotificationClientStateAsync(connectionId, cancellationToken);
        var state = await settings.NotificationsAsync(connectionId, cancellationToken);
        var saved = new List<Cx02SubscriptionState>();
        foreach (var entity in Cx02Protocol.NotificationEntities)
        {
            var resource = Cx02Protocol.SubscriptionResource(configured, entity);
            var current = state.Subscriptions.SingleOrDefault(value => value.Resource == resource);
            if (current is not null && await ExistsAsync(connectionId, configured, current, cancellationToken))
            {
                if (current.ExpiresAt > clock.UtcNow.AddHours(configured.RenewSubscriptionsBeforeHours))
                {
                    saved.Add(current);
                    continue;
                }
                current = await RenewAsync(connectionId, configured, current, callback, clientState,
                    cancellationToken);
                saved.Add(current);
                continue;
            }
            saved.Add(await CreateAsync(connectionId, configured, entity, callback, clientState,
                cancellationToken));
        }
        var businessSubscribed = state.BusinessEventsSubscribed;
        if (configured.BusinessEventHintsEnabled && !businessSubscribed)
        {
            using var catalogue = await client.GetAsync(connectionId,
                new(Cx02Protocol.RuntimeRoot(configured), "externalbusinesseventdefinitions"), cancellationToken);
            var definitions = catalogue.RootElement.GetProperty("value").EnumerateArray()
                .Where(value => value.TryGetProperty("name", out _) && value.TryGetProperty("appId", out _))
                .ToDictionary(value => value.GetProperty("name").GetString()!,
                    value => value.GetProperty("appId").GetString()!, StringComparer.Ordinal);
            foreach (var eventName in BusinessEvents)
            {
                if (!definitions.TryGetValue(eventName, out var appId))
                    throw new InvalidOperationException($"CX02 business event '{eventName}' is not available.");
                await client.SendJsonAsync(connectionId, HttpMethod.Post,
                    new(Cx02Protocol.RuntimeRoot(configured), "externaleventsubscriptions"), new
                    {
                        companyName = "",
                        companyId = configured.CompanyId,
                        eventName,
                        appId,
                        notificationUrl = callback,
                        clientState,
                    }, false, null, cancellationToken);
            }
            businessSubscribed = true;
        }
        await settings.SetNotificationsAsync(connectionId, new(saved, businessSubscribed), cancellationToken);
        await SetWatchdogAsync(connectionId, "subscribed", cancellationToken);
    }

    public async Task UnsubscribeAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        var state = await settings.NotificationsAsync(connectionId, cancellationToken);
        foreach (var subscription in state.Subscriptions)
            try
            {
                await client.SendJsonAsync(connectionId, HttpMethod.Delete,
                    new(Cx02Protocol.Subscriptions(configured, subscription.Custom),
                        $"subscriptions({subscription.SubscriptionId:D})"), null, true, null, cancellationToken);
            }
            catch (Cx02RemoteException exception) when (exception.IsNotFound) { }
        await settings.SetNotificationsAsync(connectionId, Cx02NotificationState.Empty, cancellationToken);
        await SetWatchdogAsync(connectionId, "not_subscribed", cancellationToken);
    }

    public Task PauseAsync(Guid connectionId, CancellationToken cancellationToken) =>
        UnsubscribeAsync(connectionId, cancellationToken);

    public Task ResumeAsync(Guid connectionId, CancellationToken cancellationToken) =>
        EnsureAsync(connectionId, cancellationToken);

    public async Task MaintainTenantAsync(CancellationToken cancellationToken)
    {
        var connections = await db.Connections.AsNoTracking().Where(connection => connection.TenantId == tenant.TenantId
                && connection.ConnectorCode == Cx02Capabilities.ConnectorCode
                && connection.Status == ConnectionStatus.Active)
            .Select(connection => connection.ConnectionId).ToListAsync(cancellationToken);
        foreach (var connectionId in connections)
        {
            try
            {
                await using var operation = await ConnectionOperationLock.ForConnectionAsync(db, tenant.TenantId,
                    connectionId, cancellationToken);
                await EnsureAsync(connectionId, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await SetWatchdogAsync(connectionId, "stopped", cancellationToken);
            }
        }
    }

    private async Task<bool> ExistsAsync(Guid connectionId, Cx02ConnectionSettings configured,
        Cx02SubscriptionState state, CancellationToken cancellationToken)
    {
        try
        {
            using var ignored = await client.GetAsync(connectionId,
                new(Cx02Protocol.Subscriptions(configured, state.Custom),
                    $"subscriptions({state.SubscriptionId:D})"), cancellationToken);
            return true;
        }
        catch (Cx02RemoteException exception) when (exception.IsNotFound) { return false; }
    }

    private async Task<Cx02SubscriptionState> CreateAsync(Guid connectionId,
        Cx02ConnectionSettings configured, Cx02EntityDefinition entity, Uri callback, string clientState,
        CancellationToken cancellationToken)
    {
        using var created = await client.SendJsonAsync(connectionId, HttpMethod.Post,
            Cx02Protocol.Subscriptions(configured, entity.Custom), new
            {
                notificationUrl = callback,
                resource = Cx02Protocol.SubscriptionResource(configured, entity),
                clientState,
            }, false, null, cancellationToken) ?? throw new InvalidOperationException("CX02 subscription response is empty.");
        return State(created.RootElement, entity.Custom);
    }

    private async Task<Cx02SubscriptionState> RenewAsync(Guid connectionId,
        Cx02ConnectionSettings configured, Cx02SubscriptionState current, Uri callback, string clientState,
        CancellationToken cancellationToken)
    {
        using var renewed = await client.SendJsonAsync(connectionId, HttpMethod.Patch,
            new(Cx02Protocol.Subscriptions(configured, current.Custom),
                $"subscriptions({current.SubscriptionId:D})"), new
            {
                notificationUrl = callback,
                clientState,
            }, true, new Dictionary<string, string> { ["If-Match"] = current.Etag }, cancellationToken)
            ?? throw new InvalidOperationException("CX02 subscription renewal response is empty.");
        return State(renewed.RootElement, current.Custom);
    }

    private async Task SetWatchdogAsync(Guid connectionId, string value, CancellationToken cancellationToken)
    {
        var row = await db.ConnectionNotificationWatchdogs.SingleOrDefaultAsync(item =>
            item.TenantId == tenant.TenantId && item.ConnectionId == connectionId, cancellationToken);
        if (row is null)
        {
            row = new ConnectionNotificationWatchdog
            {
                TenantId = tenant.TenantId, ConnectionId = connectionId,
            };
            db.ConnectionNotificationWatchdogs.Add(row);
        }
        row.StreamState = value;
        row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Cx02SubscriptionState State(JsonElement value, bool custom) => new(
        value.GetProperty("subscriptionId").GetGuid(), value.GetProperty("resource").GetString()!, custom,
        value.GetProperty("@odata.etag").GetString()!, value.GetProperty("expirationDateTime").GetDateTimeOffset());

    private static Uri Callback(Uri root, Guid connectionId, TenantId tenant)
    {
        var callback = new Uri(root, $"api/integration/connections/{connectionId:D}/notifications/cx02");
        var builder = new UriBuilder(callback)
        {
            Query = "tenant=" + Uri.EscapeDataString(tenant.ToString()),
        };
        return builder.Uri;
    }
}

[DisallowConcurrentExecution]
public sealed class Cx02WebhookMaintenanceJob(ITenantContextSetter setter, Cx02WebhookMaintenance maintenance)
    : TenantJob(setter)
{
    public const string JobName = "integration.cx02-webhook-maintenance";
    public const string Cron = "0 0 0/6 * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        maintenance.MaintainTenantAsync(cancellationToken);
}
