using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Identity;

public sealed class OutboundUnitSourcePort(SerialisationDbContext db, ITenantContext tenant,
    ICertificateUnitReadPort units, ILogger<OutboundUnitSourcePort> logger) : IOutboundUnitSourcePort
{
    public async Task<OutboundUnitSource> ReadAsync(Guid unitId, DocumentRef? document, CancellationToken cancellationToken, Guid? customerPartyRef = null, bool allowOwnershipFallback = true, DateTime? custodyAt = null)
    {
        var serials = await units.PrimarySerialsAsync([unitId], cancellationToken);
        Guid? customer = customerPartyRef;
        if (customer is null && document?.Type == "rma")
            customer = await db.Rmas.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.RmaId == document.Id)
                .Select(row => (Guid?)row.SettlementPartyRef).SingleOrDefaultAsync(cancellationToken);
        if (customer is null && allowOwnershipFallback) customer = await db.Episodes.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.UnitId == unitId
                && row.Kind == EpisodeKind.Ownership && row.ValidTo == null && (row.PartyType == PartyType.Customer || row.PartyType == PartyType.Distributor
                    || row.PartyType == PartyType.EndUser || row.PartyType == PartyType.MarketplaceBuyer))
            .OrderByDescending(row => row.ValidFrom).ThenByDescending(row => row.EpisodeId)
            .Select(row => row.PartyRef).FirstOrDefaultAsync(cancellationToken);
        var custody = db.Episodes.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.UnitId == unitId
            && row.Kind == EpisodeKind.Custody);
        custody = custodyAt is { } at
            ? custody.Where(row => row.ValidFrom <= at && (row.ValidTo == null || row.ValidTo >= at))
            : custody.Where(row => row.ValidTo == null);
        // At a move's exact business time, the closing episode is the source; the new one is its destination.
        var locations = await custody.OrderBy(row => custodyAt != null && row.ValidTo == custodyAt ? 0 : 1)
            .ThenByDescending(row => row.ValidFrom).ThenByDescending(row => row.EpisodeId)
            .Select(row => new { row.LocationId, row.ValidFrom, row.ValidTo }).Take(2).ToListAsync(cancellationToken);
        if (locations.Count > 1 && !(custodyAt is { } boundary && locations[0].ValidTo == boundary && locations[1].ValidFrom == boundary))
            logger.LogWarning("Overlapping custody episodes for unit {UnitId} at {CustodyAt}; using the latest effective episode", unitId, custodyAt);
        var location = locations.FirstOrDefault()?.LocationId;
        // Only a site explicitly held by this customer is usable as a delivery destination.
        var shipTo = customer is null ? null : await db.Episodes.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.UnitId == unitId && row.Kind == EpisodeKind.Custody && row.PartyRef == customer
                && row.LocationKind == LocationKind.CustomerSite)
            .OrderByDescending(row => row.ValidFrom).ThenByDescending(row => row.EpisodeId)
            .Select(row => row.LocationId).FirstOrDefaultAsync(cancellationToken);
        return new(serials.GetValueOrDefault(unitId), location, customer, shipTo);
    }
}
