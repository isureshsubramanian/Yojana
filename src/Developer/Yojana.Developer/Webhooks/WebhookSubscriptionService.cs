using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Developer.Contracts;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Developer.Events;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.Webhooks;

public sealed class WebhookSubscriptionService(DeveloperDbContext db, ITenantContext tenant, IClock clock,
    DeveloperWebhookSettings settings, WebhookTargetGuard guard, WebhookTransport transport,
    IUserSecretProtector protector, SecurityEventWriter security, PlatformDbContext platform)
{
    public async Task<WebhookSubscriptionPage> ListAsync(string? sort, string? direction, string? cursor,
        int? take, CancellationToken cancellationToken)
    {
        var paging = DeveloperEndpoints.WebhookPages.Parse(sort, direction, take, cursor);
        var rows = await paging.Apply(db.Subscriptions.AsNoTracking()
                .Where(row => row.TenantId == tenant.TenantId))
            .Take(paging.Take + 1).ToListAsync(cancellationToken);
        var selected = rows.Take(paging.Take).ToList();
        var activities = await ActivitiesAsync(selected.Select(row => row.SubscriptionId).ToList(),
            cancellationToken);
        var page = selected.Select(row => View(row,
            activities.GetValueOrDefault(row.SubscriptionId, new(0, 0, 0)))).ToList();
        var since = clock.UtcNow.AddHours(-24);
        var delivered = await db.Deliveries.AsNoTracking().LongCountAsync(row => row.TenantId == tenant.TenantId
            && row.Status == WebhookDeliveryStatus.Succeeded && row.SucceededAt >= since, cancellationToken);
        var waiting = await db.Deliveries.AsNoTracking().LongCountAsync(row => row.TenantId == tenant.TenantId
            && row.Status == WebhookDeliveryStatus.Pending, cancellationToken);
        var parked = await db.Deliveries.AsNoTracking().LongCountAsync(row => row.TenantId == tenant.TenantId
            && row.Status == WebhookDeliveryStatus.Parked, cancellationToken);
        var paused = await db.Subscriptions.AsNoTracking().CountAsync(row => row.TenantId == tenant.TenantId
            && row.Status == WebhookSubscriptionStatus.Paused, cancellationToken);
        var next = rows.Count > paging.Take && selected.Count > 0 ? paging.CursorFor(selected[^1]) : null;
        return new(page, next, new(delivered, waiting, parked, paused));
    }

    public async Task<WebhookSubscriptionView?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == tenant.TenantId
            && item.SubscriptionId == id, cancellationToken);
        if (row is null) return null;
        var activities = await ActivitiesAsync([id], cancellationToken);
        return View(row, activities.GetValueOrDefault(id, new(0, 0, 0)));
    }

    public async Task<WebhookSecretResponse> CreateAsync(CreateWebhookSubscription request, Guid actor,
        SecurityRequestContext requestContext, CancellationToken cancellationToken)
    {
        Validate(request.Name, request.EventTypes, request.Filters);
        if (request.RelayId is not null)
            throw new CommandRefusedException("webhook_private_target_needs_relay",
                "Private webhook delivery through the site relay is not available for this endpoint.");
        await guard.VetAsync(request.Url, cancellationToken);
        if (await db.Subscriptions.AnyAsync(row => row.TenantId == tenant.TenantId
            && row.Name == request.Name.Trim(), cancellationToken))
            throw new CommandRefusedException("webhook_name_exists", "A webhook subscription already uses that name.");
        var now = clock.UtcNow;
        var plain = RandomNumberGenerator.GetBytes(32);
        try
        {
            var row = new WebhookSubscription
            {
                TenantId = tenant.TenantId, SubscriptionId = Guid.CreateVersion7(now), Name = request.Name.Trim(),
                Url = request.Url, EventTypes = JsonSerializer.Serialize(NormaliseTypes(request.EventTypes), PublicEventTranslator.Json),
                Filters = JsonSerializer.Serialize(NormaliseFilters(request.Filters), PublicEventTranslator.Json),
                Status = WebhookSubscriptionStatus.PendingVerification,
                SecretSealed = await protector.ProtectAsync(plain, WebhookTransport.SecretPurpose, cancellationToken),
                RatePerSecond = await settings.RatePerSecondAsync(cancellationToken),
                CreatedAt = now, CreatedBy = actor, UpdatedAt = now, UpdatedBy = actor,
            };
            db.Subscriptions.Add(row);
            await VerifyAsync(row, cancellationToken);
            Security("webhook_created", actor, requestContext);
            await SaveAsync(cancellationToken);
            return new(View(row), EncodeSecret(plain));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public async Task<WebhookActionResponse> UpdateAsync(Guid id, UpdateWebhookSubscription request,
        Guid actor, SecurityRequestContext requestContext, CancellationToken cancellationToken)
    {
        Validate(request.Name, request.EventTypes, request.Filters);
        if (request.RelayId is not null)
            throw new CommandRefusedException("webhook_private_target_needs_relay",
                "Private webhook delivery through the site relay is not available for this endpoint.");
        await guard.VetAsync(request.Url, cancellationToken);
        var row = await RequiredAsync(id, cancellationToken);
        RequireVersion(request.ExpectedVersion, row.RowVersion);
        if (await db.Subscriptions.AnyAsync(other => other.TenantId == tenant.TenantId
            && other.SubscriptionId != id && other.Name == request.Name.Trim(), cancellationToken))
            throw new CommandRefusedException("webhook_name_exists", "A webhook subscription already uses that name.");
        var urlChanged = !StringComparer.Ordinal.Equals(row.Url, request.Url);
        row.Name = request.Name.Trim();
        row.Url = request.Url;
        row.EventTypes = JsonSerializer.Serialize(NormaliseTypes(request.EventTypes), PublicEventTranslator.Json);
        row.Filters = JsonSerializer.Serialize(NormaliseFilters(request.Filters), PublicEventTranslator.Json);
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = actor;
        if (urlChanged)
        {
            row.Status = WebhookSubscriptionStatus.PendingVerification;
            await VerifyAsync(row, cancellationToken);
        }
        Security("webhook_updated", actor, requestContext);
        await SaveAsync(cancellationToken);
        return new(View(row));
    }

    public async Task<WebhookSecretResponse> RotateAsync(Guid id, Guid actor,
        SecurityRequestContext requestContext, CancellationToken cancellationToken)
    {
        var row = await RequiredAsync(id, cancellationToken);
        if (row.Status == WebhookSubscriptionStatus.Disabled)
            throw new CommandRefusedException("webhook_disabled", "A disabled webhook cannot rotate its secret.");
        var plain = RandomNumberGenerator.GetBytes(32);
        try
        {
            row.PreviousSecretSealed = row.SecretSealed;
            row.PreviousSecretExpiresAt = clock.UtcNow.AddHours(24);
            row.SecretSealed = await protector.ProtectAsync(plain, WebhookTransport.SecretPurpose, cancellationToken);
            row.UpdatedAt = clock.UtcNow;
            row.UpdatedBy = actor;
            await VerifyAsync(row, cancellationToken);
            Security("webhook_secret_rotated", actor, requestContext);
            await SaveAsync(cancellationToken);
            return new(View(row), EncodeSecret(plain));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    public Task<WebhookActionResponse> PauseAsync(Guid id, Guid actor, SecurityRequestContext request,
        CancellationToken ct) => ChangeStatusAsync(id, WebhookSubscriptionStatus.Paused, "administrator",
            "webhook_paused", actor, request, ct);

    public async Task<WebhookActionResponse> ResumeAsync(Guid id, Guid actor,
        SecurityRequestContext request, CancellationToken ct)
    {
        var response = await ChangeStatusAsync(id, WebhookSubscriptionStatus.Active, null,
            "webhook_resumed", actor, request, ct, save: false);
        await db.Deliveries.Where(row => row.TenantId == tenant.TenantId && row.SubscriptionId == id
                && row.Status == WebhookDeliveryStatus.Parked)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, WebhookDeliveryStatus.Pending)
                .SetProperty(row => row.ParkedAt, (DateTime?)null)
                .SetProperty(row => row.NextAttemptAt, clock.UtcNow), ct);
        await db.Deliveries.Where(row => row.TenantId == tenant.TenantId && row.SubscriptionId == id
                && row.Status == WebhookDeliveryStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.RetryWindowStartedAt, (DateTime?)null), ct);
        await SaveAsync(ct);
        return response;
    }

    public Task<WebhookActionResponse> DeleteAsync(Guid id, Guid actor, SecurityRequestContext request,
        CancellationToken ct) => ChangeStatusAsync(id, WebhookSubscriptionStatus.Disabled, "deleted",
            "webhook_deleted", actor, request, ct);

    public async Task<WebhookActionResponse> VerifyAgainAsync(Guid id, Guid actor,
        SecurityRequestContext request, CancellationToken ct)
    {
        var row = await RequiredAsync(id, ct);
        if (row.Status == WebhookSubscriptionStatus.Disabled)
            throw new CommandRefusedException("webhook_disabled", "A disabled webhook cannot be verified.");
        await guard.VetAsync(row.Url, ct);
        await VerifyAsync(row, ct);
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = actor;
        Security("webhook_verified", actor, request);
        await SaveAsync(ct);
        return new(View(row));
    }

    public async Task<SendWebhookTestResponse> SendTestAsync(Guid id, SendWebhookTestRequest request,
        CancellationToken ct)
    {
        var row = await RequiredAsync(id, ct);
        if (row.Status != WebhookSubscriptionStatus.Active)
            throw new CommandRefusedException("webhook_not_active", "Only an active webhook can receive a test event.");
        var type = request.EventType ?? PublicEventTypes.UnitCreated;
        var subscribedTypes = JsonSerializer.Deserialize<List<string>>(row.EventTypes,
            PublicEventTranslator.Json) ?? [];
        if (!subscribedTypes.Contains(type, StringComparer.Ordinal)
            || !PublicEventTypes.Subscribable.Contains(type, StringComparer.Ordinal))
            throw new CommandRefusedException("webhook_event_type_unknown", "Choose an event type subscribed by this webhook.");
        var eventId = Guid.CreateVersion7(clock.UtcNow);
        var envelope = new PublicEventEnvelope(eventId, type, PublicEventTypes.CurrentVersion,
            "t_" + tenant.TenantId.Value.ToString("N"), clock.UtcNow, clock.UtcNow,
            new("test", eventId, "TEST-EVENT"), 1,
            new Dictionary<string, object?> { ["test"] = true },
            new Dictionary<string, string> { ["self"] = $"/api/public/v1/events/{eventId:D}" }, true);
        db.PublicEvents.Add(new()
        {
            TenantId = tenant.TenantId, EventId = eventId, Type = type, Version = envelope.Version,
            RecordKind = envelope.Record.Kind, RecordId = eventId, Sequence = 1,
            OccurredAt = envelope.OccurredAt, RecordedAt = envelope.RecordedAt,
            Body = JsonSerializer.Serialize(envelope, PublicEventTranslator.Json), Test = true,
        });
        var deliveryId = Guid.CreateVersion7(clock.UtcNow);
        db.Deliveries.Add(new()
        {
            TenantId = tenant.TenantId, DeliveryId = deliveryId, SubscriptionId = id,
            EventId = eventId, Status = WebhookDeliveryStatus.Pending, CreatedAt = clock.UtcNow,
            NextAttemptAt = clock.UtcNow,
        });
        await SaveAsync(ct);
        return new(eventId, deliveryId);
    }

    private async Task VerifyAsync(WebhookSubscription row, CancellationToken ct)
    {
        var challenge = Base64Url(RandomNumberGenerator.GetBytes(32));
        row.VerificationChallengeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(challenge))).ToLowerInvariant();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { type = PublicEventTypes.EndpointVerify, challenge }, PublicEventTranslator.Json);
        var result = await transport.SendAsync(row, body, Guid.CreateVersion7(clock.UtcNow),
            PublicEventTypes.EndpointVerify, 1, ct, timeoutSeconds: 10);
        if (!result.Success || !EchoesChallenge(result.ResponseBody, challenge))
            throw new CommandRefusedException("webhook_verification_failed", "The endpoint did not echo the verification challenge within 10 seconds.");
        row.Status = WebhookSubscriptionStatus.Active;
        row.VerifiedAt = clock.UtcNow;
        row.VerificationChallengeHash = null;
        row.PausedAt = null;
        row.PausedReason = null;
    }

    private static bool EchoesChallenge(string? body, string challenge)
    {
        if (body is null) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("challenge", out var value)
                && value.GetString() == challenge;
        }
        catch (JsonException) { return body.Trim() == challenge; }
    }

    private async Task<WebhookActionResponse> ChangeStatusAsync(Guid id, string status, string? reason,
        string securityReason, Guid actor, SecurityRequestContext request, CancellationToken ct,
        bool save = true)
    {
        var row = await RequiredAsync(id, ct);
        if (row.Status == WebhookSubscriptionStatus.Disabled)
            throw new CommandRefusedException("webhook_disabled", "The webhook subscription is disabled.");
        row.Status = status;
        row.PausedAt = status == WebhookSubscriptionStatus.Paused ? clock.UtcNow : null;
        row.PausedReason = reason;
        row.UpdatedAt = clock.UtcNow;
        row.UpdatedBy = actor;
        Security(securityReason, actor, request);
        if (save) await SaveAsync(ct);
        return new(View(row));
    }

    private async Task<WebhookSubscription> RequiredAsync(Guid id, CancellationToken ct) =>
        await db.Subscriptions.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.SubscriptionId == id, ct)
        ?? throw new CommandRefusedException("webhook_unknown", "The webhook subscription does not exist.");

    private void Security(string reason, Guid actor, SecurityRequestContext request) =>
        security.Add(SecurityEventKind.SettingsChanged, null, tenant.TenantId, actor, request, reason);

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await db.SaveChangesAsync(cancellationToken);
        await platform.SaveChangesAsync(cancellationToken);
    }

    private static void Validate(string name, IReadOnlyList<string> types, WebhookFilters? filters)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 120)
            throw new CommandRefusedException("webhook_name_invalid", "A webhook name is required and is at most 120 characters.");
        if (types.Count == 0 || types.Any(type => !PublicEventTypes.Subscribable.Contains(type, StringComparer.Ordinal)))
            throw new CommandRefusedException("webhook_event_type_unknown", "Every selected webhook event type must be in public catalogue v1.");
        if (types.Count != types.Distinct(StringComparer.Ordinal).Count())
            throw new CommandRefusedException("webhook_event_type_duplicate", "Select each webhook event type once.");
        if ((filters?.ItemFamilies?.Count ?? 0) > 500 || (filters?.CustomerPartyRefs?.Count ?? 0) > 500
            || (filters?.LegalEntityIds?.Count ?? 0) > 500)
            throw new CommandRefusedException("webhook_filter_too_large", "Each webhook filter accepts at most 500 values.");
        if (filters?.ItemFamilies?.Any(item => string.IsNullOrWhiteSpace(item)
                || item.Trim().Length > 64) == true)
            throw new CommandRefusedException("webhook_item_family_invalid",
                "Each item-family filter must be a code of at most 64 characters.");
    }

    private static List<string> NormaliseTypes(IReadOnlyList<string> types) =>
        types.Order(StringComparer.Ordinal).ToList();

    private static void RequireVersion(string? encoded, byte[] current)
    {
        byte[] expected;
        try { expected = string.IsNullOrWhiteSpace(encoded) ? [] : Convert.FromBase64String(encoded); }
        catch (FormatException)
        {
            throw new CommandRefusedException("webhook_version_invalid", "The webhook version is invalid.");
        }
        if (!expected.AsSpan().SequenceEqual(current))
            throw new CommandRefusedException("webhook_version_conflict",
                "The webhook changed after it was loaded. Refresh it and try again.");
    }

    private static WebhookFilters NormaliseFilters(WebhookFilters? value) => new(
        value?.ItemFamilies?.Select(item => item.Trim()).Where(item => item.Length > 0)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList() ?? [],
        value?.CustomerPartyRefs?.Distinct().Order().ToList() ?? [],
        value?.LegalEntityIds?.Distinct().Order().ToList() ?? []);

    private async Task<Dictionary<Guid, WebhookSubscriptionActivity>> ActivitiesAsync(List<Guid> ids,
        CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var since = clock.UtcNow.AddHours(-24);
        var recent = await db.Deliveries.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && ids.Contains(row.SubscriptionId) && row.CreatedAt >= since)
            .GroupBy(row => row.SubscriptionId).Select(group => new
            {
                Id = group.Key,
                Delivered = group.LongCount(row => row.Status == WebhookDeliveryStatus.Succeeded),
                Total = group.LongCount(),
            }).ToListAsync(ct);
        var parked = await db.Deliveries.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && ids.Contains(row.SubscriptionId) && row.Status == WebhookDeliveryStatus.Parked)
            .GroupBy(row => row.SubscriptionId).Select(group => new { Id = group.Key, Count = group.LongCount() })
            .ToDictionaryAsync(row => row.Id, row => row.Count, ct);
        var recentById = recent.ToDictionary(row => row.Id);
        return ids.ToDictionary(id => id, id => recentById.TryGetValue(id, out var row)
            ? new WebhookSubscriptionActivity(row.Delivered, row.Total, parked.GetValueOrDefault(id))
            : new WebhookSubscriptionActivity(0, 0, parked.GetValueOrDefault(id)));
    }

    internal static WebhookSubscriptionView View(WebhookSubscription row,
        WebhookSubscriptionActivity? activity = null) => new(row.SubscriptionId,
        row.Name, row.Url, JsonSerializer.Deserialize<List<string>>(row.EventTypes, PublicEventTranslator.Json) ?? [],
        JsonSerializer.Deserialize<WebhookFilters>(row.Filters, PublicEventTranslator.Json) ?? new(),
        row.Status, row.RelayId, row.RatePerSecond, row.VerifiedAt, row.FailingSince,
        row.PausedAt, row.PausedReason, row.CreatedAt, row.UpdatedAt,
        Convert.ToBase64String(row.RowVersion), row.PreviousSecretExpiresAt, activity ?? new(0, 0, 0));

    private static string EncodeSecret(byte[] secret) => WebhookSigner.EncodeSecret(secret);
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
