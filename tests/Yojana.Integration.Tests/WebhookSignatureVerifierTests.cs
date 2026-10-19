using Yojana.Integration.Security.Webhooks;

namespace Yojana.Integration.Tests;

public sealed class WebhookSignatureVerifierTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Payload = "{\"event\":\"changed\"}"u8.ToArray();

    [Fact]
    public void Accepts_either_active_secret_during_rotation()
    {
        var oldSecret = "old-signing-secret"u8.ToArray();
        var newSecret = "new-signing-secret"u8.ToArray();
        var request = new WebhookSignatureRequest(Now, Payload,
            HmacSha256WebhookSignatureVerifier.Sign(Payload, oldSecret),
            [new("old", oldSecret), new("new", newSecret)]);

        Assert.Equal(WebhookSignatureResult.Valid, new HmacSha256WebhookSignatureVerifier().Verify(request, Now));
    }

    [Fact]
    public void Rejects_stale_future_inactive_and_changed_signatures()
    {
        var secret = "signing-secret"u8.ToArray();
        var signature = HmacSha256WebhookSignatureVerifier.Sign(Payload, secret);
        var verifier = new HmacSha256WebhookSignatureVerifier();

        Assert.Equal(WebhookSignatureResult.TimestampOutsideTolerance,
            verifier.Verify(new(Now, Payload, signature, [new("1", secret)]), Now.AddMinutes(6)));
        Assert.Equal(WebhookSignatureResult.TimestampOutsideTolerance,
            verifier.Verify(new(Now.AddMinutes(6), Payload, signature, [new("1", secret)]), Now));
        Assert.Equal(WebhookSignatureResult.NoActiveSecret,
            verifier.Verify(new(Now, Payload, signature, [new("1", secret, false)]), Now));
        signature[0] ^= 1;
        Assert.Equal(WebhookSignatureResult.InvalidSignature,
            verifier.Verify(new(Now, Payload, signature, [new("1", secret)]), Now));
    }
}
