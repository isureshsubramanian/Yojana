namespace Yojana.Serialisation.Contracts;

public static class RefurbishmentKind
{
    public const string Refurbish = "refurbish";
    public const string Remanufacture = "remanufacture";
    public const string Rework = "rework";
    public static readonly IReadOnlyList<string> All = [Refurbish, Remanufacture, Rework];
}

public static class ReworkSelectionKind
{
    public const string ProductionOrder = "production_order";
    public const string Item = "item";
    public const string SerialList = "serial_list";
    public static readonly IReadOnlyList<string> All = [ProductionOrder, Item, SerialList];
}

public static class RefurbishmentTestKind
{
    public const string KeyFunctions = "key-functions";
}

public static class RefurbishmentStatus
{
    public const string Open = "open";
    public const string Released = "released";
    public const string Cancelled = "cancelled";
}

public static class RefurbishmentOperationStatus
{
    public const string Open = "open";
    public const string Completed = "completed";
}

public static class RefurbishmentPartAction
{
    public const string Removed = "removed";
    public const string Installed = "installed";
}

public static class RemovedPartDestination
{
    public const string Spares = "spares";
    public const string Scrap = "scrap";
    public const string VendorReturn = "vendor_return";
    public static readonly IReadOnlyList<string> All = [Spares, Scrap, VendorReturn];
}

public static class RefurbishmentCostKind
{
    public const string GradedReceipt = "graded_receipt";
    public const string Part = "part";
    public const string Labour = "labour";
    public const string Other = "other";
    public static readonly IReadOnlyList<string> All = [GradedReceipt, Part, Labour, Other];
}

public static class RefurbishmentIdentity
{
    public const string SameUnitSameSerial = "same_unit_same_serial";
    public const string SameUnitNewSerial = "same_unit_new_serial";
    public const string NewUnit = "new_unit";
    public static readonly IReadOnlyList<string> All = [SameUnitSameSerial, SameUnitNewSerial, NewUnit];
}

public sealed record RefurbishmentItemClassPolicy(string ItemClass, string Identity, int MaxCycles, string? ReplacementPolicyCode = null);

public sealed record RefurbishmentItemCyclePolicy(Guid ItemId, int MaxCycles);

public sealed record RefurbishmentPolicy(
    string DefaultIdentity,
    int DefaultMaxCycles,
    IReadOnlyList<RefurbishmentItemClassPolicy> ItemClasses,
    string? DefaultReplacementPolicyCode = null,
    IReadOnlyList<RefurbishmentItemCyclePolicy>? Items = null);

public sealed record ResolvedRefurbishmentPolicy(string Identity, int MaxCycles, string? ReplacementPolicyCode);

public sealed record OpenRefurbishment(
    Guid CommandId,
    Guid TargetUnitId,
    string Kind,
    DateTime OpenedAt,
    string ActorRef,
    Guid LegalEntityId,
    Guid? RepairLocationId = null,
    Guid? SourceRmaLineId = null,
    DocumentRef? SourceDocument = null);

public sealed record OpenRefurbishmentRequest(
    Guid CommandId,
    Guid TargetUnitId,
    string Kind,
    DateTime OpenedAt,
    Guid LegalEntityId,
    Guid? RepairLocationId = null,
    Guid? SourceRmaLineId = null,
    DocumentRef? SourceDocument = null);

public sealed record OpenRefurbishmentResult(
    Guid WorkOrderId,
    string WorkOrderNumber,
    Guid TargetUnitId,
    string Kind,
    int CycleNumber,
    Guid LifeEpisodeId,
    bool Replayed);

public sealed record OpenRework(
    Guid CommandId,
    IReadOnlyList<Guid> TargetUnitIds,
    string SelectionKind,
    string SelectionReference,
    DateTime OpenedAt,
    string ActorRef,
    Guid LegalEntityId,
    Guid? RepairLocationId = null,
    DocumentRef? SourceDocument = null);

