using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>Tenant-owned field classification. The public-enabled switch remains a platform tenant setting.</summary>
public sealed class DisclosurePolicy : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string Field { get; set; }
    public required string Tier { get; set; }
    public bool Enabled { get; set; }
    public DateTime UpdatedAt { get; set; }
    public required string UpdatedBy { get; set; }
}

/// <summary>
/// Customer-supplied ownership evidence for P7-T11. It never grants access: portal reads authorise exclusively through
/// custody ownership episodes, even after this row is matched to a unit.
/// </summary>
public sealed class CustomerAssertion : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid AssertionId { get; set; }
    public Guid CommandId { get; set; }
    public Guid PartyRef { get; set; }
    public Guid? UnitId { get; set; }
    public required string IdentifierType { get; set; }
    public required string RawIdentifier { get; set; }
    public required string NormalisedIdentifier { get; set; }
    public string? CustomerPoRef { get; set; }
    public string? CustomerItemRef { get; set; }
    public required string Status { get; set; }
    public string Source { get; set; } = "partner_report";
    public string Provenance { get; set; } = "customer_asserted";
    public string? EvidenceRef { get; set; }
    public DateTime AssertedAt { get; set; }
    public required string AssertedBy { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public string? VerifiedBy { get; set; }
    public DateTime? EvidenceDate { get; set; }
    public Guid? OwnershipEpisodeId { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? RejectedBy { get; set; }
}

/// <summary>A durable snapshot of one reconciliation so review, paging and export never reclassify changing ownership.</summary>
public sealed class CustomerReconciliation : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReconciliationId { get; set; }
    public Guid PartyRef { get; set; }
    public required string SourceKind { get; set; }
    public Guid? SourceReferenceId { get; set; }
    public int TotalCount { get; set; }
    public int OwnedCount { get; set; }
    public int OtherOwnerCount { get; set; }
    public int NotFoundCount { get; set; }
    public int InvalidCount { get; set; }
    public DateTime ReconciledAt { get; set; }
    public required string ReconciledBy { get; set; }
}

public sealed class CustomerReconciliationRow : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReconciliationId { get; set; }
    public int Ordinal { get; set; }
    public required string RawIdentifier { get; set; }
    public string? RequestedIdentifierType { get; set; }
    public string? MatchedIdentifierType { get; set; }
    public string? NormalisedIdentifier { get; set; }
    public required string Outcome { get; set; }
    public required string ValidationReasonsJson { get; set; }
    public Guid? UnitId { get; set; }
    public Guid? OtherOwnerPartyRef { get; set; }
    public Guid? AssertionId { get; set; }
    public string? CustomerPoRef { get; set; }
    public string? CustomerItemRef { get; set; }
}

/// <summary>Customer registration evidence and the optional back-office decision that applies it.</summary>
public sealed class PortalRegistration : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RegistrationId { get; set; }
    public Guid CommandId { get; set; }
    public Guid UnitId { get; set; }
    public Guid EntitlementId { get; set; }
    public Guid PartyRef { get; set; }
    public required string EvidenceKind { get; set; }
    public DateTime DeliveryAt { get; set; }
    public DateTime EvidenceAt { get; set; }
    public string? EvidenceReference { get; set; }
    public string? AttachmentFileName { get; set; }
    public string? AttachmentContentType { get; set; }
    public byte[]? AttachmentContent { get; set; }
    public int GraceDays { get; set; }
    public required string Status { get; set; }
    public DateTime SubmittedAt { get; set; }
    public required string SubmittedBy { get; set; }
    public Guid? ApprovalCommandId { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public Guid? RebaseEventId { get; set; }
}
