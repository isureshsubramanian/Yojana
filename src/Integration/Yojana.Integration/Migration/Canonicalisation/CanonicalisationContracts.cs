namespace Yojana.Integration.LegacyMigration.Canonicalisation;

public sealed record CanonicalFamilyRule(
    string ItemCodePrefix,
    bool StripHyphens,
    bool StripSpaces,
    bool StripLeadingZeros);

public sealed record CanonicalFamilySettingsView(IReadOnlyList<CanonicalFamilyRule> Families, string Version);

public sealed record UpdateCanonicalFamilySettingsRequest(
    IReadOnlyList<CanonicalFamilyRule> Families,
    string ExpectedVersion);

public sealed record StartCanonicalisationResponse(Guid RunId, Guid BatchId, string Status);

public sealed record CanonicalisationGap(string Kind, string Reason);

public sealed record CanonicalisationView(
    Guid RunId,
    Guid BatchId,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string Status,
    string? Failure,
    long SerialsInSource,
    long Exact,
    long TrimmedUpper,
    long FamilyNormalised,
    long WaitingForPerson,
    long Merged,
    long KeptApart,
    long FixInIncumbent,
    long CarriedOver,
    long UnverifiedDates,
    IReadOnlyList<CanonicalisationGap> Gaps);

public sealed record MigrationReviewRow(
    Guid ItemId,
    string? SerialAsStored,
    string? ItemCode,
    string? ItemName,
    string? Family,
    string Kind,
    string KindLabel,
    string RuleTried,
    string CandidateSummary,
    string? Suggestion,
    string? SuggestionReason,
    string? Hint,
    string Status,
    bool CarriedOver);

public sealed record MigrationReviewPage(IReadOnlyList<MigrationReviewRow> Items, string? NextCursor);

public sealed record MigrationCandidateLineage(
    string SourceKey,
    string? SerialAsStored,
    string ItemCode,
    string? ItemName,
    long Transactions,
    DateTime? FirstReceipt,
    DateTime? LastDelivery,
    string? LastDeliveryParty,
    bool HasEquipmentCard,
    bool HasContract,
    string Disposition);

public sealed record MigrationReviewDecisionView(
    Guid DecisionId,
    string Decision,
    string? KeepSourceKey,
    string Reason,
    string? Note,
    string DecidedByName,
    DateTime DecidedAt,
    bool AppliedToThisBatch);

public sealed record MigrationReviewDetail(
    Guid ItemId,
    string Kind,
    string KindLabel,
    string Status,
    string? SerialAsStored,
    string? ItemCode,
    string? ItemName,
    string RuleTried,
    IReadOnlyList<MigrationCandidateLineage> Candidates,
    string? Suggestion,
    string? SuggestedKeepSourceKey,
    string? SuggestionReason,
    string? Hint,
    IReadOnlyList<MigrationReviewDecisionView> EarlierDecisions);

public sealed record MigrationReviewDecisionRequest(
    string Decision,
    string? KeepSourceKey,
    string Reason,
    string? Note = null);

public sealed record MigrationReviewDecisionResponse(
    Guid DecisionId,
    Guid ItemId,
    string Decision,
    string Status,
    DateTime DecidedAt);
