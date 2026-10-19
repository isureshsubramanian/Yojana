using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class BackfillCursor : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid BackfillCursorId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string Entity { get; set; }
    public DateOnly RangeStart { get; set; }
    public DateOnly RangeEnd { get; set; }
    public string? Cursor { get; set; }
    public int PageSize { get; set; }
    public required string Status { get; set; }
    public required string RequestedBy { get; set; }
    public required string LastChangedBy { get; set; }
    public Guid? RequestedByReconciliationResultId { get; set; }
    public long ProcessedMessages { get; set; }
    public int ProcessedPages { get; set; }
    public long? TotalMessages { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? LastPageAt { get; set; }
    public DateTime? AwaitingReconciliationAt { get; set; }
    public Guid? CompletionReconciliationRunId { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? LastFailure { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public static class BackfillStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string AwaitingReconciliation = "awaiting_reconciliation";
    public const string Completed = "completed";
    public const string Cancelled = "cancelled";
}
