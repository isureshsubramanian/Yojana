namespace Yojana.Serialisation.Contracts;

public static class ReplacementSourceKind
{
    public const string Claim = "claim";
    public const string Rma = "rma";
    public const string Loan = "loan";
    public static readonly IReadOnlyList<string> All = [Claim, Rma, Loan];
}

public static class ReplacementDirection
{
    public const string CustomerFacing = "customer_facing";
    public const string VendorFacing = "vendor_facing";
    public static readonly IReadOnlyList<string> All = [CustomerFacing, VendorFacing];
}

public static class ReplacementReason
{
    public const string WarrantyDefect = "warranty_defect";
    public const string DeadOnArrival = "dead_on_arrival";
    public const string SellerError = "seller_error";
    public const string Goodwill = "goodwill";
    public const string Recall = "recall";
    public static readonly IReadOnlyList<string> All = [WarrantyDefect, DeadOnArrival, SellerError, Goodwill, Recall];
}

public static class ReplacementLinkState
{
    public const string Proposed = "proposed";
    public const string AwaitingReturn = "awaiting_return";
    public const string ClosedReturned = "closed_returned";
    public const string ClosedBilled = "closed_billed";
    public const string Cancelled = "cancelled";
    public const string Corrected = "corrected";
    public static readonly IReadOnlyList<string> All = [Proposed, AwaitingReturn, ClosedReturned, ClosedBilled, Cancelled, Corrected];
}

public static class WarrantyReplacementStatus
{
    public const string Proposed = "proposed";
    public const string Shipped = "shipped";
    public const string Cancelled = "cancelled";
    public static readonly IReadOnlyList<string> All = [Proposed, Shipped, Cancelled];
}

public static class ReplacementPricePolicy
{
    public const string Zero = "zero";
    public const string Difference = "difference";
    public const string Full = "full";
    public static readonly IReadOnlyList<string> All = [Zero, Difference, Full];
}

public static class ReplacementConditionAllowed
{
    public const string New = "new";
    public const string NewOrRefurbished = "new_or_refurbished";
    public const string Any = "any";
    public static readonly IReadOnlyList<string> All = [New, NewOrRefurbished, Any];
}

public static class NonReturnPriceBasis
{
    public const string List = "list";
    public const string FairMarketValue = "fmv";
    public const string Cost = "cost";
    public static readonly IReadOnlyList<string> All = [List, FairMarketValue, Cost];
}

public static class WarrantyReplacementLineKind
{
    public const string Replacement = "replacement";
    public const string ServiceRevenue = "service_revenue";
}

/// <summary>Creates the document and its proposed link. Unit, item, sale and source-number facts are resolved server-side.</summary>
public sealed record CreateWarrantyReplacement(
    Guid CommandId,
    string SourceKind,
    Guid SourceId,
    Guid SourceEntitlementId,
    Guid ReplacementUnitId,
    string ReplacementReason,
    string PricePolicy,
    decimal DifferenceAmount,
    string Currency,
    string ServiceCountry,
    bool ConsumerContract,
    bool ConsentRefurbished,
    DateTime EventTime,
    Guid LegalEntityId,
    string ActorRef = "",
    string ReplacementConditionAllowed = Yojana.Serialisation.Contracts.ReplacementConditionAllowed.Any,
    string? ReplacementPolicyCode = null,
    DateOnly? ReturnDue = null,
    string? NonReturnPriceBasis = null,
    string? ReturnReasonCode = null,
    decimal RestockingFeeBasis = 0m,
    decimal? NonReturnAmount = null);

public sealed record CreateWarrantyReplacementResult(WarrantyReplacementDetail Document, bool Replayed);

/// <summary>Ships a proposed replacement. The server reads condition and actor; neither can be asserted by the browser.</summary>
public sealed record ShipWarrantyReplacement(
    Guid CommandId,
    Guid WarrantyReplacementId,
    DateOnly ReplacementDate,
    DateTime EventTime,
    string ActorRef = "");

