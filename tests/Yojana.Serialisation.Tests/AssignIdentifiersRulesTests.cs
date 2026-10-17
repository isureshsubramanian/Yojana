using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;

namespace Yojana.Serialisation.Tests;

public sealed class AssignIdentifiersRulesTests
{
    [Fact]
    public void Plain_values_pair_in_allocation_order_and_report_the_overflow_line()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var pairs = AssignIdentifiersRules.PairLines(IdentifierAssignmentMode.AllocationOrder, [first, second], new HashSet<Guid> { first, second },
            [new("A-1"), new("A-2"), new("A-3")]);

        Assert.Equal(first, pairs[0].UnitId);
        Assert.Equal(second, pairs[1].UnitId);
        Assert.Null(pairs[2].UnitId);
        Assert.Equal("no_provisional_unit_available", pairs[2].Problem);
    }

    [Fact]
    public void Explicit_pairs_preserve_input_order_and_reject_missing_foreign_and_repeated_units()
    {
        var onOrder = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var pairs = AssignIdentifiersRules.PairLines(IdentifierAssignmentMode.ExplicitPairs, [onOrder], new HashSet<Guid> { onOrder },
            [new("A-1", onOrder), new("A-2"), new("A-3", foreign), new("A-4", onOrder)]);

        Assert.Null(pairs[0].Problem);
        Assert.Equal("unit_required_for_explicit_pair", pairs[1].Problem);
        Assert.Equal("unit_not_on_production_order", pairs[2].Problem);
        Assert.Equal("unit_repeated_in_list", pairs[3].Problem);
    }

    [Fact]
    public void A_plain_value_list_does_not_silently_accept_explicit_pairs()
    {
        var unit = Guid.NewGuid();
        var pair = Assert.Single(AssignIdentifiersRules.PairLines(IdentifierAssignmentMode.AllocationOrder, [unit], new HashSet<Guid> { unit }, [new("A-1", unit)]));
        Assert.Equal("unit_not_allowed_in_value_list", pair.Problem);
    }
}
