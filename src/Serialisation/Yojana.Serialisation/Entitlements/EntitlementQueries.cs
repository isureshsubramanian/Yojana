using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Entitlements;

/// <summary>A unit's entitlements as the Entitlements panel reads them: every one it has carried, live first, with today's effective status.</summary>
public sealed class EntitlementQueries(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<IReadOnlyList<EntitlementView>> ForUnitAsync(Guid unitId, DateOnly today, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId).OrderByDescending(e => e.CreatedAt).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.EntitlementId).ToList();
        var exhausted = (await db.EntitlementUsageLimits.AsNoTracking().Where(u => u.TenantId == tenantId && ids.Contains(u.EntitlementId) && u.UsedValue >= u.LimitValue)
            .Select(u => u.EntitlementId).ToListAsync(cancellationToken)).ToHashSet();
        return rows.Select(e =>
        {
            var effective = EntitlementStateMachine.Effective(e.Status, e.EndDate, e.DurationBasis, exhausted.Contains(e.EntitlementId), today);
            int? remaining = EntitlementStateMachine.IsLive(e.Status) && e.EndDate is { } end ? Math.Max(0, end.DayNumber - today.DayNumber) : null;
            return new EntitlementView(e.EntitlementId, e.UnitId, e.CoverageFamily, e.Direction, e.Type, e.Status, effective, e.ClosedReason, e.StartDate, e.EndDate, remaining, e.PolicyVersionId, e.StartBasis, e.StartTimeZone, e.ParentEntitlementId, e.RootEntitlementId, e.SupersedesEntitlementId);
        }).OrderBy(v => EntitlementStateMachine.IsLive(v.StoredStatus) ? 0 : 1).ToList();
    }
}
