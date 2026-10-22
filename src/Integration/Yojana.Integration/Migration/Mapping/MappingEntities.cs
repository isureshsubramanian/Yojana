using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Mapping;

public static class MappingRunStatus
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class PlanLocationState
{
    public const string InStock = "in_stock";
    public const string Shipped = "shipped";
}

public static class PlanIssuerKind
{
    public const string Tenant = "tenant";
    public const string Manufacturer = "manufacturer";
}

public static class PlanOwnershipTier
{
    public const string DirectCustomer = "direct_customer";
    public const string EndCustomer = "end_customer";
    public const string CurrentCustomer = "current_customer";
    public const string EvidenceCustomer = "evidence_customer";
}

public static class PlanWarrantyEvidenceSource
{
    public const string ContractLine = "contract_line";
    public const string EquipmentCard = "equipment_card";
    public const string SerialMaster = "serial_master";
}

public static class PlanGapKind
{
    public const string TemplateNotApproved = "template_not_approved";
    public const string PolicyNotPublished = "policy_not_published";
    public const string NoTemplate = "no_template";
    public const string ReplacementUnitMissing = "replacement_unit_missing";
    public const string AttachmentPathMissing = "attachment_path_missing";
    public const string AttachmentUnitUnknown = "attachment_unit_unknown";
    public const string WaitingReview = "waiting_review";
    public const string CardCoverPolicyNotSet = "card_cover_policy_not_set";
    public const string SerialMasterCoverPolicyNotSet = "serial_master_cover_policy_not_set";

    public static string Label(string kind) => kind switch
    {
        TemplateNotApproved => "Template not approved",
        PolicyNotPublished => "Policy not published",
        NoTemplate => "No template",
        ReplacementUnitMissing => "Replacement unit missing",
        AttachmentPathMissing => "Attachment path missing",
        AttachmentUnitUnknown => "Attachment unit unknown",
        WaitingReview => "Waiting for review",
        CardCoverPolicyNotSet => "Equipment-card cover policy not set",
        SerialMasterCoverPolicyNotSet => "Serial-master cover policy not set",
        _ => kind,
    };
}

public static class PlanLogKind
{
    public const string CandidateExcluded = "candidate_excluded";
    public const string EventExcluded = "event_excluded";
    public const string OwnershipChoice = "ownership_choice";
    public const string SourceLimitation = "source_limitation";
}

public sealed class MigrationMapRun : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RunId { get; set; }
    public Guid ReconstructionRunId { get; set; }
    public Guid BatchId { get; set; }
    public DateOnly CutoverDate { get; set; }
    public bool AllowedWaitingReviews { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public required string Status { get; set; }
    public string? FailureCode { get; set; }
    public long UnitCount { get; set; }
    public long EventCount { get; set; }
    public long ExcludedCandidateCount { get; set; }
    public long WaitingCandidateCount { get; set; }
}

public sealed class MigrationPlanUnit : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanUnitId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid CandidateId { get; set; }
    public required string ItemCode { get; set; }
    public string? ItemName { get; set; }
    public required string Origin { get; set; }
    public required string HistoryDepth { get; set; }
    public required string LocationState { get; set; }
    public string? WarehouseCode { get; set; }
    public string? CustodyLocationKind { get; set; }
    public required string ConditionGrade { get; set; }
    public required string ConditionProvenance { get; set; }
    public string? Note { get; set; }
    /// <summary>
    /// The serial-master source key this unit came from, the one identity that survives a new extraction (candidate and
    /// plan ids are new on every run). A delta load (P9-T10b) finds the unit an earlier load wrote through it. Null on
    /// plans mapped before P9-T10b.
    /// </summary>
    public string? SourceKey { get; set; }
    /// <summary>SHA-256 of <see cref="SourceKey"/> as 64 hex characters, the indexable form (a source key can exceed an index key).</summary>
    public string? SourceKeyHash { get; set; }
}

public sealed class MigrationPlanIdentifier : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanIdentifierId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid PlanUnitId { get; set; }
    public required string IdentifierType { get; set; }
    public required string RawValue { get; set; }
    public required string IssuerKind { get; set; }
    public string? SourceDataValue { get; set; }
    public required string Provenance { get; set; }
}

public sealed class MigrationPlanEvent : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanEventId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid PlanUnitId { get; set; }
    public Guid HistoryEventId { get; set; }
    public int Sequence { get; set; }
    public required string MovementKind { get; set; }
    public DateTime EffectiveDate { get; set; }
    public required string DocumentTypeLabel { get; set; }
    public int? DocumentNumber { get; set; }
    public string? PartyCode { get; set; }
    public string? WarehouseCode { get; set; }
    public decimal Quantity { get; set; }
    public required string Provenance { get; set; }
}

public sealed class MigrationPlanOwnership : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanOwnershipId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid PlanUnitId { get; set; }
    public required string Tier { get; set; }
    public string? PartyCode { get; set; }
    public string? PartyName { get; set; }
    public required string PartyReference { get; set; }
    public bool Pseudonymised { get; set; }
    public bool IsCurrent { get; set; }
    public required string EvidenceSource { get; set; }
    public DateTime? EvidenceDate { get; set; }
}

public sealed class MigrationPlanReplacementLink : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanReplacementLinkId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid ReplacedPlanUnitId { get; set; }
    public Guid ReplacementPlanUnitId { get; set; }
    public required string PolicyApplied { get; set; }
    public required string SourceKey { get; set; }
}

public sealed class MigrationPlanEntitlement : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanEntitlementId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid PlanUnitId { get; set; }
    public required string CoverageFamily { get; set; }
    public Guid? PolicyId { get; set; }
    public int? PolicyVersion { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly? TerminationDate { get; set; }
    public int RemainingDays { get; set; }
    public bool Expired { get; set; }
    public required string Direction { get; set; }
    public required string Provenance { get; set; }
    public required string DateSource { get; set; }
    public required string SourceKey { get; set; }
}

public sealed class MigrationPlanWarrantyEvidence : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanWarrantyEvidenceId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid PlanUnitId { get; set; }
    public required string SourceKind { get; set; }
    public required string SourceKey { get; set; }
    public DateOnly? StartDate { get; set; }
    public DateOnly? EndDate { get; set; }
    public DateOnly? TerminationDate { get; set; }
    public required string Direction { get; set; }
    public bool SelectedForEntitlement { get; set; }
}

public sealed class MigrationPlanAttachment : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanAttachmentId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid? PlanUnitId { get; set; }
    public required string SourceKey { get; set; }
    public string? Path { get; set; }
    public required string FileName { get; set; }
    public long? FileSize { get; set; }
    public string? ContentHash { get; set; }
}

public sealed class MigrationPlanCertificate : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanCertificateId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid PlanUnitId { get; set; }
    public int Version { get; set; }
    public required string Provenance { get; set; }
}

public sealed class MigrationPlanGap : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanGapId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid? PlanUnitId { get; set; }
    public required string Kind { get; set; }
    public required string Reason { get; set; }
    public string? SourceKey { get; set; }
}

public sealed class MigrationPlanLog : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PlanLogId { get; set; }
    public Guid MapRunId { get; set; }
    public Guid? PlanUnitId { get; set; }
    public required string Kind { get; set; }
    public string? SourceKey { get; set; }
    public string? RelatedSourceKey { get; set; }
    public required string Note { get; set; }
}
