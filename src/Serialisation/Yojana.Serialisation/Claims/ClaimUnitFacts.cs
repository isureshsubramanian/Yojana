using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Claims;

public sealed record ClaimUnitFact(Guid UnitId, Guid ItemId, IReadOnlyList<string> OverlayFlags);

/// <summary>Claim-facing read seam for ledger current state and historical as-built membership.</summary>
public sealed class ClaimUnitFacts(SerialisationDbContext db, ITenantContext tenant)
{
    private const int MaxDepth = 5;

    public async Task<ClaimUnitFact> ReadAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var row = await db.Current.AsNoTracking().Where(c => c.TenantId == tenantId && c.UnitId == unitId)
            .Select(c => new { c.UnitId, c.ItemId, c.OverlayFlags }).FirstOrDefaultAsync(cancellationToken)
            ?? throw new CommandRefusedException("unit_unknown", $"Unit {unitId} does not exist.");
        return new(row.UnitId, row.ItemId, JsonSerializer.Deserialize<List<string>>(row.OverlayFlags) ?? []);
    }

    public async Task<IReadOnlySet<Guid>> AsBuiltUnitsAsync(Guid rootUnitId, DateOnly on, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var result = new HashSet<Guid> { rootUnitId };
        var frontier = new List<Guid> { rootUnitId };
        var from = on.ToDateTime(TimeOnly.MinValue);
        var until = from.AddDays(1);
        for (var depth = 0; depth < MaxDepth && frontier.Count > 0; depth++)
        {
            var children = await db.AssemblyLinks.AsNoTracking()
                .Where(l => l.TenantId == tenantId && frontier.Contains(l.ParentUnitId) && l.ChildUnitId != null
                    && l.InstalledAt < until && (l.RemovedAt == null || l.RemovedAt >= from))
                .Select(l => l.ChildUnitId!.Value).Distinct().ToListAsync(cancellationToken);
            frontier = children.Where(result.Add).ToList();
        }

        return result;
    }
}

