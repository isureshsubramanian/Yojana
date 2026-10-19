using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Wolverine;
using Wolverine.Attributes;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Inbox;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Webhooks;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Notifications;

public sealed record WebhookNotificationBatch(string Source, IReadOnlyList<WebhookNotification> Events);
public sealed record WebhookNotification(JsonElement CanonicalMessage, JsonElement RawPayload);
public sealed record WebhookNotificationResponse(int Accepted, int AlreadyProcessed, int Superseded);
public sealed record AcceptInboundNotificationBatch(IReadOnlyList<InboundAcceptance> Acceptances);

[NonTransactional]
public static class AcceptInboundNotificationBatchHandler
{
    public static async Task Handle(AcceptInboundNotificationBatch command,
        InboundMessageStore inbox, CancellationToken cancellationToken) =>
        _ = await inbox.AcceptMany(command.Acceptances, cancellationToken);
}

public static class IntegrationNotificationEndpoints
{
    public const string TenantHeader = "X-Yojana-Tenant-Id";
    public const string TimestampHeader = "X-Yojana-Webhook-Timestamp";
    public const string SignatureHeader = "X-Yojana-Webhook-Signature";
    public const string SigningCredentialPurpose = "webhook_signing";
    public const int MaximumBatchSize = 1_024;

    public static IEndpointRouteBuilder MapIntegrationNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/integration/connections/{connectionId:guid}/notifications", ReceiveAsync)
            .AllowAnonymous();
        return app;
    }

    private static async Task<IResult> ReceiveAsync(Guid connectionId, HttpRequest request,
        ITenantContextSetter tenantSetter, IntegrationDbContext db,
        IConnectionCredentialStore credentials, ConnectionCredentialProtector protector,
        HmacSha256WebhookSignatureVerifier verifier, IMessageBus bus, IClock clock,
        CancellationToken cancellationToken)
    {
        if (!TenantId.TryParse(request.Headers[TenantHeader].FirstOrDefault(), out var tenantId))
            return Results.BadRequest(new { error = "tenant_header_required" });
        tenantSetter.Set(tenantId);

        var connection = await db.Connections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId
            && x.ConnectionId == connectionId && x.Status == Data.Entities.ConnectionStatus.Active, cancellationToken);
        if (connection is null) return Results.NotFound();
        if (!DateTime.TryParse(request.Headers[TimestampHeader].FirstOrDefault(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                out var timestamp))
            return Results.Unauthorized();
        byte[] signature;
        try { signature = Convert.FromHexString(request.Headers[SignatureHeader].FirstOrDefault() ?? ""); }
        catch (FormatException) { return Results.Unauthorized(); }

        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken);
        var bytes = body.ToArray();
        var credential = await credentials.GetCurrentAsync(tenantId, connectionId, SigningCredentialPurpose,
            cancellationToken);
        if (credential is null) return Results.Unauthorized();
        var secret = await protector.DecryptAsync(credential, cancellationToken);
        try
        {
            var signed = SignedContent(timestamp, bytes);
            var verified = verifier.Verify(new(timestamp, signed, signature,
                [new(credential.CreatedAt.ToString("O"), secret)]), clock.UtcNow);
            if (verified != WebhookSignatureResult.Valid) return Results.Unauthorized();
        }
        finally { CryptographicOperations.ZeroMemory(secret); }

        WebhookNotificationBatch? batch;
        try { batch = JsonSerializer.Deserialize<WebhookNotificationBatch>(bytes, IntegrationContracts.Json); }
        catch (JsonException) { return Results.BadRequest(new { error = "notification_batch_invalid" }); }
        if (batch is null || batch.Events.Count is < 1 or > MaximumBatchSize)
            return Results.BadRequest(new { error = "notification_batch_size_invalid", maximum = MaximumBatchSize });
        var subscribed = await db.ConnectionNotificationSources.AsNoTracking().AnyAsync(x => x.TenantId == tenantId
            && x.ConnectionId == connectionId && x.Source == batch.Source && x.Enabled, cancellationToken);
        if (!subscribed) return Results.StatusCode(StatusCodes.Status403Forbidden);

        var acceptances = new List<InboundAcceptance>(batch.Events.Count);
        foreach (var notification in batch.Events)
        {
            CanonicalMessage<JsonElement>? canonical;
            try { canonical = notification.CanonicalMessage.Deserialize<CanonicalMessage<JsonElement>>(IntegrationContracts.Json); }
            catch (JsonException) { return Results.BadRequest(new { error = "canonical_message_invalid" }); }
            if (canonical is null || canonical.Envelope.TenantId != tenantId.Value
                || canonical.Envelope.ConnectionId != connectionId
                || !StringComparer.Ordinal.Equals(canonical.Envelope.ConnectorCode, connection.ConnectorCode))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            acceptances.Add(new(canonical.Envelope, canonical.MessageType,
                notification.CanonicalMessage.GetRawText(), notification.RawPayload.GetRawText()));
        }
        await bus.PublishAsync(new AcceptInboundNotificationBatch(acceptances),
            new DeliveryOptions { TenantId = tenantId.ToString() });
        return Results.Accepted(value: new WebhookNotificationResponse(acceptances.Count, 0, 0));
    }

    public static byte[] SignedContent(DateTime timestampUtc, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.UTF8.GetBytes(timestampUtc.ToUniversalTime().ToString("O") + ".");
        var result = new byte[prefix.Length + body.Length];
        prefix.CopyTo(result, 0);
        body.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }
}
