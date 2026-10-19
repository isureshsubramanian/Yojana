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

public sealed record Cx01WebhookHint(Guid ConnectionId, string Collection, string Key, string EventId);

[NonTransactional]
public static class Cx01WebhookHintHandler
{
    public static async Task Handle(Cx01WebhookHint hint, Cx01Adapter adapter, InboundMessageStore inbox,
        CancellationToken cancellationToken)
    {
        var mapped = await adapter.CaptureKeyAsync(hint.ConnectionId, hint.Collection, hint.Key,
            cancellationToken);
        if (mapped is not null) _ = await inbox.Accept(mapped.Acceptance, cancellationToken);
    }
}

public static class Cx01WebhookEndpoints
{
    public const string SigningCredentialPurpose = "cx01_webhook";

    public static IEndpointRouteBuilder MapCx01WebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/integration/connections/{connectionId:guid}/notifications/cx01", ReceiveAsync)
            .AllowAnonymous();
        return app;
    }

    private static async Task<IResult> ReceiveAsync(Guid connectionId, HttpRequest request, HttpResponse response,
        ITenantContextSetter tenantSetter, IntegrationDbContext db, Cx01Settings settings,
        IConnectionCredentialStore credentials, ConnectionCredentialProtector protector, IMessageBus bus,
        CancellationToken cancellationToken)
    {
        if (!TenantId.TryParse(request.Headers[IntegrationNotificationEndpoints.TenantHeader].FirstOrDefault(),
                out var tenantId)) return Results.BadRequest(new { error = "tenant_header_required" });
        tenantSetter.Set(tenantId);
        var active = await db.Connections.AsNoTracking().AnyAsync(x => x.TenantId == tenantId
            && x.ConnectionId == connectionId && x.ConnectorCode == Cx01Capabilities.ConnectorCode
            && x.Status == ConnectionStatus.Active, cancellationToken);
        if (!active) return Results.NotFound();
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        if (!configured.WebhooksAvailable) return Results.NotFound();

        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken);
        var bytes = body.ToArray();
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

        var handshake = request.Headers[Cx01Protocol.HandshakeTokenHeader].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(handshake))
        {
            response.Headers[Cx01Protocol.HandshakeTokenHeader] = handshake;
            return Results.Ok();
        }

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
                var hint = Hint(item, connectionId);
                if (hint is not null)
                    await bus.PublishAsync(hint, new DeliveryOptions { TenantId = tenantId.ToString() });
            }
        }
        return Results.Accepted();
    }

    private static Cx01WebhookHint? Hint(JsonElement item, Guid connectionId)
    {
        var id = Text(item, "id") ?? Guid.CreateVersion7().ToString("D");
        var type = Text(item, "type") ?? "";
        var collection = Cx01Protocol.Entities.Values.Select(x => x.Collection)
            .FirstOrDefault(x => type.Contains(x, StringComparison.OrdinalIgnoreCase));
        if (item.TryGetProperty("data", out var data))
        {
            collection ??= Text(data, "ObjectType");
            var key = Text(data, "DocEntry") ?? Text(data, "ObjectKey") ?? Text(data, "ItemCode")
                ?? Text(data, "CardCode");
            if (collection is not null && key is not null) return new(connectionId, collection, key, id);
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

    private static bool TrySignature(string? value, out byte[] signature)
    {
        try
        {
            signature = Convert.FromHexString(value ?? "");
            if (signature.Length > 0) return true;
        }
        catch (FormatException) { }
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

public sealed class Cx01WebhookMaintenance(
    ITenantContext tenant,
    IntegrationDbContext db,
    Cx01Settings settings,
    Cx01Client client,
    Cx01Adapter adapter,
    InboundMessageStore inbox,
    IConnectionCredentialStore credentials,
    ConnectionCredentialProtector protector)
{
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
            var body = new
            {
                Name = $"Yojana-{connectionId:N}",
                CallbackUrl = callback.ToString(),
                AuthenticationType = "HMAC",
                HMACAlgorithm = configured.WebhookHmacAlgorithm,
                Secret = Encoding.UTF8.GetString(secret),
                Handshake = "tYES",
                Active = "tYES",
            };
            using var ignored = await client.SendJsonAsync(connectionId, HttpMethod.Post,
                Cx01Protocol.Root + Cx01Protocol.EventSubscriptions, body, false, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public async Task<int> WatchdogConnectionAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(connectionId, cancellationToken);
        if (!configured.WebhooksAvailable) return 0;
        using var log = await client.GetAsync(connectionId, Cx01Protocol.Root + Cx01Protocol.EventNotifications
            + "?$filter=Status%20eq%20'Failed'&$orderby=CreateDate,CreateTime&$top=1024", cancellationToken);
        if (!log.RootElement.TryGetProperty("value", out var values)) return 0;
        var accepted = 0;
        foreach (var row in values.EnumerateArray())
        {
            var collection = Text(row, "ObjectType");
            var key = Text(row, "ObjectKey");
            if (collection is null || key is null) continue;
            var mapped = await adapter.CaptureKeyAsync(connectionId, collection, key, cancellationToken);
            if (mapped is null) continue;
            var outcome = await inbox.Accept(mapped.Acceptance, cancellationToken);
            if (outcome.Outcome == InboundAcceptanceResult.Accepted) accepted++;
        }
        return accepted;
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
