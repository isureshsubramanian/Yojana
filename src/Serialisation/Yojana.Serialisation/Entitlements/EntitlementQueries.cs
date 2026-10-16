using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>A unit's entitlements as the Entitlements panel reads them: every one it has carried, live first, with today's effective status.</summary>
public sealed class EntitlementQueries(SerialisationDbContext db, ITenantContext tenant, IWarrantyClaimsPort claims)
{
    public async Task<RefurbishedEntitlementDisclosure?> DisclosureAsync(Guid entitlementId, CancellationToken cancellationToken)
    {
        var row = await db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenant.TenantId && e.EntitlementId == entitlementId, cancellationToken);
        return row is null ? null : CreateEntitlementHandler.Disclosure(row);
    }

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

    /// <summary>
    /// Complete unit-tab read. Each child collection is loaded set-wise for the unit; projection and JSON term parsing
    /// happen in memory after the bounded result sets have arrived.
    /// </summary>
    public async Task<UnitEntitlementDetail?> DetailForUnitAsync(Guid unitId, DateOnly today, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        if (!await db.Current.AsNoTracking().AnyAsync(c => c.TenantId == tenantId && c.UnitId == unitId, cancellationToken)) return null;

        var raw = await (
            from entitlement in db.Entitlements.AsNoTracking()
            join policy in db.PolicyVersions.AsNoTracking() on new { entitlement.TenantId, entitlement.PolicyVersionId } equals new { policy.TenantId, policy.PolicyVersionId }
            join family in db.CoverageFamilies.AsNoTracking() on new { entitlement.TenantId, FamilyCode = entitlement.CoverageFamily } equals new { family.TenantId, family.FamilyCode }
            join cohort in db.AccrualCohorts.AsNoTracking() on new { entitlement.TenantId, CohortId = entitlement.AccrualCohortId } equals new { cohort.TenantId, CohortId = (Guid?)cohort.CohortId } into cohorts
            from cohort in cohorts.DefaultIfEmpty()
            where entitlement.TenantId == tenantId && entitlement.UnitId == unitId
            orderby entitlement.CreatedAt descending
            select new
            {
                Entitlement = entitlement,
                policy.PolicyCode,
                PolicyName = policy.Name,
                PolicyVersion = policy.Version,
                FamilyName = family.Name,
                family.AccountingTag,
                Cohort = cohort == null ? null : cohort.Period,
            }).ToListAsync(cancellationToken);

        var ids = raw.Select(row => row.Entitlement.EntitlementId).ToList();
        var eventTypes = new[]
        {
            EntitlementEventType.Created, EntitlementEventType.ExtendedSold, EntitlementEventType.Transferred,
            EntitlementEventType.Amended, EntitlementEventType.Superseded,
            EntitlementEventType.Closed, EntitlementEventType.Rebased, EntitlementEventType.Suspended,
            EntitlementEventType.Resumed, EntitlementEventType.Voided,
        };
        var liveEvents = db.Events.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId && eventTypes.Contains(e.EventType))
            .Select(e => new EntitlementLedgerEventRow(e.EventId, e.EventType, e.EventTime, e.ActorRef, e.DocumentType, e.DocumentId, e.DocumentLine, e.Attributes ?? "{}"));
        var archivedEvents = db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId && eventTypes.Contains(e.EventType))
            .Select(e => new EntitlementLedgerEventRow(e.EventId, e.EventType, e.EventTime, e.ActorRef, e.DocumentType, e.DocumentId, e.DocumentLine, e.Attributes ?? "{}"));
        // Two reads merged here: EF cannot translate a set operation over rows built by a record constructor. A unit's
        // entitlement events are few, and archived partitions hold only the oldest of them.
        var lineage = (await liveEvents.ToListAsync(cancellationToken)).Concat(await archivedEvents.ToListAsync(cancellationToken)).OrderByDescending(e => e.At).ToList();
        var amendments = await db.EntitlementAmendments.AsNoTracking().Where(a => a.TenantId == tenantId && ids.Contains(a.EntitlementId)).OrderByDescending(a => a.At)
            .Select(a => new EntitlementAmendmentRow(a.AmendmentId, a.EntitlementId, a.Field, a.BeforeValue, a.AfterValue, a.ReasonCode, a.ApprovedBy, a.ActorRef, a.At)).ToListAsync(cancellationToken);
        var suspensions = await db.EntitlementSuspensions.AsNoTracking().Where(s => s.TenantId == tenantId && ids.Contains(s.EntitlementId)).OrderByDescending(s => s.FromDate)
            .Select(s => new EntitlementSuspensionRow(s.SuspensionId, s.EntitlementId, s.FromDate, s.ToDate, s.Reason, s.ClockPaused, s.ActorRef)).ToListAsync(cancellationToken);
        var usage = await (from limit in db.EntitlementUsageLimits.AsNoTracking()
            join entitlement in db.Entitlements.AsNoTracking() on new { limit.TenantId, limit.EntitlementId } equals new { entitlement.TenantId, entitlement.EntitlementId }
            where limit.TenantId == tenantId && ids.Contains(limit.EntitlementId)
            orderby entitlement.CoverageFamily, limit.MeterType
            select new EntitlementUsageLimitRow(limit.EntitlementId, entitlement.CoverageFamily, limit.MeterType, limit.LimitValue, limit.UsedValue,
                limit.LimitValue - limit.UsedValue, limit.Period, limit.ResetsOn)).ToListAsync(cancellationToken);

        var liveIds = raw.Where(row => EntitlementStateMachine.IsLive(row.Entitlement.Status)).Select(row => row.Entitlement.EntitlementId).ToList();
        var hasOpenClaims = await claims.HasOpenClaimsAsync(unitId, liveIds, cancellationToken);
        var rows = raw.Select(row =>
        {
            var entitlement = row.Entitlement;
            var effective = EntitlementStateMachine.Effective(entitlement.Status, entitlement.EndDate, entitlement.DurationBasis,
                usage.Any(limit => limit.EntitlementId == entitlement.EntitlementId && limit.Used >= limit.Limit), today);
            var coverage = JsonSerializer.Deserialize<CoverageTerms>(entitlement.Coverage, LedgerJson.Options) ?? new CoverageTerms(0, 0, 0, "unknown");
            var regions = JsonSerializer.Deserialize<List<string>>(entitlement.RegionScope, LedgerJson.Options) ?? [];
            int? remaining = EntitlementStateMachine.IsLive(entitlement.Status) && entitlement.EndDate is { } end ? Math.Max(0, end.DayNumber - today.DayNumber) : null;
            return new UnitEntitlementDetailRow(entitlement.EntitlementId, entitlement.CoverageFamily, row.FamilyName, row.AccountingTag,
                entitlement.Direction, entitlement.Type, entitlement.Status, effective, entitlement.StartDate > today, entitlement.ClosedReason,
                entitlement.ClosedEffective, entitlement.OwnerPartyRef, entitlement.OwnerPartyType, row.PolicyCode, row.PolicyName, row.PolicyVersion,
                coverage, regions, entitlement.Transferable, entitlement.SourceDocumentType, entitlement.SourceDocumentId, entitlement.SourceDocumentLine,
                row.Cohort, entitlement.StartDate, entitlement.StartBasis, StartExplanation(entitlement), entitlement.EndDate, entitlement.EndInclusive,
                entitlement.ComputedByRuleVersion, remaining, entitlement.ParentEntitlementId, entitlement.RootEntitlementId,
                entitlement.SupersedesEntitlementId, entitlement.SuccessorEntitlementId);
        }).OrderBy(row => EntitlementStateMachine.IsLive(row.StoredStatus) ? 0 : 1).ThenBy(row => row.CoverageFamily).ThenByDescending(row => row.StartDate).ToList();

        return new(unitId, today, hasOpenClaims, rows, lineage, amendments, suspensions, usage);
    }

    private static string StartExplanation(Yojana.Serialisation.Data.Entities.Entitlement entitlement) => entitlement.StartBasis switch
    {
        StartTrigger.Deliver => entitlement.StartEvidenceRef is null ? "delivery evidence" : $"delivery · {entitlement.StartEvidenceRef}",
        StartTrigger.BaseExpiry => "starts after the base entitlement expires",
        StartTrigger.PlanPurchase => "plan purchase",
        "supersession_effective_date" => "effective date of the coverage change",
        null => "awaiting start evidence",
        _ => entitlement.StartEvidenceRef is null ? entitlement.StartBasis.Replace('_', ' ') : $"{entitlement.StartBasis.Replace('_', ' ')} · {entitlement.StartEvidenceRef}",
    };

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
