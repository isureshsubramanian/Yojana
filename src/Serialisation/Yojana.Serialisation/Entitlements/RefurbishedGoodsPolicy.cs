using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>The pure sale decision: one policy row is mandatory, and the higher policy or jurisdiction floor wins.</summary>
public static class RefurbishedGoodsPolicyEngine
{
    public static RefurbishedEntitlementDisclosure Decide(Guid itemId, string grade, string channel, string country, string buyerKind,
        IReadOnlyList<RefurbishedTerm> rows, RefurbishedGoodsPolicy tenantPolicy, Guid? refurbisherPartyRef)
    {
        if (!RefurbishedBuyerKind.All.Contains(buyerKind)) throw new CommandRefusedException("buyer_kind_unknown", $"'{buyerKind}' is not consumer or business.");
        var policyGrade = ConditionGrades.WarrantyPolicyGrade(grade);
        var row = rows.Where(r => ConditionGrades.WarrantyPolicyGrade(r.Grade) == policyGrade && r.Channel == channel && (r.ItemId is null || r.ItemId == itemId) && Regions.Contains(r.Region, country))
            .OrderBy(r => r.ItemId is null ? 1 : 0).ThenBy(r => r.Region == country ? 0 : 1).FirstOrDefault()
            ?? throw new CommandRefusedException("refurbished_policy_missing", $"No refurbished term matches grade '{grade}', channel '{channel}' and country '{country}'.", "add a refurbished policy row for this sale");
        var jurisdiction = tenantPolicy.Floors.Where(f => f.BuyerKind == buyerKind && Regions.Contains(f.Jurisdiction, country))
            .OrderBy(f => f.Jurisdiction == country ? 0 : 1).ThenByDescending(f => f.MinimumMonths).FirstOrDefault();
        var policyFloor = row.StatutoryFloorMonths;
        var jurisdictionFloor = jurisdiction?.MinimumMonths;
        var floorMonths = Math.Max(policyFloor ?? 0, jurisdictionFloor ?? 0);
        // On a tie the law is named: the disclosure cites the statutory floor rather than the policy that repeats it.
        var floorSource = floorMonths == 0 ? null : jurisdictionFloor is { } law && law >= (policyFloor ?? 0) ? "jurisdiction" : "policy";
        var term = Math.Max(row.DurationMonths, floorMonths);
        return new(term, term > row.DurationMonths, floorMonths == 0 ? null : floorMonths, floorSource, refurbisherPartyRef, row.StatementKind, row);
    }
}

/// <summary>Tenant second-hand floors stored as JSON under <see cref="Key"/>; defaults apply until the tenant overrides them.</summary>
public sealed class RefurbishedGoodsSettings(ITenantSettings settings)
{
    public const string Key = "entitlements.refurbished_goods";
    public static readonly RefurbishedGoodsPolicy Default = new([new("EU", RefurbishedBuyerKind.Consumer, 12)]);

    public async Task<RefurbishedGoodsPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text ? JsonSerializer.Deserialize<RefurbishedGoodsPolicy>(text, LedgerJson.Options) ?? Default : Default;

    public async Task SetAsync(RefurbishedGoodsPolicy policy, CancellationToken cancellationToken)
    {
        var problems = Validate(policy);
        if (problems.Count > 0) throw new CommandRefusedException("refurbished_goods_policy_invalid", string.Join("; ", problems));
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }

    public static IReadOnlyList<string> Validate(RefurbishedGoodsPolicy policy)
    {
        var problems = new List<string>();
        foreach (var floor in policy.Floors)
        {
            if (!RefurbishedBuyerKind.All.Contains(floor.BuyerKind)) problems.Add($"'{floor.BuyerKind}' is not a buyer kind");
            if (floor.MinimumMonths <= 0) problems.Add($"{floor.Jurisdiction}/{floor.BuyerKind}: minimum months must be positive");
            if (!(floor.Jurisdiction is "EU" or "EEA" or "EFTA" or "UK" or "WORLD") && (floor.Jurisdiction.Length != 2 || !floor.Jurisdiction.All(char.IsAsciiLetterUpper))) problems.Add($"'{floor.Jurisdiction}' is not a jurisdiction");
        }
        foreach (var duplicate in policy.Floors.GroupBy(f => (f.Jurisdiction, f.BuyerKind)).Where(g => g.Count() > 1)) problems.Add($"{duplicate.Key.Jurisdiction}/{duplicate.Key.BuyerKind} is defined twice");
        return problems;
    }
}
