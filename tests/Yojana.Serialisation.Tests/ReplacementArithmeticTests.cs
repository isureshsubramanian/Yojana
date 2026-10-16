using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

/// <summary>P2-T06: replacement terms, including the monotonic chain property needed by case 59.</summary>
public sealed class ReplacementArithmeticTests
{
    private static readonly DurationRule TwoYears = new(24, DurationRule.Months, DurationRule.Clamp, EndInclusive: true);

    [Theory]
    [InlineData("carry_remaining", "2028-09-15")]
    [InlineData("max_remaining_or_min_days", "2028-09-15")]
    [InlineData("restart_full", "2029-01-09")]
    [InlineData("statutory_extension_once", "2029-09-15")]
    public void Each_rule_computes_the_promised_end(string rule, string expected)
    {
        var actual = ReplacementArithmetic.EndDate(new DateOnly(2028, 9, 15), new DateOnly(2027, 1, 10), TwoYears, rule, 90, 12);
        Assert.Equal(DateOnly.Parse(expected), actual);
    }

    [Fact]
    public void Chained_carry_and_minimum_replacements_never_shorten_the_inherited_term()
    {
        foreach (var firstReplacementDay in Enumerable.Range(0, 900))
        {
            var end = new DateOnly(2028, 9, 15);
            var replacement = new DateOnly(2026, 9, 16).AddDays(firstReplacementDay);
            for (var chainLength = 1; chainLength <= 25; chainLength++)
            {
                var next = ReplacementArithmetic.EndDate(end, replacement, TwoYears, ReplacementRule.MaxRemainingOrMinDays, 90, null);
                Assert.True(next >= end, $"chain {chainLength} shortened {end:yyyy-MM-dd} at {replacement:yyyy-MM-dd}");
                end = next;
                replacement = replacement.AddDays((chainLength * 37) % 181);
            }
        }
    }
}
