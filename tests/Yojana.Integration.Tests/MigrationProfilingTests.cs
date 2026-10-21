using Yojana.Integration.LegacyMigration.Profiling;

namespace Yojana.Integration.Tests;

public sealed class MigrationProfilingTests
{
    [Fact]
    public void Fixed_pack_has_stable_unique_codes_and_only_two_hard_stops()
    {
        Assert.Equal(14, MigrationProfileClasses.All.Count);
        Assert.Equal(14, MigrationProfileClasses.All.Select(item => item.Code)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
        [
            MigrationProfileClasses.SerialInMultipleWarehouses.Code,
            MigrationProfileClasses.NegativeQuantity.Code,
        ], MigrationProfileClasses.All.Where(item => item.HardStop).Select(item => item.Code));
        Assert.All(MigrationProfileClasses.All.Where(item => item.HardStop),
            item => Assert.Equal(MigrationProfileOutcome.StopLoad, item.DefaultOutcome));
    }

    [Theory]
    [InlineData(MigrationProfileOutcome.StopLoad, "Stop the load")]
    [InlineData(MigrationProfileOutcome.NeedsPerson, "Go to a person")]
    [InlineData(MigrationProfileOutcome.FixedByRule, "Fixed by a rule")]
    [InlineData(MigrationProfileOutcome.LoadedWithNote, "Loaded with a note")]
    public void Outcomes_have_readable_labels(string outcome, string label)
    {
        Assert.Equal(label, MigrationProfileOutcome.Label(outcome));
    }
}
