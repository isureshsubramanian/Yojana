using System.Globalization;
using System.Text.RegularExpressions;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Identity;

/// <summary>
/// How a tenant's serial scheme is written: an optional fixed prefix, an optional block (a date part such as
/// <c>yyMM</c> that bounds every range, ADR-0001 SerialRange), a separator and a zero-padded counter. A serial's
/// ordinal is <c>block × 10⁹ + counter</c>, so ranges of one scheme compare on one number line and a range can never
/// cross a block boundary (the counter cannot exceed its digits).
/// </summary>
public sealed record SerialSchemeDefinition(string Code, string IdentifierType, string Prefix, string? BlockFormat, string Separator, int CounterDigits)
{
    public const long BlockMultiplier = 1_000_000_000;

    public long MaxCounter => (long)Math.Pow(10, CounterDigits) - 1;

    /// <summary>The block a business time falls into (0 when the scheme has no block).</summary>
    public long BlockFor(DateTime at) => BlockFormat is null ? 0 : long.Parse(at.ToString(BlockFormat, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    public string Format(long block, long counter)
    {
        if (counter < 0 || counter > MaxCounter)
        {
            throw new ArgumentOutOfRangeException(nameof(counter), $"Counter {counter} does not fit {CounterDigits} digits of scheme {Code}.");
        }

        var blockText = BlockFormat is null ? "" : block.ToString(CultureInfo.InvariantCulture).PadLeft(BlockFormat.Length, '0');
        return Prefix + blockText + (blockText.Length > 0 || Prefix.Length > 0 ? Separator : "") + counter.ToString(CultureInfo.InvariantCulture).PadLeft(CounterDigits, '0');
    }

    public string FormatOrdinal(long ordinal) => Format(ordinal / BlockMultiplier, ordinal % BlockMultiplier);

    public static long OrdinalOf(long block, long counter) => block * BlockMultiplier + counter;

    /// <summary>Parses a serial written in this scheme; null when it is not one.</summary>
    public (long Block, long Counter, long Ordinal)? TryParse(string value)
    {
        var m = Pattern.Match(value.Trim());
        if (!m.Success)
        {
            return null;
        }

        var block = BlockFormat is null ? 0 : long.Parse(m.Groups["block"].Value, CultureInfo.InvariantCulture);
        var counter = long.Parse(m.Groups["counter"].Value, CultureInfo.InvariantCulture);
        return (block, counter, OrdinalOf(block, counter));
    }

    /// <summary>
    /// Matches the display form (with the separator) and the normalised form (the identifier policy strips separators
    /// before storing), so a stored value and a scanned value both resolve to the same ordinal.
    /// </summary>
    private Regex Pattern => patternCache ??= new Regex(
        "^" + Regex.Escape(Prefix) + (BlockFormat is null ? "" : $"(?<block>\\d{{{BlockFormat.Length}}})")
            + (Separator.Length > 0 && (BlockFormat is not null || Prefix.Length > 0) ? "(?:" + Regex.Escape(Separator) + ")?" : "")
            + $"(?<counter>\\d{{{CounterDigits}}})$",
        RegexOptions.CultureInvariant);

    private Regex? patternCache;
}

/// <summary>Scheme definitions for the current tenant.</summary>
public interface ISerialSchemeSource
{
    SerialSchemeDefinition? Find(string code);

    IReadOnlyList<SerialSchemeDefinition> All();

    /// <summary>The scheme whose pattern a value matches, if exactly one does.</summary>
    SerialSchemeDefinition? Recognise(string normalisedValue);
}
