using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Queries;

namespace Yojana.Serialisation.Campaigns;

public sealed class CampaignQueries(SerialisationDbContext db, CampaignSelectionFacts facts, ITenantContext tenant)
{
    public async Task<CampaignRow?> DetailAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.TenantId == tenant.TenantId && c.CampaignId == campaignId, cancellationToken);
        if (campaign is null) return null;
        var criteria = await db.CampaignCriteria.AsNoTracking().Where(c => c.TenantId == tenant.TenantId && c.CampaignId == campaignId)
            .OrderBy(c => c.Kind).ThenBy(c => c.CriterionId).ToListAsync(cancellationToken);
        return CampaignHandlers.ToRow(campaign, criteria);
    }

    public async Task<Page<CampaignUnitRow>> UnitsAsync(Guid campaignId, int skip, int take, CancellationToken cancellationToken)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var query = db.CampaignUnits.AsNoTracking().Where(u => u.TenantId == tenant.TenantId && u.CampaignId == campaignId);
        var total = await query.CountAsync(cancellationToken);
        var selected = await query.OrderBy(u => u.UnitId).Skip(skip).Take(take).ToListAsync(cancellationToken);
        var serials = await facts.SerialsAsync(selected.Select(u => u.UnitId), cancellationToken);
        return new(selected.Select(u => new CampaignUnitRow(u.UnitId, serials.GetValueOrDefault(u.UnitId), u.OwnerPartyRef,
            u.OwnerPartyType, u.CustodianPartyRef, u.CustodianPartyType, u.SelectedAt, u.SelectedBy)).ToList(), total, skip, take);
    }
}
