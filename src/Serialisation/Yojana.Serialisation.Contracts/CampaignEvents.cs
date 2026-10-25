namespace Yojana.Serialisation.Contracts;

public sealed record CampaignStarted(PublishedSummaryHeader Header, Guid CampaignId, string CampaignCode,
    int UnitCount) : IPublishedSummaryEvent
{
    public const string Schema = "Campaign.Started";
    public const string Version = "1.0";
}

public sealed record CampaignEnded(PublishedSummaryHeader Header, Guid CampaignId, string CampaignCode,
    int UnitCount) : IPublishedSummaryEvent
{
    public const string Schema = "Campaign.Ended";
    public const string Version = "1.0";
}

/// <summary>
/// One bounded batch of units added by SelectCampaignUnits. Several messages can share a selection command when the
/// campaign is large; BatchNumber and BatchCount reassemble that logical selection without one oversized outbox row.
/// </summary>
public sealed record CampaignUnitsSelected(
    PublishedSummaryHeader Header,
    Guid CampaignId,
    string CampaignCode,
    string CoverageSource,
    string ReserveTag,
    string ReasonCode,
    string? ReasonNote,
    DateOnly? RunsUntil,
    decimal PartsPct,
    decimal LabourPct,
    decimal TravelPct,
    Guid SelectionCommandId,
    int BatchNumber,
    int BatchCount,
    int MatchedCount,
    int TotalSelectedCount,
    IReadOnlyList<CampaignSelectedUnit> Units) : IPublishedSummaryEvent
{
    public const string Schema = "Campaign.UnitsSelected";
    public const string Version = "1.2";
}

public sealed record CampaignSelectedUnit(
    Guid UnitId,
    Guid? OwnerPartyRef,
    string? OwnerPartyType,
    Guid? CustodianPartyRef,
    string? CustodianPartyType);

