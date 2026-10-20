using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

// MOD-07 claim half (P4-T02). Measurements and selected entitlement ids are recorded as JSON because adjudication reads
// each value with its owning claim or line and never filters inside either document.

public sealed class Claim : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ClaimId { get; set; }
    /// <summary>Human claim number, CLM-yyyy-nnnnn, allocated from <see cref="NumberSeries"/> at submission.</summary>
    public required string ClaimNumber { get; set; }
    public Guid CommandId { get; set; }
    public Guid UnitId { get; set; }
    public Guid? CampaignId { get; set; }
    public DateOnly ClaimDate { get; set; }
    public string? ReportedCauseCode { get; set; }
    public string? ReportedDescription { get; set; }
    public DateOnly? OccurredDate { get; set; }
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

    /// <summary>Why an approved claim raised no recovery under the policy at approval (<c>RecoveryNotRaisedReason</c>); null when recovered or not decided.</summary>
    public string? RecoveryNotRaisedReason { get; set; }
    /// <summary>The campaign reserve captured when adjudication resolves the named campaign.</summary>
    public string? ReserveTag { get; set; }
    public string? CampaignCoverReason { get; set; }
    public string EntitlementIds { get; set; } = "[]";
    public string? DenialReason { get; set; }
    public Guid? SuccessorItemId { get; set; }
    public int ChainLength { get; set; }
    public DateTime SubmittedAt { get; set; }
    public DateTime? AdjudicatedAt { get; set; }
    public string? AdjudicatedBy { get; set; }
    public string? ReviewerRole { get; set; }
    public DateTime? SettledAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public required string SubmittedBy { get; set; }
    public Guid LastEventId { get; set; }
    public string LastModifiedBy { get; set; } = "";
    public DateTime LastModifiedAt { get; set; }
    /// <summary>Persisted sort key for list paging: active work first, settled and closed work second.</summary>
    public int ListOrderBucket { get; private set; }
    public byte[] RowVersion { get; set; } = [];
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
    public DateOnly? WaitingPeriodPurchaseDate { get; set; }
    public DateOnly? FirstClaimableDate { get; set; }
    public string? WaitingPeriodNotAppliedReason { get; set; }
    public int ChainLength { get; set; }
    public Guid? CoverageDecisionRef { get; set; }
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
    /// <summary>Human recovery number, RCV-yyyy-nnnn, allocated from <see cref="NumberSeries"/> when raised.</summary>
    public required string RecoveryNumber { get; set; }
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
    public string? VendorReference { get; set; }
    public string? VendorNote { get; set; }
    public Guid RaisedEventId { get; set; }
    public Guid? ApprovedEventId { get; set; }
    public string LastModifiedBy { get; set; } = "";
    public DateTime LastModifiedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// The last number issued in one tenant's document series for one year. Claims and recovery claims draw from it today;
/// later documents (RMA, certificates) add their own series codes rather than their own counters.
/// </summary>
public sealed class NumberSeries : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string Series { get; set; }
    public int Year { get; set; }
    public int LastValue { get; set; }
}

/// <summary>The allocation mode selected for one tenant document series. A missing row means <c>sequence</c>.</summary>
public sealed class NumberSeriesSetting : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string Series { get; set; }
    public required string Mode { get; set; }
    public Guid LastCommandId { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
}
