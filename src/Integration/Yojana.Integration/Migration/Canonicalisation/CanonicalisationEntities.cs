using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Canonicalisation;

public static class CanonicalisationRunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class CanonicalMatchRule
{
    public const string Exact = "exact";
    public const string TrimmedUpper = "trimmed_upper";
    public const string FamilyNormalised = "family_normalised";
    public const string Reviewed = "reviewed";
}

public static class CanonicalProvenance
{
    public const string Source = "source";
    public const string Unverified = "unverified";
}

public static class CandidateDisposition
{
    public const string Active = "active";
    public const string Review = "review";
    public const string Merged = "merged";
    public const string Excluded = "excluded";
}

public static class CandidateSourceKind
{
    public const string SerialMaster = "serial_master";
    public const string EquipmentCard = "equipment_card";
    public const string Contract = "contract";
    public const string ContractLine = "contract_line";
}

public static class CandidateSourceRole
{
    public const string Kept = "kept";
    public const string Merged = "merged";
}

public static class MigrationReviewKind
{
    public const string SameSerialOneItem = "same_serial_one_item";
    public const string SameSerialDifferentItems = "same_serial_different_items";
    public const string OrphanEquipmentCard = "orphan_equipment_card";
    public const string SerialInUserField = "serial_in_user_field";
    public const string HistoryNeedsReview = "history_needs_review";
}

public static class MigrationReviewStatus
{
    public const string Waiting = "waiting";
    public const string Decided = "decided";
    public const string CarriedOver = "carried_over";
}

public static class MigrationReviewDecisionKind
{
    public const string MergeInto = "merge_into";
    public const string KeepApart = "keep_apart";
    public const string FixInIncumbent = "fix_in_incumbent";
    public const string AcceptAsRecorded = "accept_as_recorded";
    public const string ExcludeFromHistory = "exclude_from_history";
}

public sealed class MigrationCanonRun : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RunId { get; set; }
    public Guid BatchId { get; set; }
    public Guid ProfileRunId { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public required string Status { get; set; }
    public string? FailureCode { get; set; }
    public long SourceSerialCount { get; set; }
    public long ExactCount { get; set; }
    public long TrimmedUpperCount { get; set; }
    public long FamilyNormalisedCount { get; set; }
    public long WaitingCount { get; set; }
    public long MergedCount { get; set; }
    public long KeptApartCount { get; set; }
    public long FixInIncumbentCount { get; set; }
    public long CarriedOverCount { get; set; }
}

public sealed class MigrationUnitCandidate : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid CandidateId { get; set; }
    public Guid RunId { get; set; }
    public required string PrimarySourceKey { get; set; }
    public required string ItemCode { get; set; }
    public string? ItemName { get; set; }
    public required string Family { get; set; }
    public required string CanonicalSerial { get; set; }
    public string? StoredSerial { get; set; }
    public required string TrimmedSerial { get; set; }
    public required string FamilyNormalisedSerial { get; set; }
    public required string MatchRule { get; set; }
    public required string Provenance { get; set; }
    public required string Disposition { get; set; }
    public string? HistoryDepth { get; set; }
    public string? Note { get; set; }
}

public sealed class MigrationUnitCandidateSource : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid CandidateSourceId { get; set; }
    public Guid RunId { get; set; }
    public Guid CandidateId { get; set; }
    public required string MatchedSerialSourceKey { get; set; }
    public required string SourceKind { get; set; }
    public required string SourceKey { get; set; }
    public required string Role { get; set; }
}

public sealed class MigrationEventCandidate : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EventCandidateId { get; set; }
    public Guid RunId { get; set; }
    public Guid CandidateId { get; set; }
    public required string SerialMasterSourceKey { get; set; }
    public required string TransactionSourceKey { get; set; }
    public required string MatchRule { get; set; }
    public required string Provenance { get; set; }
}

public sealed class MigrationEntitlementCandidate : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EntitlementCandidateId { get; set; }
    public Guid RunId { get; set; }
    public Guid CandidateId { get; set; }
    public required string SerialMasterSourceKey { get; set; }
    public required string SourceKind { get; set; }
    public required string SourceKey { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public required string MatchRule { get; set; }
    public required string Provenance { get; set; }
}

public sealed class MigrationReviewItem : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReviewItemId { get; set; }
    public Guid RunId { get; set; }
    public Guid BatchId { get; set; }
    public required string SourceSystem { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    public required string Fingerprint { get; set; }
    public required string CorrelationKey { get; set; }
    public required string CanonicalKey { get; set; }
    public string? ItemCode { get; set; }
    public string? ItemName { get; set; }
    public string? Family { get; set; }
    public string? StoredSerial { get; set; }
    public required string RuleTried { get; set; }
    public required string SourceKeysJson { get; set; }
    public required string CandidateSourceKeysJson { get; set; }
    public required string CandidateSummary { get; set; }
    public string? Suggestion { get; set; }
    public string? SuggestedKeepSourceKey { get; set; }
    public string? SuggestionReason { get; set; }
    public string? Hint { get; set; }
    public Guid? AppliedDecisionId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class MigrationReviewDecision : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid DecisionId { get; set; }
    public Guid ReviewItemId { get; set; }
    public required string Fingerprint { get; set; }
    public required string CorrelationKey { get; set; }
    public required string Decision { get; set; }
    public string? KeepSourceKey { get; set; }
    public required string Reason { get; set; }
    public string? Note { get; set; }
    public required string DecidedBy { get; set; }
    public DateTime DecidedAt { get; set; }
}
