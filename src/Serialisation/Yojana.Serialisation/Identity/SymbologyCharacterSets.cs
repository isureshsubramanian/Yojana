namespace Yojana.Serialisation.Identity;

/// <summary>Character sets a symbology can encode (ADR-0001 AI 41). A value outside its type's set cannot be printed and is refused at entry.</summary>
public static class SymbologyCharacterSets
{
    private static readonly HashSet<char> Code39Set = new("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%");

    /// <summary>Null when every character is encodable; otherwise the first offending character.</summary>
    public static char? FirstUnencodable(string value, Symbology symbology)
    {
        foreach (var c in value)
        {
            var ok = symbology switch
            {
                Symbology.Code39 => Code39Set.Contains(c),
                Symbology.EanUpc => char.IsAsciiDigit(c),
                Symbology.Code128 or Symbology.DataMatrix or Symbology.QrCode => c is >= ' ' and <= '~' || c == '\u001d',
                _ => true,
            };
            if (!ok)
            {
                return c;
            }
        }

        return null;
    }
}
