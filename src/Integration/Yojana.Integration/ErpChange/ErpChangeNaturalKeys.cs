using System.Globalization;
using System.Text;

namespace Yojana.Integration.ErpChange;

public static class ErpChangeNaturalKeys
{
    public static string Text(string? value) => Compact(value, keepLettersAndDigitsOnly: false);
    public static string ItemCode(string? value) => Compact(value, keepLettersAndDigitsOnly: true);
    public static string TaxId(string? value) => Compact(value, keepLettersAndDigitsOnly: true);
    public static string Country(string? value) => Compact(value, keepLettersAndDigitsOnly: true);
    public static string Serial(string? value) => Compact(value, keepLettersAndDigitsOnly: true);

    private static string Compact(string? value, bool keepLettersAndDigitsOnly)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Trim().Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(character))
            {
                if (!keepLettersAndDigitsOnly && pendingSpace && result.Length > 0) result.Append(' ');
                result.Append(char.ToUpperInvariant(character));
                pendingSpace = false;
            }
            else if (!keepLettersAndDigitsOnly) pendingSpace = true;
        }
        return result.ToString().Normalize(NormalizationForm.FormC);
    }
}
