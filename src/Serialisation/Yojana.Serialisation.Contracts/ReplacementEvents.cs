namespace Yojana.Serialisation.Contracts;

public sealed record ReplacementReturned(
    PublishedEventHeader Header,
    Guid WarrantyReplacementId,
    Guid ReplacementLinkId,
    Guid OriginalUnitId,
    Guid ReplacementUnitId,
    Guid RmaId,
    string ReplacementNumber,
    string Status) : IPublishedUnitEvent
{
    public const string Schema = "Replacement.Returned";
    public const string Version = "1.0";
}

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
    decimal RestockingFeeAmount,
    Guid? CustomerPartyRef = null) : IPublishedUnitEvent
{
    public const string Schema = "Replacement.Shipped";
    public const string Version = "1.1";
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
    int DaysOverdue,
    decimal? NonReturnAmount = null,
    string? Currency = null) : IPublishedUnitEvent
{
    public const string Schema = "Replacement.ReturnOverdue";
    public const string Version = "1.1";
}

public sealed record ReplacementLinkCorrected(
    PublishedEventHeader Header,
    PublishedCorrection Correction,
    Guid WarrantyReplacementId,
    Guid PreviousReplacementLinkId,
    Guid CorrectedReplacementLinkId,
    Guid OriginalUnitId,
    Guid RecordedReplacementUnitId,
    Guid ActualReplacementUnitId,
    Guid VoidedEntitlementId,
    Guid TargetEntitlementId,
    string ReplacementReason,
    string PolicyApplied,
    DateOnly ComputedEnd,
    bool AssetTagRepointed) : IPublishedUnitEvent
{
    public const string Schema = "Replacement.LinkCorrected";
    public const string Version = "1.0";
}

/// <summary>P6 consumes this request and decides certificate versions; P5 records neither certificate rows nor files.</summary>
public sealed record CertificateReissueRequested(
    PublishedEventHeader Header,
    Guid ReplacementLinkId,
    IReadOnlyList<string> DocumentKinds,
    string Reason) : IPublishedUnitEvent
{
    public const string Schema = "Certificate.ReissueRequested";
    public const string Version = "1.0";
}

public static class ReplacementCertificateDocumentKind
{
    public const string ReleaseRecord = "release_record";
    public const string WarrantyCertificate = "warranty_certificate";
    public static readonly IReadOnlyList<string> All = [ReleaseRecord, WarrantyCertificate];
}

