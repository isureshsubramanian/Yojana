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

    public async Task<WarrantyCheckResult?> WarrantyCheckAsync(Guid unitId, DateOnly on, string? family, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (!await db.Current.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.UnitId == unitId, cancellationToken)) return null;
        var rows = await db.WarrantyCurrent.AsNoTracking()
            .Where(w => w.TenantId == tenantId && w.UnitId == unitId && (family == null || w.CoverageFamily == family))
            .Select(w => new WarrantyCheckRow(w.EntitlementId, w.CoverageFamily, w.Type, w.Status, w.StartDate, w.EndDate, w.DurationBasis, w.Coverage))
            .ToListAsync(cancellationToken);
        return WarrantyCheck.Evaluate(unitId, on, family, rows);
    }
}

public sealed record WarrantyCheckRow(Guid EntitlementId, string Family, string Type, string Status, DateOnly? Start, DateOnly? End, string DurationBasis, string Coverage);

/// <summary>Pure effective-date evaluation over rows already narrowed by the warranty view.</summary>
public static class WarrantyCheck
{
    public static WarrantyCheckResult Evaluate(Guid unitId, DateOnly on, string? family, IEnumerable<WarrantyCheckRow> source)
    {
        var rows = source.Where(r => family is null || r.Family == family).ToList();
        var covering = rows.Where(r => EntitlementStateMachine.Effective(r.Status, r.End, r.DurationBasis, false, on) == EntitlementStatus.Active && r.Start is { } start && start <= on)
            .Select(r => new WarrantyCoveringEntitlement(r.EntitlementId, r.Family, r.Type, r.Status, EntitlementStatus.Active, r.Start, r.End,
                r.End is { } end ? Math.Max(0, end.DayNumber - on.DayNumber) : null, r.Coverage))
            .OrderBy(r => r.End).ThenBy(r => r.Family).ToList();
        var endDate = covering.Select(r => r.End).Max();
        var reason = covering.Count > 0 ? null
            : rows.Any(r => r.Status == EntitlementStatus.Suspended) ? WarrantyCheckReason.Suspended
            : rows.Any(r => r.Start is null || r.Start > on || r.Status == EntitlementStatus.PendingEvidence) ? WarrantyCheckReason.PendingStart
            : rows.Any(r => r.End is { } end && end < on) ? WarrantyCheckReason.Expired
            : WarrantyCheckReason.None;
        return new(unitId, on, family, covering.Count > 0, covering, endDate,
            endDate is { } last ? Math.Max(0, last.DayNumber - on.DayNumber) : null, reason);
    }
}

public static class WarrantyCheckReason
{
    public const string None = "none";
    public const string Expired = "expired";
    public const string PendingStart = "pending_start";
    public const string Suspended = "suspended";
}
