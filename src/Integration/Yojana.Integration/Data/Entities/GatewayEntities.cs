using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class ReferenceProjection : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string EntityType { get; set; }
    public Guid ModuleId { get; set; }
    public string? Code { get; set; }
    public string? DisplayName { get; set; }
    public string? TrackingMode { get; set; }
    public string? Uom { get; set; }
    public bool Deleted { get; set; }
    public required string AttributesJson { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ConnectorCatalogueEntry : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string ConnectorCode { get; set; }
    public required string DisplayName { get; set; }
    public required string CustomerCostDisclosure { get; set; }
    public bool Enabled { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class CanonicalDocument : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid DocumentId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ErpEntity { get; set; }
    public required string ErpExternalId { get; set; }
    public required string MessageType { get; set; }
    public required string Payload { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid SourceInboundMessageId { get; set; }
}

public sealed class ConnectionCapability : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string CanonicalMessage { get; set; }
    public required string SupportLevel { get; set; }
}

public static class CapabilitySupport
{
    public const string Native = "native";
    public const string TenantCode = "tenant_code";
    public const string QuantityOnly = "quantity_only";
    public const string Manual = "manual";
    public const string Unsupported = "unsupported";
}

public sealed class ConnectionDegradationSetting : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public int RuleNumber { get; set; }
    public bool Enabled { get; set; }
    public required string SettingsJson { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ConnectionGatewaySetting : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid LegalEntityId { get; set; }
    public Guid? LedgerConnectionId { get; set; }
    public required string OutboundMode { get; set; }
    public required string NativeWarrantyObjectMode { get; set; }
    public bool ReadOnly { get; set; }
    public int OrderingWaitMinutes { get; set; } = 15;
    public string OnHandStatusMappingJson { get; set; } = "{}";
    public string? SyncStartMode { get; set; }
    public DateOnly? SyncStartDate { get; set; }
    public DateTime? SyncStartConfiguredAt { get; set; }
    public bool MasterDataFull { get; set; } = true;
    public string? ServiceCertificateSha256 { get; set; }
}

public sealed class OutboundCommandRow : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid OutboundCommandId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string CommandType { get; set; }
    public Guid IdempotencyKey { get; set; }
    public required string ModuleDocumentRef { get; set; }
    public required string ErpReferenceText { get; set; }
    public required string Payload { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErpExternalId { get; set; }
    public string? ErpChangeMarker { get; set; }
    public string? Outcome { get; set; }
    public string? LastFailure { get; set; }
    // Remote key of the record held for review (for example an ERP draft) while Status is awaiting_review.
    public string? PendingRemoteKey { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public static class OutboundCommandStatus
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Retry = "retry";
    public const string Done = "done";
    public const string Parked = "parked";
    // Never claimed for create again: the remote already holds the record. Only the review sweep moves it on.
    public const string AwaitingReview = "awaiting_review";
}

public sealed class IntegrationExceptionTask : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ExceptionTaskId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? InboundMessageId { get; set; }
    public required string Kind { get; set; }
    public required string SubjectRef { get; set; }
    public required string Detail { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
}
