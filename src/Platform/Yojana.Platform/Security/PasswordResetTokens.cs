using System.Security.Cryptography;

namespace Yojana.Platform.Security;

public static class PasswordResetTokens
{
    public static (string Token, byte[] Hash) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return (Base64Url(bytes), SHA256.HashData(bytes));
    }

    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return false;
        try
        {
            var value = token.Replace('-', '+').Replace('_', '/');
            value += new string('=', (4 - value.Length % 4) % 4);
            var bytes = Convert.FromBase64String(value);
            if (bytes.Length != 32) return false;
            hash = SHA256.HashData(bytes);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public static class TemporaryPasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!#%+=?@";
    private const string Alphabet = Upper + Lower + Digits + Symbols;
    public const int ShortestLength = 16;

    /// <summary>
    /// At least 16 characters, or the company's minimum when longer, with one of every character class so the
    /// password meets any class rule a company sets (ADR-0008). Look-alike characters are left out.
    /// </summary>
    public static string Create(int minimumLength = ShortestLength)
    {
        var result = new char[Math.Max(ShortestLength, minimumLength)];
        string[] required = [Upper, Lower, Digits, Symbols];
        for (var index = 0; index < result.Length; index++)
            result[index] = Pick(index < required.Length ? required[index] : Alphabet);
        RandomNumberGenerator.Shuffle(result.AsSpan());
        return new string(result);
    }

    private static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
}

public static class BrowserSummary
{
    public static string FromUserAgent(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return "Unknown browser";
        var value = userAgent;
        var browser = value.Contains("Edg/", StringComparison.OrdinalIgnoreCase) ? "Edge"
            : value.Contains("OPR/", StringComparison.OrdinalIgnoreCase) ? "Opera"
            : value.Contains("Chrome/", StringComparison.OrdinalIgnoreCase) ? "Chrome"
            : value.Contains("Firefox/", StringComparison.OrdinalIgnoreCase) ? "Firefox"
            : value.Contains("Safari/", StringComparison.OrdinalIgnoreCase) ? "Safari"
            : "A browser";
        var system = value.Contains("Windows", StringComparison.OrdinalIgnoreCase) ? "Windows"
            : value.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ? "iPhone"
            : value.Contains("iPad", StringComparison.OrdinalIgnoreCase) ? "iPad"
            : value.Contains("Android", StringComparison.OrdinalIgnoreCase) ? "Android"
            : value.Contains("Mac OS", StringComparison.OrdinalIgnoreCase) ? "macOS"
            : value.Contains("Linux", StringComparison.OrdinalIgnoreCase) ? "Linux"
            : null;
        return system is null ? browser : $"{browser} on {system}";
    }
}
