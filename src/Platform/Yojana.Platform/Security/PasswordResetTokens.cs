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
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    public static string Create()
    {
        Span<char> result = stackalloc char[16];
        Span<byte> sample = stackalloc byte[1];
        var ceiling = byte.MaxValue - (byte.MaxValue + 1) % Alphabet.Length;
        for (var index = 0; index < result.Length;)
        {
            RandomNumberGenerator.Fill(sample);
            if (sample[0] > ceiling) continue;
            result[index++] = Alphabet[sample[0] % Alphabet.Length];
        }
        return new string(result);
    }
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
