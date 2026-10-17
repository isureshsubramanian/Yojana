using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// Indexed projection of correction ledger facts for the correction-volume KPI. The ledger remains the fact; one row
/// represents one correction even when that correction writes events for many units.
/// </summary>
public sealed class CorrectionRecord : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid CorrectionId { get; set; }
    public required string Command { get; set; }
    public required string ReasonCode { get; set; }
    public int RootCauseCategory { get; set; }
    public required string InitiatedVia { get; set; }
    public required string InitiatingRole { get; set; }
    public required string ActorRef { get; set; }
    public DateTime EventTime { get; set; }
    public DateTime RecordedTime { get; set; }
    public Guid? CorrectsCorrectionId { get; set; }
    public int UnitCount { get; set; }
    public string? Note { get; set; }
    public string EvidenceRefs { get; set; } = "[]";
    public string Approvers { get; set; } = "[]";
}

/// <summary>
/// Per-serial correction fact for Identity rule 13. No unit exists yet, so this is the immutable ledger beside the
/// current reservation override; every row carries the same correction metadata as a unit-ledger fact would.
/// TIGHT COUPLING (P3-T11 reservation audit): ReassignAllocation appends this fact and updates the matching
/// SerialAllocationOverride in the same local transaction. Separating them would make the audit disagree with the
/// effective production order after either side failed. Keep both writes in that handler.
/// </summary>
public sealed class SerialAllocationReassignment : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReassignmentId { get; set; }
    public Guid CommandId { get; set; }
    public Guid CorrectionId { get; set; }
    public Guid EventId { get; set; }
    public int Pair { get; set; }
    public required string Mode { get; set; }
    public Guid SerialRangeId { get; set; }
    public required string Scheme { get; set; }
    public long SerialOrdinal { get; set; }
    public required string SerialValue { get; set; }
    public required string PreviousProductionOrderType { get; set; }
    public Guid PreviousProductionOrderId { get; set; }
    public int? PreviousProductionOrderLine { get; set; }
    public required string NewProductionOrderType { get; set; }
    public Guid NewProductionOrderId { get; set; }
    public int? NewProductionOrderLine { get; set; }
    public DateTime EventTime { get; set; }
    public DateTime RecordedTime { get; set; }
    public required string ActorRef { get; set; }
    public Guid LegalEntityId { get; set; }
    public required string Attributes { get; set; }
}

/// <summary>
/// Current per-serial exception to a SerialRange's production order. Rows stay after a serial returns to its original
/// order so the commissioning lookup has one stable, tenant-led seek shape; immutable history is held separately in
/// SerialAllocationReassignment.
/// </summary>
public sealed class SerialAllocationOverride : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string Scheme { get; set; }
    public long SerialOrdinal { get; set; }
    public required string SerialValue { get; set; }
    public Guid SerialRangeId { get; set; }
    public required string ProductionOrderType { get; set; }
    public Guid ProductionOrderId { get; set; }
    public int? ProductionOrderLine { get; set; }
    public Guid LastCorrectionId { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? CommissionedUnitId { get; set; }
}
