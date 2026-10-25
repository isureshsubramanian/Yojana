namespace Yojana.Developer.Contracts;

public sealed record WebhookFilters(
    IReadOnlyList<string>? ItemFamilies = null,
    IReadOnlyList<Guid>? CustomerPartyRefs = null,
    IReadOnlyList<Guid>? LegalEntityIds = null);

public sealed record CreateWebhookSubscription(
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes,
    WebhookFilters? Filters = null,
    Guid? RelayId = null);

public sealed record UpdateWebhookSubscription(
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes,
    WebhookFilters? Filters = null,
    Guid? RelayId = null,
    string? ExpectedVersion = null);

public sealed record WebhookSubscriptionView(
    Guid Id,
    string Name,
    string Url,
    IReadOnlyList<string> EventTypes,
    WebhookFilters Filters,
    string Status,
    Guid? RelayId,
    int RatePerSecond,
    DateTime? VerifiedAt,
    DateTime? FailingSince,
    DateTime? PausedAt,
    string? PausedReason,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string Version);

public sealed record WebhookSubscriptionPage(IReadOnlyList<WebhookSubscriptionView> Items, string? NextCursor);

/// <summary>The signing secret is returned only by create and rotate.</summary>
public sealed record WebhookSecretResponse(WebhookSubscriptionView Subscription, string Secret);

public sealed record WebhookActionResponse(WebhookSubscriptionView Subscription);

public sealed record SendWebhookTestRequest(string? EventType = null);
public sealed record SendWebhookTestResponse(Guid EventId, Guid DeliveryId);

public sealed record WebhookDeliveryRow(
    Guid Id,
    Guid EventId,
    string EventType,
    string Status,
    int AttemptCount,
    DateTime CreatedAt,
    DateTime NextAttemptAt,
    DateTime? LastAttemptAt,
    DateTime? SucceededAt,
    DateTime? ParkedAt,
    DateTime? DiscardedAt,
    Guid? ReplayOf);

public sealed record WebhookDeliveryPage(IReadOnlyList<WebhookDeliveryRow> Items, string? NextCursor);

public sealed record WebhookDeliveryAttemptView(
    Guid Id,
    DateTime AttemptedAt,
    string Outcome,
    int? StatusCode,
    int DurationMs,
    string? ResponseExcerpt,
    string? ErrorCode);

public sealed record WebhookDeliveryDetail(
    WebhookDeliveryRow Delivery,
    string EventBody,
    IReadOnlyList<WebhookDeliveryAttemptView> Attempts);

public sealed record RedeliverRangeRequest(DateTime From, DateTime To);
public sealed record RedeliverRangeResponse(int Created);
public sealed record RedeliverWebhookResponse(Guid DeliveryId, Guid ReplayOf);
public sealed record DiscardWebhookDeliveryResponse(Guid DeliveryId, string Status);

public sealed record BulkOperationUnitRow(Guid UnitId, string? Reference);
public sealed record BulkOperationUnitPage(IReadOnlyList<BulkOperationUnitRow> Items, string? NextCursor);
