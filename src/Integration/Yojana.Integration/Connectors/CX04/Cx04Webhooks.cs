using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Attributes;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Connectors.CX04;

public sealed record Cx04NotificationHint(Guid ConnectionId, string EventId, string EntityType, string Key);

[NonTransactional]
public static class Cx04NotificationHintHandler
{
    public static async Task Handle(Cx04NotificationHint hint, Cx04Adapter adapter,
        InboundMessageStore inbox, CancellationToken ct)
    {
        foreach (var acceptance in await adapter.CaptureHintAsync(hint.ConnectionId, hint.EntityType, hint.Key, ct))
            _ = await inbox.Accept(acceptance, ct);
    }
}

public static class Cx04WebhookEndpoints
{
    public static IEndpointRouteBuilder MapCx04WebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/integration/connections/{connectionId:guid}/notifications/cx04", ReceiveAsync)
            .AllowAnonymous();
        return app;
    }

    private static async Task<IResult> ReceiveAsync(Guid connectionId, HttpRequest request,
        ITenantContextSetter setter, IntegrationDbContext db, Cx04Settings settings, IClock clock,
        IMessageBus bus, CancellationToken ct)
    {
        if (!TenantId.TryParse(request.Headers[IntegrationNotificationEndpoints.TenantHeader].FirstOrDefault(),
                out var tenantId)) return Results.BadRequest(new { error = "tenant_header_required" });
        setter.Set(tenantId);
        var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(value =>
            value.TenantId == tenantId && value.ConnectionId == connectionId
            && value.ConnectorCode == Cx04Capabilities.ConnectorCode
            && value.Status == ConnectionStatus.Active, ct);
        if (connection is null) return Results.NotFound();
        var configured = await settings.GetAsync(connectionId, ct);
        if (string.IsNullOrWhiteSpace(configured.NotificationSecretSha256))
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        var supplied = request.Headers[Cx04Protocol.NotificationSecretHeader].FirstOrDefault();
        if (string.IsNullOrEmpty(supplied)) return Results.Unauthorized();
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        byte[] expected;
        try { expected = Convert.FromHexString(configured.NotificationSecretSha256); }
        catch (FormatException) { return Results.StatusCode(StatusCodes.Status500InternalServerError); }
        if (!CryptographicOperations.FixedTimeEquals(actual, expected)) return Results.Unauthorized();

        JsonDocument body;
        try { body = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct); }
        catch (JsonException) { return Results.BadRequest(new { error = "notification_invalid" }); }
        using (body)
        {
            var root = body.RootElement;
            var eventId = root.TryGetProperty("Id", out var id) ? id.GetString() : null;
            if (string.IsNullOrWhiteSpace(eventId)) return Results.BadRequest(new { error = "event_id_required" });
            if (await db.ConnectionNotificationReceipts.AsNoTracking().AnyAsync(value =>
                    value.TenantId == tenantId && value.ConnectionId == connectionId
                    && value.EventId == eventId, ct)) return Results.Accepted();
            if (!TryHint(root, out var entity, out var key))
                return Results.BadRequest(new { error = "notification_key_invalid" });
            var subscribed = await db.ConnectionNotificationSources.AsNoTracking().AnyAsync(value =>
                value.TenantId == tenantId && value.ConnectionId == connectionId
                && value.Source == entity.EntityType && value.Enabled, ct);
            if (!subscribed) return Results.StatusCode(StatusCodes.Status403Forbidden);
            db.ConnectionNotificationReceipts.Add(new()
            {
                TenantId = tenantId, ConnectionId = connectionId, EventId = eventId,
                LastObservedAt = clock.UtcNow, ProcessedAt = clock.UtcNow,
            });
            await db.SaveChangesAsync(ct);
            await bus.PublishAsync(new Cx04NotificationHint(connectionId, eventId, entity.EntityType, key),
                new DeliveryOptions { TenantId = tenantId.ToString() });
            return Results.Accepted();
        }
    }

    internal static bool TryHint(JsonElement root, out Cx04EntityDefinition entity, out string key)
    {
        entity = null!;
        key = "";
        var candidates = new List<JsonElement>();
        if (root.TryGetProperty("Inserted", out var inserted) && inserted.ValueKind == JsonValueKind.Array)
            candidates.AddRange(inserted.EnumerateArray());
        if (root.TryGetProperty("Deleted", out var deleted) && deleted.ValueKind == JsonValueKind.Array)
            candidates.AddRange(deleted.EnumerateArray());
        foreach (var candidate in candidates)
        foreach (var definition in Cx04Protocol.Entities.Values)
        {
            var found = Cx04Mapper.Text(candidate, definition.KeyField);
            if (string.IsNullOrWhiteSpace(found)) continue;
            entity = definition;
            key = found;
            return true;
        }
        return false;
    }
}
