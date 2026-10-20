using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

public sealed class PolicyCoverItemRow : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PolicyVersionId { get; set; }
    public required string Code { get; set; }
    public required string Type { get; set; }
    public required string CustomerWording { get; set; }
    public string? InternalNote { get; set; }
    public string? Category { get; set; }
    // Persisted scope uses the same snake-case contract as PolicyCoverItems.Read and the legacy backfill.
    public string PartsScope { get; set; } = "{\"part_categories\":[],\"item_families\":[]}";
}
public sealed class PolicyCoverItemCause : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PolicyVersionId { get; set; }
    public required string ItemCode { get; set; }
    public required string CauseCode { get; set; }
}
public sealed class ClaimCauseRow : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string Code { get; set; }
    public required string CustomerWording { get; set; }
    public bool Active { get; set; } = true;
    public string UpdatedBy { get; set; } = "";
    public DateTime UpdatedAt { get; set; }
}
