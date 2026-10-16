using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

/// <summary>The four replacement end-date rules, kept independent of persistence so chains can be checked as properties.</summary>
public static class ReplacementArithmetic
{
    public static DateOnly EndDate(DateOnly sourceEnd, DateOnly replacementDate, DurationRule duration, string rule, int? minDays, int? extensionMonths) => rule switch
    {
        ReplacementRule.CarryRemaining => sourceEnd,
        ReplacementRule.MaxRemainingOrMinDays when minDays is { } days => Max(sourceEnd, replacementDate.AddDays(days)),
        ReplacementRule.RestartFull => TermArithmetic.EndDate(replacementDate, duration),
        ReplacementRule.StatutoryExtensionOnce when extensionMonths is { } months => sourceEnd.AddMonths(months),
        _ => throw new ArgumentException($"Replacement rule '{rule}' has no valid parameter.", nameof(rule)),
    };

    private static DateOnly Max(DateOnly left, DateOnly right) => left >= right ? left : right;
}
