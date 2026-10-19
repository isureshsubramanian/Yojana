using System.Security.Cryptography;

namespace Yojana.Integration.Security.Webhooks;

public sealed record WebhookSigningSecret(string Version, byte[] Secret, bool IsActive = true);

public sealed record WebhookSignatureRequest(
    DateTime TimestampUtc,
    ReadOnlyMemory<byte> SignedContent,
    ReadOnlyMemory<byte> Signature,
    IReadOnlyList<WebhookSigningSecret> Secrets);

public enum WebhookSignatureResult
{
    Valid,
    TimestampOutsideTolerance,
    NoActiveSecret,
    InvalidSignature,
}

public sealed class HmacSha256WebhookSignatureVerifier(TimeSpan? timestampTolerance = null)
{
    public static readonly TimeSpan DefaultTimestampTolerance = TimeSpan.FromMinutes(5);
    private readonly TimeSpan tolerance = timestampTolerance ?? DefaultTimestampTolerance;

    public WebhookSignatureResult Verify(WebhookSignatureRequest request, DateTime nowUtc)
    {
        if (request.TimestampUtc.Kind != DateTimeKind.Utc || nowUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Webhook timestamps and current time must be UTC.");
        if (tolerance <= TimeSpan.Zero) throw new InvalidOperationException("Timestamp tolerance must be positive.");
        if ((nowUtc - request.TimestampUtc).Duration() > tolerance)
            return WebhookSignatureResult.TimestampOutsideTolerance;

        var active = request.Secrets.Where(x => x.IsActive).ToList();
        if (active.Count == 0) return WebhookSignatureResult.NoActiveSecret;

        var match = false;
        Span<byte> presented = stackalloc byte[32];
        if (request.Signature.Length == presented.Length) request.Signature.Span.CopyTo(presented);
        foreach (var secret in active)
        {
            using var hmac = new HMACSHA256(secret.Secret);
            var expected = hmac.ComputeHash(request.SignedContent.ToArray());
            // Evaluate every active secret so the match position does not reveal which rotation version succeeded.
            match |= request.Signature.Length == expected.Length &&
                     CryptographicOperations.FixedTimeEquals(presented, expected);
        }
        return match ? WebhookSignatureResult.Valid : WebhookSignatureResult.InvalidSignature;
    }

    public static byte[] Sign(ReadOnlySpan<byte> signedContent, ReadOnlySpan<byte> secret)
    {
        using var hmac = new HMACSHA256(secret.ToArray());
        return hmac.ComputeHash(signedContent.ToArray());
    }
}
