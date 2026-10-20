using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Bounded history of one unit, including the original sale on an ancestor unit.</summary>
public sealed class CoverHistory(SerialisationDbContext db, ITenantContext tenant)
{
    public async Task<CoverHistoryData> LoadAsync(Guid unitId, DateOnly today, CancellationToken ct) =>
        (await LoadManyAsync([unitId], today, ct))[unitId];

    public async Task<IReadOnlyDictionary<Guid, CoverHistoryData>> LoadManyAsync(IReadOnlyList<Guid> unitIds, DateOnly today, CancellationToken ct)
    {
        var rows = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && unitIds.Contains(e.UnitId) && e.Direction == EntitlementDirection.Outbound).ToListAsync(ct);
        var units = unitIds.ToHashSet();
        var visited = rows.Select(e => e.EntitlementId).ToHashSet();
        while (true)
        {
            var ids = rows.SelectMany(e => new[] { e.RootEntitlementId, e.ParentEntitlementId, e.SupersedesEntitlementId, e.SuccessorEntitlementId })
                .OfType<Guid>().Where(id => !visited.Contains(id)).Distinct().ToList();
            if (ids.Count == 0) break;
            visited.UnionWith(ids);
            var linked = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && ids.Contains(e.EntitlementId) && e.Direction == EntitlementDirection.Outbound).ToListAsync(ct);
            var ancestorUnits = linked.Select(e => e.UnitId).Where(id => !units.Contains(id)).Distinct().ToList();
            units.UnionWith(ancestorUnits);
            if (ancestorUnits.Count > 0) linked.AddRange(await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && ancestorUnits.Contains(e.UnitId) && e.Direction == EntitlementDirection.Outbound).ToListAsync(ct));
            rows.AddRange(linked.Where(e => !rows.Any(r => r.EntitlementId == e.EntitlementId)).DistinctBy(e => e.EntitlementId));
            visited.UnionWith(rows.Select(e => e.EntitlementId));
        }
        var allUnits = units.ToList();
        var live = await db.Events.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && allUnits.Contains(e.UnitId) && e.EventType.StartsWith("entitlement."))
            .Select(e => new CoverFact(e.EventId, e.UnitId, e.EventTime, e.EventType, e.ActorRef, e.DocumentType, e.DocumentId, e.DocumentLine, e.Attributes ?? "{}")).ToListAsync(ct);
        var archive = await db.ArchivedEvents.AsNoTracking().Where(e => e.TenantId == tenant.TenantId && allUnits.Contains(e.UnitId) && e.EventType.StartsWith("entitlement."))
            .Select(e => new CoverFact(e.EventId, e.UnitId, e.EventTime, e.EventType, e.ActorRef, e.DocumentType, e.DocumentId, e.DocumentLine, e.Attributes ?? "{}")).ToListAsync(ct);
        var facts = live.Concat(archive).DistinctBy(e => e.EventId).OrderBy(e => e.At).ThenBy(e => e.EventId).ToList();
        return unitIds.ToDictionary(id => id, id => Build(id, rows, facts, today));
    }

    private static CoverHistoryData Build(Guid unitId, IReadOnlyList<Entitlement> all, IReadOnlyList<CoverFact> allFacts, DateOnly today)
    {
        var rows = all.Where(e => e.UnitId == unitId).ToList();
        var visited = rows.Select(e => e.EntitlementId).ToHashSet();
        while (true)
        {
            var ids = rows.SelectMany(e => new[] { e.RootEntitlementId, e.ParentEntitlementId, e.SupersedesEntitlementId, e.SuccessorEntitlementId }).OfType<Guid>().Where(id => !visited.Contains(id)).ToHashSet();
            if (ids.Count == 0) break;
            visited.UnionWith(ids);
            rows.AddRange(all.Where(e => ids.Contains(e.EntitlementId)));
        }
        var factById = allFacts.ToDictionary(f => f.EventId);
        var earliestBase = rows.Where(e => (e.CoverageFamily is CoverageFamilyCode.Base or "base") && factById.ContainsKey(e.CreatedEventId))
            .OrderBy(e => factById[e.CreatedEventId].At).ThenBy(e => e.EntitlementId).FirstOrDefault();
        if (earliestBase is null || earliestBase.SourceDocumentId is null || earliestBase.SourceDocumentType is null)
            return new(unitId, null, null, rows, [], [], [], today, "No sale recorded");
        // Successors may inherit the original document reference; they are not part of the original sale.
        var baseline = all.Where(e => e.SourceDocumentType == earliestBase.SourceDocumentType && e.SourceDocumentId == earliestBase.SourceDocumentId
            && e.ParentEntitlementId == null && e.SupersedesEntitlementId == null && e.UnitId == earliestBase.UnitId).ToList();
        rows.AddRange(baseline.Where(e => !rows.Any(r => r.EntitlementId == e.EntitlementId)));
        var involvedUnits = rows.Select(e => e.UnitId).ToHashSet();
        var facts = allFacts.Where(f => involvedUnits.Contains(f.UnitId)).ToList();
        var sale = factById[earliestBase.CreatedEventId];
        var date = TermArithmetic.LocalDay(sale.At, earliestBase.StartTimeZone);
        return new(unitId, earliestBase, date, rows, facts, baseline, CoverHistoryRules.Changes(rows, facts, baseline, date), today, null);
    }

}

