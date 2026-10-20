using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Yojana.Serialisation.Ledger;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

public sealed class WhatsCoveredQuery(SerialisationDbContext db, ITenantContext tenant, IClock clock,
    IItemNamePort items, IPartyNamePort parties, Yojana.Serialisation.TimeZones.TenantTimeZoneSettings zones, IItemFamilyNamePort itemFamilies, IItemPolicyPort itemPolicies)
{
    public const string ApplicabilityNotice = "— Region-scoped policy availability needs the unit’s country, which is not recorded. Parts-scoped items require the failed part to be checked.";

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
        var mappedCodes = (await itemPolicies.PoliciesForAsync(unit.ItemId, ct)).Select(x => x.PolicyCode).Distinct().ToList();
        var offered = mappedCodes.Count == 0 ? [] : (await db.PolicyVersions.AsNoTracking()
            .Where(x => x.TenantId == tenant.TenantId && mappedCodes.Contains(x.PolicyCode)
                && x.Status == PolicyVersionStatus.Published && x.AppliesToContractsFrom <= today).ToListAsync(ct))
            .GroupBy(x => x.PolicyCode).Select(g => g.OrderByDescending(x => x.AppliesToContractsFrom).ThenByDescending(x => x.Version).First()).ToList();
        var policyIds = rows.Select(x => x.Policy.PolicyVersionId).Concat(offered.Select(x => x.PolicyVersionId)).Distinct().ToList();
        var entitlementIds = rows.Select(x => x.Entitlement.EntitlementId).ToList();
        var itemRows = await db.PolicyCoverItems.AsNoTracking().Where(x => x.TenantId == tenant.TenantId && policyIds.Contains(x.PolicyVersionId)).ToListAsync(ct);
        var causes = await (from link in db.PolicyCoverItemCauses.AsNoTracking()
            join cause in db.ClaimCauses.AsNoTracking() on new { link.TenantId, Code = link.CauseCode } equals new { cause.TenantId, cause.Code }
            where link.TenantId == tenant.TenantId && policyIds.Contains(link.PolicyVersionId)
            select new { link.PolicyVersionId, link.ItemCode, link.CauseCode, cause.CustomerWording }).ToListAsync(ct);
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
                    item.PartsScope.PartCategories.Concat(item.PartsScope.ItemFamilies.Select(code => familyNames.FirstOrDefault(f => f.Code == code)?.Name ?? "Item family unavailable"))), item.CauseCodes));
        })).ToList();
        // One line per promise: the same type, category and customer wording across covers (a base and an extended
        // cover copying one item show once, with both covers). An item answering several causes stays one line.
        // An exclusion is named on every covered line that grants one of its causes (grant over exclusion), and keeps
        // its own Not covered line only while some of its causes, or no cause at all, are granted nowhere.
        // Guidance still matches each contribution's own cause codes.
        static string Promise(PolicyCoverItem item) => $"{item.Category}\u001f{item.CustomerWording}";
        var granted = contributions.Where(x => x.Item.Type == "covered").GroupBy(x => Promise(x.Item)).Select(g => g.ToList()).ToList();
        var grantedCauses = granted.SelectMany(g => g.SelectMany(x => x.Item.CauseCodes)).ToHashSet();
        var excluded = contributions.Where(x => x.Item.Type == "excluded").GroupBy(x => Promise(x.Item)).Select(g => g.ToList()).ToList();
        string CauseName(string code) => causes.First(x => x.CauseCode == code).CustomerWording;
        var coveredLines = granted.Select(g =>
        {
            var answeredCauses = g.SelectMany(x => x.Item.CauseCodes).Distinct().Order().ToList();
            var grants = g.Select(x => x.Contribution).ToList();
            var excludes = excluded.SelectMany(e => e).Where(e => e.Item.CauseCodes.Intersect(answeredCauses).Any()).Select(e => e.Contribution).ToList();
            var primary = grants.FirstOrDefault(x => !x.Cover.Future && x.Cover.From != null) ?? grants[0];
            return new UnitCoverItem("covered:" + Promise(g[0].Item), primary.CustomerWording, primary.Category, answeredCauses,
                true, grants, excludes, Availability(answeredCauses), answeredCauses.Select(CauseName).ToList());
        });
        var excludedLines = excluded.Where(g => g.Any(x => x.Item.CauseCodes.Count == 0 || x.Item.CauseCodes.Any(c => !grantedCauses.Contains(c)))).Select(g =>
        {
            var answeredCauses = g.SelectMany(x => x.Item.CauseCodes).Distinct().Order().ToList();
            var primary = g[0].Contribution;
            return new UnitCoverItem("excluded:" + Promise(g[0].Item), primary.CustomerWording, primary.Category, answeredCauses,
                false, [], g.Select(x => x.Contribution).ToList(), Availability(answeredCauses), answeredCauses.Select(CauseName).ToList());
        });
        var lines = coveredLines.Concat(excludedLines).OrderBy(x => x.CustomerWording).ToList();

        string Availability(IReadOnlyList<string> answeredCauses)
        {
            if (offered.Count == 0) return "No · not offered on this item";
            var results = new List<string>();
            foreach (var policy in offered)
            {
                var coveredCodes = itemRows.Where(x => x.PolicyVersionId == policy.PolicyVersionId && x.Type == "covered").Select(x => x.Code).ToHashSet();
                if (!causes.Any(x => x.PolicyVersionId == policy.PolicyVersionId && coveredCodes.Contains(x.ItemCode) && answeredCauses.Contains(x.CauseCode))) continue;
                var scope = JsonSerializer.Deserialize<List<string>>(policy.RegionScope, LedgerJson.Options) ?? [];
                // The unit read model has no country. WORLD needs no country; scoped offers remain explicitly unknown.
                if (!scope.Contains("WORLD")) { results.Add("—"); continue; }
                var rule = policy.Eligibility is null ? null : JsonSerializer.Deserialize<EligibilityRule>(policy.Eligibility, LedgerJson.Options);
                var baseCover = rows.Select(x => x.Entitlement).FirstOrDefault(x => x.CoverageFamily == (rule?.BaseFamily ?? CoverageFamilyCode.Base));
                var day = baseCover is null ? today : TermArithmetic.LocalDay(asOfInstant, baseCover.StartTimeZone);
                if (rule is not null && (baseCover?.StartDate is null || baseCover.StartDate > day
                    || ExtendedPlanArithmetic.Ineligible(rule, new(baseCover.EntitlementId, baseCover.Status, baseCover.StartDate, baseCover.EndDate), day) is not null)) continue;
                var start = policy.StartTrigger == StartTrigger.BaseExpiry ? baseCover?.EndDate?.AddDays(1) : day;
                if (start is null) { results.Add("—"); continue; }
                var end = TermArithmetic.EndDate(start.Value, new(policy.DurationValue, policy.DurationUnit, policy.EndOfMonthRule, policy.EndInclusive));
                results.Add($"Yes · {policy.Name}, until {end:yyyy-MM-dd}");
            }
            return results.Count == 0 ? "No" : string.Join("; ", results.Distinct());
        }
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
        var filtered = FilterItems(all, type, search);
        return all with { Items = filtered.Skip(skip).Take(take).ToList(), Total = filtered.Count, Skip = skip, Take = take };
    }

    private static List<UnitCoverItem> FilterItems(WhatsCoveredPage all, string? type, string? search)
    {
        var needle = search?.Trim();
        bool Matches(string value) => string.IsNullOrEmpty(needle) || value.Contains(needle, StringComparison.OrdinalIgnoreCase);
        return all.Items.Where(x => type is null || (type == "covered" ? x.Covered : !x.Covered))
            .Where(x => Matches(x.CustomerWording) || x.CauseCodes.Any(c => Matches(c.Replace('_', ' ')))
                || x.Grants.Concat(x.Excludes).Any(c => Matches(c.CustomerWording) || Matches(c.Code)
                    || Matches(c.PartsScopeDescription) || Matches(c.Cover.PolicyName) || Matches(c.Cover.FamilyName))).ToList();
    }

    public static WhatsCoveredPage Sections(WhatsCoveredPage all, WhatsCoveredRequest request)
    {
        var matches = FilterItems(all, null, request.Search);
        var covered = matches.Where(x => x.Covered).ToList();
        var excluded = matches.Where(x => !x.Covered).ToList();
        return all with { Items = covered.Skip(Math.Max(0, request.CoveredSkip)).Take(Math.Clamp(request.CoveredTake, 1, 200))
            .Concat(excluded.Skip(Math.Max(0, request.ExcludedSkip)).Take(Math.Clamp(request.ExcludedTake, 1, 200))).ToList(),
            Total = matches.Count, CoveredTotal = covered.Count, ExcludedTotal = excluded.Count };
    }

    public async Task<ClaimGuidance?> GuidanceAsync(Guid unitId, string cause, CancellationToken ct)
        => await GuidanceAsync(unitId, cause, null, null, ct);

    public async Task<ClaimGuidance?> GuidanceAsync(Guid unitId, string cause, DateOnly? claimDate, DateOnly? occurredDate, CancellationToken ct)
    {
        var all = await ReadAsync(unitId, ct);
        if (all is null) return null;
        var matched = all.Items.Where(x => x.CauseCodes.Contains(cause)).ToList();
        var grants = matched.SelectMany(x => x.Grants).Where(x => !x.Cover.Future && x.Cover.From != null && x.CauseCodes!.Contains(cause)).ToList();
        var entitlementIds = grants.Select(x => x.Cover.EntitlementId).Distinct().ToList();
        var waitingPeriods = await (from entitlement in db.Entitlements.AsNoTracking()
            join policy in db.PolicyVersions.AsNoTracking() on new { entitlement.TenantId, entitlement.PolicyVersionId } equals new { policy.TenantId, policy.PolicyVersionId }
            where entitlement.TenantId == tenant.TenantId && entitlementIds.Contains(entitlement.EntitlementId)
            select new { entitlement.EntitlementId, entitlement.PurchaseDate, policy.NoClaimWithinDaysOfPurchase }).ToDictionaryAsync(x => x.EntitlementId, ct);
        var incidentDate = occurredDate ?? claimDate;
        var covers = grants.GroupBy(x => x.Cover.EntitlementId).Select(g => new GuidedCover(g.First().Cover,
            g.First().Cover.Usage.Select(u => new GuidanceUsage(u.MeterType, u.Period, u.Remaining,
                null)).ToList(), g.ToList(), WaitingUntil(g.Key))).ToList();

        DateOnly? WaitingUntil(Guid entitlementId)
        {
            if (incidentDate is null || !waitingPeriods.TryGetValue(entitlementId, out var terms)
                || terms.PurchaseDate is not { } purchase || (terms.NoClaimWithinDaysOfPurchase ?? 0) <= 0) return null;
            var firstClaimable = purchase.AddDays(terms.NoClaimWithinDaysOfPurchase!.Value);
            return incidentDate < firstClaimable ? firstClaimable : null;
        }
        // An exclusion is named on every covered line it touches; count it once.
        var excludes = matched.SelectMany(x => x.Excludes).Where(x => !x.Cover.Future && x.CauseCodes!.Contains(cause))
            .DistinctBy(x => (x.Cover.EntitlementId, x.Code)).ToList();
        var wording = grants.Count > 0 ? string.Join(" · ", grants.Select(x => x.CustomerWording).Distinct())
            : string.Join(" · ", excludes.Select(x => x.CustomerWording).Distinct());
        return new(cause, covers.Count > 0, covers, excludes,
            wording.Length == 0 ? "No customer wording has been recorded for this cause." : wording,
            "Guidance only · adjudication confirms the cover. Check any parts scope. Usage after this claim is unknown; consumption is decided at adjudication.");
    }
}
