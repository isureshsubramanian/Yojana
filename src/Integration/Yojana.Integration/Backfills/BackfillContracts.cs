namespace Yojana.Integration.Backfills;

public sealed record StartBackfillRequest(
    Guid ConnectionId,
    string Entity,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    int PageSize = 100);

public sealed record BackfillListRequest(
    Guid? ConnectionId,
    string? Entity,
    string? Status,
    string? Cursor,
    int Take = 50);

public sealed record BackfillView(
    Guid BackfillId,
    Guid ConnectionId,
    string ConnectionName,
    string Entity,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    string Status,
    long ProcessedMessages,
    int ProcessedPages,
    long? TotalMessages,
    decimal? ProgressPercent,
    TimeSpan? EstimatedRemaining,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? LastPageAt,
    DateTime? AwaitingReconciliationAt,
    DateTime? CompletedAt,
    Guid? CompletionReconciliationRunId,
    string? LastFailure,
    string? SavedCursor = null, int PageSize = 100);

public sealed record BackfillPage(IReadOnlyList<BackfillView> Items, string? NextCursor);

public sealed class BackfillRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}