public sealed record OpenReworkRequest(
    Guid CommandId,
    IReadOnlyList<Guid> TargetUnitIds,
    string SelectionKind,
    string SelectionReference,
    DateTime OpenedAt,
    Guid LegalEntityId,
    Guid? RepairLocationId = null,
    DocumentRef? SourceDocument = null);

public sealed record OpenReworkResult(IReadOnlyList<OpenRefurbishmentResult> WorkOrders, bool Replayed);

public sealed record AddRefurbishmentOperation(
    Guid CommandId, Guid WorkOrderId, int Sequence, string Description, string? TechnicianRef,
    decimal PlannedHours, DateTime EventTime, string ActorRef, Guid LegalEntityId);

public sealed record AddRefurbishmentOperationRequest(
    Guid CommandId, int Sequence, string Description, string? TechnicianRef,
    decimal PlannedHours, DateTime EventTime, Guid LegalEntityId);

public sealed record CompleteRefurbishmentOperation(
    Guid CommandId, Guid WorkOrderId, Guid OperationId, decimal ActualHours,
    DateTime CompletedAt, string ActorRef, Guid LegalEntityId);

public sealed record CompleteRefurbishmentOperationRequest(
    Guid CommandId, decimal ActualHours, DateTime CompletedAt, Guid LegalEntityId);

public sealed record RefurbishmentOperationResult(
    Guid WorkOrderId, Guid OperationId, int Sequence, string Status, decimal PlannedHours,
    decimal? ActualHours, string? SignedBy, DateTime? SignedAt, bool Replayed);

public sealed record RemoveRefurbishmentPart(
    Guid CommandId, Guid WorkOrderId, Guid AssemblyLinkId, string Destination,
    DateTime EventTime, string ActorRef, Guid LegalEntityId, Guid? DestinationLocationId = null,
    Guid? VendorPartyRef = null);

public sealed record RemoveRefurbishmentPartRequest(
    Guid CommandId, Guid AssemblyLinkId, string Destination, DateTime EventTime,
    Guid LegalEntityId, Guid? DestinationLocationId = null, Guid? VendorPartyRef = null);

public sealed record InstallRefurbishmentPart(
    Guid CommandId, Guid WorkOrderId, ChildInput Part, decimal UnitCost, string Currency,
    DateTime EventTime, string ActorRef, Guid LegalEntityId, Guid? RepairLocationId = null);

public sealed record InstallRefurbishmentPartRequest(
    Guid CommandId, ChildInput Part, decimal UnitCost, string Currency,
    DateTime EventTime, Guid LegalEntityId, Guid? RepairLocationId = null);

public sealed record RefurbishmentPartResult(
    Guid WorkOrderId, Guid PartId, string Action, Guid? ChildUnitId, Guid? ChildItemId,
    string? Slot, string? Destination, Guid? AssemblyLinkId, Guid EventId, bool Replayed);

public sealed record RecordRefurbishmentLabour(
    Guid CommandId, Guid WorkOrderId, Guid? OperationId, decimal Hours, decimal HourlyRate,
    string Currency, DateTime EventTime, string ActorRef, Guid LegalEntityId);

public sealed record RecordRefurbishmentLabourRequest(
    Guid CommandId, Guid? OperationId, decimal Hours, decimal HourlyRate,
    string Currency, DateTime EventTime, Guid LegalEntityId);

public sealed record RecordRefurbishmentCost(
    Guid CommandId, Guid WorkOrderId, string Kind, decimal Amount, string Currency,
    string Description, DateTime EventTime, string ActorRef, Guid LegalEntityId);

public sealed record RecordRefurbishmentCostRequest(
    Guid CommandId, string Kind, decimal Amount, string Currency,
    string Description, DateTime EventTime, Guid LegalEntityId);

public sealed record RefurbishmentCostResult(
    Guid WorkOrderId, Guid CostId, string Kind, decimal Amount, string Currency,
    Guid EventId, bool Replayed);

