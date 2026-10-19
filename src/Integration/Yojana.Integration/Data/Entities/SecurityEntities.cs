using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class TenantDataKeyEntity : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public int Version { get; set; }
    public required string KeyEncryptionKeyId { get; set; }
    public required string KeyEncryptionKeyVersion { get; set; }
    public required string KeyEncryptionAlgorithm { get; set; }
    public required byte[] WrappedKeyNonce { get; set; }
    public required byte[] WrappedKeyCiphertext { get; set; }
    public required byte[] WrappedKeyTag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RotatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool IsActive { get; set; }
}

public sealed class ConnectionCredentialEntity : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string Purpose { get; set; }
    public int DataKeyVersion { get; set; }
    public required byte[] Nonce { get; set; }
    public required byte[] Ciphertext { get; set; }
    public required byte[] Tag { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RotatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public DateTime RotationDueAt { get; set; }
}

public sealed class TenantDataKeyRewrapCheckpointEntity : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string OperationId { get; set; }
    public int DataKeyVersion { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ConnectionEventEntity : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public long SequenceNo { get; set; }
    public Guid EventId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string EventType { get; set; }
    public required string ActorRef { get; set; }
    public DateTime OccurredAt { get; set; }
    public required string DetailsJson { get; set; }
    public required string PreviousRecordHash { get; set; }
    public required string RecordHash { get; set; }
}

public sealed class ConnectionThrottleProfileEntity : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ConnectorCode { get; set; }
    public int? RequestsPerSecond { get; set; }
    public int? RequestsPerMinute { get; set; }
    public int? RequestsPerDay { get; set; }
    public int Burst { get; set; }
    public int MaxConcurrentCalls { get; set; }
    public int RetryAfterCeilingSeconds { get; set; }
    public int AlertThresholdPercent { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateOnly? BudgetDateUtc { get; set; }
    public int UsedCalls { get; set; }
    public bool ApproachAlertRaised { get; set; }
    public bool CapAlertRaised { get; set; }
    public long BudgetRevision { get; set; }
}
