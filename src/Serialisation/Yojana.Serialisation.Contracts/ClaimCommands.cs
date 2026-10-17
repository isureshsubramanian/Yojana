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

public sealed record AdjudicateClaim(Guid CommandId, Guid ClaimId, DateTime EventTime, string ActorRef, ClaimReviewer? ReviewedBy = null);

/// <summary>The tenant-selected role that must review adjudication based on low-confidence cover.</summary>
public sealed record ClaimReviewPolicy(string LowConfidenceReviewerRole);

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

public sealed record ApproveRecoveryClaim(
    Guid CommandId,
    Guid RecoveryClaimId,
    decimal ApprovedAmount,
    DateTime EventTime,
    string ActorRef = "",
    string? VendorReference = null,
    string? VendorNote = null);

public sealed record RecoveryClaimChangeResult(
    Guid RecoveryClaimId,
    string Status,
    decimal RequestedAmount,
    decimal? ApprovedAmount,
    Guid EventId,
    bool Replayed,
    string? VendorReference = null,
    string? VendorNote = null);

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
    IReadOnlyList<ClaimEvidenceRow> Evidence,
    string? AdjudicatedBy = null,
    string? ReviewerRole = null,
    Guid? SuccessorItemId = null);

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

public sealed record RecoveryClaimListRow(
    Guid RecoveryClaimId,
    string RecoveryNumber,
    DateTime RaisedAt,
    Guid CustomerClaimId,
    string CustomerClaimNumber,
    DateOnly ClaimDate,
    DateTime? ClaimApprovedAt,
    Guid ChildUnitId,
    string ChildSerial,
    Guid ChildItemId,
    Guid? ParentUnitId,
    string? ParentSerial,
    Guid VendorPartyRef,
    DateOnly? VendorTermStart,
    DateOnly? VendorTermEnd,
    int? VendorTermMonths,
    int? ClaimMonthOfTerm,
    decimal RequestedAmount,
    decimal? ApprovedAmount,
    string Currency,
    string Status,
    int AgeDays,
    DateTime? ApprovedAt,
    string? ApprovedBy,
    string? VendorReference,
    string? VendorNote);

public sealed record RecoveryClaimPage(IReadOnlyList<RecoveryClaimListRow> Rows, int Total, int Skip, int Take);

public sealed record RecoveryCurrencyTotal(string Currency, int ClaimCount, decimal RequestedAmount, decimal ApprovedAmount);

public sealed record RecoveryFilterOption(Guid Id, int Count);

public sealed record OldestOpenRecovery(int AgeDays, string RecoveryNumber, Guid VendorPartyRef);

public sealed record RecoveryClaimSummary(
    int OpenCount,
    IReadOnlyList<RecoveryCurrencyTotal> OpenAmounts,
    IReadOnlyList<RecoveryCurrencyTotal> ApprovedThisYear,
    int TermExpiredWithoutRecovery,
    OldestOpenRecovery? OldestOpen,
    IReadOnlyList<RecoveryFilterOption> Vendors,
    IReadOnlyList<RecoveryFilterOption> ChildItems);

public static class RecoveryNotRaisedReason
{
    public const string VendorTermEnded = "vendor_term_ended";
    public const string NoVendorTerm = "no_vendor_term";
    public const string RecallClaim = "recall_claim";
}

public sealed record RecoveryNotRaisedRow(
    Guid CustomerClaimId,
    string CustomerClaimNumber,
    DateOnly ClaimDate,
    DateTime ApprovedAt,
    Guid UnitId,
    string UnitSerial,
    Guid ItemId,
    string Reason,
    Guid? VendorPartyRef,
    DateOnly? VendorTermStart,
    DateOnly? VendorTermEnd,
    int? VendorTermMonths,
    int? ClaimMonthOfTerm);

public sealed record RecoveryNotRaisedReport(int Days, IReadOnlyList<RecoveryNotRaisedRow> Rows);

