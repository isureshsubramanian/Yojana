using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

namespace Yojana.Serialisation.Identity;

/// <summary>What a scanned string turned out to be.</summary>
public enum ScanFormat
{
    /// <summary>GS1 element strings (FNC1 or HRI form).</summary>
    Gs1,

    /// <summary>ANSI MH10.8.2 data identifiers, usually inside an ISO/IEC 15434 format "06" envelope.</summary>
    Mh10,

    /// <summary>No recognised structure: the whole value is the identifier.</summary>
    Plain,

    /// <summary>An HTTP or HTTPS address. It is classified only; the application never opens or fetches it.</summary>
    Url,
}

/// <summary>One parsed field: a GS1 application identifier or an MH10.8.2 data identifier with its value.</summary>
public sealed record ScanField(string Key, string Value, string? Description, IReadOnlyList<string> Errors);

/// <summary>
/// The parsed scan. <see cref="Fields"/> is empty for a plain scan; <see cref="SymbologyIdentifier"/> is the AIM
/// prefix the scanner sent (<c>]d2</c>, <c>]C1</c>, <c>]Q3</c>, <c>]e0</c>), when it did.
/// </summary>
public sealed record ScanResult(ScanFormat Format, string Raw, string Cleaned, string? SymbologyIdentifier, IReadOnlyList<ScanField> Fields, int SeparatorsRecovered)
{
    /// <summary>Less likely identifier values that lookups may try after <see cref="Cleaned"/> has no match.</summary>
    public IReadOnlyList<string> Alternatives { get; init; } = [];

    public string? this[string key] => Fields.FirstOrDefault(f => f.Key == key && f.Errors.Count == 0)?.Value;

    public bool HasErrors => Fields.Any(f => f.Errors.Count > 0);
}

/// <summary>
/// Identity rule 11: every serial entry surface accepts a scan; GS1 element strings and MH10.8.2 data identifiers are
/// parsed on the server after the keyboard-wedge FNC1 control sequence is converted back to a group separator.
/// Wedges render ASCII 29 in several ways (nothing, Ctrl+], a literal "&lt;GS&gt;", "{GS}", "\x1d" or "^]"); the ones
/// that carry information are restored, and a GS1 string whose separators were dropped entirely still parses because
/// the fixed-length AIs need none and the parser detects the rest from the AI table.
/// </summary>
public static partial class ScanParser
{
    private const char Gs = '\u001d';
    private const char Rs = '\u001e';
    private const char Eot = '\u0004';

    public static ScanResult Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var (cleaned, recovered) = RecoverSeparators(raw.Trim());
        string? aim = null;
        if (AimPrefix().Match(cleaned) is { Success: true } m)
        {
            aim = m.Value;
            cleaned = cleaned[m.Length..];
        }

        if (cleaned.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || cleaned.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return new ScanResult(ScanFormat.Url, raw, cleaned, aim, [], recovered);
        }

        if (IsIso15434(cleaned) || LooksLikeMh10(cleaned))
        {
            return new ScanResult(ScanFormat.Mh10, raw, cleaned, aim, ParseMh10(cleaned), recovered);
        }

        if (TypedSeparatorSplit(cleaned) is { } split)
        {
            return new ScanResult(ScanFormat.Gs1, raw, split.Candidate, aim, split.Fields, recovered);
        }

        // A symbology prefix, brackets or a separator says the scanner sent GS1; digits alone only suggest it.
        var declaredGs1 = aim is "]d2" or "]C1" or "]Q3" or "]e0" || cleaned.StartsWith('(') || cleaned.Contains(Gs);
        if (declaredGs1 || LooksLikeGs1(cleaned))
        {
            var elements = Gs1ElementStrings.Parse(cleaned);
            // An undeclared string that does not parse cleanly is a manufacturer serial that happens to start with AI-like
            // digits (memory module labels such as "1721 0000007724188-P001291"), so it falls through to plain.
            if (elements.Count > 0 && (declaredGs1 || elements.All(e => e.Errors.Count == 0)))
            {
                return new ScanResult(ScanFormat.Gs1, raw, cleaned, aim, elements.Select(e => new ScanField(e.ApplicationIdentifier, e.Value, e.Description, e.Errors)).ToList(), recovered);
            }
        }

