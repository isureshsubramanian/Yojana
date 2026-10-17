using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Queries;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class CorrectionKpiRulesTests
{
    [Fact]
    public void Month_bucket_is_stable_and_zero_padded() =>
        Assert.Equal("2026-09", CorrectionKpiRules.MonthBucket(2026, 9));

    [Fact]
    public void First_ninety_days_build_the_baseline()
    {
        var first = new DateOnly(2026, 1, 1);
        Assert.Equal(new(CorrectionKpiStatus.BaselineBuilding, 0, 90, first, new DateOnly(2026, 4, 1)),
            CorrectionKpiRules.Baseline(first, first));
        Assert.Equal(new(CorrectionKpiStatus.BaselineBuilding, 89, 1, first, new DateOnly(2026, 4, 1)),
            CorrectionKpiRules.Baseline(first, new DateOnly(2026, 3, 31)));
    }

    [Fact]
    public void Targets_take_effect_on_day_ninety()
    {
        var first = new DateOnly(2026, 1, 1);
        Assert.Equal(new(CorrectionKpiStatus.Ready, 90, 0, first, new DateOnly(2026, 4, 1)),
            CorrectionKpiRules.Baseline(first, new DateOnly(2026, 4, 1)));
    }

    [Fact]
    public void No_history_reports_the_full_baseline_window() =>
        Assert.Equal(new(CorrectionKpiStatus.BaselineBuilding, 0, 90, null, null),
            CorrectionKpiRules.Baseline(null, new DateOnly(2026, 9, 1)));

    [Fact]
    public void Targets_are_non_negative_unique_root_causes()
    {
        Assert.Empty(CorrectionKpiRules.Validate(new([new(1, 2.5m), new(7, 1m)])));
        Assert.Contains("a root cause has more than one target", CorrectionKpiRules.Validate(new([new(1, 1m), new(1, 2m)])));
        Assert.Contains("root-cause categories must be between 1 and 7", CorrectionKpiRules.Validate(new([new(8, 1m)])));
        Assert.Contains("targets cannot be negative", CorrectionKpiRules.Validate(new([new(1, -0.1m)])));
    }

    [Theory]
    [InlineData("=SUM(A1)", "'=SUM(A1)")]
    [InlineData("plain", "plain")]
    public void Csv_cells_are_formula_safe(string value, string expected) =>
        Assert.Equal(expected, CorrectionKpiCsv.Cell(value));
}
