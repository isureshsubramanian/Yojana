using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.Tests;

/// <summary>P2-T04: the term arithmetic, by example and as properties over every start day of thirteen years (cases 112, 113).</summary>
public sealed class TermArithmeticTests
{
    private static readonly int[] Terms = [1, 3, 6, 12, 18, 24, 36, 60];

    private static IEnumerable<DateOnly> EveryDay(int fromYear = 2020, int toYear = 2032)
    {
        for (var d = new DateOnly(fromYear, 1, 1); d <= new DateOnly(toYear, 12, 31); d = d.AddDays(1)) yield return d;
    }

    [Theory]
    [InlineData("2026-10-02", 24, "clamp", true, "2028-10-01")]
    [InlineData("2026-10-02", 24, "clamp", false, "2028-10-02")]
    [InlineData("2024-02-29", 12, "clamp", true, "2025-02-27")]
    [InlineData("2024-02-29", 12, "roll", true, "2025-02-28")]
    [InlineData("2024-02-29", 48, "clamp", false, "2028-02-29")]
    [InlineData("2026-01-31", 1, "clamp", false, "2026-02-28")]
    [InlineData("2026-01-31", 1, "roll", false, "2026-03-03")]
    [InlineData("2028-01-31", 1, "roll", false, "2028-03-02")]
    [InlineData("2026-03-31", 6, "clamp", true, "2026-09-29")]
    [InlineData("2026-08-31", 6, "clamp", false, "2027-02-28")]
    public void Months_end_by_the_rule(string start, int months, string endOfMonth, bool inclusive, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), TermArithmetic.EndDate(DateOnly.Parse(start), new DurationRule(months, DurationRule.Months, endOfMonth, inclusive)));

    [Theory]
    [InlineData("2026-10-02", 30, true, "2026-10-31")]
    [InlineData("2026-10-02", 30, false, "2026-11-01")]
    [InlineData("2028-02-28", 2, true, "2028-02-29")]
    public void Days_count_the_start_day_when_inclusive(string start, int days, bool inclusive, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), TermArithmetic.EndDate(DateOnly.Parse(start), new DurationRule(days, DurationRule.Days, DurationRule.Clamp, inclusive)));

    [Fact]
    public void Clamp_keeps_the_day_of_month_when_it_exists_otherwise_the_month_end_and_is_monotonic()
    {
        foreach (var n in Terms)
        {
            DateOnly? previous = null;
            foreach (var start in EveryDay())
            {
                var anniversary = TermArithmetic.EndDate(start, new DurationRule(n, DurationRule.Months, DurationRule.Clamp, EndInclusive: false));
                var target = start.AddMonths(n);
                var daysInTarget = DateTime.DaysInMonth(target.Year, target.Month);
                Assert.Equal(Math.Min(start.Day, daysInTarget), anniversary.Day);
                Assert.Equal((start.Year * 12) + start.Month - 1 + n, (anniversary.Year * 12) + anniversary.Month - 1);
                Assert.InRange(anniversary.DayNumber - start.DayNumber, (28 * n) - 3, 31 * n);
                if (previous is { } p) Assert.True(anniversary >= p, $"clamp {n} m: start {start} ended before the day before it");
                previous = anniversary;
                // the inclusive end is the day before the anniversary, always
                Assert.Equal(anniversary.AddDays(-1), TermArithmetic.EndDate(start, new DurationRule(n, DurationRule.Months, DurationRule.Clamp, EndInclusive: true)));
            }
        }
    }

    [Fact]
    public void Roll_never_ends_before_clamp_carries_at_most_three_days_and_is_not_monotonic_at_month_ends()
    {
        var nonMonotonic = 0;
        foreach (var n in Terms)
        {
            DateOnly? previous = null;
            foreach (var start in EveryDay())
            {
                var rule = new DurationRule(n, DurationRule.Months, DurationRule.Roll, EndInclusive: false);
                var roll = TermArithmetic.EndDate(start, rule);
                var clamp = TermArithmetic.EndDate(start, rule with { EndOfMonth = DurationRule.Clamp });
                Assert.InRange(roll.DayNumber - clamp.DayNumber, 0, 3);
                if (roll != clamp) Assert.True(start.Day > 28, $"roll differs from clamp only for days 29-31, not {start}");
                if (previous is { } p && roll < p) nonMonotonic++;
                previous = roll;
            }
        }

        // The validator's warning rests on this: roll does go backwards somewhere.
        Assert.True(nonMonotonic > 0);
    }

    [Theory]
    [InlineData("2026-10-02T23:30:00Z", "Europe/Berlin", "2026-10-03")]
    [InlineData("2026-10-02T23:30:00Z", "America/Los_Angeles", "2026-10-02")]
    [InlineData("2026-10-02T10:30:00Z", "Pacific/Kiritimati", "2026-10-03")]
    [InlineData("2026-10-02T10:30:00Z", "Pacific/Pago_Pago", "2026-10-01")]
    [InlineData("2026-03-29T00:30:00Z", "Europe/Berlin", "2026-03-29")]
    [InlineData("2026-03-28T23:30:00Z", "Europe/Berlin", "2026-03-29")]
    [InlineData("2026-10-24T22:30:00Z", "Europe/Berlin", "2026-10-25")]
    [InlineData("2026-10-24T21:30:00Z", "Europe/Berlin", "2026-10-24")]
    [InlineData("2026-10-02T18:45:00Z", "Asia/Kolkata", "2026-10-03")]
    public void The_reference_zone_decides_the_calendar_day_of_an_instant(string utc, string zone, string expected) =>
        Assert.Equal(DateOnly.Parse(expected), TermArithmetic.LocalDay(DateTime.Parse(utc, null, System.Globalization.DateTimeStyles.AdjustToUniversal), zone));

    [Fact]
    public void Across_a_year_of_hours_the_local_day_never_goes_back_and_never_skips_whatever_the_zone_and_daylight_saving()
    {
        foreach (var zone in new[] { "Europe/Berlin", "Europe/London", "America/New_York", "Australia/Lord_Howe", "Pacific/Kiritimati", "Pacific/Pago_Pago", "Asia/Kathmandu" })
        {
            var previous = TermArithmetic.LocalDay(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc), zone);
            for (var at = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); at.Year == 2026; at = at.AddMinutes(30))
            {
                var day = TermArithmetic.LocalDay(at, zone);
                Assert.InRange(day.DayNumber - previous.DayNumber, 0, 1);
                previous = day;
            }
        }
    }

    private static readonly StartRule Chain = new(StartTrigger.Deliver, [new StartFallback(StartTrigger.Invoice, 14), new StartFallback(StartTrigger.Ship, 21)], 90);

    [Fact]
    public void The_start_takes_the_first_trigger_with_evidence_its_offset_and_the_cap()
    {
        var invoice = new DateTime(2026, 9, 2, 8, 40, 0, DateTimeKind.Utc);
        var pod = new DateTime(2026, 10, 2, 9, 14, 0, DateTimeKind.Utc);
        var delivered = TermArithmetic.ResolveStart(Chain, new Dictionary<string, DateTime> { [StartTrigger.Invoice] = invoice, [StartTrigger.Deliver] = pod }, "Europe/Berlin");
        Assert.Equal((StartTrigger.Deliver, new DateOnly(2026, 10, 2), false), (delivered.Basis, delivered.Start, delivered.CapApplied));

        var fallback = TermArithmetic.ResolveStart(Chain, new Dictionary<string, DateTime> { [StartTrigger.Invoice] = invoice }, "Europe/Berlin");
        Assert.Equal((StartTrigger.Invoice, new DateOnly(2026, 9, 16)), (fallback.Basis, fallback.Start));

        var late = TermArithmetic.ResolveStart(Chain, new Dictionary<string, DateTime> { [StartTrigger.Invoice] = invoice, [StartTrigger.Deliver] = invoice.AddDays(200) }, "Europe/Berlin");
        Assert.Equal((new DateOnly(2026, 12, 1), true), (late.Start, late.CapApplied));

        var none = TermArithmetic.ResolveStart(Chain, new Dictionary<string, DateTime>(), "Europe/Berlin");
        Assert.Null(none.Start);
        Assert.Null(none.Basis);

        // the same delivery is a different day on two sides of the world
        var nearMidnight = new Dictionary<string, DateTime> { [StartTrigger.Deliver] = new DateTime(2026, 10, 2, 23, 30, 0, DateTimeKind.Utc) };
        Assert.Equal(new DateOnly(2026, 10, 3), TermArithmetic.ResolveStart(Chain, nearMidnight, "Europe/Berlin").Start);
        Assert.Equal(new DateOnly(2026, 10, 2), TermArithmetic.ResolveStart(Chain, nearMidnight, "America/Chicago").Start);
    }

    [Fact]
    public void A_refurbished_sale_takes_the_most_specific_row_and_never_less_than_its_floor()
    {
        var item = Guid.NewGuid();
        var terms = PolicyValidatorTests.Standard24() with
        {
            Refurbished =
            [
                new RefurbishedTerm(null, "C", "portal_consumer", "EU", 6, 12),
                new RefurbishedTerm(null, "C", "portal_consumer", "DE", 9, 12),
                new RefurbishedTerm(item, "C", "portal_consumer", "EU", 18, 12),
                new RefurbishedTerm(null, "B", "direct", "UK", 9, null),
            ],
        };
        Assert.Equal((24, false), Pick(TermArithmetic.ForCondition(terms, item, "new", "direct", "DE")));
        Assert.Equal((12, true), Pick(TermArithmetic.ForCondition(terms, Guid.NewGuid(), "C", "portal_consumer", "FR")));
        Assert.Equal((12, true), Pick(TermArithmetic.ForCondition(terms, Guid.NewGuid(), "C", "portal_consumer", "DE")));
        Assert.Equal("DE", TermArithmetic.ForCondition(terms, Guid.NewGuid(), "C", "portal_consumer", "DE").Row!.Region);
        Assert.Equal((18, false), Pick(TermArithmetic.ForCondition(terms, item, "C", "portal_consumer", "DE")));
        Assert.Equal((9, false), Pick(TermArithmetic.ForCondition(terms, item, "B", "direct", "GB")));
        Assert.Equal((24, false), Pick(TermArithmetic.ForCondition(terms, item, "B", "direct", "US")));

        static (int, bool) Pick(EffectiveDuration e) => (e.Duration.Value, e.FloorApplied);
    }

    [Theory]
    [InlineData("EU", "DE", true)]
    [InlineData("EU", "CH", false)]
    [InlineData("EFTA", "CH", true)]
    [InlineData("EEA", "NO", true)]
    [InlineData("EEA", "CH", false)]
    [InlineData("UK", "GB", true)]
    [InlineData("WORLD", "BR", true)]
    [InlineData("FR", "FR", true)]
    public void Region_groups_contain_their_member_countries(string region, string country, bool expected) => Assert.Equal(expected, Regions.Contains(region, country));
}
