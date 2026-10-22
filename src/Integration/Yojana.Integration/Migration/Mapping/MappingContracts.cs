namespace Yojana.Integration.LegacyMigration.Mapping;

public sealed record StartMappingResponse(Guid RunId, Guid BatchId, string Status);
public sealed record MappingCount(string Key, long Count);
public sealed record MigrationGapCount(string Kind, string Label, long Count);
public sealed record MappingWaitingReviewCount(string Kind, long Count);

public sealed record MigrationMapView(
    Guid RunId,
    Guid BatchId,
    Guid ReconstructionRunId,
    DateOnly CutoverDate,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string Status,
    string? Failure,
    IReadOnlyList<MappingCount> UnitsByOrigin,
    IReadOnlyList<MappingCount> UnitsByLocationState,
    IReadOnlyList<MappingCount> IdentifiersByIssuer,
    IReadOnlyList<MappingCount> EventsByProvenance,
    IReadOnlyList<MappingCount> Ownership,
    long ReplacementLinks,
    IReadOnlyList<MappingCount> EntitlementsByFamily,
    IReadOnlyList<MappingCount> EntitlementsByDateSource,
    long ExpiredEntitlements,
    IReadOnlyList<MigrationGapCount> GapsByKind,
    long ExcludedCandidates,
    long WaitingCandidatesLeftOut);

public sealed record MigrationPlanGapView(Guid GapId, Guid? PlanUnitId, string? ItemCode, string? ItemName,
    string? Serial, string Kind, string Label, string Reason, string? SourceKey);
public sealed record MigrationPlanGapPage(IReadOnlyList<MigrationPlanGapView> Items, string? NextCursor);

public sealed record MigrationPlanIdentifierView(string Type, string RawValue, string Issuer,
    string? SourceDataValue, string Provenance);
public sealed record MigrationPlanEventView(int Sequence, string MovementKind, DateTime EffectiveDate,
    string DocumentTypeLabel, int? DocumentNumber, string? Party, string? Warehouse, decimal Quantity,
    string Provenance);
public sealed record MigrationPlanOwnershipView(string Tier, string PartyReference, string? Party,
    bool Pseudonymised, bool IsCurrent, string EvidenceSource, DateTime? EvidenceDate);
public sealed record MigrationPlanWarrantyEvidenceView(string SourceKind, string SourceKey, DateOnly? StartDate,
    DateOnly? EndDate, DateOnly? TerminationDate, string Direction, bool SelectedForEntitlement);
public sealed record MigrationPlanEntitlementView(string CoverageFamily, Guid? PolicyId, int? PolicyVersion,
    DateOnly? StartDate, DateOnly? EndDate, DateOnly? TerminationDate, int RemainingDays, bool Expired,
    string Direction, string Provenance, string DateSource, string SourceKey,
    IReadOnlyList<MigrationPlanWarrantyEvidenceView> Evidence);
public sealed record MigrationPlanReplacementView(Guid LinkId, Guid ReplacedPlanUnitId,
    Guid ReplacementPlanUnitId, string PolicyApplied, string SourceKey);
public sealed record MigrationPlanAttachmentView(Guid AttachmentId, string? Path, string FileName,
    long? FileSize, string? ContentHash, string SourceKey);
public sealed record MigrationPlanLogView(Guid LogId, string Kind, string? SourceKey,
    string? RelatedSourceKey, string Note);
public sealed record MigrationPlanCertificateView(int Version, string Provenance);

public sealed record MigrationPlanUnitView(
    Guid PlanUnitId,
    string ItemCode,
    string? ItemName,
    string Origin,
    string HistoryDepth,
    string LocationState,
    string? Warehouse,
    string? CustodyLocationKind,
    string ConditionGrade,
    string ConditionProvenance,
    string? Note,
    IReadOnlyList<MigrationPlanIdentifierView> Identifiers,
    IReadOnlyList<MigrationPlanEventView> Events,
    IReadOnlyList<MigrationPlanOwnershipView> Ownership,
    IReadOnlyList<MigrationPlanEntitlementView> Entitlements,
    IReadOnlyList<MigrationPlanReplacementView> ReplacementLinks,
    IReadOnlyList<MigrationPlanAttachmentView> Attachments,
    MigrationPlanCertificateView Certificate,
    IReadOnlyList<MigrationPlanGapView> Gaps,
    IReadOnlyList<MigrationPlanLogView> Log);

public sealed record ConditionMappingRule(string? CardStatus, string? LastMovementKind, string Grade);
public sealed record MappingSettingsView(bool AllowWaitingReviews, DateOnly? CutoverDate,
    IReadOnlyList<ConditionMappingRule> ConditionMap, Guid? CardCoverPolicyId = null,
    Guid? SerialMasterCoverPolicyId = null);
public sealed record UpdateMappingSettingsRequest(bool AllowWaitingReviews, DateOnly? CutoverDate,
    IReadOnlyList<ConditionMappingRule> ConditionMap, Guid? CardCoverPolicyId = null,
    Guid? SerialMasterCoverPolicyId = null);

public sealed class MappingRuleException(string rule, string message,
    IReadOnlyList<MappingWaitingReviewCount>? waitingReviews = null) : InvalidOperationException(message)
{
    public string Rule { get; } = rule;
    public IReadOnlyList<MappingWaitingReviewCount> WaitingReviews { get; } = waitingReviews ?? [];
}