public sealed record RefurbishmentLabourResult(
    Guid WorkOrderId, Guid LabourId, decimal Hours, decimal HourlyRate, decimal Amount,
    string Currency, Guid CostId, Guid EventId, bool Replayed);

public sealed record ReleaseRefurbishment(
    Guid CommandId, Guid WorkOrderId, string Grade, string ValuationRule, DateTime ReleasedAt,
    string ActorRef, Guid LegalEntityId, Guid WarehouseLocationId, string? NewSerial = null,
    string? FCode = null, string? CCode = null);

public sealed record ReleaseRefurbishmentRequest(
    Guid CommandId, string Grade, string ValuationRule, DateTime ReleasedAt,
    Guid LegalEntityId, Guid WarehouseLocationId, string? NewSerial = null,
    string? FCode = null, string? CCode = null);

public sealed record ReleaseRefurbishmentResult(
    Guid WorkOrderId, string WorkOrderNumber, Guid SourceUnitId, Guid ReleasedUnitId,
    string IdentityPolicy, string Grade, int CycleNumber, decimal TotalCost,
    string Currency, Guid EventId, bool Replayed);

public sealed record CancelRefurbishment(
    Guid CommandId, Guid WorkOrderId, string Reason, DateTime CancelledAt,
    string ActorRef, Guid LegalEntityId, Guid WarehouseLocationId);

public sealed record CancelRefurbishmentRequest(
    Guid CommandId, string Reason, DateTime CancelledAt,
    Guid LegalEntityId, Guid WarehouseLocationId);

public sealed record CancelRefurbishmentResult(Guid WorkOrderId, string Status, Guid EventId, bool Replayed);

public sealed record RefurbishmentOperationRow(
    Guid OperationId, int Sequence, string Description, string? TechnicianRef,
    decimal PlannedHours, decimal? ActualHours, string Status, string? SignedBy, DateTime? SignedAt);

public sealed record RefurbishmentPartRow(
    Guid PartId, string Action, Guid? ChildUnitId, Guid? ChildItemId, string? Slot,
    string? Destination, Guid? VendorPartyRef, Guid? AssemblyLinkId, decimal? UnitCost, string? Currency, DateTime RecordedAt);

public sealed record RefurbishmentLabourRow(
    Guid LabourId, Guid? OperationId, decimal Hours, decimal HourlyRate, decimal Amount,
    string Currency, string TechnicianRef, DateTime RecordedAt);

public sealed record RefurbishmentCostRow(
    Guid CostId, string Kind, decimal Amount, string Currency, string Description,
    DateTime PostedAt, Guid EventId);

public sealed record RefurbishmentReleaseCheck(string Key, string Label, string Detail, bool Passed, bool Required);

public sealed record RefurbishmentWorkOrderRow(
    Guid WorkOrderId, string WorkOrderNumber, string Kind, Guid TargetUnitId, Guid ItemId,
    Guid? SourceRmaLineId, string Status, int CycleNumber, string IdentityPolicy,
    int MaxCycles, DateTime OpenedAt, string OpenedBy, DateTime? ReleasedAt,
    string? ReleasedBy, Guid? ReleasedUnitId, string? Grade, decimal TotalCost,
    string? Currency, IReadOnlyList<RefurbishmentOperationRow> Operations,
    IReadOnlyList<RefurbishmentPartRow> Parts, IReadOnlyList<RefurbishmentLabourRow> Labour,
    IReadOnlyList<RefurbishmentCostRow> Costs, string? TargetSerial, string? ReleasedSerial,
    Guid? CurrentLocationId, IReadOnlyList<RefurbishmentReleaseCheck> ReleaseChecks);

public sealed record RefurbishmentWorkOrderPage(IReadOnlyList<RefurbishmentWorkOrderRow> Items, int Total, int Skip, int Take);