public sealed record ShipWarrantyReplacementResult(
    Guid WarrantyReplacementId,
    string ReplacementNumber,
    Guid ReplacementLinkId,
    string LinkState,
    Guid TargetEntitlementId,
    string PolicyApplied,
    DateOnly ComputedEnd,
    string ReplacementCondition,
    string StockPostingReference,
    bool AssetTagRepointed,
    bool AccrualSuppressed,
    Guid EventId,
    bool Replayed);

public sealed record WarrantyReplacementLineRow(
    Guid LineId,
    int Sequence,
    string Kind,
    Guid? ItemId,
    string Description,
    decimal Quantity,
    decimal UnitAmount,
    decimal LineAmount,
    string Currency,
    string? RevenueRule);

public sealed record WarrantyReplacementRow(
    Guid WarrantyReplacementId,
    string ReplacementNumber,
    string SourceKind,
    Guid SourceId,
    string SourceNumber,
    Guid OriginalUnitId,
    Guid ReplacementUnitId,
    Guid OriginalItemId,
    Guid ReplacementItemId,
    string ReplacementReason,
    string State,
    string Status,
    string PricePolicy,
    decimal DifferenceAmount,
    string Currency,
    DateOnly? ReturnDue,
    string? NonReturnPriceBasis,
    string ReplacementConditionAllowed,
    string? ReplacementCondition,
    bool ConsentRefurbished,
    string? PolicyApplied,
    DateOnly? ComputedEnd,
    decimal RestockingFeeAmount,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime? ShippedAt,
    string? ShippedBy);

public sealed record WarrantyReplacementDetail(
    WarrantyReplacementRow Document,
    Guid SourceEntitlementId,
    Guid? TargetEntitlementId,
    Guid ReplacementLinkId,
    Guid? PreviousReplacementLinkId,
    DocumentRef? OriginalInvoice,
    string? ReturnReasonCode,
    string? RestockingFeeRule,
    string? StockPostingReference,
    IReadOnlyList<WarrantyReplacementLineRow> Lines,
    WarrantyReplacementUnitView OriginalUnit,
    WarrantyReplacementUnitView ReplacementUnit,
    Guid? OwnerPartyRef,
    string? CustomerAssetTag,
    string ReplacementPolicyCode,
    int ChainLength,
    WarrantyReplacementNonReturnCharge? NonReturnCharge,
    IReadOnlyList<WarrantyReplacementCheck> Checks,
    IReadOnlyList<WarrantyReplacementTransferRow> Transfers);

public sealed record WarrantyReplacementNonReturnCharge(
    string State,
    string PriceBasis,
    decimal Amount,
    DateOnly? BilledOn);

public sealed record WarrantyReplacementUnitView(
    Guid UnitId,
    string? Serial,
    Guid ItemId,
    string ConditionGrade,
    string LifecycleStatus,
    string? LocationKind,
    Guid? LocationId);

public sealed record WarrantyReplacementCheck(string Code, string Label, string Detail, string Outcome, bool Passed);

public sealed record WarrantyReplacementTransferRow(
    Guid? EntitlementId,
    string CoverageFamily,
    string EntitlementType,
    Guid UnitId,
    string? UnitSerial,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Effect,
    string Detail);

public sealed record WarrantyReplacementPage(IReadOnlyList<WarrantyReplacementRow> Rows, int Total, int Skip, int Take);

public sealed record PendingWarrantyReplacementRow(
    Guid RmaId,
    Guid RmaLineId,
    string RmaNumber,
    Guid OriginalUnitId,
    Guid SourceEntitlementId,
    string CoverageFamily,
    DateTime DispositionedAt);

public sealed record PendingWarrantyReplacementPage(IReadOnlyList<PendingWarrantyReplacementRow> Rows, int Total, int Skip, int Take);

public sealed record ExtendReturnDue(
    Guid CommandId,
    Guid ReplacementLinkId,
    DateOnly NewReturnDue,
    string Reason,
    DateTime EventTime,
    string ActorRef = "");

