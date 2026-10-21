using System.Security.Cryptography;
using System.Text;
using Xunit;
using Yojana.Platform.Security;

namespace Yojana.Platform.Tests;

public sealed class AuthenticatorCodesTests
{
    [Theory]
    [InlineData(59, "94287082")]
    [InlineData(1111111109, "07081804")]
    [InlineData(1111111111, "14050471")]
    [InlineData(1234567890, "89005924")]
    [InlineData(2000000000, "69279037")]
    [InlineData(20000000000, "65353130")]
    public void Rfc_6238_sha1_vectors(long unixSeconds, string expected)
    {
        Assert.Equal(expected, AuthenticatorCodes.Totp(Encoding.ASCII.GetBytes("12345678901234567890"),
            unixSeconds, 8));
    }

    [Fact]
    public void Window_accepts_one_step_either_side_and_refuses_replay()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        var now = DateTimeOffset.FromUnixTimeSeconds(1_234_567_890).UtcDateTime;
        var current = new DateTimeOffset(now).ToUnixTimeSeconds() / AuthenticatorCodes.PeriodSeconds;
        foreach (var offset in new long[] { -1, 0, 1 })
        {
            var code = AuthenticatorCodes.Totp(secret, (current + offset) * AuthenticatorCodes.PeriodSeconds);
            Assert.Equal(current + offset, AuthenticatorCodes.Verify(secret, code, now, null));
        }
        var accepted = AuthenticatorCodes.Verify(secret,
            AuthenticatorCodes.Totp(secret, current * AuthenticatorCodes.PeriodSeconds), now, null);
        Assert.Null(AuthenticatorCodes.Verify(secret,
            AuthenticatorCodes.Totp(secret, current * AuthenticatorCodes.PeriodSeconds), now, accepted));
        Assert.Null(AuthenticatorCodes.Verify(secret,
            AuthenticatorCodes.Totp(secret, (current - 2) * AuthenticatorCodes.PeriodSeconds), now, null));
    }

    [Fact]
    public void Base32_has_no_padding_and_preserves_rfc_examples()
    {
        Assert.Equal("MY", AuthenticatorCodes.Base32Encode("f"u8));
        Assert.Equal("MZXW6YTBOI", AuthenticatorCodes.Base32Encode("foobar"u8));
        Assert.DoesNotContain('=', AuthenticatorCodes.Base32Encode(RandomNumberGenerator.GetBytes(20)));
    }

    [Fact]
    public void Recovery_codes_are_unambiguous_and_normalise_case_and_dash()
    {
        var codes = RecoveryCodes.Create();
        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", code));
        Assert.Equal("ABCD2345", RecoveryCodes.Normalise("abcd-2345"));
        Assert.Null(RecoveryCodes.Normalise("ABCI-2345"));
        var hash = RecoveryCodes.Hash("ABCD2345");
        Assert.True(RecoveryCodes.Verify("ABCD2345", hash));
        Assert.False(RecoveryCodes.Verify("ABCD2346", hash));
    }

    [Fact]
    public void Strictest_mode_and_otpauth_label_are_stable()
    {
        Assert.Equal(SecondStepMode.Required, SecondStepMode.Strictest(
            [SecondStepMode.Off, SecondStepMode.Required, SecondStepMode.Optional]));
        var uri = AuthenticatorCodes.OtpAuthUri("person+service@example.invalid", "ABC234");
        Assert.Equal("otpauth://totp/Yojana%3Aperson%2Bservice%40example.invalid?secret=ABC234&issuer=Yojana&digits=6&period=30", uri);
    }
}