        var plain = new ScanResult(ScanFormat.Plain, raw, cleaned, aim, [], recovered);
        var codabar = Codabar().Match(cleaned);
        return codabar.Success ? plain with { Alternatives = [codabar.Groups["value"].Value] } : plain;
    }

    /// <summary>Restores group separators a wedge rendered as text; returns the cleaned string and how many were recovered.</summary>
    public static (string Cleaned, int Recovered) RecoverSeparators(string value)
    {
        var recovered = 0;
        string Replace(string text, string token)
        {
            var count = (text.Length - text.Replace(token, "", StringComparison.OrdinalIgnoreCase).Length) / token.Length;
            recovered += count;
            return count == 0 ? text : Regex.Replace(text, Regex.Escape(token), Gs.ToString(), RegexOptions.IgnoreCase);
        }

        var cleaned = value;
        foreach (var token in new[] { "<GS>", "{GS}", "[GS]", "\\x1d", "\\u001d", "^]", "␝" })
        {
            cleaned = Replace(cleaned, token);
        }

        cleaned = cleaned.Replace("<RS>", Rs.ToString(), StringComparison.OrdinalIgnoreCase).Replace("<EOT>", Eot.ToString(), StringComparison.OrdinalIgnoreCase);
        return (cleaned, recovered);
    }

    public const string TypedSeparatorError =
        "The separator arrived as the digits 0029, so this split is a guess. Set the scanner to send the separator as Ctrl+] and scan again.";

    /// <summary>
    /// OD-88: a scanner on factory settings sends the separator as Alt + 0029, which macOS types as the digits, so a
    /// variable-length field runs into the next one. When the scan carries no separator and reading one "0029" as the
    /// separator gives a clean parse with more fields, the split is returned with the field before it flagged: the
    /// guess is shown but never accepted as the identifier.
    /// </summary>
    private static (string Candidate, List<ScanField> Fields)? TypedSeparatorSplit(string cleaned)
    {
        if (cleaned.Contains(Gs) || cleaned.StartsWith('(') || !LooksLikeGs1(cleaned))
        {
            return null;
        }

        var occurrences = new List<int>();
        for (var at = cleaned.IndexOf("0029", 2, StringComparison.Ordinal); at >= 0 && occurrences.Count < 4; at = cleaned.IndexOf("0029", at + 4, StringComparison.Ordinal))
        {
            occurrences.Add(at);
        }

        var original = Gs1ElementStrings.Parse(cleaned);
        var originalClean = original.Count > 0 && original.All(e => e.Errors.Count == 0) ? original.Count : 0;
        // Fewest separators first: each set of occurrences read as separators is one candidate (at most 15).
        var sets = Enumerable.Range(1, (1 << occurrences.Count) - 1).OrderBy(set => BitOperations.PopCount((uint)set)).ThenBy(set => set);
        foreach (var set in sets)
        {
            var candidate = new StringBuilder(cleaned.Length);
            var splits = new HashSet<int>();
            var from = 0;
            for (var i = 0; i < occurrences.Count; i++)
            {
                if ((set & (1 << i)) == 0)
                {
                    continue;
                }

                candidate.Append(cleaned, from, occurrences[i] - from);
                splits.Add(candidate.Length);
                candidate.Append(Gs);
                from = occurrences[i] + 4;
            }

            candidate.Append(cleaned, from, cleaned.Length - from);
            var text = candidate.ToString();
            var elements = Gs1ElementStrings.Parse(text);
            if (elements.Count < 2 || elements.Count <= originalClean || elements.Any(e => e.Errors.Count > 0))
            {
                continue;
            }

            // The flagged fields are the ones that end where a separator was read.
            var fields = new List<ScanField>(elements.Count);
            var position = 0;
            foreach (var e in elements)
            {
                position += e.ApplicationIdentifier.Length + e.Value.Length;
                var endsAtSplit = splits.Contains(position);
                if (position < text.Length && text[position] == Gs)
                {
                    position++;
                }

                fields.Add(new ScanField(e.ApplicationIdentifier, e.Value, e.Description, endsAtSplit ? [TypedSeparatorError] : []));
            }

            if (fields.Count(f => f.Errors.Count > 0) == splits.Count)
            {
                return (text, fields);
            }
        }

        return null;
    }

    private static bool IsIso15434(string value) => value.StartsWith("[)>", StringComparison.Ordinal);

    private static bool LooksLikeMh10(string value) => value.Contains(Gs) && Mh10Field().IsMatch(value.Split(Gs)[0]) && !LooksLikeGs1(value);

    /// <summary>A GS1 FNC1 string starts with a two-to-four-digit AI; the common openers are 00, 01, 02, 10, 11–17, 21, 240, 241, 8003, 8004, 8018.</summary>
    private static bool LooksLikeGs1(string value) => Gs1Opener().IsMatch(value);

    private static List<ScanField> ParseMh10(string value)
    {
        var body = value;
        if (IsIso15434(body))
        {
            // [)> RS 06 GS <fields separated by GS> RS EOT ; several format blocks may follow one another.
            body = body[3..].TrimEnd(Eot).TrimEnd(Rs);
            var blocks = body.Split(Rs, StringSplitOptions.RemoveEmptyEntries);
            var fields = new List<ScanField>();
            foreach (var block in blocks)
            {
                var parts = block.Split(Gs);
                var format = parts[0];
                if (format is not ("06" or "05" or "12" or "DD"))
                {
                    fields.Add(new ScanField("format", format, "ISO/IEC 15434 format indicator", ["unsupported_format_indicator"]));
                    continue;
                }

                fields.AddRange(parts.Skip(1).Where(p => p.Length > 0).Select(ParseDataIdentifier));
            }

            return fields;
        }

        return body.Split(Gs, StringSplitOptions.RemoveEmptyEntries).Select(ParseDataIdentifier).ToList();
    }

    private static ScanField ParseDataIdentifier(string field)
    {
        var m = Mh10Field().Match(field);
        if (!m.Success)
        {
            return new ScanField("?", field, null, ["data_identifier_not_recognised"]);
        }

        var di = m.Groups["di"].Value;
        var value = m.Groups["value"].Value;
        return new ScanField(di, value, DataIdentifiers.TryGetValue(di, out var d) ? d : null, value.Length == 0 ? ["empty_value"] : []);
    }

    /// <summary>The MH10.8.2 data identifiers this module reads; others parse but carry no description.</summary>
    public static readonly IReadOnlyDictionary<string, string> DataIdentifiers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["P"] = "Item identification (customer)",
        ["1P"] = "Item identification (supplier part number)",
        ["S"] = "Serial number",
        ["1S"] = "Additional serial number",
        ["2S"] = "Serial number of the next level of packaging",
        ["18S"] = "UII construct 1 (CAGE + serial)",
        ["25S"] = "UII construct 2 (IAC + EID + serial)",
        ["T"] = "Traceability number (lot/batch)",
        ["1T"] = "Lot/batch number",
        ["Q"] = "Quantity",
        ["V"] = "Supplier code",
        ["12V"] = "DUNS of the supplier",
        ["17V"] = "Enterprise identifier (CAGE)",
        ["18V"] = "Enterprise identifier (DoDAAC)",
        ["3V"] = "Enterprise identifier (CAGE, concatenated)",
        ["7L"] = "Country of origin",
        ["D"] = "Date (YYMMDD)",
        ["14D"] = "Expiry date (YYYYMMDD)",
        ["16D"] = "Production date (YYYYMMDD)",
        ["20P"] = "Part number (first item)",
        ["K"] = "Order number (customer)",
        ["1K"] = "Order number (supplier)",
        ["4L"] = "Country of origin (ISO 3166)",
        ["30P"] = "GTIN",
    };

    [GeneratedRegex(@"^\](?:d2|C1|Q3|e0|d1|C0|Q1|E0|A0|I1|L2|U1|X0|z0|Z0)")]
    private static partial Regex AimPrefix();

    [GeneratedRegex(@"^(?<di>\d{0,3}[A-Z])(?<value>.*)$", RegexOptions.Singleline)]
    private static partial Regex Mh10Field();

    // A fixed-length opener (SSCC, GTIN) may be the whole scan, as on a carton label carrying only its GTIN.
    [GeneratedRegex(@"^(?:(?:00\d{18}|01\d{14}|02\d{14})(?:\d|$)|(?:1[0-7]|2[0-1]|240|241|253|254|255|8003|8004|8006|8010|8017|8018|90)\d)")]
    private static partial Regex Gs1Opener();

    [GeneratedRegex(@"^[A-D](?<value>[0-9\-$:/.+]{4,})[A-D]$")]
    private static partial Regex Codabar();
}
