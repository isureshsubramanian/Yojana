using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

public sealed class Rma : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RmaId { get; set; }
    public Guid CommandId { get; set; }
    public string? RmaNumber { get; set; }
    public required string Stage { get; set; }
    public DateOnly RequestDate { get; set; }
    public Guid ReturningPartyRef { get; set; }
    public Guid SettlementPartyRef { get; set; }
    public Guid ServiceEntityId { get; set; }
    public Guid ObligorEntityId { get; set; }
    public required string ServiceCountry { get; set; }
    public required string Currency { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? AuthorisedAt { get; set; }
    public DateTime? InTransitAt { get; set; }
    public DateTime? ReceivedAt { get; set; }
    public DateTime? DispositionedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public required string RequestedBy { get; set; }
    public string? LastActorRef { get; set; }
    public Guid LastEventId { get; set; }
}

public sealed class RmaLine : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RmaLineId { get; set; }
    public Guid RmaId { get; set; }
    public int Sequence { get; set; }
    public Guid RequestedUnitId { get; set; }
    public Guid? AuthorisedUnitId { get; set; }
    public Guid OwnershipEpisodeId { get; set; }
    public required string OriginalSaleDocumentType { get; set; }
    public Guid OriginalSaleDocumentId { get; set; }
    public int? OriginalSaleDocumentLine { get; set; }
    public Guid? ParentUnitId { get; set; }
    public decimal? PriceAllocation { get; set; }
    public bool ParentIncomplete { get; set; }
    public required string CoverageFamily { get; set; }
    public required string ClaimLineKind { get; set; }
    public decimal RequestedAmount { get; set; }
    public string? Description { get; set; }
    public decimal? CoveragePct { get; set; }
    public decimal? ApprovedAmount { get; set; }
    public string? CoverageSource { get; set; }
    public Guid? EntitlementId { get; set; }
    public Guid? PolicyVersionId { get; set; }
    public string? DenialReason { get; set; }
    public Guid? ReceivedUnitId { get; set; }
    public Guid? ProvisionalUnitId { get; set; }
    public string? ReceiptOutcome { get; set; }
    public string? ConditionGrade { get; set; }
    public string? ValuationRule { get; set; }
    public string? Disposition { get; set; }
    public Guid? ReplacementIntentId { get; set; }
    public string? EntitlementClosedReason { get; set; }
}

public sealed class ReturnReceipt : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReturnReceiptId { get; set; }
    public Guid CommandId { get; set; }
    public Guid RmaId { get; set; }
    public Guid RmaLineId { get; set; }
    public Guid AuthorisedUnitId { get; set; }
    public Guid? ReceivedUnitId { get; set; }
    public Guid? ProvisionalUnitId { get; set; }
    public required string Outcome { get; set; }
    public required string ConditionGrade { get; set; }
    public required string ValuationRule { get; set; }
    public string? ExceptionNote { get; set; }
    public Guid ReceivingLocationId { get; set; }
    public DateTime BusinessTime { get; set; }
    public required string PostingPeriod { get; set; }
    public DateTime RecordedAt { get; set; }
    public required string ReceivedBy { get; set; }
    public Guid EventId { get; set; }
}

public sealed class ReturnReason : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string RestockFeeRule { get; set; }
    public decimal? RestockFeeValue { get; set; }
    public int? DoaWindowDays { get; set; }
    public bool Active { get; set; }
    public DateTime CreatedAt { get; set; }
}
