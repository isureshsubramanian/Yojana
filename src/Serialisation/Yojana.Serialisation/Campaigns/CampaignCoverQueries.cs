using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Campaigns;

/// <summary>
/// Campaigns' implementation of the only claims-to-campaigns seam. Keep membership and lifecycle interpretation here;
/// moving either into claims would couple claim decisions to campaign persistence and let future schema changes leak.
/// </summary>
public sealed class CampaignCoverQueries(SerialisationDbContext db, ITenantContext tenant) : ICampaignCoverPort
{
    public async Task<IReadOnlyList<CampaignCover>> ListForUnitAsync(Guid unitId, DateOnly claimDate, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var until = claimDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var rows = await (from selected in db.CampaignUnits.AsNoTracking()
                          join campaign in db.Campaigns.AsNoTracking()
                              on new { selected.TenantId, selected.CampaignId } equals new { campaign.TenantId, campaign.CampaignId }
                          where selected.TenantId == tenantId && selected.UnitId == unitId && selected.SelectedAt < until
                              && campaign.Status == CampaignStatus.Active
                          orderby campaign.Code
                          select new CampaignCoverRow
                          {
                              CampaignId = campaign.CampaignId,
                              Code = campaign.Code,
                              Name = campaign.Name,
                              CoverageSource = campaign.CoverageSource,
                              ReserveTag = campaign.ReserveTag,
                              Status = campaign.Status,
                              ReasonCode = campaign.ReasonCode,
                              RunsUntil = campaign.RunsUntil,
                              Selected = true,
                              PartsPct = campaign.PartsPct,
                              LabourPct = campaign.LabourPct,
                              TravelPct = campaign.TravelPct,
                          }).ToListAsync(cancellationToken);
        return rows.Select(ToCover).ToList();
    }

    public async Task<CampaignCover?> FindAsync(Guid campaignId, Guid unitId, DateOnly claimDate, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var until = claimDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var row = await db.Campaigns.AsNoTracking()
            .Where(campaign => campaign.TenantId == tenantId && campaign.CampaignId == campaignId)
            .Select(campaign => new CampaignCoverRow
            {
                CampaignId = campaign.CampaignId,
                Code = campaign.Code,
                Name = campaign.Name,
                CoverageSource = campaign.CoverageSource,
                ReserveTag = campaign.ReserveTag,
                Status = campaign.Status,
                ReasonCode = campaign.ReasonCode,
                RunsUntil = campaign.RunsUntil,
                PartsPct = campaign.PartsPct,
                LabourPct = campaign.LabourPct,
                TravelPct = campaign.TravelPct,
                Selected = db.CampaignUnits.Any(selected => selected.TenantId == tenantId
                    && selected.CampaignId == campaign.CampaignId && selected.UnitId == unitId && selected.SelectedAt < until),
            }).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : ToCover(row);
    }

    private static CampaignCover ToCover(CampaignCoverRow row) => new(row.CampaignId, row.Code, row.Name,
        row.CoverageSource, row.ReserveTag, row.Status, row.ReasonCode, row.RunsUntil, row.Selected,
        row.PartsPct, row.LabourPct, row.TravelPct);

    private sealed class CampaignCoverRow
    {
        public Guid CampaignId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string CoverageSource { get; init; } = "";
        public string ReserveTag { get; init; } = "";
        public string Status { get; init; } = "";
        public string ReasonCode { get; init; } = "";
        public DateOnly? RunsUntil { get; init; }
        public bool Selected { get; init; } = true;
        public decimal PartsPct { get; init; }
        public decimal LabourPct { get; init; }
        public decimal TravelPct { get; init; }
    }
}
