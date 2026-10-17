namespace Yojana.Serialisation.Contracts;

public static class RefurbishmentEventType
{
    public const string Opened = "refurbishment.opened";
    public const string OperationAdded = "refurbishment.operation_added";
    public const string OperationCompleted = "refurbishment.operation_completed";
    public const string PartRemoved = "refurbishment.part_removed";
    public const string PartInstalled = "refurbishment.part_installed";
    public const string CostPosted = "refurbishment.cost_posted";
    public const string Released = "refurbishment.released";
    public const string Cancelled = "refurbishment.cancelled";
    public const string ReleaseRecordReissueRequested = "release_record.reissue_requested";
}

public sealed record RefurbishmentReleased(
    PublishedEventHeader Header,
    Guid WorkOrderId,
    string WorkOrderNumber,
    Guid SourceUnitId,
    Guid ReleasedUnitId,
    string Kind,
    string Grade,
    int CycleNumber,
    string IdentityPolicy,
    decimal TotalCost,
    string Currency) : IPublishedUnitEvent
{
    public const string Schema = "Refurbishment.Released";
    public const string Version = "1.0";
}

public sealed record RefurbishmentCostPosted(
    PublishedEventHeader Header,
    Guid WorkOrderId,
    Guid CostId,
    string Kind,
    decimal Amount,
    string Currency,
    string Description) : IPublishedUnitEvent
{
    public const string Schema = "Refurbishment.CostPosted";
    public const string Version = "1.0";
}

public sealed record ReleaseRecordReissueRequested(
    PublishedEventHeader Header,
    Guid WorkOrderId,
    string Reason) : IPublishedUnitEvent
{
    public const string Schema = "ReleaseRecord.ReissueRequested";
    public const string Version = "1.0";
}
