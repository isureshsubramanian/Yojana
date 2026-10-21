using System.Text.Json;

namespace Yojana.Serialisation.Contracts;

public static class ClaimRiskRuleCode
{
    public const string RecentCoverPurchase = "recent_cover_purchase";
    public const string RepeatUnitClaims = "repeat_unit_claims";
    public const string RepeatFault = "repeat_fault";
    public const string ClaimantRate = "claimant_rate";
    public const string ChannelMismatch = "channel_mismatch";
    public const string RecentOwnershipChange = "recent_ownership_change";
    public const string PartNotReturned = "part_not_returned";
    public const string DuplicateOpenClaim = "duplicate_open_claim";

    public static readonly IReadOnlyList<string> All =
    [
        RecentCoverPurchase, RepeatUnitClaims, RepeatFault, ClaimantRate, ChannelMismatch,
        RecentOwnershipChange, PartNotReturned, DuplicateOpenClaim,
    ];
}

public static class ClaimRiskFlagStatus
{
    public const string Open = "open";
    public const string Cleared = "cleared";
    public const string Confirmed = "confirmed";
    public static readonly IReadOnlyList<string> All = [Open, Cleared, Confirmed];
}

public static class ClaimRiskEvaluation
{
    public const string Submission = "submission";
    public const string Adjudication = "adjudication";
    public static readonly IReadOnlyList<string> All = [Submission, Adjudication];
}

public sealed record ClaimRiskEnabledRule(bool Enabled);
public sealed record ClaimRiskDaysRule(bool Enabled, int Days);
public sealed record ClaimRiskRepeatRule(bool Enabled, int MinimumEarlierClaims, int Days);
public sealed record ClaimRiskRateRule(bool Enabled, int Days, decimal RateMultiplier, int MinimumClaims, int MinimumUnits);

/// <summary>All deterministic claim-risk rules and the second-review policy, stored together as claims.risk_rules.</summary>
public sealed record ClaimRiskRulesPolicy(
    ClaimRiskDaysRule RecentCoverPurchase,
    ClaimRiskRepeatRule RepeatUnitClaims,
    ClaimRiskDaysRule RepeatFault,
    ClaimRiskRateRule ClaimantRate,
    ClaimRiskEnabledRule ChannelMismatch,
    ClaimRiskDaysRule RecentOwnershipChange,
    ClaimRiskEnabledRule PartNotReturned,
    ClaimRiskEnabledRule DuplicateOpenClaim,
    string ReviewerRole,
    bool RequireDifferentReviewer);

public sealed record ClaimRiskDecisionRequest(string Decision, string Reason);

/// <summary>Internal command; actor, roles and time are stamped by the trusted HTTP entry point.</summary>
public sealed record DecideClaimRiskFlag(
    Guid ClaimId,
    Guid FlagId,
    string Decision,
    string Reason,
    DateTime DecidedAt,
    string ActorRef,
    IReadOnlyList<string> ActorRoles);

public sealed record ClaimRiskFlagRow(
    Guid FlagId,
    Guid ClaimId,
    string ClaimNumber,
    string RuleCode,
    string Status,
    JsonElement Thresholds,
    JsonElement Evidence,
    DateTime RaisedAt,
    string RaisedByEvaluation,
    DateTime? DecidedAt,
    string? ReviewerName,
    string? DecisionReason);

public sealed record ClaimRiskFlagPage(
    IReadOnlyList<ClaimRiskFlagRow> Items,
    string? NextCursor,
    bool HasMore,
    int Take);

public sealed record ClaimRiskFlagDecisionResult(
    Guid FlagId,
    Guid ClaimId,
    string ClaimNumber,
    string RuleCode,
    string Status,
    DateTime DecidedAt,
    string Reason);

public sealed record ClaimRiskFlagReportRow(
    string RuleCode,
    int Hits,
    int Cleared,
    int Confirmed,
    decimal ConfirmationRate,
    int ConfirmedClaimsDeniedAfterReview);

