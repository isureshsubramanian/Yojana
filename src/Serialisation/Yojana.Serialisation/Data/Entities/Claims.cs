using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

// MOD-07 claim half (P4-T02). Measurements and selected entitlement ids are recorded as JSON because adjudication reads
// each value with its owning claim or line and never filters inside either document.

public sealed class Claim : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid CommandId { get; set; }
    public Guid UnitId { get; set; }
    public DateOnly ClaimDate { get; set; }
    public Guid ClaimantPartyRef { get; set; }
    public Guid SettlementPartyRef { get; set; }
    public Guid ServiceEntityId { get; set; }
    public Guid ObligorEntityId { get; set; }
    public required string ServiceCountry { get; set; }
    public required string Currency { get; set; }
    public required string CoverageSource { get; set; }
    public required string Remedy { get; set; }
    public required string Status { get; set; }
    public bool RecoveryRaised { get; set; }
    public string EntitlementIds { get; set; } = "[]";
    public string? DenialReason { get; set; }
    public Guid? SuccessorItemId { get; set; }
    public int ChainLength { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? AdjudicatedAt { get; set; }
    public DateTime? SettledAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public required string SubmittedBy { get; set; }
    public Guid LastEventId { get; set; }
}

public sealed class ClaimLine : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ClaimLineId { get; set; }
    public Guid ClaimId { get; set; }
    public Guid UnitId { get; set; }
    public int Sequence { get; set; }
    public required string Kind { get; set; }
    public required string CoverageFamily { get; set; }
    public string? Description { get; set; }
    public decimal RequestedAmount { get; set; }
    public decimal CoveragePct { get; set; }
    public decimal ApprovedAmount { get; set; }
    public string Measurements { get; set; } = "{}";
    public Guid? EntitlementId { get; set; }
    public Guid? PolicyVersionId { get; set; }
    public Guid? AccrualCohortId { get; set; }
    public required string CoverageSource { get; set; }
    public string? SettlementFormula { get; set; }
    public string? DenialReason { get; set; }
    public int ChainLength { get; set; }
}

public sealed class ClaimEvidence : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid EvidenceId { get; set; }
    public Guid ClaimId { get; set; }
    public required string Kind { get; set; }
    public required string Reference { get; set; }
    public string? Description { get; set; }
    public DateTime RecordedAt { get; set; }
    public required string ActorRef { get; set; }
}

public sealed class RecoveryClaim : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RecoveryClaimId { get; set; }
    public Guid CustomerClaimId { get; set; }
    public Guid ClaimLineId { get; set; }
    public Guid InboundEntitlementId { get; set; }
    public Guid ChildUnitId { get; set; }
    public Guid VendorPartyRef { get; set; }
    public decimal RequestedAmount { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public required string Currency { get; set; }
    public required string Status { get; set; }
    public DateTime RaisedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public Guid RaisedEventId { get; set; }
    public Guid? ApprovedEventId { get; set; }
}
