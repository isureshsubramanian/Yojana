using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>The pure sale decision: one policy row is mandatory, and the higher policy or jurisdiction floor wins.</summary>
public static class RefurbishedGoodsPolicyEngine
{
    public static RefurbishedEntitlementDisclosure Decide(Guid itemId, string grade, string channel, string country, string buyerKind,
        IReadOnlyList<RefurbishedTerm> rows, IReadOnlySet<string> applicableScopes, int? jurisdictionFloorMonths,
        Guid? refurbisherPartyRef)
    {
        if (!RefurbishedBuyerKind.All.Contains(buyerKind)) throw new CommandRefusedException("buyer_kind_unknown", $"'{buyerKind}' is not consumer or business.");
        var policyGrade = ConditionGrades.WarrantyPolicyGrade(grade);
        var row = rows.Where(r => ConditionGrades.WarrantyPolicyGrade(r.Grade) == policyGrade && r.Channel == channel
                && (r.ItemId is null || r.ItemId == itemId) && applicableScopes.Contains(r.Region))
            .OrderBy(r => r.ItemId is null ? 1 : 0).ThenBy(r => r.Region == country ? 0 : 1).FirstOrDefault()
            ?? throw new CommandRefusedException("refurbished_policy_missing", $"No refurbished term matches grade '{grade}', channel '{channel}' and country '{country}'.", "add a refurbished policy row for this sale");
        var policyFloor = row.StatutoryFloorMonths;
        var jurisdictionFloor = jurisdictionFloorMonths;
        var floorMonths = Math.Max(policyFloor ?? 0, jurisdictionFloor ?? 0);
        // On a tie the law is named: the disclosure cites the statutory floor rather than the policy that repeats it.
        var floorSource = floorMonths == 0 ? null : jurisdictionFloor is { } law && law >= (policyFloor ?? 0) ? "jurisdiction" : "policy";
        var term = Math.Max(row.DurationMonths, floorMonths);
        return new(term, term > row.DurationMonths, floorMonths == 0 ? null : floorMonths, floorSource, refurbisherPartyRef, row.StatementKind, row);
    }
}

/// <summary>Tenant second-hand floors stored as JSON under <see cref="Key"/>; defaults apply until the tenant overrides them.</summary>
public sealed class RefurbishedGoodsSettings(ITenantSettings settings, IJurisdictionRegionPort regions)
{
    public const string Key = "entitlements.refurbished_goods";
    public static readonly RefurbishedGoodsPolicy Default = new([]);

    public async Task<RefurbishedGoodsPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text ? JsonSerializer.Deserialize<RefurbishedGoodsPolicy>(text, LedgerJson.Options) ?? Default : Default;

    public async Task SetAsync(RefurbishedGoodsPolicy policy, CancellationToken cancellationToken)
    {
        var problems = Validate(policy);
        if (problems.Count > 0) throw new CommandRefusedException("refurbished_goods_policy_invalid", string.Join("; ", problems));
        foreach (var code in policy.Floors.Select(floor => floor.Jurisdiction).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!await regions.IsKnownScopeAsync(code, cancellationToken))
                throw new CommandRefusedException("refurbished_region_unknown",
                    $"Region code '{code}' is neither WORLD, an ISO 3166-1 country nor a tenant region group.",
                    $"define '{code}' in Settings → Jurisdictions, or use an ISO 3166-1 alpha-2 country code");
        }
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }

    public static IReadOnlyList<string> Validate(RefurbishedGoodsPolicy policy)
    {
        var problems = new List<string>();
        foreach (var floor in policy.Floors)
        {
            if (!RefurbishedBuyerKind.All.Contains(floor.BuyerKind)) problems.Add($"'{floor.BuyerKind}' is not a buyer kind");
            if (floor.MinimumMonths <= 0) problems.Add($"{floor.Jurisdiction}/{floor.BuyerKind}: minimum months must be positive");
            if (floor.Jurisdiction.Length is 0 or > 40) problems.Add($"'{floor.Jurisdiction}' is not a jurisdiction or region-group code");
        }
        foreach (var duplicate in policy.Floors.GroupBy(f => (f.Jurisdiction, f.BuyerKind)).Where(g => g.Count() > 1)) problems.Add($"{duplicate.Key.Jurisdiction}/{duplicate.Key.BuyerKind} is defined twice");
        return problems;
    }
}