public sealed record CoverFact(Guid EventId, Guid UnitId, DateTime At, string Type, string Actor, string? DocumentType,
    Guid? DocumentId, int? DocumentLine, string Attributes);
public sealed record CoverHistoryChange(CoverFact Fact, Entitlement Entitlement, string Kind, DateOnly Date,
    int Months, decimal? Amount, string? Currency, string? Reason, string? Approver, bool Counted, IReadOnlyList<CoverFieldChange> Fields);
public sealed record CoverHistoryData(Guid UnitId, Entitlement? Base, DateOnly? SaleDate, IReadOnlyList<Entitlement> Entitlements,
    IReadOnlyList<CoverFact> Facts, IReadOnlyList<Entitlement> Baseline, IReadOnlyList<CoverHistoryChange> Changes, DateOnly Today, string? Notice)
{
    public Guid? SaleCustomerPartyRef
    {
        get
        {
            var sale = Facts.FirstOrDefault(f => f.EventId == Base?.CreatedEventId);
            if (sale is null) return null;
            using var json = JsonDocument.Parse(sale.Attributes);
            return Guid.TryParse(CoverHistoryRules.TextValue(json.RootElement, "owner_party_ref"), out var owner) ? owner
                : Facts.Any(f => f.Type == EntitlementEventType.OwnerTransferred) ? null : Base?.OwnerPartyRef;
        }
    }
}

public static class CoverHistoryRules
{
    public static readonly IReadOnlyList<string> CountedKinds = ["added", "upgraded", "changed", "amended", "closed_early", "suspended"];

    public static int WholeMonths(DateOnly sale, DateOnly day)
    {
        var months = (day.Year - sale.Year) * 12 + day.Month - sale.Month;
        return sale.AddMonths(months) > day ? months - 1 : months;
    }

