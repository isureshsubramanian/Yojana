using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;

namespace Yojana.Serialisation.Tests;

public sealed class MergeUnitsRulesTests
{
    [Fact]
    public void Preview_default_is_earliest_commissioned_then_unit_id()
    {
        var early = Guid.Parse("00000000-0000-0000-0000-000000000099");
        var tieWinner = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var at = new DateTime(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(early, MergeUnitsRules.RecommendedSurvivor([(Guid.NewGuid(), at.AddMinutes(1)), (early, at)]));
        Assert.Equal(tieWinner, MergeUnitsRules.RecommendedSurvivor([(early, at), (tieWinner, at)]));
    }

    [Fact]
    public void Pair_refusals_are_named()
    {
        var id = Guid.NewGuid();
        Assert.Equal("cannot_merge_unit_into_itself", MergeUnitsRules.PairProblem(id, id, LifecycleStatus.Released, LifecycleStatus.Released)!.Rule);
        Assert.Equal("unit_already_merged", MergeUnitsRules.PairProblem(Guid.NewGuid(), Guid.NewGuid(), LifecycleStatus.Merged, LifecycleStatus.Released)!.Rule);
    }

    [Fact]
    public void Different_shipped_parties_means_two_units()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Assert.True(MergeUnitsRules.DifferentShippedParties([a], [b]));
        Assert.False(MergeUnitsRules.DifferentShippedParties([a], [a]));
        Assert.False(MergeUnitsRules.DifferentShippedParties([], [b]));
    }
}
