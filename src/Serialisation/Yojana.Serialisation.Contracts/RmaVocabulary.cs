namespace Yojana.Serialisation.Contracts;

public static class RmaStage
{
    public const string Requested = "requested";
    public const string Authorised = "authorised";
    public const string InTransit = "in_transit";
    public const string Received = "received";
    public const string Dispositioned = "dispositioned";
    public const string Closed = "closed";

    public static readonly IReadOnlyList<string> All = [Requested, Authorised, InTransit, Received, Dispositioned, Closed];
}

public static class RmaDisposition
{
    public const string ReturnToStock = "return_to_stock";
    public const string Refurbish = "refurbish";
    public const string RepairAndReturn = "repair_and_return";
    public const string Scrap = "scrap";
    public const string ReturnToVendor = "return_to_vendor";
    public const string Credit = "credit";
    public const string Refund = "refund";
    public const string Replace = "replace";
    public const string Reject = "reject";
    public const string RestockAsNew = "restock_as_new";

    public static readonly IReadOnlyList<string> All =
    [
        ReturnToStock, Refurbish, RepairAndReturn, Scrap, ReturnToVendor,
        Credit, Refund, Replace, Reject, RestockAsNew,
    ];
}

public static class ReturnOutcome
{
    public const string Accepted = "accepted";
    public const string Mismatch = "mismatch";
    public const string Unauthorised = "unauthorised";
    public const string Counterfeit = "counterfeit";

    public static readonly IReadOnlyList<string> All = [Accepted, Mismatch, Unauthorised, Counterfeit];
}

public static class RestockFeeRule
{
    public const string None = "none";
    public const string Fixed = "fixed";
    public const string Percentage = "percentage";

    public static readonly IReadOnlyList<string> All = [None, Fixed, Percentage];
}

public static class RmaEventType
{
    public const string Requested = "rma.requested";
    public const string Authorised = "rma.authorised";
    public const string Dispatched = "rma.dispatched";
    public const string Received = "rma.received";
    public const string Dispositioned = "rma.dispositioned";
    public const string Closed = "rma.closed";
    public const string KitIncomplete = "unit.kit_incomplete";
}
