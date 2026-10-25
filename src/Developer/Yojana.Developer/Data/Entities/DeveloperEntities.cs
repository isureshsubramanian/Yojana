using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Developer.Data.Entities;

// ADR-0018. Event payloads are thin (ids and changed fields, no names), so no entity here holds personal data; the
// created/updated-by columns hold user ids only, resolved through the name ports at display time.

/// <summary>A customer application's credentials (D5). Only hashes of the secret are kept.</summary>
public sealed class ApiClient : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ClientId { get; set; }
    public required string Name { get; set; }
    public required byte[] SecretHash { get; set; }
    /// <summary>First characters of the secret, shown so an administrator can tell two secrets apart.</summary>
    public required string SecretHint { get; set; }
    /// <summary>The replaced secret stays valid until <see cref="PreviousSecretExpiresAt"/> during a rotation.</summary>
    public byte[]? PreviousSecretHash { get; set; }
    public DateTime? PreviousSecretExpiresAt { get; set; }
    /// <summary>Space-separated scopes from <c>PublicApiScopes</c>.</summary>
    public required string Scopes { get; set; }
    public required string DisclosureTier { get; set; }
    /// <summary>When set, the client sees only this customer's records.</summary>
    public Guid? CustomerPartyRef { get; set; }
    public bool Sandbox { get; set; }
    public required string Status { get; set; }
    public int RateLimitPerMinute { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? SecretRotatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid? RevokedBy { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>An issued opaque bearer token, stored as its SHA-256 hash.</summary>
public sealed class ApiAccessToken : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required byte[] TokenHash { get; set; }
    public Guid ClientId { get; set; }
    public DateTime IssuedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>A stored response for a write command's Idempotency-Key, replayed for the retention period.</summary>
public sealed class ApiIdempotencyRecord : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ClientId { get; set; }
    public required string IdempotencyKey { get; set; }
    public required byte[] RequestHash { get; set; }
    public int StatusCode { get; set; }
    public required string ResponseBody { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}

/// <summary>An HTTPS endpoint a tenant administrator registered (D3, D4).</summary>
public sealed class WebhookSubscription : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid SubscriptionId { get; set; }
    public required string Name { get; set; }
    public required string Url { get; set; }
    /// <summary>Set when the endpoint is on a private network and is reached only through a registered site relay.</summary>
    public Guid? RelayId { get; set; }
    /// <summary>JSON array of public event types.</summary>
    public required string EventTypes { get; set; }
    /// <summary>JSON object of optional filters: item families, customers, legal entities.</summary>
    public required string Filters { get; set; }
    public required string Status { get; set; }
    /// <summary>Signing secret, sealed with the platform secret protector.</summary>
    public required byte[] SecretSealed { get; set; }
    public byte[]? PreviousSecretSealed { get; set; }
    public DateTime? PreviousSecretExpiresAt { get; set; }
    public int RatePerSecond { get; set; }
    public string? VerificationChallengeHash { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime? FailingSince { get; set; }
    public DateTime? PausedAt { get; set; }
    public string? PausedReason { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A translated public event, stored once and shared by every delivery, the replay and the event lookup.</summary>
public sealed class PublicEvent : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EventId { get; set; }
    public required string Type { get; set; }
    public required string Version { get; set; }
    public required string RecordKind { get; set; }
    public Guid RecordId { get; set; }
    public long Sequence { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime RecordedAt { get; set; }
    /// <summary>The envelope exactly as delivered, so a replay sends the same bytes.</summary>
    public required string Body { get; set; }
    public bool Test { get; set; }
    /// <summary>Filter keys copied from the source event, so subscriptions match without reading other modules.</summary>
    public string? ItemFamily { get; set; }
    public Guid? CustomerPartyRef { get; set; }
    public Guid? LegalEntityId { get; set; }
}

/// <summary>One event for one subscription. A replay adds a new row pointing at the delivery it repeats.</summary>
public sealed class WebhookDelivery : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid DeliveryId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid EventId { get; set; }
    public Guid? ReplayOf { get; set; }
    public required string Status { get; set; }
    public int AttemptCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime NextAttemptAt { get; set; }
    public DateTime? RetryWindowStartedAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? SucceededAt { get; set; }
    public DateTime? ParkedAt { get; set; }
    public DateTime? DiscardedAt { get; set; }
    public Guid? DiscardedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>One HTTP attempt of a delivery: what came back and how long it took.</summary>
public sealed class WebhookDeliveryAttempt : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid AttemptId { get; set; }
    public Guid DeliveryId { get; set; }
    public DateTime AttemptedAt { get; set; }
    public required string Outcome { get; set; }
    public int? StatusCode { get; set; }
    public int DurationMs { get; set; }
    /// <summary>The first 2 KB of the endpoint's reply.</summary>
    public string? ResponseExcerpt { get; set; }
    public string? ErrorCode { get; set; }
}

/// <summary>A bulk operation above the threshold, announced by one <c>bulk.completed</c> event (OD-47).</summary>
public sealed class BulkOperation : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid OperationId { get; set; }
    public required string Kind { get; set; }
    public int UnitCount { get; set; }
    public DateTime CompletedAt { get; set; }
    public Guid EventId { get; set; }
}

/// <summary>The units a bulk operation touched, paged by the public API.</summary>
public sealed class BulkOperationUnit : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid OperationId { get; set; }
    public Guid UnitId { get; set; }
}
