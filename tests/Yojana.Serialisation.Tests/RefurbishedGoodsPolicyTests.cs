using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

public sealed class RefurbishedGoodsPolicyTests
{
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly RefurbishedTerm Eu = new(null, "B", "direct", "EU", 6, 9, RefurbishedWarrantyStatement.SellerRefurbished);

    [Fact]
    public void Jurisdiction_floor_above_the_policy_floor_wins()
    {
        var result = RefurbishedGoodsPolicyEngine.Decide(Item, "B", "direct", "DE", RefurbishedBuyerKind.Consumer, [Eu], Scopes("DE", "EU"), 12, Guid.Empty);
        Assert.Equal((12, true, 12, "jurisdiction", RefurbishedWarrantyStatement.SellerRefurbished, (Guid?)Guid.Empty),
            (result.TermMonths, result.FloorApplied, result.FloorMonths, result.FloorSource, result.StatementKind, result.RefurbisherPartyRef));
    }

    [Fact]
    public void Policy_floor_above_the_jurisdiction_floor_wins()
    {
        var result = RefurbishedGoodsPolicyEngine.Decide(Item, "B", "direct", "DE", RefurbishedBuyerKind.Consumer, [Eu with { StatutoryFloorMonths = 18 }], Scopes("DE", "EU"), 12, null);
        Assert.Equal((18, true, 18, "policy"), (result.TermMonths, result.FloorApplied, result.FloorMonths, result.FloorSource));
    }

    [Fact]
    public void A_tie_between_policy_and_jurisdiction_floors_names_the_jurisdiction()
    {
        var result = RefurbishedGoodsPolicyEngine.Decide(Item, "B", "direct", "DE", RefurbishedBuyerKind.Consumer, [Eu with { StatutoryFloorMonths = 12 }], Scopes("DE", "EU"), 12, null);
        Assert.Equal((12, 12, "jurisdiction"), (result.TermMonths, result.FloorMonths, result.FloorSource));
    }

    [Fact]
    public void Business_buyer_gets_no_consumer_floor()
    {
        var result = RefurbishedGoodsPolicyEngine.Decide(Item, "B", "direct", "DE", RefurbishedBuyerKind.Business, [Eu with { StatutoryFloorMonths = null }], Scopes("DE", "EU"), null, null);
        Assert.Equal((6, false, (int?)null, (string?)null), (result.TermMonths, result.FloorApplied, result.FloorMonths, result.FloorSource));
    }

    [Fact]
    public void Most_specific_row_and_each_statement_kind_are_policy_outputs()
    {
        foreach (var statement in RefurbishedWarrantyStatement.All)
        {
            var exact = Eu with { ItemId = Item, Region = "DE", DurationMonths = 15, StatementKind = statement };
            var result = RefurbishedGoodsPolicyEngine.Decide(Item, "B", "direct", "DE", RefurbishedBuyerKind.Business, [Eu, exact], Scopes("DE", "EU"), null, null);
            Assert.Same(exact, result.MatchedRow);
            Assert.Equal((15, statement), (result.TermMonths, result.StatementKind));
        }
    }

    [Fact]
    public void A_non_new_sale_without_a_matching_row_is_refused()
    {
        var error = Assert.Throws<CommandRefusedException>(() => RefurbishedGoodsPolicyEngine.Decide(Item, "C", "direct", "DE", RefurbishedBuyerKind.Consumer, [Eu], Scopes("DE", "EU"), null, null));
        Assert.Equal("refurbished_policy_missing", error.Rule);
    }

    private static HashSet<string> Scopes(params string[] values) => values.ToHashSet(StringComparer.Ordinal);
}
