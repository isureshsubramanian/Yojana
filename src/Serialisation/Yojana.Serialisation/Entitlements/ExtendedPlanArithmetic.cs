using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

/// <summary>The base entitlement a plan is judged against: its status and resolved term (either end may be unknown while it waits for evidence).</summary>
public sealed record BaseCoverage(Guid EntitlementId, string Status, DateOnly? StartDate, DateOnly? EndDate);

/// <summary>
/// The arithmetic of a separately priced plan (P2-T07, ADR-0001 "Extended warranty"): who may buy it, when it starts,
/// and the revenue schedule published with <c>Entitlement.ExtendedSold</c>. Pure, so the rules are tested without a database.
/// </summary>
public static class ExtendedPlanArithmetic
{
    /// <summary>Null when the purchase on <paramref name="purchaseDay"/> is eligible; otherwise the refusal rule and why.</summary>
    public static (string Rule, string Message)? Ineligible(EligibilityRule? rule, BaseCoverage? baseCoverage, DateOnly purchaseDay)
    {
        if (rule is null) return null;
        if (baseCoverage is null || baseCoverage.Status == EntitlementStatus.Closed)
            return ("base_not_live", $"The unit has no live {rule.BaseFamily} entitlement; this plan is sold only beside one.");
        if (baseCoverage.EndDate is { } end && purchaseDay > end)
            return ("base_expired", $"The {rule.BaseFamily} entitlement ended on {end:yyyy-MM-dd}, before the purchase on {purchaseDay:yyyy-MM-dd}.");
        // A base still waiting for its start evidence has not begun, so a purchase now is inside any window counted from it.
        if (rule.Kind == EligibilityRule.WithinDaysOfBaseStart && baseCoverage.StartDate is { } start && purchaseDay > start.AddDays(rule.WithinDays ?? 0))
            return ("purchase_window_passed", $"The plan must be bought within {rule.WithinDays} days of the {rule.BaseFamily} start on {start:yyyy-MM-dd}.");
        return null;
    }

    /// <summary>
    /// The first covered day: the purchase day for <c>plan_purchase</c>, the day after the base's last covered day for
    /// <c>base_expiry</c> (stored end dates are always the last covered day, see <see cref="TermArithmetic"/>).
    /// </summary>
    public static DateOnly? Start(string trigger, DateOnly purchaseDay, BaseCoverage? baseCoverage) => trigger switch
    {
        StartTrigger.PlanPurchase => purchaseDay,
        StartTrigger.BaseExpiry => baseCoverage?.EndDate?.AddDays(1),
        _ => null,
    };

    /// <summary>
    /// Straight-line recognition over the term, by calendar month, in proportion to the covered days in each: the shape
    /// ASC 606 / IFRS 15 gives a stand-ready service. Amounts are rounded to <paramref name="decimals"/> and the last period
    /// takes the rounding, so the schedule always sums to the price. Finance may re-cut it; this is the module's proposal.
    /// </summary>
    public static IReadOnlyList<EntitlementRevenueSchedulePeriod> RevenueSchedule(DateOnly start, DateOnly end, decimal price, int decimals = 2)
    {
        if (end < start) throw new ArgumentException("The term ends before it starts.", nameof(end));
        var totalDays = end.DayNumber - start.DayNumber + 1;
        var periods = new List<EntitlementRevenueSchedulePeriod>();
        var allocated = 0m;
        for (var from = start; from <= end;)
        {
            var monthEnd = new DateOnly(from.Year, from.Month, 1).AddMonths(1).AddDays(-1);
            var to = monthEnd < end ? monthEnd : end;
            var amount = to == end ? price - allocated : Math.Round(price * (to.DayNumber - from.DayNumber + 1) / totalDays, decimals, MidpointRounding.ToEven);
            periods.Add(new EntitlementRevenueSchedulePeriod(from, to, amount));
            allocated += amount;
            from = to.AddDays(1);
        }

        return periods;
    }

    /// <summary>Whole months for the published term: the rule's months, or 0 for a term in days (the dates and schedule then carry it).</summary>
    public static int TermMonths(DurationRule rule) => rule.Unit == DurationRule.Months ? rule.Value : 0;
}
