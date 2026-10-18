using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Portal;

public sealed record PortalOwnershipWindow(DateTime From, DateTime? To)
{
    public bool Contains(DateTime value) => value >= From && (To is null || value < To.Value);
}

/// <summary>
/// The one row-security seam for customer reads. Direct ownership and a component's containment in an owned parent are
/// both effective-dated; callers receive no unscoped query helper and must pass the claim-derived principal.
/// COUPLING (SQL portal RLS): P7T08OwnershipRls repeats these half-open direct-or-contained windows in schema-bound
/// predicates because raw SQL bypasses this application seam. A change here must change those functions in the same
/// migration; separating the interval rules would make application and database authorisation disagree.
/// </summary>
public sealed class PortalOwnershipScope(SerialisationDbContext db, ITenantContext tenant)
{
    public IQueryable<Guid> AccessibleUnitIds(PortalPrincipal principal)
    {
        EnsureTenant(principal);
        var tenantId = tenant.TenantId;
        return db.Units.AsNoTracking().Where(unit => unit.TenantId == tenantId
                && (db.Episodes.Any(episode => episode.TenantId == tenantId && episode.UnitId == unit.UnitId
                        && episode.Kind == EpisodeKind.Ownership && episode.PartyRef == principal.PartyRef)
                    || db.AssemblyLinks.Any(link => link.TenantId == tenantId && link.ChildUnitId == unit.UnitId
                        && db.Episodes.Any(episode => episode.TenantId == tenantId
                            && episode.UnitId == link.ParentUnitId && episode.Kind == EpisodeKind.Ownership
                            && episode.PartyRef == principal.PartyRef
                            && (episode.ValidTo == null || link.InstalledAt < episode.ValidTo)
                            && (link.RemovedAt == null || link.RemovedAt > episode.ValidFrom)))))
            .Select(unit => unit.UnitId);
    }

    public async Task<bool> CanReadAsync(PortalPrincipal principal, Guid unitId,
        CancellationToken cancellationToken) =>
        await AccessibleUnitIds(principal).AnyAsync(id => id == unitId, cancellationToken);

    public async Task<IReadOnlyList<PortalOwnershipWindow>> WindowsAsync(PortalPrincipal principal, Guid unitId,
        CancellationToken cancellationToken)
    {
        EnsureTenant(principal);
        var tenantId = tenant.TenantId;
        var direct = await db.Episodes.AsNoTracking().Where(episode => episode.TenantId == tenantId
                && episode.UnitId == unitId && episode.Kind == EpisodeKind.Ownership
                && episode.PartyRef == principal.PartyRef)
            .Select(episode => new { episode.ValidFrom, episode.ValidTo }).ToListAsync(cancellationToken);
        var contained = await (from link in db.AssemblyLinks.AsNoTracking()
                               join episode in db.Episodes.AsNoTracking()
                                   on new { link.TenantId, UnitId = link.ParentUnitId }
                                   equals new { episode.TenantId, episode.UnitId }
                               where link.TenantId == tenantId && link.ChildUnitId == unitId
                                   && episode.Kind == EpisodeKind.Ownership && episode.PartyRef == principal.PartyRef
                                   && (episode.ValidTo == null || link.InstalledAt < episode.ValidTo)
                                   && (link.RemovedAt == null || link.RemovedAt > episode.ValidFrom)
                               select new { EpisodeFrom = episode.ValidFrom, EpisodeTo = episode.ValidTo,
                                   LinkFrom = link.InstalledAt, LinkTo = link.RemovedAt })
            .ToListAsync(cancellationToken);
        return direct.Select(row => new PortalOwnershipWindow(row.ValidFrom, row.ValidTo))
            .Concat(contained.Select(row => new PortalOwnershipWindow(
                row.EpisodeFrom > row.LinkFrom ? row.EpisodeFrom : row.LinkFrom,
                Earlier(row.EpisodeTo, row.LinkTo))))
            .OrderBy(row => row.From).ToList();
    }

    public void EnsureTenant(PortalPrincipal principal)
    {
        if (principal.TenantId != tenant.TenantId)
            throw new UnauthorizedAccessException("The portal principal does not belong to the selected tenant.");
    }

    private static DateTime? Earlier(DateTime? left, DateTime? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left < right ? left : right;
    }
}
