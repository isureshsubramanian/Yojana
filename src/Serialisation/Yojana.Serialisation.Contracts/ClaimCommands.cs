namespace Yojana.Serialisation.Contracts;

/// <summary>One monetary claim line; a null unit means the claim's root unit.</summary>
public sealed record ClaimLineInput(
    string Kind,
    string CoverageFamily,
    decimal RequestedAmount,
    Guid? UnitId = null,
    string? Description = null,
    IReadOnlyDictionary<string, decimal>? Measurements = null);

public sealed record ClaimEvidenceInput(string Kind, string Reference, string? Description = null);

/// <summary>
/// Opens a claim against an existing unit, resolved either directly or through any identifier. Proof of purchase is
/// evidence for the claim; creating a missing legacy unit remains P4-T04.
/// </summary>
public sealed record SubmitClaim(
    Guid CommandId,
    Guid? UnitId,
    DocumentSerial? Identifier,
    DateOnly ClaimDate,
    Guid ClaimantPartyRef,
    Guid SettlementPartyRef,
    Guid ServiceEntityId,
    Guid ObligorEntityId,
    string ServiceCountry,
    string Currency,
    IReadOnlyList<ClaimLineInput> Lines,
    IReadOnlyList<ClaimEvidenceInput> Evidence,
    string Remedy,
    string ConsumerChoiceEvidenceRef,
    DateTime EventTime,
    string ActorRef,
    string? ProofOfPurchaseRef = null);

public sealed record SubmitClaimResult(Guid ClaimId, Guid UnitId, string Status, IReadOnlyList<Guid> ClaimLineIds, Guid EventId, bool Replayed);

public sealed record AdjudicateClaim(Guid CommandId, Guid ClaimId, DateTime EventTime, string ActorRef);

public sealed record ClaimLineDecision(
    Guid ClaimLineId,
    Guid UnitId,
    string CoverageFamily,
    decimal RequestedAmount,
    decimal CoveragePct,
    decimal ApprovedAmount,
    string CoverageSource,
    Guid? EntitlementId,
    string? DenialReason,
    int ChainLength);

public sealed record AdjudicateClaimResult(
    Guid ClaimId,
    string Status,
    string CoverageSource,
    string? DenialReason,
    int ChainLength,
    IReadOnlyList<ClaimLineDecision> Lines,
    IReadOnlyList<Guid> RecoveryClaimIds,
    Guid EventId,
    bool Replayed);

public sealed record ApproveRecoveryClaim(Guid CommandId, Guid RecoveryClaimId, decimal ApprovedAmount, DateTime EventTime, string ActorRef);

public sealed record RecoveryClaimChangeResult(Guid RecoveryClaimId, string Status, decimal RequestedAmount, decimal? ApprovedAmount, Guid EventId, bool Replayed);

public sealed record SettleClaim(Guid CommandId, Guid ClaimId, DateTime EventTime, string ActorRef);

public sealed record CloseClaim(Guid CommandId, Guid ClaimId, DateTime EventTime, string ActorRef);

public sealed record ClaimChangeResult(Guid ClaimId, string Status, Guid EventId, bool Replayed);

public sealed record ClaimListRow(Guid ClaimId, Guid UnitId, string Status, string CoverageSource, DateOnly ClaimDate, decimal RequestedAmount, decimal ApprovedAmount, int AgeDays, string? DenialReason);

public sealed record ClaimPage(IReadOnlyList<ClaimListRow> Items, int Total, int Skip, int Take);

public sealed record ClaimEvidenceRow(Guid EvidenceId, string Kind, string Reference, string? Description, DateTime RecordedAt, string ActorRef);

public sealed record ClaimDetail(
    Guid ClaimId,
    Guid UnitId,
    string Status,
    bool RecoveryRaised,
    DateOnly ClaimDate,
    Guid ClaimantPartyRef,
    Guid SettlementPartyRef,
    Guid ServiceEntityId,
    Guid ObligorEntityId,
    string ServiceCountry,
    string Currency,
    string CoverageSource,
    string Remedy,
    string? DenialReason,
    int ChainLength,
    IReadOnlyList<Guid> EntitlementIds,
    IReadOnlyList<ClaimLineDecision> Lines,
    IReadOnlyList<ClaimEvidenceRow> Evidence);

public sealed record RecoveryClaimRow(
    Guid RecoveryClaimId,
    Guid CustomerClaimId,
    Guid ClaimLineId,
    Guid InboundEntitlementId,
    Guid ChildUnitId,
    Guid VendorPartyRef,
    decimal RequestedAmount,
    decimal? ApprovedAmount,
    string Currency,
    string Status,
    DateTime RaisedAt,
    DateTime? ApprovedAt,
    string? ApprovedBy);

