using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

public sealed class ImportJob : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid JobId { get; set; }
    public Guid CommandId { get; set; }
    public Guid? ParentJobId { get; set; }
    public required string Source { get; set; }
    public required string FileName { get; set; }
    public required string OriginalHash { get; set; }
    public string? ConvertedHash { get; set; }
    public required string Format { get; set; }
    public required string Mode { get; set; }
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public int? TargetLine { get; set; }
    public required string Status { get; set; }
    public int AppliedCount { get; set; }
    public int SkippedCount { get; set; }
    public int RejectedCount { get; set; }
    public string? OriginalArchiveReference { get; set; }
    public string? ConvertedArchiveReference { get; set; }
    public string? InboxReference { get; set; }
    public string? OriginalFileReference { get; set; }
    public string? ConvertedFileReference { get; set; }
    public long? OriginalLength { get; set; }
    public long? ConvertedLength { get; set; }
    public bool OriginalArchived { get; set; }
    public bool ConvertedArchived { get; set; }
    public byte[]? OriginalContent { get; set; }
    public byte[]? ConvertedContent { get; set; }
    public required string ActorRef { get; set; }
    public DateTime RecordedTime { get; set; }
    public DateTime? CompletedTime { get; set; }
}

public sealed class ImportFileDownloadAudit : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid AuditId { get; set; }
    public Guid JobId { get; set; }
    public required string CopyKind { get; set; }
    public required string ActorRef { get; set; }
    public DateTime DownloadedAt { get; set; }
}

public sealed class ImportRowResult : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RowResultId { get; set; }
    public Guid JobId { get; set; }
    public int RowNumber { get; set; }
    public string? Sheet { get; set; }
    public string? RawValue { get; set; }
    public string? NormalisedValue { get; set; }
    public required string Outcome { get; set; }
    public string? Rule { get; set; }
    public string? OrderNumber { get; set; }
    public string? Lot { get; set; }
    public string? CustomerAssetTag { get; set; }
    public Guid? UnitId { get; set; }
    public Guid? CollisionUnitId { get; set; }
    public Guid? CollisionOwnerPartyRef { get; set; }
    public string? ComparisonPartition { get; set; }
    public string? ProposedAction { get; set; }
    public string? ReviewedAction { get; set; }
    public DateTime RecordedTime { get; set; }
}

public sealed class ColumnMapping : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid MappingId { get; set; }
    public required string Source { get; set; }
    public required string Name { get; set; }
    public required string MappingJson { get; set; }
    public bool IsDefault { get; set; }
    public required string ActorRef { get; set; }
    public DateTime RecordedTime { get; set; }
}

public sealed class ThreePlFeedMessage : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid FeedMessageId { get; set; }
    public Guid CommandId { get; set; }
    public required string MessageReference { get; set; }
    public required string DocumentType { get; set; }
    public Guid DocumentId { get; set; }
    public int DocumentLine { get; set; }
    public Guid ImportJobId { get; set; }
    public required string PayloadHash { get; set; }
    public required string Status { get; set; }
    public int ShippedCount { get; set; }
    public int AssetTagCount { get; set; }
    public int RejectedCount { get; set; }
    public required string ActorRef { get; set; }
    public DateTime RecordedTime { get; set; }
}