public sealed record ExtendReturnDueResult(Guid ReplacementLinkId, DateOnly PreviousReturnDue, DateOnly NewReturnDue, Guid EventId, bool Replayed);

public sealed record BillNonReturn(
    Guid CommandId,
    Guid ReplacementLinkId,
    string PriceBasis,
    decimal Amount,
    DateOnly BillingDate,
    DateTime EventTime,
    string ActorRef = "",
    string Reason = "");

public sealed record BillNonReturnResult(Guid ReplacementLinkId, string State, string PriceBasis, decimal Amount, string Currency, Guid EventId, bool Replayed);

public sealed record LiftReplacementHold(
    Guid CommandId,
    Guid PartyRef,
    string Reason,
    DateTime EventTime,
    string ActorRef = "");

public sealed record LiftReplacementHoldResult(Guid HoldId, Guid PartyRef, DateTime LiftedAt, bool Replayed);

public sealed record ConvertLoanToReplacement(
    Guid CommandId,
    DocumentRef LoanerIssue,
    Guid OriginalUnitId,
    Guid SourceEntitlementId,
    Guid LoanerUnitId,
    string ReplacementReason,
    DateOnly ReplacementDate,
    DateTime EventTime,
    Guid LegalEntityId,
    string ServiceCountry,
    bool ConsumerContract,
    bool ConsentRefurbished,
    string ReplacementConditionAllowed = Yojana.Serialisation.Contracts.ReplacementConditionAllowed.Any,
    string? ReplacementPolicyCode = null,
    string ActorRef = "");

public sealed record ConvertLoanToReplacementResult(
    Guid WarrantyReplacementId,
    string ReplacementNumber,
    Guid ReplacementLinkId,
    Guid TargetEntitlementId,
    string PolicyApplied,
    DateOnly ComputedEnd,
    Guid EventId,
    bool Replayed);

public sealed record ReceiveVendorReplacement(
    Guid CommandId,
    Guid RmaLineId,
    Guid SourceEntitlementId,
    Guid ReplacementUnitId,
    Guid ReceivingLocationId,
    DateOnly ReplacementDate,
    DateTime ReceivedAt,
    Guid LegalEntityId,
    string ReplacementReason = Yojana.Serialisation.Contracts.ReplacementReason.WarrantyDefect,
    string ReplacementConditionAllowed = Yojana.Serialisation.Contracts.ReplacementConditionAllowed.Any,
    string? ReplacementPolicyCode = null,
    string ActorRef = "");

public sealed record ReceiveVendorReplacementResult(
    Guid WarrantyReplacementId,
    string ReplacementNumber,
    Guid ReplacementLinkId,
    Guid TargetEntitlementId,
    string PolicyApplied,
    DateOnly ComputedEnd,
    Guid EventId,
    bool Replayed);

public sealed record ReplacementLinkKpis(int Awaiting, int Overdue, int DueInSevenDays, int CustomersOnHold);

public sealed record ReplacementLinkRow(
    Guid ReplacementLinkId,
    Guid WarrantyReplacementId,
    string ReplacementNumber,
    string Direction,
    string State,
    Guid OriginalUnitId,
    Guid ReplacementUnitId,
    Guid? CounterpartyPartyRef,
    DateOnly? ReturnDue,
    int? DaysOverdue,
    string? NonReturnPriceBasis,
    decimal? NonReturnAmount,
    string Currency,
    DateTime? OverduePublishedAt,
    bool PartyOnHold,
    string? OriginalSerial,
    string? ReplacementSerial,
    Guid OriginalItemId,
    Guid ReplacementItemId,
    string SourceNumber,
    string ReplacementReason,
    DateTime? ShippedAt);

public sealed record ReplacementLinkPage(IReadOnlyList<ReplacementLinkRow> Rows, ReplacementLinkKpis Kpis, int Total, int Skip, int Take);

