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
    Guid? ItemId = null,
    Guid? CampaignId = null,
    string? ReportedCauseCode = null,
    string? ReportedDescription = null,
    DateOnly? OccurredDate = null);

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
public sealed record RequestClaimEvidence(Guid CommandId, Guid ClaimId, string Note, DateTime EventTime, string ActorRef,
    string? ExpectedVersion = null) : IExpectedDocumentVersion;

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
    string? OpenWork = null,
    Guid? CampaignId = null,
    string? CampaignCode = null,
    string? CampaignName = null,
    string? ReserveTag = null,
    string? CampaignCoverReason = null,
    string Version = "",
    string? ReportedCauseCode = null, string? ReportedDescription = null, DateOnly? OccurredDate = null);

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
    string CustomerClaimNumber = "",
    string Version = "");

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
    string? VendorNote,
    string Version = "");

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
    public const string CampaignClaim = "campaign_claim";

    /// <summary>A line on the whole unit found live vendor cover on more than one part; the failed part must be named.</summary>
    public const string SeveralCoveredParts = "several_covered_parts";

    /// <summary>The tenant recovers only from the part named on the line, and the line named the whole unit.</summary>
    public const string PartNotNamed = "part_not_named";

    /// <summary>The tenant does not recover this kind of line (for example labour or travel).</summary>
    public const string LineKindNotRecovered = "line_kind_not_recovered";
}

/// <summary>How a line on the whole unit (rather than a named part) picks the vendor term recovered from.</summary>
public static class RecoveryWholeUnitRule
{
    /// <summary>Recover when exactly one part inside the unit carries live vendor cover; several covered parts raise none.</summary>
    public const string SingleCoveredPart = "single_covered_part";

    /// <summary>Recover only from the part named on the line; a line on the whole unit recovers only the unit's own vendor term.</summary>
    public const string NamedPartOnly = "named_part_only";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { SingleCoveredPart, NamedPartOnly };
}

/// <summary>Whether a claim that names campaign cover participates in the tenant's normal supplier-recovery rules.</summary>
public static class RecoveryCampaignClaims
{
    public const string NoRecovery = "no_recovery";
    public const string RecoverLikeWarranty = "recover_like_warranty";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { NoRecovery, RecoverLikeWarranty };
}

/// <summary>
/// Tenant-wide supplier-recovery policy, applied when a claim is approved; a change affects later approvals only.
/// <paramref name="VendorAnswerDueDays"/> is when an unanswered recovery claim shows as waiting on the vendor.
/// </summary>
public sealed record RecoveryPolicy(
    string WholeUnitLines,
    IReadOnlyList<string> LineKinds,
    int VendorAnswerDueDays,
    string CampaignClaims = RecoveryCampaignClaims.NoRecovery)
{
    public static readonly RecoveryPolicy Default = new(RecoveryWholeUnitRule.SingleCoveredPart,
        [ClaimLineKind.Parts, ClaimLineKind.Labour, ClaimLineKind.Travel], 7, RecoveryCampaignClaims.NoRecovery);
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

