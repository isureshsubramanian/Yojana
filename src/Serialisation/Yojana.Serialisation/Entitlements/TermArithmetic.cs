using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

/// <summary>Which trigger fired, the start it gives, and whether the cap moved it; <see cref="Start"/> is null while evidence is missing.</summary>
public sealed record StartResolution(string? Basis, DateOnly? Start, bool CapApplied, string Explanation);

/// <summary>A refurbished or new term after the (item, grade, channel, region) row and the statutory floor (P2-T15 uses it at sale).</summary>
public sealed record EffectiveDuration(DurationRule Duration, bool FloorApplied, RefurbishedTerm? Row);

/// <summary>
/// The date arithmetic of an entitlement term, one implementation used everywhere (ADR-0001 Terms: "one rule used
/// everywhere"; cases 112, 113). Its version is <see cref="PolicyStore.DurationRuleImplementation"/>: an entitlement
/// records it with its end date, so a change here is a new number and old end dates are never re-read differently.
/// <para>
/// The stored <c>end_date</c> is always the <b>last covered day</b>. <see cref="DurationRule.EndInclusive"/> says
/// whether the term counts its start day: inclusive, 24 months from 2026-10-02 ends 2028-10-01; not inclusive, it
/// ends on the anniversary 2028-10-02.
/// </para>
/// </summary>
public static class TermArithmetic
{
    public static DateOnly EndDate(DateOnly start, DurationRule rule)
    {
        var anniversary = rule.Unit switch
        {
            DurationRule.Days => start.AddDays(rule.Value),
            DurationRule.Months => AddMonths(start, rule.Value, rule.EndOfMonth),
            _ => throw new ArgumentException($"Unit '{rule.Unit}' is not months or days.", nameof(rule)),
        };
        return rule.EndInclusive ? anniversary.AddDays(-1) : anniversary;
    }

    /// <summary>
    /// Clamp: 31 Jan + 1 month is the last day of February (DateOnly.AddMonths). Roll: the days February lacks carry into
    /// March (31 Jan + 1 month = 3 Mar, or 2 Mar in a leap year). Roll is not monotonic: a start of 31 Jan ends after a
    /// start of 1 Feb; the policy validator warns about it.
    /// </summary>
    public static DateOnly AddMonths(DateOnly start, int months, string endOfMonth)
    {
        var clamped = start.AddMonths(months);
        if (endOfMonth != DurationRule.Roll || clamped.Day == start.Day) return clamped;
        return new DateOnly(clamped.Year, clamped.Month, 1).AddDays(start.Day - 1);
    }

    /// <summary>The calendar day an instant falls on in an IANA zone (case 112: a delivery at 23:30 UTC is the next day in Berlin).</summary>
    public static DateOnly LocalDay(DateTime utc, string ianaZone)
    {
        if (utc.Kind == DateTimeKind.Local) throw new ArgumentException("Pass UTC or unspecified-as-UTC instants.", nameof(utc));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(ianaZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone));
    }

    public static bool IsKnownZone(string ianaZone) => TimeZoneInfo.TryFindSystemTimeZoneById(ianaZone, out _);

    /// <summary>
    /// Walks the trigger chain: the first trigger with evidence wins, plus its offset; then the cap holds the start to no
    /// later than invoice + cap days. Evidence instants are read as calendar days in <paramref name="zone"/>.
    /// </summary>
    public static StartResolution ResolveStart(StartRule rule, IReadOnlyDictionary<string, DateTime> evidence, string zone)
    {
        foreach (var (trigger, offset) in new[] { (rule.Trigger, 0) }.Concat(rule.Fallbacks.Select(f => (f.Trigger, f.OffsetDays))))
        {
            if (!evidence.TryGetValue(trigger, out var at)) continue;
            var start = LocalDay(at, zone).AddDays(offset);
            var why = offset == 0 ? $"{trigger} on {start:yyyy-MM-dd}" : $"{trigger} + {offset} d";
            if (rule.CapDays is { } cap && evidence.TryGetValue(StartTrigger.Invoice, out var invoice))
            {
                var latest = LocalDay(invoice, zone).AddDays(cap);
                if (start > latest) return new StartResolution(trigger, latest, true, $"{why}, held to invoice + {cap} d");
            }

            return new StartResolution(trigger, start, false, why);
        }

        return new StartResolution(null, null, false, $"no evidence for {string.Join(", ", new[] { rule.Trigger }.Concat(rule.Fallbacks.Select(f => f.Trigger)))}");
    }

    /// <summary>
    /// The term for a unit sold in a condition, channel and region. A new unit takes the policy's duration. A non-new
    /// unit takes the most specific refurbished row (its item before any item, an exact country before a region group),
    /// and never less than that row's statutory floor; with no row it takes the policy's duration.
    /// </summary>
    public static EffectiveDuration ForCondition(PolicyTerms terms, Guid itemId, string? grade, string? channel, string? country)
    {
        if (grade is null or ConditionGrades.New) return new EffectiveDuration(terms.Duration, false, null);
        var row = terms.Refurbished
            .Where(r => r.Grade == grade && r.Channel == channel && (r.ItemId is null || r.ItemId == itemId) && country is not null && Regions.Contains(r.Region, country))
            .OrderBy(r => r.ItemId is null ? 1 : 0).ThenBy(r => r.Region == country ? 0 : 1).FirstOrDefault();
        if (row is null) return new EffectiveDuration(terms.Duration, false, null);
        var months = Math.Max(row.DurationMonths, row.StatutoryFloorMonths ?? 0);
        return new EffectiveDuration(terms.Duration with { Value = months, Unit = DurationRule.Months }, months > row.DurationMonths, row);
    }
}

/// <summary>Region groups the policies use, as ISO 3166 alpha-2 members. "UK" is the policies' name for GB.</summary>
public static class Regions
{
    private static readonly HashSet<string> Eu = ["AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR", "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL", "PL", "PT", "RO", "SK", "SI", "ES", "SE"];
    private static readonly HashSet<string> Efta = ["IS", "LI", "NO", "CH"];

    public static bool Contains(string region, string country) => region switch
    {
        "WORLD" => true,
        "EU" => Eu.Contains(country),
        "EEA" => Eu.Contains(country) || country is "IS" or "LI" or "NO",
        "EFTA" => Efta.Contains(country),
        "UK" => country == "GB",
        _ => region == country,
    };

    public static bool InScope(IEnumerable<string> scope, string country) => scope.Any(r => Contains(r, country));
}
