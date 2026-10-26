using Yojana.Integration.Connections;

namespace Yojana.Integration.ErpChange;

public sealed record StartErpChangeRequest(Guid SourceConnectionId, RegisterConnection NewConnection);
public sealed record CancelErpChangeRequest(string Reason);
public sealed record StartReverseErpChangeRequest(RegisterConnection? NewConnection = null);
public sealed record UpdateErpChangeCleanPeriodRequest(int Days, string ExpectedVersion);
public sealed record ErpChangeCleanPeriodView(int Days, string SettingsVersion);
public sealed record ErpChangeNotificationSettingsView(bool DigestEnabled, int DigestHour,
    IReadOnlyList<string> MasterDataRecipientRoles, IReadOnlyList<string> StockRecipientRoles,
    string SettingsVersion);
public sealed record UpdateErpChangeNotificationSettings(bool DigestEnabled, int DigestHour,
    IReadOnlyList<string> MasterDataRecipientRoles, IReadOnlyList<string> StockRecipientRoles,
    string ExpectedVersion);
public sealed record ErpChangeMyWorkView(Guid RunId, int CurrentStep, string SourceName, string TargetName,
    int MasterDataExceptions, int CoveredStockDifferences, int OtherStockDifferencesAwaitingSignOff,
    int StockTypeDivergences, string ScreenPath)
{
    public int Total => MasterDataExceptions + CoveredStockDifferences
        + OtherStockDifferencesAwaitingSignOff + StockTypeDivergences;
}
public sealed record StartDualRunResult(Guid RunId, DateTime StartedAt, int MinimumDays,
    int CleanDaysRequired, bool SourceReadOnly, string SourceOutboundMode,
    bool TargetReadOnly, string TargetOutboundMode);

public sealed record ErpChangeConnectionView(Guid ConnectionId, string Name, string ConnectorCode,
    string Status, string Health, bool ReadOnly, string OutboundMode);
public sealed record ErpChangeStepCheckView(int Step, bool Passed, IReadOnlyDictionary<string, int> Counts,
    string Detail, DateTime CheckedAt, string CheckedBy);
public sealed record ErpChangeTransitionView(int FromStep, int ToStep, string Kind, DateTime OccurredAt,
    string ActorRef, string? Reason);
public sealed record ErpChangeRunbookView(Guid RunId, string Status, int CurrentStep,
    ErpChangeConnectionView Source, ErpChangeConnectionView Target, string StartedBy, DateTime StartedAt,
    string? ClosedBy, DateTime? ClosedAt, string? CloseReason,
    IReadOnlyList<ErpChangeStepCheckView> Checks,
    IReadOnlyList<ErpChangeTransitionView> Transitions,
    int UnitsCompared, int OpenDifferences, int CoveredOpenDifferences, int ResolvedDifferences,
    int OpenMasterDataExceptions, DualRunSummaryView? DualRun = null,
    IReadOnlyList<CutoverChecklistItemView>? CutoverChecklist = null,
    ArchiveStateView? Archive = null, int UnitHistoriesChanged = 0,
    int DeliberateUnitHistoriesChanged = 0,
    DateTime? UnitHistoryCheckedAt = null);

public sealed record ErpChangeRunListRequest(string? Cursor, int Take = 25, string? Sort = null,
    string? Direction = null);
public sealed record ErpChangeRunHistoryRow(Guid RunId, string SourceName, string TargetName,
    DateTime StartedAt, DateTime? FinishedAt, string Outcome, string StartedBy,
    int CurrentStep, string CurrentStepName);
public sealed record ErpChangeRunHistoryPage(IReadOnlyList<ErpChangeRunHistoryRow> Items,
    string? NextCursor);
public sealed record UnitHistoryChangeRow(Guid UnitId, string Identifier, string Kind, int EventsAdded,
    DateTime LastRecordedAt);
