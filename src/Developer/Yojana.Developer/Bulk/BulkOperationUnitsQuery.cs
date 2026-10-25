using Microsoft.EntityFrameworkCore;
using Yojana.Developer.Contracts;
using Yojana.Developer.Data;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.Bulk;

/// <summary>Keyset-paged operation membership seam consumed by P14-B's public read endpoint.</summary>
public sealed class BulkOperationUnitsQuery(DeveloperDbContext db, ITenantContext tenant,
    IPublicEventContextPort context)
{
    public async Task<BulkOperationUnitPage?> PageAsync(Guid operationId, Guid? afterUnitId,
        int? requestedTake, CancellationToken ct)
    {
        if (!await db.BulkOperations.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId
            && row.OperationId == operationId, ct)) return null;
        var take = Math.Clamp(requestedTake ?? 100, 1, 200);
        var query = db.BulkOperationUnits.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.OperationId == operationId);
        if (afterUnitId is { } after) query = query.Where(row => row.UnitId.CompareTo(after) > 0);
        var ids = await query.OrderBy(row => row.UnitId).Select(row => row.UnitId).Take(take + 1).ToListAsync(ct);
        var pageIds = ids.Take(take).ToList();
        var serials = await context.SerialsAsync(pageIds, ct);
        var rows = pageIds.Select(id => new BulkOperationUnitRow(id, serials.GetValueOrDefault(id))).ToList();
        return new(rows, ids.Count > take && rows.Count > 0 ? rows[^1].UnitId.ToString("D") : null);
    }
}
