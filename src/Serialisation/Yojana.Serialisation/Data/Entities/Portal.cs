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
}