public sealed record UnitHistoryChangePage(IReadOnlyList<UnitHistoryChangeRow> Items,
    string? NextCursor, string Kind, int Total, int UnexpectedTotal, int DeliberateTotal,
    DateTime? CheckedAt);

public sealed record DualRunSummaryView(DateTime? StartedAt, int MinimumDays, int ElapsedDays,
    int CleanDaysRequired, int ConsecutiveCleanDays, int OpenDivergences, int AgedOpenDivergences,
    DateOnly? LastCheckedOn);
public sealed record DivergenceListRequest(string? Kind, string? Status, string? Query,
    string? Cursor, int Take = 25);
public sealed record ErpChangeDivergenceRow(Guid DivergenceId, string Kind, string SubjectType,
    Guid? SubjectId, string SubjectRef, string SourceValue, string TargetValue, DateTime FirstSeenAt,
    DateTime LastSeenAt, DateTime ReviewDueAt, string Status, string? Resolution,
    string? ResolutionNote, Guid? ResolutionCommandId, string? ResolvedBy, DateTime? ResolvedAt);
public sealed record ErpChangeDivergencePage(IReadOnlyList<ErpChangeDivergenceRow> Items,
    string? NextCursor, int Open, int AgedOpen, int Resolved);
public sealed record ReportErpChangeDivergence(string Kind, string SubjectType, Guid? SubjectId,
    string SubjectRef, string SourceValue, string TargetValue, Guid? SourceInboundMessageId = null,
    Guid? TargetInboundMessageId = null, Guid? StockDifferenceId = null, int AgeLimitDays = 1);
public sealed record ExplainDivergenceRequest(string Note);
public sealed record AcceptDivergenceSourceRequest(Guid CommandId);
public sealed record RaiseDivergenceCorrectionRequest(ResolveStockDifferenceRequest Correction);
public sealed record ErpChangeDailyCheckView(DateOnly BusinessDate, int NewDivergences,
    int AgedOpenDivergences, bool Clean, int ConsecutiveCleanDays, DateTime CheckedAt, string CheckedBy);

public sealed record BeginCutoverRequest(string TargetOutboundMode);
public sealed record RecordOutboundValidationRequest(string CommandType, string Method,
    string Evidence, bool Passed = true);
public sealed record ConfirmFirstOutboundRequest(string CommandType, string ExternalDocumentRef,
    string Evidence);
public sealed record ConvertParkedOutboundRequest(Guid OutboundCommandId, string Reason);
public sealed record ParkedOutboundRow(Guid OutboundCommandId, string DocumentKind,
    string DocumentReference, int UnitCount, string ParkedReason, DateTime ParkedAt,
    bool OwnedByManualTask);
public sealed record ParkedOutboundPage(IReadOnlyList<ParkedOutboundRow> Items, string? NextCursor);
public sealed record CutoverChecklistItemView(string Code, string State, string Detail,
    string? Evidence, string? CompletedBy, DateTime? CompletedAt);
public sealed record CutoverChecklistView(Guid RunId, DateTime? CutoverAt,
    IReadOnlyList<CutoverChecklistItemView> Items, bool CanAdvance);
public sealed record ArchiveStateView(bool SourceArchived, DateTime? ArchivedAt,
    int ActiveConnections, int RetainedMappings, int RetainedInboundMessages);
public sealed record ArchiveErpChangeResult(Guid RunId, bool Archived,
    ArchiveStateView State, string? FailureCode = null);

public sealed record RemapMasterDataResult(Guid RunId, IReadOnlyList<Guid> BackfillIds,
    int ItemsMapped, int PartiesMapped, int ExceptionsOpen, int ExceptionsCreated);
public sealed record MasterDataExceptionListRequest(string? Kind, string? Status, string? Query,
    string? Cursor, int Take = 25);
