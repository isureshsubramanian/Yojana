using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class ReconciliationRun : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReconciliationRunId { get; set; }
    public Guid ConnectionId { get; set; }
    public DateOnly RangeStart { get; set; }
    public DateOnly RangeEnd { get; set; }
    public required string Trigger { get; set; }
    public required string Status { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int MismatchCount { get; set; }
    public int LocationMismatchCount { get; set; }
    public string? Failure { get; set; }
    // The signed-in user who asked for an on-demand run; null for scheduled runs and runs recorded before P8.
    public string? RequestedBy { get; set; }
}

public sealed class ReconciliationEntityDayResult : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReconciliationEntityDayResultId { get; set; }
    public Guid ReconciliationRunId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string Entity { get; set; }
    public DateOnly BusinessDate { get; set; }
    public long SourceCount { get; set; }
    public long ModuleCount { get; set; }
    public decimal SourceQuantity { get; set; }
    public decimal ModuleQuantity { get; set; }
    public long SourceDistinctSerials { get; set; }
    public long ModuleDistinctSerials { get; set; }
    public required string SourceKeyFieldHash { get; set; }
    public required string ModuleKeyFieldHash { get; set; }
    public required string Status { get; set; }
    public bool RangedReadSupported { get; set; }
    public Guid? BackfillJobId { get; set; }
    public Guid? DeadLetterItemId { get; set; }
}

public sealed class ReconciliationLocationResult : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReconciliationLocationResultId { get; set; }
    public Guid ReconciliationRunId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid ItemId { get; set; }
    public Guid LocationId { get; set; }
    public required string ItemCode { get; set; }
    public string? ItemName { get; set; }
    public required string LocationCode { get; set; }
    public string? LocationName { get; set; }
    public decimal SourceOnHand { get; set; }
    public long ModuleUnitCount { get; set; }
    public required string Status { get; set; }
}

public static class ReconciliationRunStatus
{
    public const string Running = "running";
    public const string Clean = "clean";
    public const string Mismatch = "mismatch";
    public const string Failed = "failed";
}

public static class ReconciliationResultStatus
{
    public const string Clean = "clean";
    public const string Mismatch = "mismatch";
}

public static class ReconciliationTrigger
{
    public const string Scheduled = "scheduled";
    public const string OnDemand = "on_demand";
}
