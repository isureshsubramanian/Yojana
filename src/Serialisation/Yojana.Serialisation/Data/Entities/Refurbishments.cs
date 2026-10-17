using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

public sealed class RefurbishmentWorkOrder : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid OpenCommandId { get; set; }
    public required string WorkOrderNumber { get; set; }
    public required string Kind { get; set; }
    public Guid TargetUnitId { get; set; }
    public Guid ItemId { get; set; }
    public Guid? SourceRmaLineId { get; set; }
    public string? SourceDocumentType { get; set; }
    public Guid? SourceDocumentId { get; set; }
    public int? SourceDocumentLine { get; set; }
    public string? SelectionKind { get; set; }
    public string? SelectionReference { get; set; }
    public required string Status { get; set; }
    public int CycleNumber { get; set; }
    public required string IdentityPolicy { get; set; }
    public int MaxCycles { get; set; }
    public string? ReplacementPolicyCode { get; set; }
    public Guid LifeEpisodeId { get; set; }
    public bool AsBuiltChanged { get; set; }
    public DateTime OpenedAt { get; set; }
    public required string OpenedBy { get; set; }
    public Guid OpeningEventId { get; set; }
    public Guid? ReleaseCommandId { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public string? ReleasedBy { get; set; }
    public Guid? ReleasedEventId { get; set; }
    public Guid? ReleasedUnitId { get; set; }
    public string? Grade { get; set; }
    public Guid? CancelCommandId { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }
    public string? CancellationReason { get; set; }
    public Guid? CancelledEventId { get; set; }
}

public sealed class RefurbishmentOperation : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid OperationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid CommandId { get; set; }
    public int Sequence { get; set; }
    public required string Description { get; set; }
    public string? TechnicianRef { get; set; }
    public decimal PlannedHours { get; set; }
    public decimal? ActualHours { get; set; }
    public required string Status { get; set; }
    public Guid? CompleteCommandId { get; set; }
    public string? SignedBy { get; set; }
    public DateTime? SignedAt { get; set; }
    public Guid AddedEventId { get; set; }
    public Guid? CompletedEventId { get; set; }
}

public sealed class RefurbishmentPart : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PartId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid CommandId { get; set; }
    public required string Action { get; set; }
    public Guid? ChildUnitId { get; set; }
    public Guid? ChildItemId { get; set; }
    public Guid? ChildLotId { get; set; }
    public decimal? Quantity { get; set; }
    public string? Slot { get; set; }
    public string? Destination { get; set; }
    public Guid? DestinationLocationId { get; set; }
    public Guid? VendorPartyRef { get; set; }
    public Guid? AssemblyLinkId { get; set; }
    public decimal? UnitCost { get; set; }
    public string? Currency { get; set; }
    public DateTime RecordedAt { get; set; }
    public Guid EventId { get; set; }
}

public sealed class RefurbishmentLabour : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid LabourId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid CommandId { get; set; }
    public Guid? OperationId { get; set; }
    public decimal Hours { get; set; }
    public decimal HourlyRate { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string TechnicianRef { get; set; }
    public DateTime RecordedAt { get; set; }
    public Guid EventId { get; set; }
    public Guid CostId { get; set; }
}

public sealed class RefurbishmentCost : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid CostId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid CommandId { get; set; }
    public required string Kind { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string Description { get; set; }
    public DateTime PostedAt { get; set; }
    public required string PostedBy { get; set; }
    public Guid EventId { get; set; }
}
