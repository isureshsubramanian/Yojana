using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class ErpChangeRun : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ErpChangeRunId { get; set; }
    public Guid SourceConnectionId { get; set; }
    public Guid TargetConnectionId { get; set; }
    public int CurrentStep { get; set; }
    public required string Status { get; set; }
    public required string StartedBy { get; set; }
    public DateTime StartedAt { get; set; }
    public string? ClosedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public string? CloseReason { get; set; }
    public DateTime? MasterDataRemappedAt { get; set; }
    public int UnitsCompared { get; set; }
    public DateTime? StockComparedAt { get; set; }
}

public sealed class ErpChangeStepCheck : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ErpChangeStepCheckId { get; set; }
    public Guid ErpChangeRunId { get; set; }
    public int Step { get; set; }
    public bool Passed { get; set; }
    public required string CountsJson { get; set; }
    public required string Detail { get; set; }
    public required string CheckedBy { get; set; }
    public DateTime CheckedAt { get; set; }
}

public sealed class ErpChangeTransition : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ErpChangeTransitionId { get; set; }
    public Guid ErpChangeRunId { get; set; }
    public int FromStep { get; set; }
    public int ToStep { get; set; }
    public required string Kind { get; set; }
    public required string ActorRef { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? Reason { get; set; }
}

public sealed class ErpChangeMasterDataException : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ErpChangeMasterDataExceptionId { get; set; }
    public Guid ErpChangeRunId { get; set; }
    public required string EntityKind { get; set; }
    public required string TargetExternalId { get; set; }
    public required string DisplayName { get; set; }
    public string? TaxId { get; set; }
    public string? Country { get; set; }
    public required string Problem { get; set; }
    public required string CandidateModuleIdsJson { get; set; }
    public required string CandidateLabelsJson { get; set; }
    public int LiveUnitsAffected { get; set; }
    public required string Status { get; set; }
    public Guid? ResolvedModuleId { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public string? ResolutionNote { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ErpChangeStockDifference : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ErpChangeStockDifferenceId { get; set; }
    public Guid ErpChangeRunId { get; set; }
    public Guid? UnitId { get; set; }
    public Guid? ItemId { get; set; }
    public required string Identifier { get; set; }
    public string? ItemName { get; set; }
    public required string ReasonClass { get; set; }
    public string? ModuleLocation { get; set; }
    public string? SourceLocation { get; set; }
    public string? TargetLocation { get; set; }
    public string? ModuleStatus { get; set; }
    public string? SourceStatus { get; set; }
    public string? TargetStatus { get; set; }
    public bool HasLiveEntitlement { get; set; }
    public DateOnly? WarrantyEnd { get; set; }
    public required string SuggestedResolution { get; set; }
    public required string Status { get; set; }
    public string? ResolutionCommand { get; set; }
    public Guid? ResolutionCommandId { get; set; }
    public string? ResolvedBy { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public DateTime ComparedAt { get; set; }
    public bool IsCurrent { get; set; } = true;
}

public sealed class ErpChangeStockSignoff : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ErpChangeStockSignoffId { get; set; }
    public Guid ErpChangeRunId { get; set; }
    public int ResidualCount { get; set; }
    public required string Note { get; set; }
    public required string SignedOffBy { get; set; }
    public DateTime SignedOffAt { get; set; }
}

public static class ErpChangeRunStatus
{
    public const string Active = "active";
    public const string Cancelled = "cancelled";
    public const string Completed = "completed";
}

public static class ErpChangeExceptionStatus
{
    public const string Open = "open";
    public const string Mapped = "mapped";
    public const string NotNeeded = "not_needed";
}

public static class ErpChangeDifferenceStatus
{
    public const string Open = "open";
    public const string Resolved = "resolved";
}
