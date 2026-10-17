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
/// Opens a claim against a unit, resolved either directly or through any identifier. When an identifier has no unit,
/// <see cref="ItemId"/>, <see cref="ProofOfPurchaseRef"/> and <see cref="ProofOfPurchaseDate"/> are the evidence needed
/// to reconstruct the low-confidence unit and entitlement; no shipment or delivery facts are inferred from them.
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
    string? ProofOfPurchaseRef = null,
    DateOnly? ProofOfPurchaseDate = null,
    Guid? ItemId = null);

public sealed record SubmitClaimResult(Guid ClaimId, Guid UnitId, string Status, IReadOnlyList<Guid> ClaimLineIds, Guid EventId, bool Replayed);

/// <summary>The signed-in adjudicator and the tenant roles established by the trusted entry point.</summary>
public sealed record ClaimReviewer(string UserRef, IReadOnlyList<string> Roles);

/// <summary>
/// Adjudicates every line against the policy stack, or, when <see cref="DenialReason"/> is given, records the
/// adjudicator's claim-level finding (<see cref="ClaimManualDenialReason"/>) with its note instead.
/// </summary>
public sealed record AdjudicateClaim(Guid CommandId, Guid ClaimId, DateTime EventTime, string ActorRef, ClaimReviewer? ReviewedBy = null,
    string? DenialReason = null, string? DenialNote = null);

/// <summary>Asks the claimant for more evidence; the claim keeps its status and the request shows on the list until evidence arrives.</summary>
public sealed record RequestClaimEvidence(Guid CommandId, Guid ClaimId, string Note, DateTime EventTime, string ActorRef);

/// <summary>
/// Tenant claim settings: the role that must review adjudication based on low-confidence cover, and the adjudication
/// target the claims list measures ageing against (working days from submission).
/// </summary>
public sealed record ClaimReviewPolicy(string LowConfidenceReviewerRole, int AdjudicationTargetWorkingDays = 5);

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
    bool Replayed,
    Guid? SuccessorItemId = null);

public sealed record ApproveRecoveryClaim(Guid CommandId, Guid RecoveryClaimId, decimal ApprovedAmount, DateTime EventTime, string ActorRef);

public sealed record RecoveryClaimChangeResult(Guid RecoveryClaimId, string Status, decimal RequestedAmount, decimal? ApprovedAmount, Guid EventId, bool Replayed);

public sealed record SettleClaim(Guid CommandId, Guid ClaimId, DateTime EventTime, string ActorRef);

public sealed record CloseClaim(Guid CommandId, Guid ClaimId, DateTime EventTime, string ActorRef);

public sealed record ClaimChangeResult(Guid ClaimId, string Status, Guid EventId, bool Replayed);

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
    IReadOnlyList<ClaimEvidenceRow> Evidence,
    string? AdjudicatedBy = null,
    string? ReviewerRole = null,
    Guid? SuccessorItemId = null,
    string ClaimNumber = "",
    string? Serial = null,
    Guid? ItemId = null,
    DateTime SubmittedAt = default,
    string SubmittedBy = "",
    DateTime? AdjudicatedAt = null,
    IReadOnlyList<ClaimLineRow>? LineRows = null,
    IReadOnlyList<ClaimRecoveryRef>? Recoveries = null,
    string? OpenWork = null);

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
    string? ApprovedBy,
    string RecoveryNumber = "",
    string CustomerClaimNumber = "");