public sealed record MasterDataCandidate(Guid ModuleId, string Label);
public sealed record MasterDataExceptionRow(Guid ExceptionId, string Kind, string TargetExternalId,
    string DisplayName, string? TaxId, string? Country, string Problem,
    IReadOnlyList<MasterDataCandidate> Candidates, int LiveUnitsAffected, string Status,
    Guid? ResolvedModuleId, string? ResolvedBy, DateTime? ResolvedAt, string? ResolutionNote);
public sealed record MasterDataExceptionPage(IReadOnlyList<MasterDataExceptionRow> Items, string? NextCursor,
    int ItemsMapped, int PartiesMapped, int OpenExceptions, int Ambiguous);
public sealed record ResolveMasterDataExceptionRequest(Guid ModuleId, string? Note = null);
public sealed record MarkMasterDataNotNeededRequest(string Note);

public sealed record ReconcileStockResult(Guid RunId, int UnitsCompared, int Differences,
    int CoveredOpen, int ResolvedByNormalisation, DateTime ComparedAt);
public sealed record StockDifferenceListRequest(string? Reason, bool? LiveCover, string? Status,
    string? Query, string? Cursor, int Take = 25);
public sealed record StockDifferenceRow(Guid DifferenceId, Guid? UnitId, Guid? ItemId, string Identifier,
    string? ItemName, string Reason, string? ModuleLocation, string? SourceLocation,
    string? TargetLocation, string? ModuleStatus, string? SourceStatus, string? TargetStatus,
    bool HasLiveEntitlement, DateOnly? WarrantyEnd, string SuggestedResolution, string Status,
    string? ResolutionCommand, Guid? ResolutionCommandId, string? ResolvedBy, DateTime? ResolvedAt,
    DateTime ComparedAt);
public sealed record StockDifferencePage(IReadOnlyList<StockDifferenceRow> Items, string? NextCursor,
    int Compared, int Open, int CoveredOpen, int Resolved,
    IReadOnlyList<StockReasonCount> ReasonCounts);
public sealed record StockReasonCount(string Reason, int Open, int Resolved, int CoveredOpen);

public sealed record ResolveStockDifferenceRequest(string Command, Guid CommandId,
    string? IdentifierType = null, string? NewIdentifier = null, Guid? SurvivorUnitId = null,
    Guid? AbsorbedUnitId = null, Guid? LocationId = null, string? LocationKind = null,
    string? Note = null);
public sealed record SignOffStockResidualRequest(string Note);
public sealed record StockResidualSignoffView(Guid SignoffId, int ResidualCount, string Note,
    string SignedOffBy, DateTime SignedOffAt);

public sealed class ErpChangeRuleException(string rule, string message) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
}

public static class ErpChangeStep
{
    public const int Register = 1;
    public const int MasterData = 2;
    public const int Stock = 3;
    public const int DualRun = 4;
    public const int Cutover = 5;
    public const int Archive = 6;
}

public static class ErpChangeDivergenceResolution
{
    public const string AcceptedSource = "accepted_source";
    public const string Explained = "explained";
    public const string CorrectionRaised = "correction_raised";
}

public static class ErpChangeValidationMethod
{
    public const string TestCompany = "test_company";
    public const string Manual = "manual";
}

public static class MasterDataProblem
{
    public const string Unmatched = "unmatched";
    public const string Ambiguous = "ambiguous";
    public const string MissingInTarget = "missing_in_target";
}

public static class StockDifferenceReason
{
    public const string MissingInTarget = "missing_in_b";
    public const string ExtraInTarget = "extra_in_b";
    public const string LocationDiffers = "location_differs";
    public const string SerialDiffersByNormalisation = "serial_differs_by_normalisation";
}

public static class StockResolutionCommand
{
    public const string CorrectIdentifier = "correct_identifier";
    public const string MergeUnits = "merge_units";
    public const string ChangeCustody = "change_custody";
    public static readonly IReadOnlyList<string> All = [CorrectIdentifier, MergeUnits, ChangeCustody];
}
