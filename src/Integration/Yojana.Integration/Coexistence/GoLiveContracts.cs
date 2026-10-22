namespace Yojana.Integration.Coexistence;

/// <summary>
/// What the migration side knows about a connection's cutover, read by the go-live record (P9-T11). The migration
/// pipeline implements it; coexistence never reads migration tables itself.
/// </summary>
public interface IMigrationGoLiveFacts
{
    Task<MigrationGoLiveFacts> ReadAsync(Guid connectionId, CancellationToken cancellationToken);
}

/// <summary>Validation signed off for a final load: when, and how many sign-off lines with how many clean.</summary>
public sealed record FinalSignoffFact(DateTime SignedAt, int Checks, int CleanOrExplained);

/// <summary>A load (or its undo) with its label (R1, F1, D1) and how long it took.</summary>
public sealed record TimedLoadFact(string Label, string Kind, DateTime At, TimeSpan Duration);

public sealed record MigrationGoLiveFacts(
    FinalSignoffFact? FinalSignoff,
    TimedLoadFact? CutoverLoad,
    TimedLoadFact? RollbackRehearsal,
    DateTime? CutoverAt)
{
    public static readonly MigrationGoLiveFacts None = new(null, null, null, null);
}

public static class HandoverKinds
{
    public const string InstalledBaseAndCover = "installed_base_and_cover";
    public const string Returns = "returns";
    public const string Certificates = "certificates";
    public const string RetireOwnedObjects = "retire_owned_objects";

    /// <summary>OD-64 (a): the ADR-0001 phasing, one kind at a time; the order is the list order.</summary>
    public static readonly (string Kind, string Label, string Description)[] Steps =
    [
        (InstalledBaseAndCover, "Installed base and cover",
            "The module owns owner, condition and cover; contracts are no longer created in the incumbent."),
        (Returns, "Returns (RMA)",
            "Return authorisations are made in the module; the incumbent is read-only for them."),
        (Certificates, "Certificates",
            "Certificates are issued by the module; version 1 of every migrated certificate is marked migrated."),
        (RetireOwnedObjects, "Retire the owned objects in the incumbent",
            "After the month-end close passes in both systems, the owned objects are retired in the incumbent."),
    ];
}

public static class GoLiveItemCodes
{
    public const string ValidationSignedOff = "validation_signed_off";
    public const string DeltaLoad = "delta_load";
    public const string RollbackRehearsed = "rollback_rehearsed";
    public const string UsersTrained = "users_trained";
    public const string ReturnsHandover = "returns_handover";
    // The same confirmation row as the parallel-run checklist item: one fact, one place.
    public const string MonthEndClose = "month_end_close";
    public const string CustomerSignoff = "customer_signoff";

    public static readonly string[] Confirmed = [UsersTrained, MonthEndClose, CustomerSignoff];
}

public sealed record RefusalReason(string Code, string Message);

public sealed record SyncLagView(int? NowSeconds, int TargetSeconds, decimal? ShareWithinTargetToday, int MessagesToday);

public sealed record ConflictSummaryView(int Open, DateTime? OldestDetectedAt, int AutoResolved);

public sealed record FailedMessageSummaryView(int Open, int OlderThanSevenDays);

public sealed record ReconciliationSummaryView(int Runs, int Clean, int Explained, int Mismatch, int Failed,
    DateOnly? LastExplainedOn);

public sealed record HandoverStepView(int Order, string Kind, string Label, string Description, string State,
    DateOnly? PlannedOn, DateOnly? OwnedSince, string? Evidence, string? ConfirmedByName, DateTime? ConfirmedAt,
    IReadOnlyList<string> Facts, bool CanConfirm, IReadOnlyList<RefusalReason> ConfirmRefusals);

public sealed record GoLiveItemView(string Code, string Label, string Source, string Status, string Detail,
    string? Evidence = null, string? ConfirmedByName = null, DateTime? ConfirmedAt = null);

public sealed record GoLiveView(int Done, int Total, DateTime? IssuedAt, Guid? RecordId, bool CanIssue,
    IReadOnlyList<RefusalReason> IssueRefusals, IReadOnlyList<GoLiveItemView> Items);

public sealed record ParallelRunDashboard(Guid ConnectionId, string ConnectionLabel, string Status, int? Days,
    DateOnly? StartDate, DateOnly? EndDate, int? DayNumber, int? DaysLeft, DateOnly? MonthEndCloseOn,
    DateTime? CutoverAt, SyncLagView SyncLag, ConflictSummaryView Conflicts, FailedMessageSummaryView FailedMessages,
    ReconciliationSummaryView Reconciliation, IReadOnlyList<HandoverStepView> Handover, GoLiveView GoLive);

public sealed record SetMonthEndClose(DateOnly CloseOn);

public sealed record PlanHandover(DateOnly PlannedOn);

public sealed record ConfirmHandover(string Evidence);

public sealed record ConfirmGoLiveItem(string Evidence);

public sealed record GoLiveRecordView(Guid RecordId, Guid ConnectionId, string ConnectionLabel, DateTime IssuedAt,
    string IssuedByName, IReadOnlyList<GoLiveItemView> Items, IReadOnlyList<HandoverStepView> Handover);

public sealed record ExplainReconciliationRun(string Note);
