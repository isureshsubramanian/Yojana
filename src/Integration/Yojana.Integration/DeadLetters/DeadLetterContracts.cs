namespace Yojana.Integration.DeadLetters;

public sealed record DeadLetterCursor(DateTime CreatedAt, Guid DeadLetterItemId);

public sealed record DeadLetterListRequest(
    Guid? ConnectionId,
    string? Reason,
    string? RecordType,
    double? MinimumAgeHours,
    double? MaximumAgeHours,
    string? Status,
    string? Query,
    string? Sort,
    string? Direction,
    string? Cursor,
    int? Take = null)
{
    public DeadLetterListRequest(Guid? connectionId, string? reason, string? recordType,
        double? minimumAgeHours, double? maximumAgeHours, string? status, string? query,
        string? cursor, int? take = null)
        : this(connectionId, reason, recordType, minimumAgeHours, maximumAgeHours, status, query,
            null, null, cursor, take) { }
}

public sealed record DeadLetterListRow(
    Guid DeadLetterItemId,
    Guid ConnectionId,
    string ConnectionLabel,
    string Direction,
    string RecordType,
    string? RecordReference,
    string ReasonCode,
    string Failure,
    string Status,
    int AttemptCount,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record DeadLetterSummary(
    int Open,
    int Retried,
    int Discarded,
    int UnknownItem,
    int SerialNotFound,
    int PartyNotLinked,
    int CouldNotSend,
    int ResolvedLast30Days,
    int RetriedLast30Days,
    int DiscardedLast30Days);

public sealed record IntegrationAttention(int NeedsAttention);

public sealed record DeadLetterPage(
    IReadOnlyList<DeadLetterListRow> Items,
    string? NextCursor,
    int Total,
    DeadLetterSummary Summary);

public sealed record DeadLetterEventView(
    Guid DeadLetterEventId,
    string Action,
    string ActorRef,
    string? Reason,
    string DetailsJson,
    DateTime OccurredAt);

public sealed record ManualOutboundTaskView(
    Guid ManualOutboundTaskId,
    Guid? DeadLetterItemId,
    Guid? OutboundCommandId,
    Guid ConnectionId,
    string ConnectionLabel,
    string CommandType,
    string Desk,
    string Draft,
    string Status,
    DateTime CreatedAt,
    string? ErpExternalId,
    string? ErpChangeMarker,
    string? CompletedBy,
    DateTime? CompletedAt,
    string? Note = null);

public sealed record ManualTaskDeskCount(string Desk, string Label, int Count);
public sealed record ManualOutboundTaskPage(
    IReadOnlyList<ManualOutboundTaskView> Items,
    string? NextCursor,
    int Total,
    IReadOnlyList<ManualTaskDeskCount> Desks);

public sealed record DeadLetterDetail(
    Guid DeadLetterItemId,
    Guid ConnectionId,
    string ConnectionLabel,
    string Direction,
    string RecordType,
    string SourceKind,
    Guid SourceId,
    string ReasonCode,
    string RawPayload,
    string CanonicalAttempt,
    string Failure,
    string Status,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? ResolvedAt,
    IReadOnlyList<DeadLetterEventView> Events,
    ManualOutboundTaskView? ManualTask);

public sealed record MapAndRetryDeadLetterRequest(string EntityType, string ExternalId, Guid ModuleId);
public sealed record CreateProvisionalAndRetryDeadLetterRequest(string EntityType, string ExternalId);
public sealed record DiscardDeadLetterRequest(string Reason);
public sealed record CompleteManualTaskRequest(
    string? ErpExternalId = null,
    string? ErpChangeMarker = null,
    bool AllowUnverified = false,
    System.Text.Json.JsonElement? CorrectedDraft = null);
public sealed record DeadLetterActionResult(Guid DeadLetterItemId, string Status, Guid? ModuleId = null);

public interface IInboundRetryPreparer
{
    bool Applies(Yojana.Integration.Data.Entities.InboundMessage message);
    Task PrepareAsync(Yojana.Integration.Data.Entities.InboundMessage message,
        CancellationToken cancellationToken);
}

public sealed class DeadLetterRuleException(string rule, string message) : Exception(message)
{
    public string Rule { get; } = rule;
}
