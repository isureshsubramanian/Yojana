namespace Yojana.Serialisation.Contracts;

public sealed record RmaListFilter(string? Stage, int? MinimumAgeDays, int? MaximumAgeDays, string? Find);

public sealed record RmaListRow(Guid RmaId, string? RmaNumber, string Stage, DateOnly RequestDate, DateTime UpdatedAt,
    int AgeDays, Guid ReturningPartyRef, int LineCount, int ExceptionCount);

public sealed record RmaStageCount(string Stage, int Count);

public sealed record RmaListResult(IReadOnlyList<RmaListRow> Rows, IReadOnlyList<RmaStageCount> Summary, int Total, int Skip, int Take);

public sealed record RmaDetail(
    Guid RmaId,
    string? RmaNumber,
    string Stage,
    DateOnly RequestDate,
    Guid ReturningPartyRef,
    Guid SettlementPartyRef,
    Guid ServiceEntityId,
    string ServiceCountry,
    string Currency,
    DateTime RequestedAt,
    DateTime? AuthorisedAt,
    DateTime? InTransitAt,
    DateTime? ReceivedAt,
    DateTime? DispositionedAt,
    DateTime? ClosedAt,
    IReadOnlyList<RmaDetailLine> Lines);

public sealed record RmaDetailLine(
    Guid RmaLineId,
    int Sequence,
    Guid RequestedUnitId,
    Guid? AuthorisedUnitId,
    Guid OwnershipEpisodeId,
    DocumentRef OriginalSaleRef,
    Guid? ParentUnitId,
    decimal? PriceAllocation,
    bool ParentIncomplete,
    decimal? CoveragePct,
    decimal? ApprovedAmount,
    string? CoverageSource,
    Guid? EntitlementId,
    string? DenialReason,
    Guid? ReceivedUnitId,
    Guid? ProvisionalUnitId,
    string? ReceiptOutcome,
    string? ConditionGrade,
    string? ValuationRule,
    string? Disposition,
    Guid? ReplacementIntentId,
    string? EntitlementClosedReason);