    public static IReadOnlyList<CoverHistoryChange> Changes(IReadOnlyList<Entitlement> rows, IReadOnlyList<CoverFact> facts, IReadOnlyList<Entitlement> baseline, DateOnly sale)
    {
        var result = new List<CoverHistoryChange>();
        var baselineIds = baseline.Select(e => e.EntitlementId).ToHashSet();
        foreach (var fact in facts)
        {
            using var json = JsonDocument.Parse(fact.Attributes);
            var a = json.RootElement;
            var id = GuidValue(a, "entitlement_id") ?? GuidValue(a, "new_entitlement_id") ?? GuidValue(a, "target_entitlement_id");
            var e = rows.FirstOrDefault(e => e.EntitlementId == id || e.CreatedEventId == fact.EventId);
            if (e is null) continue;
            var day = TermArithmetic.LocalDay(fact.At, e.StartTimeZone);
            if (day < sale) continue;
            var reason = fact.Type is EntitlementEventType.Closed or EntitlementEventType.Voided
                ? TextValue(a, "reason") ?? TextValue(a, "closed_reason") ?? e.ClosedReason
                : TextValue(a, "reason_code") ?? TextValue(a, "reason");
            string? kind = fact.Type switch
            {
                EntitlementEventType.Created or EntitlementEventType.ExtendedSold when !baselineIds.Contains(e.EntitlementId) && e.ParentEntitlementId is null => "added",
                EntitlementEventType.Superseded => (e.SalePrice ?? MoneyValue(a, "price")) > 0 && e.SupersedesEntitlementId is { } previous
                    && rows.Any(r => r.EntitlementId == previous && r.CoverageFamily == e.CoverageFamily && r.PolicyVersionId != e.PolicyVersionId) ? "upgraded" : "changed",
                EntitlementEventType.Amended => "amended",
                EntitlementEventType.Transferred or EntitlementEventType.OwnerTransferred => "transferred",
                EntitlementEventType.Suspended => "suspended",
                EntitlementEventType.Resumed => "resumed",
                EntitlementEventType.Closed or EntitlementEventType.Voided when reason is not (EntitlementClosedReason.Expired or EntitlementClosedReason.ReplacedByChange or EntitlementClosedReason.Superseded or EntitlementClosedReason.Replaced or EntitlementClosedReason.TransferredOut) => "closed_early",
                _ => null,
            };
            if (kind is null) continue;
            decimal? amount = null;
            string? currency = null;
            if (kind is "added" or "upgraded" or "changed")
            {
                amount = e.SalePrice ?? MoneyValue(a, "price");
                currency = e.SaleCurrency ?? TextValue(a, "currency");
            }
            else if (kind == "amended") { amount = MoneyValue(a, "price"); currency = TextValue(a, "currency"); }
            else if (kind == "closed_early")
            {
                // A refund is not the original purchase price. Absent recorded refund money stays absent.
                amount = MoneyValue(a, "refund_amount");
                if (amount is { } recorded) amount = -Math.Abs(recorded);
                currency = TextValue(a, "refund_currency") ?? TextValue(a, "currency");
            }
            if (amount is null || currency is null) { amount = null; currency = null; }
            var fields = new List<CoverFieldChange>();
            if (a.TryGetProperty("changes", out var changed) && changed.ValueKind == JsonValueKind.Array)
                foreach (var field in changed.EnumerateArray()) fields.Add(new(TextValue(field, "field") ?? "Term", TextValue(field, "before") ?? TextValue(field, "before_value"), TextValue(field, "after") ?? TextValue(field, "after_value")));
            result.Add(new(fact, e, kind, day, WholeMonths(sale, day), amount, currency, reason,
                TextValue(a, "approved_by"), CountedKinds.Contains(kind), fields));
        }
        return result.OrderByDescending(c => c.Fact.At).ThenByDescending(c => c.Fact.EventId).ToList();
    }

    public static bool DescendsFrom(Entitlement row, Entitlement baseline, IReadOnlyList<Entitlement> rows)
    {
        var seen = new HashSet<Guid>();
        while (seen.Add(row.EntitlementId))
        {
            if (row.EntitlementId == baseline.EntitlementId) return true;
            var parent = row.SupersedesEntitlementId ?? row.ParentEntitlementId ?? row.RootEntitlementId;
            if (parent is null || rows.FirstOrDefault(e => e.EntitlementId == parent) is not { } previous) return false;
            row = previous;
        }
        return false;
    }

    public static string? TextValue(JsonElement a, string key) => a.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText() : null;
    public static decimal? MoneyValue(JsonElement a, string key) => a.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var amount) ? amount : null;
    private static Guid? GuidValue(JsonElement a, string key) => Guid.TryParse(TextValue(a, key), out var id) ? id : null;
}
