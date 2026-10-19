using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ports;

/// <summary>Serialisation-owned implementation of the grouped contract consumed by companion reconciliation.</summary>
public sealed class SerialisedOnHandQuery(SerialisationDbContext db, ITenantContext tenant)
    : ISerialisedOnHandQuery
{
    public async Task<IReadOnlyList<SerialisedOnHandCount>> CountsAsync(
        CancellationToken cancellationToken)
    {
        var includedCustody = SerialisedOnHandCustodyPolicy.IncludedLocationKinds;
        var excludedLifecycle = SerialisedOnHandCustodyPolicy.ExcludedLifecycleStatuses;
        var rows = await db.Current.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.LocationId != null
                && includedCustody.Contains(row.LocationKind!)
                && !excludedLifecycle.Contains(row.LifecycleStatus)
                && !db.AssemblyLinks.Any(link => link.TenantId == row.TenantId
                    && link.ChildUnitId == row.UnitId && link.RemovedAt == null))
            .GroupBy(row => new { row.ItemId, row.LocationId })
            .Select(group => new CountProjection
            {
                ItemId = group.Key.ItemId,
                LocationId = group.Key.LocationId!.Value,
                UnitCount = group.LongCount(),
            }).ToListAsync(cancellationToken);
        return rows.Select(row => new SerialisedOnHandCount(row.ItemId, row.LocationId, row.UnitCount))
            .ToList();
    }

    private sealed class CountProjection
    {
        public Guid ItemId { get; init; }
        public Guid LocationId { get; init; }
        public long UnitCount { get; init; }
    }
}
