using Yojana.Integration.Connections;

namespace Yojana.Integration.ErpChange;

public sealed record StartErpChangeRequest(Guid SourceConnectionId, RegisterConnection NewConnection);
public sealed record CancelErpChangeRequest(string Reason);

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
    int OpenMasterDataExceptions);

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
    int Compared, int Open, int CoveredOpen, int Resolved);

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
