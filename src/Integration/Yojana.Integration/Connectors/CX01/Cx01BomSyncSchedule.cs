using System.Globalization;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Connectors.CX01;

/// <summary>
/// When the bill-of-materials read is due (ADR-0020 A1, owner 2026-10-26: once a day at 02:00 site time). The service
/// exposes no BOM change date, so every read is a full read; the poll row wakes every 15 minutes and this decides whether
/// to call the remote at all. The last complete read is kept as the poll row's watermark (UTC, round-trip format).
/// </summary>
public sealed class Cx01BomSyncSchedule(ITenantTimeZoneReadPort timeZones)
{
    public const string Hourly = "hourly";
    public const string Every6Hours = "every_6_hours";
    public const string Every12Hours = "every_12_hours";
    public const string Daily = "daily";

    /// <summary>How often the generic poller wakes the BOM entity; a wake that is not due makes no remote call.</summary>
    public const int WakeSeconds = 900;

    public static IReadOnlyList<string> All { get; } = [Hourly, Every6Hours, Every12Hours, Daily];

    public static IReadOnlyList<ConnectionFieldOption> Options { get; } =
    [
        new(Hourly, "Every hour"), new(Every6Hours, "Every 6 hours"), new(Every12Hours, "Every 12 hours"),
        new(Daily, "Once a day"),
    ];

    public async Task<bool> DueAsync(Cx01ConnectionSettings settings, string? watermark, DateTime utcNow,
        CancellationToken cancellationToken)
    {
        if (LastRead(watermark) is not { } last) return true;
        var interval = settings.BomSyncSchedule switch
        {
            Hourly => TimeSpan.FromHours(1),
            Every6Hours => TimeSpan.FromHours(6),
            Every12Hours => TimeSpan.FromHours(12),
            _ => (TimeSpan?)null,
        };
        if (interval is { } every) return utcNow - last >= every;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(await timeZones.GetAsync(cancellationToken));
        return last < MostRecentDailyRun(settings.BomSyncDailyAt, zone, utcNow);
    }

    public static string Watermark(DateTime completedAtUtc) =>
        DateTime.SpecifyKind(completedAtUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);

    /// <summary>The latest daily run time at or before now, in UTC; a skipped local time (a clock change) moves forward.</summary>
    public static DateTime MostRecentDailyRun(string dailyAt, TimeZoneInfo zone, DateTime utcNow)
    {
        var at = TimeOnly.ParseExact(dailyAt, "HH:mm", CultureInfo.InvariantCulture);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc), zone);
        var date = DateOnly.FromDateTime(localNow);
        var candidate = ToUtc(date.ToDateTime(at), zone);
        return candidate <= utcNow ? candidate : ToUtc(date.AddDays(-1).ToDateTime(at), zone);
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddMinutes(30);
        return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone);
    }

    private static DateTime? LastRead(string? watermark) =>
        DateTime.TryParse(watermark, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value)
            ? DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Utc)
            : null;
}
