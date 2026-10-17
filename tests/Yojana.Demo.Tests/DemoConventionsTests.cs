using Xunit;

namespace Yojana.Demo.Tests;

public sealed class DemoConventionsTests
{
    [Fact]
    public void Stable_names_produce_repeatable_version_five_style_ids()
    {
        var first = DemoIds.FromName("claim:twin-dimm");

        Assert.Equal(first, DemoIds.FromName("claim:twin-dimm"));
        Assert.NotEqual(first, DemoIds.FromName("claim:storage-nvme"));
        Assert.Equal('5', first.ToString("D")[14]);
        Assert.Contains(first.ToString("D")[19], "89ab");
    }

    [Fact]
    public void Date_spreading_keeps_claims_inside_the_requested_window()
    {
        var today = new DateOnly(2026, 10, 17);

        var dates = DemoDates.Spread(today, 25, 24, 1);

        Assert.Equal(25, dates.Count);
        Assert.Equal(today.AddDays(-24), dates[0]);
        Assert.Equal(today.AddDays(-1), dates[^1]);
        Assert.True(dates.Zip(dates.Skip(1)).All(pair => pair.First <= pair.Second));
        Assert.All(dates, date => Assert.InRange(today.DayNumber - date.DayNumber, 1, 24));
    }
}
