using System.Security.Cryptography;
using System.Text;

namespace Yojana.Demo;

public static class DemoIds
{
    public static Guid FromName(string name)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("yojana-it-demo:" + name));
        Span<byte> value = stackalloc byte[16];
        bytes.AsSpan(0, 16).CopyTo(value);
        value[6] = (byte)((value[6] & 0x0f) | 0x50);
        value[8] = (byte)((value[8] & 0x3f) | 0x80);
        return new Guid(value, bigEndian: true);
    }
}

public static class DemoDates
{
    public static IReadOnlyList<DateOnly> Spread(DateOnly today, int count, int oldestDaysAgo, int newestDaysAgo = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(newestDaysAgo);
        ArgumentOutOfRangeException.ThrowIfLessThan(oldestDaysAgo, newestDaysAgo);
        if (count == 0) return [];
        if (count == 1) return [today.AddDays(-oldestDaysAgo)];
        return Enumerable.Range(0, count)
            .Select(index => today.AddDays(-(oldestDaysAgo - (oldestDaysAgo - newestDaysAgo) * index / (count - 1))))
            .ToList();
    }

    public static DateTime At(DateOnly day, int hour = 9, int minute = 0) =>
        DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(hour, minute)), DateTimeKind.Utc);
}
