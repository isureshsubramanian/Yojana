namespace Yojana.Serialisation.Contracts;

// Read models for the claim screens (P4-T09). Party, item and user names are resolved by the client through the
// reference-name ports; these records carry references only.

/// <summary>
/// Claims list filters. <see cref="Status"/> is a claim status, <c>open</c> (not yet settled) or <c>all</c>;
/// <see cref="Query"/> matches a claim number, or any unit in <see cref="UnitIds"/> that the caller resolved it to.
/// </summary>
public sealed record ClaimListFilter(string? Status = "open", string? CoverageSource = null, string? ServiceCountry = null, string? Recovery = null,
    string? Query = null, IReadOnlyList<Guid>? UnitIds = null);

public sealed record ClaimListRow(
    Guid ClaimId,
    string ClaimNumber,
    Guid UnitId,
    string? Serial,
    Guid? ItemId,
    Guid ClaimantPartyRef,
    Guid SettlementPartyRef,
    string Status,
    string CoverageSource,
    string Remedy,
    string ServiceCountry,
    string Currency,
    DateOnly ClaimDate,
    DateTime SubmittedAt,
    decimal RequestedAmount,
    decimal ApprovedAmount,
    int AgeDays,
    string? DenialReason,
    bool RecoveryRaised,
    bool EvidenceRequested,
    int LineCount,
    int LinesDecided,
    string? RecoveryVendorNote = null);

public sealed record ClaimPage(IReadOnlyList<ClaimListRow> Items, int Total, int Skip, int Take);

public sealed record MoneyTotal(string Currency, decimal Amount);

public sealed record ReasonCount(string Reason, int Count);

/// <summary>The claims list's KPI strip and ageing bar, all as of one date.</summary>
public sealed record ClaimSummary(
    DateOnly AsOf,
    int Open,
    int Submitted,
    int Adjudicating,
    int ApprovedNotSettled,
    int DeniedNotSettled,
    IReadOnlyList<MoneyTotal> ApprovedThisMonth,
    int ApprovedCountThisMonth,
    int PartialCountThisMonth,
    int DeniedCountThisMonth,
    IReadOnlyList<ReasonCount> DeniedReasonsThisMonth,
    IReadOnlyList<MoneyTotal> RecoveryOpen,
    int RecoveryOpenCount,
    int RecoveryVendorCount,
    string? OldestOpenNumber,
    Guid? OldestOpenClaimId,
    int? OldestOpenAgeDays,
    string? OldestOpenStatus,
    bool OldestOpenEvidenceRequested,
    IReadOnlyList<int> AgeBuckets,
    int AdjudicationTargetWorkingDays,
    IReadOnlyList<string> CoverageSources,
    IReadOnlyList<string> ServiceCountries,
    long AllClaims);

/// <summary>One stored claim line with its decision, for the claim drawer and the adjudication screen.</summary>
public sealed record ClaimLineRow(
    Guid ClaimLineId,
    int Sequence,
    Guid UnitId,
    string? Serial,
    Guid? ItemId,
    string Kind,
    string CoverageFamily,
    string? Description,
    decimal RequestedAmount,
    decimal CoveragePct,
    decimal ApprovedAmount,
    string CoverageSource,
    string? DenialReason,
    IReadOnlyDictionary<string, decimal> Measurements);

public sealed record ClaimRecoveryRef(Guid RecoveryClaimId, string RecoveryNumber, Guid VendorPartyRef, string Status);

/// <summary>One level of the policy stack as asked for a line, in resolution order.</summary>
public sealed record PolicyStackStep(string Level, string Outcome, string Detail);

/// <summary>What adjudication would decide for one line now, before anything is recorded.</summary>
public sealed record ClaimLinePreview(
    Guid ClaimLineId,
    int Sequence,
    Guid UnitId,
    string? Serial,
    Guid? ItemId,
    bool IsChild,
    string Kind,
    string CoverageFamily,
    string? Description,
    decimal RequestedAmount,
    decimal CoveragePct,
    decimal ApprovedAmount,
    string CoverageSource,
    string Level,
    string? PolicyCode,
    int? PolicyVersion,
    Guid? EntitlementId,
    DateOnly? CoverStart,
    DateOnly? CoverEnd,
    string? DenialReason,
    IReadOnlyList<PolicyStackStep> Stack);

public sealed record ClaimCheck(string Key, string Value, string Outcome);

public sealed record RecoveryPreview(Guid ChildUnitId, string? ChildSerial, Guid VendorPartyRef, decimal RequestedAmount, DateOnly? TermStart, DateOnly? TermEnd, int? TermMonths, int? MonthOfTerm);

/// <summary>
/// The adjudication screen's model: the decision per line exactly as approve would record it, the checks behind it,
/// the facts approve will publish, and the refusal approve would meet (mixed sources, cohorts or obligors) if any.
/// </summary>
public sealed record ClaimAdjudicationPreview(
    Guid ClaimId,
    string ClaimNumber,
    string Outcome,
    string CoverageSource,
    string Currency,
    decimal RequestedAmount,
    decimal ApprovedAmount,
    IReadOnlyList<ClaimLinePreview> Lines,
    IReadOnlyList<ClaimCheck> Checks,
    Guid? CohortId,
    Guid ObligorEntityId,
    IReadOnlyList<RecoveryPreview> Recoveries,
    decimal ChargeableAmount,
    string? Refusal,
    string? RefusalStep,
    string? RequiredReviewerRole,
    Guid? SuccessorItemId,
    IReadOnlyList<string> ManualDenialReasons);
