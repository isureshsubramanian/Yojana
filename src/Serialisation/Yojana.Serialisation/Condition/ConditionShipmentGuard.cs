using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Condition;

public sealed class ConditionShipmentGuard(SerialisationDbContext db, IItemQueryPort items, ITenantContext tenant) : IUnitSaleShipmentGuard
{
    public async Task<IReadOnlyDictionary<Guid, UnitShipmentGuardDecision>> CheckAsync(IReadOnlyList<UnitShipmentCheck> units, CancellationToken cancellationToken)
    {
        if (units.Count == 0) return new Dictionary<Guid, UnitShipmentGuardDecision>();
        var flaggedItems = new HashSet<Guid>();
        foreach (var itemId in units.Select(u => u.ItemId).Distinct())
        {
            if (await items.GetAsync(itemId, cancellationToken) is { RequiresSanitisation: true }) flaggedItems.Add(itemId);
        }

        var flagged = units.Where(u => flaggedItems.Contains(u.ItemId)).ToList();
        if (flagged.Count == 0) return units.Select(u => u.UnitId).Distinct().ToDictionary(id => id, id => new UnitShipmentGuardDecision(id, true));
        var ids = flagged.Select(u => u.UnitId).Distinct().ToList();
        var latestShipment = flagged.GroupBy(u => u.UnitId).ToDictionary(g => g.Key, g => g.Max(u => u.ShipmentAt));
        var latestExternalOwnershipClosures = await db.Episodes.AsNoTracking()
            .Where(e => e.TenantId == tenant.TenantId && ids.Contains(e.UnitId) && e.Kind == EpisodeKind.Ownership
                && e.PartyType != PartyType.Us && e.ValidTo != null)
            .GroupBy(e => e.UnitId)
            .Select(g => new { UnitId = g.Key, ReturnedAt = g.Max(e => e.ValidTo) })
            .ToDictionaryAsync(e => e.UnitId, e => e.ReturnedAt!.Value, cancellationToken);
        var sanitisedAfterReturn = (await db.SanitisationRecords.AsNoTracking()
            .Where(r => r.TenantId == tenant.TenantId && ids.Contains(r.UnitId))
            .Select(r => new { r.UnitId, r.SanitisedAt }).ToListAsync(cancellationToken))
            .Where(r => latestExternalOwnershipClosures.TryGetValue(r.UnitId, out var returnedAt)
                && r.SanitisedAt > returnedAt && r.SanitisedAt <= latestShipment[r.UnitId])
            .Select(r => r.UnitId).ToHashSet();
        return units.GroupBy(u => u.UnitId).ToDictionary(g => g.Key, g => g.Any(u => flaggedItems.Contains(u.ItemId))
            && latestExternalOwnershipClosures.ContainsKey(g.Key) && !sanitisedAfterReturn.Contains(g.Key)
            ? new UnitShipmentGuardDecision(g.Key, false, "sanitisation_required", "The item class requires a sanitisation record before sale or shipment.")
            : new UnitShipmentGuardDecision(g.Key, true));
    }
}
