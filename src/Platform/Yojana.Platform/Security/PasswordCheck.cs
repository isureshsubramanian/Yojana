using System.Globalization;
using System.Text;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Platform.Security;

/// <summary>The words of a person and their companies that a password must not simply be (ADR-0008 §2b).</summary>
public sealed record PasswordIdentity(string Email, string DisplayName, IReadOnlyList<string> CompanyNames);

/// <summary>
/// ADR-0008 rules with no I/O: length, the company's character classes and the always-on easy-password block.
/// Messages never echo the password.
/// </summary>
public static class PasswordCheck
{
    public const string ProductName = "Yojana";
    private const int RunLength = 6;
    private const int ShortestNamePart = 4;
    private const int ShortestListCore = 4;

    private static readonly string[] Runs =
    [
        "qwertyuiop", "asdfghjkl", "zxcvbnm", "qazwsxedcrfvtgbyhnujmikolp", "1234567890", "01234567890",
        "abcdefghijklmnopqrstuvwxyz",
    ];

    public static PasswordPolicyResult Evaluate(string password, PasswordRules rules, PasswordIdentity identity,
        int maximumLength)
    {
        var reasons = new List<PasswordPolicyReason>();
        var length = new StringInfo(password).LengthInTextElements;
        if (length < rules.MinimumLength)
            reasons.Add(new("password_too_short", $"Use at least {rules.MinimumLength} characters."));
        if (password.Length > maximumLength)
            reasons.Add(new("password_too_long", $"Use no more than {maximumLength} characters."));

        var classes = Classes(password);
        if (rules.RequireUppercase && !classes.Upper)
            reasons.Add(new("password_needs_uppercase", "Include a capital letter."));
        if (rules.RequireLowercase && !classes.Lower)
            reasons.Add(new("password_needs_lowercase", "Include a small letter."));
        if (rules.RequireDigit && !classes.Digit)
            reasons.Add(new("password_needs_digit", "Include a number."));
        if (rules.RequireSymbol && !classes.Symbol)
            reasons.Add(new("password_needs_symbol", "Include a symbol, such as ! # % or a space."));

        if (password.Length > 0 && IsCommon(password))
            reasons.Add(new("password_too_common",
                "This password is on the list of common passwords people try first. Choose another."));
        else if (password.Length > 0 && IsPredictable(password, identity))
            reasons.Add(new("password_too_predictable",
                "This password is easy to guess: avoid your name, e-mail, the company name, keyboard runs and repeated characters."));
        return new PasswordPolicyResult(reasons.Count == 0, reasons);
    }

    /// <summary>
    /// Letters from a script without capitals (Tamil, Devanagari, Chinese…) count as both capital and small, so a
    /// person writing in such a script is not refused for a rule their script cannot meet.
    /// </summary>
    private static (bool Upper, bool Lower, bool Digit, bool Symbol) Classes(string password)
    {
        bool upper = false, lower = false, digit = false, symbol = false;
        foreach (var rune in password.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter: upper = true; break;
                case UnicodeCategory.LowercaseLetter: lower = true; break;
                case UnicodeCategory.OtherLetter or UnicodeCategory.ModifierLetter: upper = lower = true; break;
                case UnicodeCategory.DecimalDigitNumber: digit = true; break;
                case UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
                    or UnicodeCategory.EnclosingMark: break;
                default: symbol = true; break;
            }
        }
        return (upper, lower, digit, symbol);
    }

    /// <summary>
    /// On the list as typed, or once digits and symbols at the ends are removed (<c>Password1!</c> is
    /// <c>password</c> with decoration).
    /// </summary>
    private static bool IsCommon(string password)
    {
        var lowered = password.ToLowerInvariant();
        if (CommonPasswords.Contains(lowered)) return true;
        var core = Core(lowered);
        return core.Length >= ShortestListCore && CommonPasswords.Contains(core);
    }

    private static bool IsPredictable(string password, PasswordIdentity identity)
    {
        var lowered = password.ToLowerInvariant();
        var core = Squash(Core(lowered));
        if (core.Length > 0 && IdentityWords(identity).Contains(core)) return true;
        if (Runs.Any(run => ContainsRun(lowered, run) || ContainsRun(lowered, Reverse(run)))) return true;
        var counts = new Dictionary<Rune, int>();
        var total = 0;
        foreach (var rune in lowered.EnumerateRunes())
        {
            counts[rune] = counts.GetValueOrDefault(rune) + 1;
            total++;
        }
        return counts.Values.Max() * 2 > total;
    }

    private static HashSet<string> IdentityWords(PasswordIdentity identity)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        void Add(string value) { var squashed = Squash(value.ToLowerInvariant()); if (squashed.Length > 0) words.Add(squashed); }
        var email = identity.Email.Trim();
        Add(email);
        var at = email.IndexOf('@');
        if (at > 0) Add(email[..at]);
        Add(identity.DisplayName);
        foreach (var part in identity.DisplayName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (part.Count(char.IsLetter) >= ShortestNamePart) Add(part);
        foreach (var company in identity.CompanyNames) Add(company);
        Add(ProductName);
        return words;
    }

    private static bool ContainsRun(string value, string run)
    {
        for (var start = 0; start + RunLength <= run.Length; start++)
            if (value.Contains(run.AsSpan(start, RunLength), StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>The password without digits and symbols at either end.</summary>
    private static string Core(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && !char.IsLetter(value[start])) start++;
        while (end > start && !char.IsLetter(value[end - 1])) end--;
        return value[start..end];
    }

    /// <summary>Spaces, dots and similar separators do not make a name a different word.</summary>
    private static string Squash(string value) =>
        new(value.Where(character => char.IsLetterOrDigit(character)).ToArray());

    private static string Reverse(string value)
    {
        var characters = value.ToCharArray();
        Array.Reverse(characters);
        return new string(characters);
    }
}
