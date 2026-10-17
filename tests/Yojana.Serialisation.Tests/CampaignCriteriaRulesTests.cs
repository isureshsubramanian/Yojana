using Xunit;
using Yojana.Serialisation.Campaigns;

namespace Yojana.Serialisation.Tests;

public sealed class CampaignCriteriaRulesTests
{
    private sealed record Criterion(string Kind, string Value);

    [Fact]
    public void Criteria_are_or_within_a_kind_and_and_across_kinds()
    {
        Criterion[] criteria =
        [
            new("lot", "lot-a"),
            new("lot", "lot-b"),
            new("firmware", "2.4.1"),
        ];

        Assert.True(CampaignCriteriaRules.Matches(criteria, c => c.Kind,
            c => c.Value is "lot-b" or "2.4.1"));
        Assert.False(CampaignCriteriaRules.Matches(criteria, c => c.Kind,
            c => c.Value is "lot-b" or "2.4.0"));
    }

    [Theory]
    [InlineData("VX2610000001", "VX2610000001", "VX2610000010", true)]
    [InlineData("VX2610000005", "VX2610000001", "VX2610000010", true)]
    [InlineData("VX2610000010", "VX2610000001", "VX2610000010", true)]
    [InlineData("VX2610000011", "VX2610000001", "VX2610000010", false)]
    [InlineData("VX2609999999", "VX2610000001", "VX2610000010", false)]
    public void Identifier_range_compares_the_schemes_normalised_fixed_width_form(
        string value, string start, string end, bool expected)
    {
        Assert.Equal(expected, CampaignCriteriaRules.IdentifierInRange(value, start, end));
    }
}

