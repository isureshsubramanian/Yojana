namespace Yojana.Integration.Reconciliation;

public sealed record ReconciliationRunListRequest(
    Guid? ConnectionId,
    string? Status,
    string? Cursor,
    int Take = 50);

public sealed record ReconciliationRunRow(
    Guid RunId,
    Guid ConnectionId,
    string ConnectionName,
    DateOnly RangeStart,
    DateOnly RangeEnd,
    string Trigger,
    string Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    int MismatchCount,
    int LocationMismatchCount);

public sealed record ReconciliationRunPage(
    IReadOnlyList<ReconciliationRunRow> Items,
    string? NextCursor);

public sealed record ReconciliationEntityDayView(
    Guid ResultId,
    string Entity,
    DateOnly BusinessDate,
    long SourceCount,
    long ModuleCount,
    decimal SourceQuantity,
    decimal ModuleQuantity,
    long SourceDistinctSerials,
    long ModuleDistinctSerials,
    string SourceKeyFieldHash,
    string ModuleKeyFieldHash,
    string Status,
    bool RangedReadSupported,
    Guid? BackfillJobId,
    Guid? AttentionItemId);

public sealed record ReconciliationLocationView(
    Guid ResultId,
    string ItemCode,
    string? ItemName,
    string LocationCode,
    string? LocationName,
    decimal SourceOnHand,
    long ModuleUnitCount,
    string Status);

public sealed record ReconciliationRunDetail(
    ReconciliationRunRow Run,
    string? Failure,
    IReadOnlyList<ReconciliationEntityDayView> EntityDays,
    IReadOnlyList<ReconciliationLocationView> LocationChecks);

public sealed record RunLocationReconciliationRequest(Guid ConnectionId);

public interface IReconciliationBackfillScheduler
{
    Task<Guid> ScheduleAsync(Guid connectionId, string entity, DateOnly rangeStart,
        DateOnly rangeEnd, Guid reconciliationResultId, CancellationToken cancellationToken);

    Task ConfirmCleanAsync(Guid connectionId, Guid reconciliationRunId,
        DateOnly rangeStart, DateOnly rangeEnd, IReadOnlySet<string> cleanEntities,
        CancellationToken cancellationToken);
}

public sealed class NullReconciliationBackfillScheduler : IReconciliationBackfillScheduler
{
    public Task<Guid> ScheduleAsync(Guid connectionId, string entity, DateOnly rangeStart,
        DateOnly rangeEnd, Guid reconciliationResultId, CancellationToken cancellationToken) =>
        Task.FromResult(Guid.Empty);

    public Task ConfirmCleanAsync(Guid connectionId, Guid reconciliationRunId,
        DateOnly rangeStart, DateOnly rangeEnd, IReadOnlySet<string> cleanEntities,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ReconciliationRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}
