namespace Yojana.Serialisation.Contracts;

public sealed record ReplacementShipped(
    PublishedEventHeader Header,
    Guid WarrantyReplacementId,
    string ReplacementNumber,
    Guid ReplacementLinkId,
    Guid OriginalUnitId,
    Guid ReplacementUnitId,
    Guid OriginalItemId,
    Guid ReplacementItemId,
    Guid SourceEntitlementId,
    Guid TargetEntitlementId,
    string SourceKind,
    Guid SourceId,
    string SourceNumber,
    string Reason,
    string PricePolicy,
    decimal DifferenceAmount,
    string Currency,
    string PolicyApplied,
    DateOnly ComputedEnd,
    string ReplacementCondition,
    bool AccrualSuppressed,
    decimal RestockingFeeAmount) : IPublishedUnitEvent
{
    public const string Schema = "Replacement.Shipped";
    public const string Version = "1.0";
}

/// <summary>P6 consumes this recorded request; P5 neither chooses a template nor reports a print result.</summary>
public sealed record ReplacementAssetTagPrintRequested(
    PublishedEventHeader Header,
    Guid WarrantyReplacementId,
    Guid ReplacementLinkId,
    Guid IdentifierId,
    string AssetTag) : IPublishedUnitEvent
{
    public const string Schema = "Replacement.AssetTagPrintRequested";
    public const string Version = "1.0";
}

public sealed record ReplacementReturnOverdue(
    PublishedEventHeader Header,
    Guid ReplacementLinkId,
    Guid OriginalUnitId,
    Guid ReplacementUnitId,
    Guid PartyRef,
    DateOnly ReturnDue,
    string NonReturnPriceBasis,
    int DaysOverdue) : IPublishedUnitEvent
{
    public const string Schema = "Replacement.ReturnOverdue";
    public const string Version = "1.0";
}

