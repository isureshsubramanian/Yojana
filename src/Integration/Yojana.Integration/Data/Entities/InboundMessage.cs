using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class InboundMessage : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid InboundMessageId { get; set; }
    public Guid MessageId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ConnectorCode { get; set; }
    public required string ErpEntity { get; set; }
    public required string ErpExternalId { get; set; }
    public required string ErpChangeMarker { get; set; }
    public required string MarkerKind { get; set; }
    public required string ContractVersion { get; set; }
    public required string MessageType { get; set; }
    public string CaptureMode { get; set; } = "observed";
    public required string RawPayload { get; set; }
    public required string CanonicalMessage { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ObservedAt { get; set; }
    public DateTime AcceptedAt { get; set; }
    public bool IsLatest { get; set; }
    public Guid? SupersedesInboundMessageId { get; set; }
    public required string TranslationStatus { get; set; }
    public int TranslationAttemptCount { get; set; }
    public DateTime? TranslationStartedAt { get; set; }
    public DateTime? NextTranslationAttemptAt { get; set; }
    public string? LastTranslationError { get; set; }
    public string? AttentionReasonCode { get; set; }
    public DateTime? TranslatedAt { get; set; }
}

public static class InboundTranslationStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Held = "held";
    public const string Failed = "failed";
    public const string Superseded = "superseded";
    public const string Completed = "completed";
    public const string Late = "late";
    public const string NeedsAttention = "needs_attention";
}

public sealed class IdempotencyClaim : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ErpEntity { get; set; }
    public required string ErpExternalId { get; set; }
    public required string ErpChangeMarker { get; set; }
    public Guid InboundMessageId { get; set; }
    public DateTime ClaimedAt { get; set; }
}
