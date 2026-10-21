using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Yojana.Platform.Security;

public static class AuthenticatorCodes
{
    public const int SecretBytes = 20;
    public const int PeriodSeconds = 30;

    public static byte[] CreateSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static string Base32Encode(ReadOnlySpan<byte> value)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var result = new StringBuilder((value.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var item in value)
        {
            buffer = (buffer << 8) | item;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) result.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return result.ToString();
    }

    public static string Totp(ReadOnlySpan<byte> secret, long unixSeconds, int digits = 6)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(digits, 6);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(digits, 8);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, unixSeconds / PeriodSeconds);
        Span<byte> digest = stackalloc byte[20];
        // RFC 6238's interoperable authenticator-app profile mandates HMAC-SHA-1 here; this is a short-lived MAC,
        // not a password hash or signature, and changing it would make standard authenticator apps reject the secret.
#pragma warning disable CA5350
        HMACSHA1.HashData(secret, counter, digest);
#pragma warning restore CA5350
        var offset = digest[^1] & 0x0f;
        var binary = BinaryPrimitives.ReadUInt32BigEndian(digest[offset..]) & 0x7fffffff;
        var modulus = digits == 8 ? 100_000_000 : digits == 7 ? 10_000_000 : 1_000_000;
        return (binary % modulus).ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    public static long? Verify(ReadOnlySpan<byte> secret, string? code, DateTime utcNow, long? lastUsedStep)
    {
        var supplied = (code ?? string.Empty).Trim();
        if (supplied.Length != 6 || supplied.Any(character => !char.IsAsciiDigit(character))) return null;
        var current = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds()
                      / PeriodSeconds;
        for (var offset = -1; offset <= 1; offset++)
        {
            var step = current + offset;
            if (lastUsedStep is not null && step <= lastUsedStep) continue;
            var expected = Encoding.ASCII.GetBytes(Totp(secret, step * PeriodSeconds));
            var candidate = Encoding.ASCII.GetBytes(supplied);
            if (CryptographicOperations.FixedTimeEquals(expected, candidate)) return step;
        }
        return null;
    }

    public static string OtpAuthUri(string email, string base32Secret)
    {
        var label = Uri.EscapeDataString("Yojana:" + email);
        return $"otpauth://totp/{label}?secret={base32Secret}&issuer=Yojana&digits=6&period=30";
    }

    public static string GroupManualKey(string base32Secret) => string.Join(' ',
        Enumerable.Range(0, (base32Secret.Length + 3) / 4)
            .Select(index => base32Secret.Substring(index * 4, Math.Min(4, base32Secret.Length - index * 4))));
}

public static class RecoveryCodes
{
    // The approved eight-character display format carries 40 bits from this 32-symbol alphabet, so it cannot meet the
    // alternative 50-bit threshold for a fast SHA-256 hash. Use salted PBKDF2 instead: an offline database disclosure
    // does not turn the human-friendly format into a cheap exhaustive search. Codes remain single-use and throttled.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int SaltSize = 8;
    private const int HashSize = 24;
    private const int Iterations = 210_000;

    public static IReadOnlyList<string> Create(int count = 10)
    {
        var result = new List<string>(count);
        while (result.Count < count)
        {
            var random = RandomNumberGenerator.GetBytes(8);
            var chars = new char[8];
            for (var index = 0; index < chars.Length; index++) chars[index] = Alphabet[random[index] & 31];
            var value = new string(chars);
            if (!result.Contains(value, StringComparer.Ordinal)) result.Add(value[..4] + "-" + value[4..]);
        }
        return result;
    }

    public static string? Normalise(string? value)
    {
        var result = new string((value ?? string.Empty).Where(character => character != '-').ToArray())
            .Trim().ToUpperInvariant();
        return result.Length == 8 && result.All(Alphabet.Contains)
            ? result : null;
    }

    public static byte[] Hash(string normalised)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var result = new byte[SaltSize + HashSize];
        salt.CopyTo(result, 0);
        Rfc2898DeriveBytes.Pbkdf2(normalised, salt, result.AsSpan(SaltSize), Iterations,
            HashAlgorithmName.SHA256);
        return result;
    }

    public static bool Verify(string normalised, ReadOnlySpan<byte> stored)
    {
        if (stored.Length != SaltSize + HashSize) return false;
        Span<byte> candidate = stackalloc byte[HashSize];
        Rfc2898DeriveBytes.Pbkdf2(normalised, stored[..SaltSize], candidate, Iterations,
            HashAlgorithmName.SHA256);
        return CryptographicOperations.FixedTimeEquals(candidate, stored[SaltSize..]);
    }
}
