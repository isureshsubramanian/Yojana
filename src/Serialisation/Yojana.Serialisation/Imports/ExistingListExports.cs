using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Reports;

namespace Yojana.Serialisation.Imports;

/// <summary>
/// Shared bounded adapter for established back-office lists. It deliberately omits opaque Guid references from the
/// file: a user-facing identifier, number or name is exported instead, and a feature needing another reference adds a
/// name-port projection rather than leaking the storage key.
/// </summary>
public static class ExistingListExports
{
    public const int PageSize = 500;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    public static IReadOnlyList<string> Headers<T>() => ScalarProperties(typeof(T)).Select(property => Snake(property.Name)).ToList();

    public static async IAsyncEnumerable<IReadOnlyList<string?>> OffsetPages<T>(
        Func<int, int, CancellationToken, Task<IReadOnlyList<T>>> page,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var skip = 0;
        var exported = 0;
        while (true)
        {
            var rows = await page(skip, PageSize, cancellationToken);
            foreach (var row in rows)
            {
                EnsureCap(++exported);
                yield return Values(row);
            }
            if (rows.Count < PageSize) yield break;
            skip += rows.Count;
        }
    }

    public static async IAsyncEnumerable<IReadOnlyList<string?>> CursorPages<T>(
        Func<string?, int, CancellationToken, Task<(IReadOnlyList<T> Rows, string? Cursor)>> page,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        var exported = 0;
        do
        {
            var next = await page(cursor, PageSize, cancellationToken);
            foreach (var row in next.Rows)
            {
                EnsureCap(++exported);
                yield return Values(row);
            }
            cursor = next.Cursor;
        } while (cursor is not null);
    }

    public static async IAsyncEnumerable<IReadOnlyList<string?>> OnePage<T>(
        Task<IReadOnlyList<T>> page,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var exported = 0;
        foreach (var row in await page)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureCap(++exported);
            yield return Values(row);
        }
    }

    private static List<string?> Values<T>(T row) => ScalarProperties(typeof(T))
        .Select(property => Format(property.GetValue(row))).ToList();

    private static PropertyInfo[] ScalarProperties(Type type) => Properties.GetOrAdd(type, candidate => candidate
        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(property => IsScalar(property.PropertyType))
        .OrderBy(property => property.MetadataToken)
        .ToArray());

    private static bool IsScalar(Type type)
    {
        var actual = Nullable.GetUnderlyingType(type) ?? type;
        if (actual == typeof(Guid) || actual == typeof(byte[])) return false;
        if (actual == typeof(string) || actual == typeof(DateTime) || actual == typeof(DateTimeOffset)
            || actual == typeof(DateOnly) || actual == typeof(TimeOnly) || actual == typeof(bool)
            || actual.IsEnum || actual.IsPrimitive || actual == typeof(decimal)) return true;
        return typeof(IEnumerable<string>).IsAssignableFrom(actual);
    }

    private static string? Format(object? value) => value switch
    {
        null => null,
        DateTime time => time.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset time => time.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("O", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),
        float number => number.ToString(CultureInfo.InvariantCulture),
        IEnumerable<string> values => string.Join("; ", values),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };

    private static string Snake(string value)
    {
        var text = new StringBuilder(value.Length + 4);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (index > 0 && char.IsUpper(character)) text.Append('_');
            text.Append(char.ToLowerInvariant(character));
        }
        return text.ToString();
    }

    private static void EnsureCap(int count)
    {
        if (count > BackOfficeReportQueries.MaximumExportRows)
            throw new CommandRefusedException("export_too_large",
                $"An export may contain at most {BackOfficeReportQueries.MaximumExportRows:N0} rows; narrow the list.");
    }
}
