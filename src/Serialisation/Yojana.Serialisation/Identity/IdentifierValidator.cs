using System.Globalization;
using System.Text.RegularExpressions;

namespace Yojana.Serialisation.Identity;

/// <summary>Outcome of a check: a failing regulated type blocks, a failing unregulated type warns (Identity rule 4).</summary>
public sealed record ValidationOutcome(bool IsValid, FailureSeverity Severity, IReadOnlyList<string> Reasons)
{
    public static readonly ValidationOutcome Valid = new(true, FailureSeverity.Warn, []);

    public bool Blocks => !IsValid && Severity == FailureSeverity.Block;
}

/// <summary>
/// Validators per identifier type (Identity rule 4, AI 41): VIN position 9, IMEI Luhn, GTIN and SSCC mod-10, GS1
/// element strings through the validating parser, UDI device identifiers (GS1 GTIN or HIBC), DoD UIIs, MAC
/// addresses, plus the symbology character set of the type. Pure functions over the normalised value.
/// </summary>
public static partial class IdentifierValidator
{
    public static ValidationOutcome Validate(string normalised, IdentifierPolicy policy)
    {
        var reasons = new List<string>();
        if (string.IsNullOrEmpty(normalised))
        {
            reasons.Add("empty");
        }

        if (SymbologyCharacterSets.FirstUnencodable(normalised, policy.Symbology) is { } bad)
        {
            reasons.Add(bad == '*' && policy.Symbology == Symbology.Code39 ? "asterisk_not_encodable_in_code39" : $"character_not_encodable_in_{policy.Symbology.ToString().ToLowerInvariant()}:{bad}");
        }

        if (reasons.Count == 0)
        {
            reasons.AddRange(policy.Validator switch
            {
                Validator.None => [],
                Validator.Vin => Vin(normalised),
                Validator.ImeiLuhn => Imei(normalised),
                Validator.Gtin => Gtin(normalised),
                Validator.Sscc => Sscc(normalised),
                Validator.Gs1ElementString => Gs1ElementStrings.Validate(normalised),
                Validator.UdiDeviceIdentifier => UdiDi(normalised),
                Validator.Uii => Uii(normalised),
                Validator.MacAddress => Mac(normalised),
                _ => throw new ArgumentOutOfRangeException(nameof(policy)),
            });
        }

        return reasons.Count == 0 ? ValidationOutcome.Valid : new ValidationOutcome(false, policy.OnFailure, reasons);
    }

