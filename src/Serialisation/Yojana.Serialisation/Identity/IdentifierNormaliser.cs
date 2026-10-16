using System.Text;

namespace Yojana.Serialisation.Identity;

/// <summary>Result of normalising a captured value: what is stored as <c>normalised_value</c>, and what was taken off as a scanner artefact.</summary>
public sealed record Normalised(string Value, IReadOnlyList<string> ArtefactsStripped);

/// <summary>
/// Identity rule 3: trim always; case per policy; strip the type's separator characters; leading zeros per policy;
/// symbology safety (case 139): under Code 39 a leading or trailing '*' is the scanner echoing the start/stop
/// character and is removed (kept in <c>raw_value</c>, reported here), while a '*' inside the value is left for the
/// validator to reject.
/// </summary>
public static class IdentifierNormaliser
{
    public static Normalised Normalise(string raw, IdentifierPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var artefacts = new List<string>();
        var value = raw.Trim();
        value = value.Replace("\u001d", "", StringComparison.Ordinal); // a stray group separator from a wedge is never data
        if (policy.Symbology == Symbology.Code39)
        {
            if (value.Length > 1 && value[0] == '*')
            {
                value = value[1..];
                artefacts.Add("leading_asterisk");
            }

            if (value.Length > 1 && value[^1] == '*')
            {
                value = value[..^1];
                artefacts.Add("trailing_asterisk");
            }
        }

        if (policy.StripCharacters.Count > 0)
        {
            var sb = new StringBuilder(value.Length);
            foreach (var c in value)
            {
                if (!policy.StripCharacters.Contains(c))
                {
                    sb.Append(c);
                }
            }

            value = sb.ToString();
        }

        if (policy.Case == CasePolicy.Upper)
        {
            value = value.ToUpperInvariant();
        }

        if (policy.LeadingZeros == LeadingZeroPolicy.Strip && value.Length > 1 && value.All(char.IsAsciiDigit))
        {
            value = value.TrimStart('0');
            if (value.Length == 0)
            {
                value = "0";
            }
        }

        return new Normalised(value, artefacts);
    }
}
