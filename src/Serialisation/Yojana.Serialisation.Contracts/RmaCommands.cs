namespace Yojana.Serialisation.Contracts;

public sealed record DefineReturnReason(
    string Code,
    string Name,
    string RestockFeeRule,
    decimal? RestockFeeValue = null,
    int? DoaWindowDays = null,
    bool Active = true);

public sealed record ReturnReasonRow(
    string Code,
    string Name,
    string RestockFeeRule,
    decimal? RestockFeeValue,
    int? DoaWindowDays,
    bool Active);

/// <summary>One RMA line identified by a unit/serial, or by a sales document whose serial is resolved server-side.</summary>
public sealed record RmaRequestLineInput(
    Guid? UnitId = null,
    DocumentSerial? Identifier = null,
    DocumentRef? ProofOfPurchase = null,
    int? ProofLine = null,
    string CoverageFamily = CoverageFamilyCode.Base,
    decimal RequestedAmount = 0,
    string ClaimLineKind = ClaimLineKind.Parts,
    string? Description = null);

public sealed record RequestRma(
    Guid CommandId,
    DateOnly RequestDate,
    Guid ReturningPartyRef,
    Guid SettlementPartyRef,
    Guid ServiceEntityId,
    Guid ObligorEntityId,
    string ServiceCountry,
    string Currency,
    IReadOnlyList<RmaRequestLineInput> Lines,
    DateTime EventTime,
    string ActorRef = "");

public sealed record RequestRmaResult(Guid RmaId, string Stage, IReadOnlyList<Guid> LineIds, Guid EventId, bool Replayed);

public sealed record AuthoriseRma(Guid CommandId, Guid RmaId, DateTime EventTime, string ActorRef = "");

public sealed record AuthoriseRmaResult(Guid RmaId, string RmaNumber, string Stage, IReadOnlyList<RmaLineCoverageDecision> Lines, Guid EventId, bool Replayed);

public sealed record RmaLineCoverageDecision(
    Guid RmaLineId,
    Guid AuthorisedUnitId,
    decimal CoveragePct,
    decimal ApprovedAmount,
    string CoverageSource,
    Guid? EntitlementId,
    Guid? PolicyVersionId,
    string? DenialReason);

public sealed record ReturnReceiptLineInput(
    Guid RmaLineId,
    DocumentSerial? ReceivedIdentifier,
    string ConditionGrade,
    string ValuationRule,
    string Outcome = ReturnOutcome.Accepted,
    string? ExceptionNote = null);

public sealed record ReceiveReturn(
    Guid CommandId,
    Guid RmaId,
    Guid ReceivingLocationId,
    IReadOnlyList<ReturnReceiptLineInput> Lines,
    DateTime DispatchedAt,
    DateTime ReceivedAt,
    DateTime EventTime,
    string ActorRef = "");

public sealed record ReceiveReturnResult(Guid RmaId, string Stage, IReadOnlyList<ReturnReceiptResultLine> Lines, Guid EventId, bool Replayed);

public sealed record ReturnReceiptResultLine(
    Guid RmaLineId,
    Guid ReturnReceiptId,
    Guid? ReceivedUnitId,
    Guid? ProvisionalUnitId,
    string Outcome,
    bool ExceptionRaised);

public sealed record RmaDispositionLineInput(Guid RmaLineId, string Disposition, Guid? VendorPartyRef = null);

public sealed record DispositionRma(
    Guid CommandId,
    Guid RmaId,
    IReadOnlyList<RmaDispositionLineInput> Lines,
    DateOnly EffectiveDate,
    DateTime EventTime,
    string ActorRef = "");

public sealed record DispositionRmaResult(Guid RmaId, string Stage, IReadOnlyList<RmaDispositionResultLine> Lines, Guid EventId, bool Replayed);

public sealed record RmaDispositionResultLine(Guid RmaLineId, string Disposition, Guid? ReplacementIntentId, string? EntitlementClosedReason);
