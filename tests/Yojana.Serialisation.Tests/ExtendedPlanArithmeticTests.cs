using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

/// <summary>P2-T07: plan eligibility, plan start and the straight-line revenue schedule published with Entitlement.ExtendedSold.</summary>
public sealed class ExtendedPlanArithmeticTests
{
    private static readonly BaseCoverage Base = new(Guid.NewGuid(), EntitlementStatus.Active, new DateOnly(2026, 9, 16), new DateOnly(2028, 9, 15));

    [Fact]
    public void Within_base_coverage_needs_a_live_base_that_has_not_ended()
    {
        var rule = new EligibilityRule(EligibilityRule.WithinBaseCoverage, "base_warranty");
        Assert.Null(ExtendedPlanArithmetic.Ineligible(rule, Base, new DateOnly(2028, 9, 15)));
        Assert.Equal("base_expired", ExtendedPlanArithmetic.Ineligible(rule, Base, new DateOnly(2028, 9, 16))?.Rule);
        Assert.Equal("base_not_live", ExtendedPlanArithmetic.Ineligible(rule, null, new DateOnly(2027, 1, 1))?.Rule);
        Assert.Equal("base_not_live", ExtendedPlanArithmetic.Ineligible(rule, Base with { Status = EntitlementStatus.Closed }, new DateOnly(2027, 1, 1))?.Rule);
        Assert.Null(ExtendedPlanArithmetic.Ineligible(null, null, new DateOnly(2030, 1, 1)));
    }

    [Fact]
    public void Within_days_counts_from_the_base_start_and_a_base_not_yet_started_is_inside_the_window()
    {
        var rule = new EligibilityRule(EligibilityRule.WithinDaysOfBaseStart, "base_warranty", 30);
        Assert.Null(ExtendedPlanArithmetic.Ineligible(rule, Base, new DateOnly(2026, 10, 16)));
        Assert.Equal("purchase_window_passed", ExtendedPlanArithmetic.Ineligible(rule, Base, new DateOnly(2026, 10, 17))?.Rule);
        Assert.Null(ExtendedPlanArithmetic.Ineligible(rule, Base with { Status = EntitlementStatus.PendingEvidence, StartDate = null, EndDate = null }, new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void A_plan_starts_at_purchase_or_the_day_after_the_base_last_covered_day()
    {
        Assert.Equal(new DateOnly(2027, 2, 3), ExtendedPlanArithmetic.Start(StartTrigger.PlanPurchase, new DateOnly(2027, 2, 3), null));
        Assert.Equal(new DateOnly(2028, 9, 16), ExtendedPlanArithmetic.Start(StartTrigger.BaseExpiry, new DateOnly(2027, 2, 3), Base));
        Assert.Null(ExtendedPlanArithmetic.Start(StartTrigger.BaseExpiry, new DateOnly(2027, 2, 3), Base with { EndDate = null }));
    }

    [Fact]
    public void The_schedule_cuts_by_calendar_month_in_proportion_to_days_and_sums_to_the_price()
    {
        // 2028-09-16 .. 2029-09-15 (365 days): 15 days in September 2028 first, 15 days in September 2029 last
        var schedule = ExtendedPlanArithmetic.RevenueSchedule(new DateOnly(2028, 9, 16), new DateOnly(2029, 9, 15), 199.00m);
        Assert.Equal(13, schedule.Count);
        Assert.Equal((new DateOnly(2028, 9, 16), new DateOnly(2028, 9, 30), 8.18m), (schedule[0].PeriodStart, schedule[0].PeriodEnd, schedule[0].Amount));
        Assert.Equal((new DateOnly(2028, 10, 1), new DateOnly(2028, 10, 31), 16.90m), (schedule[1].PeriodStart, schedule[1].PeriodEnd, schedule[1].Amount));
        Assert.Equal(new DateOnly(2029, 9, 15), schedule[^1].PeriodEnd);
        Assert.Equal(199.00m, schedule.Sum(p => p.Amount));
    }

    [Theory]
    [InlineData("2026-01-31", "2026-01-31", "0.01")]
    [InlineData("2024-02-29", "2027-02-28", "1000")]
    [InlineData("2026-10-16", "2031-10-15", "0.07")]
    [InlineData("2026-12-31", "2027-01-01", "99.99")]
    public void Periods_are_contiguous_cover_the_term_and_sum_to_the_price(string start, string end, string price)
    {
        var (from, to, amount) = (DateOnly.Parse(start), DateOnly.Parse(end), decimal.Parse(price, System.Globalization.CultureInfo.InvariantCulture));
        var schedule = ExtendedPlanArithmetic.RevenueSchedule(from, to, amount);
        Assert.Equal(from, schedule[0].PeriodStart);
        Assert.Equal(to, schedule[^1].PeriodEnd);
        for (var i = 1; i < schedule.Count; i++) Assert.Equal(schedule[i - 1].PeriodEnd.AddDays(1), schedule[i].PeriodStart);
        Assert.All(schedule, p => Assert.Equal(p.PeriodStart.Month, p.PeriodEnd.Month));
        Assert.Equal(amount, schedule.Sum(p => p.Amount));
    }
}
