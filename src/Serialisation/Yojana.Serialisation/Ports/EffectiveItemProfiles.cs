using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ports;

/// <summary>
/// Narrow read seam combining the external item profile with Serialisation's effective-dated tracking corrections.
/// No caller reaches into item-master storage or the local correction table independently.
/// </summary>
public sealed class EffectiveItemProfiles(SerialisationDbContext db, IItemQueryPort source, ITenantContext tenant)
{
    public async Task<ItemSerialisationProfile?> GetAsync(Guid itemId, DateTime effectiveAt, CancellationToken cancellationToken)
    {
        var profile = await source.GetAsync(itemId, cancellationToken);
        if (profile is null) return null;
        var mode = await db.ItemTrackingModeChanges.AsNoTracking()
            .Where(c => c.TenantId == tenant.TenantId && c.ItemId == itemId && c.EffectiveAt <= effectiveAt)
            .OrderByDescending(c => c.EffectiveAt).ThenByDescending(c => c.RecordedAt)
            .Select(c => c.TrackingMode).FirstOrDefaultAsync(cancellationToken);
        return mode is null ? profile : profile with { TrackingMode = mode };
    }
}
