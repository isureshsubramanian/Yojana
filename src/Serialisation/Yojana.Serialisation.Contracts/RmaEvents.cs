namespace Yojana.Serialisation.Contracts;

public sealed record RmaAuthorised(
    PublishedEventHeader Header,
    Guid RmaId,
    Guid RmaLineId,
    string RmaNumber,
    Guid AuthorisedUnitId,
    Guid OwnershipEpisodeId,
    DocumentRef OriginalSaleRef,
    decimal CoveragePct,
    string CoverageSource,
    Guid? EntitlementId,
    Guid? CustomerPartyRef = null) : IPublishedUnitEvent
{
    public const string Schema = "Rma.Authorised";
    public const string Version = "1.1";
}

public sealed record RmaReceived(
    PublishedEventHeader Header,
    Guid RmaId,
    Guid RmaLineId,
    Guid ReturnReceiptId,
    Guid AuthorisedUnitId,
    Guid? ReceivedUnitId,
    Guid? ProvisionalUnitId,
    string Outcome,
    string ConditionGrade,
    string ValuationRule,
    string PostingPeriod,
    Guid? SourceLocationId = null) : IPublishedUnitEvent
{
    public const string Schema = "Rma.Received";
    public const string Version = "1.1";
}

public sealed record RmaDispositioned(
    PublishedEventHeader Header,
    Guid RmaId,
    Guid RmaLineId,
    Guid UnitId,
    string Disposition,
    string? EntitlementClosedReason,
    Guid? ReplacementIntentId,
    string? Currency = null,
    Guid? CustomerPartyRef = null,
    Guid? SourceLocationId = null) : IPublishedUnitEvent
{
    public const string Schema = "Rma.Dispositioned";
    public const string Version = "1.2";
}
