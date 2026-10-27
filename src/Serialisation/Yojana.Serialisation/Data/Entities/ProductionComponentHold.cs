using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>An issued serialised component held against a production order until its physical parent is known.</summary>
public sealed class ProductionComponentHold : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid HoldId { get; set; }
    public Guid UnitId { get; set; }
    public Guid ItemId { get; set; }
    public required string ProductionOrderType { get; set; }
    public Guid ProductionOrderId { get; set; }
    public required string IssueDocumentType { get; set; }
    public Guid IssueDocumentId { get; set; }
    public int IssueLine { get; set; }
    public DateTime IssuedAt { get; set; }
    public Guid? ParentUnitId { get; set; }
    public Guid? AssemblyLinkId { get; set; }
    public string? ReceiptDocumentType { get; set; }
    public Guid? ReceiptDocumentId { get; set; }
    public DateTime? LinkedAt { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime? ReturnedAt { get; set; }
}

/// <summary>Durable assignment and stale-hold attention work; one open row per order and kind.</summary>
public sealed class ProductionHoldTask : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid TaskId { get; set; }
    public required string ProductionOrderType { get; set; }
    public Guid ProductionOrderId { get; set; }
    public required string Kind { get; set; }
    public required string Status { get; set; }
    public DateTime RaisedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? CompletedBy { get; set; }
}

/// <summary>Immutable audit row for each confirmed parent/component scan pairing.</summary>
public sealed class ProductionComponentAssignment : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid AssignmentId { get; set; }
    public Guid CommandId { get; set; }
    public Guid HoldId { get; set; }
    public Guid ParentUnitId { get; set; }
    public Guid ComponentUnitId { get; set; }
    public required string ActorRef { get; set; }
    public DateTime AssignedAt { get; set; }
}

/// <summary>Parent serials received for an order, retained so a later issue still names its receipt source.</summary>
public sealed class ProductionOrderReceipt : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ParentUnitId { get; set; }
    public required string ProductionOrderType { get; set; }
    public Guid ProductionOrderId { get; set; }
    public required string ReceiptDocumentType { get; set; }
    public Guid ReceiptDocumentId { get; set; }
    public DateTime ReceivedAt { get; set; }
}
