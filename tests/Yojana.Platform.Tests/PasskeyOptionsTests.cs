using Xunit;
using Yojana.Platform.Security;

namespace Yojana.Platform.Tests;

public sealed class PasskeyOptionsTests
{
    [Fact]
    public void Passkeys_stay_off_until_appsettings_names_the_host_and_its_origins()
    {
        Assert.False(new PasskeyOptions().Resolve().Configured);
        Assert.False(new PasskeyOptions { RelyingPartyId = "example.com" }.Resolve().Configured);
    }

    [Fact]
    public void The_host_name_is_normalised()
    {
        var resolved = new PasskeyOptions { RelyingPartyId = " Yojana.Example.com ", Origins = ["https://yojana.example.com/"] }.Resolve();
        Assert.Equal("yojana.example.com", resolved.RelyingPartyId);
        Assert.Equal(["https://yojana.example.com"], resolved.Origins!);
    }

    [Fact]
    public void Several_addresses_on_one_domain_may_share_a_relying_party()
    {
        var resolved = new PasskeyOptions
        {
            RelyingPartyId = "example.com", Origins = ["https://yojana.example.com/", "https://service.example.com"],
        }.Resolve();
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
        Assert.Throws<InvalidOperationException>(() => options.Resolve());
    }

    [Fact]
    public void Http_is_accepted_only_on_this_computer()
    {
        var resolved = new PasskeyOptions { RelyingPartyId = "localhost", Origins = ["http://localhost:5173"] }
            .Resolve();
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
