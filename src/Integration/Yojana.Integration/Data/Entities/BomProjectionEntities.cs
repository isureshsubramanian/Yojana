using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class BomProjection : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid BomProjectionId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid ParentItemId { get; set; }
    public required string ParentItemExternalId { get; set; }
    public required string ContentHash { get; set; }
    public DateTimeOffset SourceChangedAt { get; set; }
    public bool Deleted { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class BomProjectionLine : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid BomProjectionId { get; set; }
    public int LineNo { get; set; }
    public required string Kind { get; set; }
    public Guid? ItemId { get; set; }
    public string? ItemExternalId { get; set; }
    public required string Description { get; set; }
    public decimal Quantity { get; set; }
    public string? Uom { get; set; }
}
