using Xunit;
using Yojana.Platform.Mail;
using Yojana.Platform.Security;

namespace Yojana.Platform.Tests;

public sealed class PasskeyOptionsTests
{
    [Fact]
    public void The_product_link_gives_the_relying_party_and_its_one_origin_by_default()
    {
        var resolved = new PasskeyOptions().Resolve(new MailOptions { LinkBaseUrl = "https://Yojana.Example.com/app/" });
        Assert.True(resolved.Configured);
        Assert.Equal("yojana.example.com", resolved.RelyingPartyId);
        Assert.Equal(["https://yojana.example.com"], resolved.Origins!);
    }

    [Fact]
    public void Without_a_link_or_settings_passkeys_are_simply_not_configured()
    {
        Assert.False(new PasskeyOptions().Resolve(new MailOptions()).Configured);
    }

    [Fact]
    public void Several_addresses_on_one_domain_may_share_a_relying_party()
    {
        var resolved = new PasskeyOptions
        {
            RelyingPartyId = "example.com", Origins = ["https://yojana.example.com/", "https://service.example.com"],
        }.Resolve(new MailOptions());
        Assert.Equal(["https://yojana.example.com", "https://service.example.com"], resolved.Origins!);
    }

    [Theory]
    [InlineData("https://yojana.other.com")]       // not on the relying party id
    [InlineData("http://yojana.example.com")]       // clear text away from this computer
    [InlineData("https://yojana.example.com/path")] // an origin has no path
    [InlineData("https://badexample.com")]          // a suffix match must be a whole label
    public void An_origin_the_browser_would_never_accept_stops_the_host_at_startup(string origin)
    {
        var options = new PasskeyOptions { RelyingPartyId = "example.com", Origins = [origin] };
        Assert.Throws<InvalidOperationException>(() => options.Resolve(new MailOptions()));
    }

    [Fact]
    public void Http_is_accepted_only_on_this_computer()
    {
        var resolved = new PasskeyOptions { RelyingPartyId = "localhost", Origins = ["http://localhost:5173"] }
            .Resolve(new MailOptions());
        Assert.True(resolved.Configured);
    }

    [Theory]
    [InlineData(null, null, true, false)]
    [InlineData("false", "true", false, false)]
    [InlineData("true", "true", true, true)]
    [InlineData("not-a-bool", "true", true, true)]
    public void Passkeys_are_allowed_and_passwordless_is_off_until_a_company_says_otherwise(string? allowed,
        string? passwordless, bool expectedAllowed, bool expectedPasswordless)
    {
        Assert.Equal(new PasskeyPermission(expectedAllowed, expectedPasswordless), PasskeyPolicy.Parse(allowed, passwordless));
    }
}
