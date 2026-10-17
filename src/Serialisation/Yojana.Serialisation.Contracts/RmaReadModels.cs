namespace Yojana.Serialisation.Contracts;

public sealed record RmaListFilter(string? Stage, int? MinimumAgeDays, int? MaximumAgeDays, string? Find);

public sealed record RmaListRow(Guid RmaId, string? RmaNumber, string Stage, DateOnly RequestDate, DateTime UpdatedAt,
    int AgeDays, Guid ReturningPartyRef, int LineCount, int ExceptionCount, Guid? PrimaryUnitId, string? PrimarySerial,
    Guid? PrimaryItemId, string? ReceiptOutcome, string? ConditionGrade, string? CoverageSource, DateTime? ReceivedAt,
    string RequestedBy, string? LastActorRef);

public sealed record RmaStageCount(string Stage, int Count, int? OldestAgeDays, int ExceptionCount);

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
    string RequestedBy,
    string? LastActorRef,
    IReadOnlyList<RmaDetailLine> Lines,
    RmaDispatchDetail? Dispatch,
    IReadOnlyList<RmaOpenFollowUp> OpenFollowUps);

public sealed record RmaDispatchDetail(
    DateTime DispatchedAt,
    string? CarrierName,
    string? TrackingReference,
    string DispatchedBy);

public sealed record RmaOpenFollowUp(
    Guid RmaLineId,
    int Sequence,
    string Disposition,
    string Code,
    string Detail);

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
    string? EntitlementClosedReason,
    string? Description,
    Guid? RequestedItemId,
    string? RequestedSerial,
    string? AuthorisedSerial,
    Guid? ReceivedItemId,
    string? ReceivedSerial,
    DateTime? ReceiptBusinessTime,
    string? PostingPeriod,
    string? ReceivedBy,
    Guid? ReceivingLocationId,
    string? ExceptionNote);