    /// <summary>ISO 3779 VIN: 17 characters, no I, O or Q, position 9 check digit (North American rule; applied to all, as the ADR specifies).</summary>
    public static IReadOnlyList<string> Vin(string value)
    {
        if (value.Length != 17) return ["vin_length_not_17"];
        if (value.Any(c => c is 'I' or 'O' or 'Q' || !(char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)))) return ["vin_invalid_character"];
        int[] weights = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];
        var sum = 0;
        for (var i = 0; i < 17; i++)
        {
            sum += VinTransliterate(value[i]) * weights[i];
        }

        var check = sum % 11;
        var expected = check == 10 ? 'X' : (char)('0' + check);
        return value[8] == expected ? [] : ["vin_check_digit"];
    }

    private static int VinTransliterate(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        'A' or 'J' => 1, 'B' or 'K' or 'S' => 2, 'C' or 'L' or 'T' => 3, 'D' or 'M' or 'U' => 4, 'E' or 'N' or 'V' => 5,
        'F' or 'W' => 6, 'G' or 'P' or 'X' => 7, 'H' or 'Y' => 8, 'R' or 'Z' => 9,
        _ => 0,
    };

    /// <summary>IMEI: 15 digits with a Luhn check digit (14-digit IMEI without check digit is refused; IMEISV is 16 digits without Luhn).</summary>
    public static IReadOnlyList<string> Imei(string value)
    {
        if (value.Length != 15 || !value.All(char.IsAsciiDigit)) return ["imei_not_15_digits"];
        return Luhn(value) ? [] : ["imei_luhn"];
    }

    public static bool Luhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    /// <summary>GTIN-8, -12, -13, -14: GS1 mod-10 (weights 3,1 from the right).</summary>
    public static IReadOnlyList<string> Gtin(string value)
    {
        if (value.Length is not (8 or 12 or 13 or 14) || !value.All(char.IsAsciiDigit)) return ["gtin_length"];
        return Gs1Mod10(value) ? [] : ["gtin_check_digit"];
    }

    /// <summary>SSCC: 18 digits, GS1 mod-10.</summary>
    public static IReadOnlyList<string> Sscc(string value)
    {
        if (value.Length != 18 || !value.All(char.IsAsciiDigit)) return ["sscc_length"];
        return Gs1Mod10(value) ? [] : ["sscc_check_digit"];
    }

    /// <summary>GS1 check digit over the whole string (last digit is the check).</summary>
    public static bool Gs1Mod10(string digits)
    {
        var sum = 0;
        var weight = 3;
        for (var i = digits.Length - 2; i >= 0; i--)
        {
            sum += (digits[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        var check = (10 - sum % 10) % 10;
        return digits[^1] - '0' == check;
    }

    /// <summary>
    /// UDI device identifier: a GS1 GTIN (8–14 digits, mod-10) or an HIBC primary code (+LIC + product + unit of measure + mod-43 check).
    /// ICCBBA and IFA issuing agencies are accepted structurally (prefix) without a check here.
    /// </summary>
    public static IReadOnlyList<string> UdiDi(string value)
    {
        if (value.StartsWith('+'))
        {
            return HibcPrimary(value);
        }

        if (value.StartsWith("=/", StringComparison.Ordinal) || value.StartsWith("=+", StringComparison.Ordinal))
        {
            return value.Length >= 10 ? [] : ["udi_iccbba_too_short"];
        }

        if (value.StartsWith('(') || value.StartsWith("01", StringComparison.Ordinal) && value.Length > 16)
        {
            var problems = Gs1ElementStrings.Validate(value);
            return problems.Count == 0 && Gs1ElementStrings.Contains(value, "01") ? [] : problems.Count == 0 ? ["udi_di_requires_ai_01"] : problems;
        }

        return Gtin(value).Count == 0 ? [] : ["udi_di_not_gtin_hibc_or_iccbba"];
    }

    private const string Hibc43 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    /// <summary>HIBC primary data structure: '+', 4-character LIC, product code (1–18 alphanumerics), unit-of-measure digit, mod-43 check character.</summary>
    public static IReadOnlyList<string> HibcPrimary(string value)
    {
        if (value.Length < 8 || value[0] != '+') return ["hibc_structure"];
        var body = value[..^1];
        var check = value[^1];
        if (body.Any(c => Hibc43.IndexOf(c, StringComparison.Ordinal) < 0) || Hibc43.IndexOf(check, StringComparison.Ordinal) < 0) return ["hibc_character"];
        var sum = body.Sum(c => Hibc43.IndexOf(c, StringComparison.Ordinal));
        return Hibc43[sum % 43] == check ? [] : ["hibc_check_character"];
    }

    /// <summary>
    /// DoD UII (MIL-STD-130 constructs): Construct 1 = enterprise identifier + unique serial (`25S` + issuing agency code + EID + serial, or `18S`);
    /// Construct 2 = EID + part/lot + serial (`1P`/`1T` + `S` + `17V`/`18V`), concatenated. Accepts a concatenated UII (no data identifiers, 10–50
    /// characters, no lowercase) or the MH10.8.2 DI form; checks structure, not registry membership.
    /// </summary>
    public static IReadOnlyList<string> Uii(string value)
    {
        if (value.Length is < 10 or > 50) return ["uii_length"];
        if (value.Any(c => !(char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c) || c is '-' or '/' or '.' or '\u001d'))) return ["uii_character"];
        if (value.Contains('\u001d'))
        {
            // DI form: first element must be an EID carrier (18S, 25S, 17V, 18V, 3V, 12V, 7L, 1P, 1T or S)
            var first = value.Split('\u001d')[0];
            return UiiDataIdentifier().IsMatch(first) ? [] : ["uii_data_identifier"];
        }

        return [];
    }

    [GeneratedRegex("^(25S|18S|17V|18V|3V|12V|7L|1P|1T|S)[A-Z0-9]")]
    private static partial Regex UiiDataIdentifier();

    /// <summary>MAC-48 as 12 hex digits after normalisation (separators stripped).</summary>
    public static IReadOnlyList<string> Mac(string value) =>
        value.Length == 12 && value.All(Uri.IsHexDigit) ? [] : ["mac_not_12_hex_digits"];

    /// <summary>Convenience: the GS1 check digit for a body (GTIN-13 body of 12 digits etc.), for range generators and tests.</summary>
    public static char Gs1CheckDigit(string body)
    {
        var sum = 0;
        var weight = 3;
        for (var i = body.Length - 1; i >= 0; i--)
        {
            sum += (body[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return (char)('0' + (10 - sum % 10) % 10);
    }

    public static string FormatInvariant(long value) => value.ToString(CultureInfo.InvariantCulture);
}
