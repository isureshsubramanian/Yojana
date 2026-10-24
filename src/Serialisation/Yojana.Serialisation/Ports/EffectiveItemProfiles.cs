using Microsoft.EntityFrameworkCore;
using System.Text.Json;
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
        var local = await db.ItemSerialisationSettings.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.ItemId == itemId)
            .Select(row => row.ModelNumbersJson)
            .SingleOrDefaultAsync(cancellationToken);
        return profile with
        {
            TrackingMode = mode ?? profile.TrackingMode,
            ModelNumbers = local is null ? profile.ModelNumbers ?? [] : JsonSerializer.Deserialize<string[]>(local) ?? [],
        };
    }

    public async Task<IReadOnlyDictionary<Guid, ItemSerialisationProfile?>> GetManyAsync(
        IReadOnlyCollection<Guid> itemIds,
        DateTime effectiveAt,
        CancellationToken cancellationToken)
    {
        var ids = itemIds.Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<Guid, ItemSerialisationProfile?>();
        var profiles = await source.GetManyAsync(ids, cancellationToken);
        var changes = await db.ItemTrackingModeChanges.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.ItemId) && row.EffectiveAt <= effectiveAt)
            .OrderByDescending(row => row.EffectiveAt).ThenByDescending(row => row.RecordedAt)
            .Select(row => new { row.ItemId, row.TrackingMode })
            .ToListAsync(cancellationToken);
        var modes = changes.GroupBy(row => row.ItemId).ToDictionary(group => group.Key, group => group.First().TrackingMode);
        var local = await db.ItemSerialisationSettings.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && ids.Contains(row.ItemId))
            .Select(row => new { row.ItemId, row.ModelNumbersJson })
            .ToDictionaryAsync(row => row.ItemId, row => row.ModelNumbersJson, cancellationToken);
        return ids.ToDictionary(itemId => itemId, itemId => profiles.GetValueOrDefault(itemId) is { } profile
            ? profile with
            {
                TrackingMode = modes.GetValueOrDefault(itemId) ?? profile.TrackingMode,
                ModelNumbers = local.TryGetValue(itemId, out var json)
                    ? JsonSerializer.Deserialize<string[]>(json) ?? []
                    : profile.ModelNumbers ?? [],
            }
            : null);
    }
}
