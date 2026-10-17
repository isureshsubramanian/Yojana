using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Claims;

/// <summary>
/// The only claims-to-campaigns seam. Claims consume this narrow snapshot and never depend on campaign tables,
/// selection queries or lifecycle handlers; separating it would otherwise let the two modules disagree about cover.
/// </summary>
public interface ICampaignCoverPort
{
    Task<IReadOnlyList<CampaignCover>> ListForUnitAsync(Guid unitId, DateOnly claimDate, CancellationToken cancellationToken);
    Task<CampaignCover?> FindAsync(Guid campaignId, Guid unitId, DateOnly claimDate, CancellationToken cancellationToken);
}

public sealed record CampaignCover(
    Guid CampaignId,
    string Code,
    string Name,
    string CoverageSource,
    string ReserveTag,
    string Status,
    string ReasonCode,
    DateOnly? RunsUntil,
    bool Selected,
    decimal PartsPct,
    decimal LabourPct,
    decimal TravelPct)
{
    public bool CoversOn(DateOnly claimDate) =>
        Status == CampaignStatus.Active && (RunsUntil is null || claimDate <= RunsUntil);

    public string? NotCoveredReason(DateOnly claimDate) => Status switch
    {
        CampaignStatus.Closed => ClaimCampaignCoverReason.Closed,
        CampaignStatus.Active when RunsUntil is { } runsUntil && claimDate > runsUntil => ClaimCampaignCoverReason.Ended,
        _ => null,
    };
}
