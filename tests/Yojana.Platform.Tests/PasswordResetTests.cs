using Xunit;
using Yojana.Platform.Security;

namespace Yojana.Platform.Tests;

public sealed class PasswordResetTests
{
    [Theory]
    [InlineData("Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/140.0 Safari/537.36", "Chrome on Windows")]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0) AppleWebKit/605.1 Safari/604.1", "Safari on iPhone")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64) Gecko/20100101 Firefox/142.0", "Firefox on Linux")]
    [InlineData("", "Unknown browser")]
    public void Browser_summary_uses_a_small_stable_browser_and_platform_vocabulary(string value,
        string expected) => Assert.Equal(expected, BrowserSummary.FromUserAgent(value));

    [Fact]
    public void Reset_tokens_are_256_bit_base64url_values_and_only_their_digest_is_recoverable()
    {
        var created = PasswordResetTokens.Create();

        Assert.DoesNotContain('=', created.Token);
        Assert.True(PasswordResetTokens.TryHash(created.Token, out var hash));
        Assert.Equal(32, hash.Length);
        Assert.Equal(created.Hash, hash);
        Assert.False(PasswordResetTokens.TryHash(created.Token + "!", out _));
    }

    [Fact]
    public void Generated_temporary_passwords_are_unambiguous_and_meet_the_platform_policy()
    {
        var policy = new PasswordPolicy(new SignInOptions());
        for (var index = 0; index < 100; index++)
        {
            var password = TemporaryPasswordGenerator.Create();
            Assert.Equal(16, password.Length);
            Assert.DoesNotContain(password, character => "0O1Il".Contains(character));
            Assert.True(policy.Validate(password, "person@example.invalid", "Fictional Person").IsValid);
        }
    }
}
