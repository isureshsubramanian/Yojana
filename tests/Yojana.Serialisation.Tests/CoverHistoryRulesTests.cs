using Xunit;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

public sealed class CoverHistoryRulesTests
{
    [Theory]
    [InlineData(2025, 11, 4, 2026, 3, 12, 4)]
    [InlineData(2025, 11, 4, 2026, 6, 2, 6)]
    [InlineData(2026, 1, 31, 2026, 2, 28, 1)]
    [InlineData(2026, 1, 31, 2026, 2, 27, 0)]
    [InlineData(2026, 1, 15, 2026, 1, 15, 0)]
    public void Months_are_whole_calendar_months_with_clamped_month_ends(int sy, int sm, int sd, int ey, int em, int ed, int expected) =>
        Assert.Equal(expected, CoverHistoryRules.WholeMonths(new(sy, sm, sd), new(ey, em, ed)));
}
