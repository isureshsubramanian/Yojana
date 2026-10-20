using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

public sealed class WhatsCoveredQuery(SerialisationDbContext db, ITenantContext tenant, IClock clock,
    IItemNamePort items, IPartyNamePort parties, Yojana.Serialisation.TimeZones.TenantTimeZoneSettings zones, IItemFamilyNamePort itemFamilies)
{
    public const string ApplicabilityNotice = "— Policy applicability cannot be established: the sale history does not retain the country and context needed. Parts-scoped items require the failed part to be checked.";

    public async Task<WhatsCoveredPage?> ReadAsync(Guid unitId, CancellationToken ct)
    {
        var unit = await db.Current.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenant.TenantId && x.UnitId == unitId, ct);
        if (unit is null) return null;
        var zone = (await zones.PolicyAsync(ct))?.TenantTimeZone ?? "UTC";
        var asOfInstant = clock.UtcNow;
        var today = TermArithmetic.LocalDay(asOfInstant, zone);
        var rows = await (from e in db.Entitlements.AsNoTracking()
            join p in db.PolicyVersions.AsNoTracking() on new { e.TenantId, e.PolicyVersionId } equals new { p.TenantId, p.PolicyVersionId }
            join f in db.CoverageFamilies.AsNoTracking() on new { e.TenantId, FamilyCode = e.CoverageFamily } equals new { f.TenantId, f.FamilyCode }
            where e.TenantId == tenant.TenantId && e.UnitId == unitId && e.Status == EntitlementStatus.Active && e.Direction != EntitlementDirection.Inbound
            select new { Entitlement = e, Policy = p, FamilyName = f.Name }).ToListAsync(ct);
        rows = rows.Where(x => x.Entitlement.EndDate is null || x.Entitlement.EndDate >= TermArithmetic.LocalDay(asOfInstant, x.Entitlement.StartTimeZone)).ToList();
        var policyIds = rows.Select(x => x.Policy.PolicyVersionId).Distinct().ToList();
        var entitlementIds = rows.Select(x => x.Entitlement.EntitlementId).ToList();
        var itemRows = await db.PolicyCoverItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && policyIds.Contains(x.PolicyVersionId)).ToListAsync(ct);
        var causes = await db.PolicyCoverItemCauses.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && policyIds.Contains(x.PolicyVersionId)).ToListAsync(ct);
        var usage = await db.EntitlementUsageLimits.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && entitlementIds.Contains(x.EntitlementId)).ToListAsync(ct);
        var covers = rows.Select(x =>
        {
            var e = x.Entitlement;
            var day = TermArithmetic.LocalDay(asOfInstant, e.StartTimeZone);
            return new ItemCover(e.EntitlementId, e.CoverageFamily, x.FamilyName, x.Policy.Name, x.Policy.Version,
                e.StartDate, e.EndDate, day, e.StartDate > day,
                usage.Where(u => u.EntitlementId == e.EntitlementId).Select(u => new CoverUsage(u.MeterType, u.LimitValue, u.UsedValue, u.LimitValue - u.UsedValue, u.Period)).ToList(), x.Policy.PolicyCode);
        }).OrderBy(x => x.FamilyName).ToList();
        var familyNames = await itemFamilies.ListAsync(ct);
        var contributions = rows.SelectMany(x => itemRows.Where(i => i.PolicyVersionId == x.Policy.PolicyVersionId).Select(i =>
        {
            var item = PolicyCoverItems.Read(i, causes.Where(c => c.PolicyVersionId == i.PolicyVersionId && c.ItemCode == i.Code).Select(c => c.CauseCode).Order().ToList());
            var cover = covers.Single(c => c.EntitlementId == x.Entitlement.EntitlementId);
            return (Item: item, Contribution: new CoverItemContribution(item.Code, item.CustomerWording, item.InternalNote, item.Category, item.PartsScope, cover,
                item.PartsScope.PartCategories.Count + item.PartsScope.ItemFamilies.Count == 0 ? "All parts" : string.Join(", ",
                    item.PartsScope.PartCategories.Concat(item.PartsScope.ItemFamilies.Select(code => familyNames.FirstOrDefault(f => f.Code == code)?.Name ?? "Item family unavailable")))));
        })).ToList();
        // Cause codes are the stated cross-policy seam. Text without a cause is merged only when the customer wording
        // and category agree; similar prose is never guessed to be the same promise.
        var lines = contributions.SelectMany(x => (x.Item.CauseCodes.Count == 0
                ? new[] { "text:" + x.Item.Category + ":" + x.Item.CustomerWording }
                : x.Item.CauseCodes.Select(c => "cause:" + c)).Select(key => (Key: key, x.Item, x.Contribution)))
            .GroupBy(x => x.Key, StringComparer.Ordinal).Select(g =>
            {
                var grants = g.Where(x => x.Item.Type == "covered").Select(x => x.Contribution).ToList();
                var excludes = g.Where(x => x.Item.Type == "excluded").Select(x => x.Contribution).ToList();
                var primary = grants.FirstOrDefault(x => !x.Cover.Future && x.Cover.From != null) ?? grants.FirstOrDefault() ?? excludes[0];
                return new UnitCoverItem(g.Key, primary.CustomerWording, primary.Category,
                    g.Key.StartsWith("cause:", StringComparison.Ordinal) ? [g.Key[6..]] : [],
                    grants.Any(x => !x.Cover.Future && x.Cover.From != null), grants, excludes, "—");
            }).OrderBy(x => x.CustomerWording).ToList();
        var serial = await db.Identifiers.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && x.UnitId == unitId && x.ValidTo == null)
            .OrderBy(x => x.Type == IdentifierType.InternalSerial ? 0 : 1).ThenBy(x => x.Type).Select(x => x.RawValue).FirstOrDefaultAsync(ct) ?? "Serial not recorded";
        var itemNames = await items.NamesAsync([unit.ItemId], ct);
        IReadOnlyDictionary<Guid, PartyLabel> partyNames = unit.OwnerPartyRef is { } owner ? await parties.NamesAsync([owner], ct) : new Dictionary<Guid, PartyLabel>();
        return new(unitId, serial, itemNames.TryGetValue(unit.ItemId, out var itemName) ? itemName.Name ?? itemName.Code : "Item unavailable",
            unit.OwnerPartyRef is { } party && partyNames.TryGetValue(party, out var name) ? name.Name ?? name.Code : null,
            today, covers, lines, lines.Count(x => x.Covered), lines.Count(x => !x.Covered), lines.Count, 0, lines.Count, ApplicabilityNotice);
    }

    public static WhatsCoveredPage Page(WhatsCoveredPage all, string? type, string? search, int skip, int take)
    {
        skip = Math.Max(0, skip); take = Math.Clamp(take, 1, 200);
        var needle = search?.Trim();
        bool Matches(string value) => string.IsNullOrEmpty(needle) || value.Contains(needle, StringComparison.OrdinalIgnoreCase);
        var filtered = all.Items.Where(x => type is null || (type == "covered" ? x.Covered : !x.Covered))
            .Where(x => Matches(x.CustomerWording) || x.CauseCodes.Any(c => Matches(c.Replace('_', ' ')))
                || x.Grants.Concat(x.Excludes).Any(c => Matches(c.CustomerWording) || Matches(c.Code)
                    || Matches(c.PartsScopeDescription) || Matches(c.Cover.PolicyName) || Matches(c.Cover.FamilyName))).ToList();
        return all with { Items = filtered.Skip(skip).Take(take).ToList(), Total = filtered.Count, Skip = skip, Take = take };
    }

    public async Task<ClaimGuidance?> GuidanceAsync(Guid unitId, string cause, CancellationToken ct)
    {
        var all = await ReadAsync(unitId, ct);
        if (all is null) return null;
        var matched = all.Items.Where(x => x.CauseCodes.Contains(cause)).ToList();
        var grants = matched.SelectMany(x => x.Grants).Where(x => !x.Cover.Future && x.Cover.From != null).ToList();
        var covers = grants.GroupBy(x => x.Cover.EntitlementId).Select(g => new GuidedCover(g.First().Cover,
            g.First().Cover.Usage.Select(u => new GuidanceUsage(u.MeterType, u.Period, u.Remaining,
                u.MeterType is "claims" or "claim" ? u.Remaining - 1 : null)).ToList(), g.ToList())).ToList();
        var wording = grants.Count > 0 ? string.Join(" ", grants.Select(x => x.CustomerWording).Distinct())
            : string.Join(" ", matched.SelectMany(x => x.Excludes).Where(x => !x.Cover.Future).Select(x => x.CustomerWording).Distinct());
        return new(cause, covers.Count > 0, covers, matched.SelectMany(x => x.Excludes).Where(x => !x.Cover.Future).ToList(),
            wording.Length == 0 ? "No customer wording has been recorded for this cause." : wording,
            "Guidance only · adjudication confirms the cover. Check any parts scope. Usage after this claim is unknown for meters other than claim counts; consumption is decided at adjudication.");
    }
}
