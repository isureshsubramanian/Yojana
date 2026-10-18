using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

public sealed class ReplacementLink : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ReplacementLinkId { get; set; }
    public Guid WarrantyReplacementId { get; set; }
    public Guid? PreviousReplacementLinkId { get; set; }
    public Guid? CorrectedByLinkId { get; set; }
    public Guid? CorrectionCommandId { get; set; }
    public required string Direction { get; set; }
    public required string ReplacementReason { get; set; }
    public required string State { get; set; }
    public required string SourceKind { get; set; }
    public Guid SourceId { get; set; }
    public required string SourceNumber { get; set; }
    public Guid OriginalUnitId { get; set; }
    public Guid ReplacementUnitId { get; set; }
    public Guid OriginalItemId { get; set; }
    public Guid ReplacementItemId { get; set; }
    public Guid SourceEntitlementId { get; set; }
    public Guid? TargetEntitlementId { get; set; }
    public Guid? CounterpartyPartyRef { get; set; }
    public string? PolicyApplied { get; set; }
    public DateOnly? ComputedEnd { get; set; }
    public required string PricePolicy { get; set; }
    public decimal DifferenceAmount { get; set; }
    public required string Currency { get; set; }
    public DateOnly? ReturnDue { get; set; }
    public string? NonReturnPriceBasis { get; set; }
    public decimal? NonReturnAmount { get; set; }
    public DateTime? OverduePublishedAt { get; set; }
    public Guid? OverdueEventId { get; set; }
    public Guid? ReturnDueCommandId { get; set; }
    public Guid? BillCommandId { get; set; }
    public DateOnly? BilledOn { get; set; }
    public string? BilledPriceBasis { get; set; }
    public decimal? BilledAmount { get; set; }
    public DateTime? BilledAt { get; set; }
    public string? BilledBy { get; set; }
    public required string ReplacementConditionAllowed { get; set; }
    public string? ReplacementCondition { get; set; }
    public bool ConsentRefurbished { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime? ShippedAt { get; set; }
    public string? ShippedBy { get; set; }
    public Guid? ShippedEventId { get; set; }
}

public sealed class PartyReplacementHold : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid PartyReplacementHoldId { get; set; }
    public Guid PartyRef { get; set; }
    public required string Reason { get; set; }
    public DateTime SetAt { get; set; }
    public required string SetBy { get; set; }
    public Guid SetCommandId { get; set; }
    public DateTime? LiftedAt { get; set; }
    public string? LiftedBy { get; set; }
    public string? LiftReason { get; set; }
    public Guid? LiftCommandId { get; set; }
}

public sealed class WarrantyReplacement : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid WarrantyReplacementId { get; set; }
    public Guid CommandId { get; set; }
    public required string ReplacementNumber { get; set; }
    public required string SourceKind { get; set; }
    public Guid SourceId { get; set; }
    public required string SourceNumber { get; set; }
    public string? OriginalInvoiceType { get; set; }
    public Guid? OriginalInvoiceId { get; set; }
    public int? OriginalInvoiceLine { get; set; }
    public Guid LegalEntityId { get; set; }
    public required string ServiceCountry { get; set; }
    public bool ConsumerContract { get; set; }
    public required string Status { get; set; }
    public string? ReplacementPolicyCode { get; set; }
    public string? ReturnReasonCode { get; set; }
    public string? RestockingFeeRule { get; set; }
    public decimal RestockingFeeAmount { get; set; }
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public Guid? ShipCommandId { get; set; }
    public DateTime? ShippedAt { get; set; }
    public string? ShippedBy { get; set; }
    public string? StockPostingReference { get; set; }
    public string LastModifiedBy { get; set; } = "";
    public DateTime LastModifiedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class WarrantyReplacementLine : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid LineId { get; set; }
    public Guid WarrantyReplacementId { get; set; }
    public Guid? ReplacementLinkId { get; set; }
    public int Sequence { get; set; }
    public required string Kind { get; set; }
    public Guid? ItemId { get; set; }
    public required string Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitAmount { get; set; }
    public decimal LineAmount { get; set; }
    public required string Currency { get; set; }
    public string? RevenueRule { get; set; }
}
