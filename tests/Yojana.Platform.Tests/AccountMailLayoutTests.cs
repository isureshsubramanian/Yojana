using Xunit;
using Yojana.Platform.Mail;

namespace Yojana.Platform.Tests;

public sealed class AccountMailLayoutTests
{
    private static AccountMailContent Content(string tenant = "Asterra Compute Works") => new(tenant, "Security",
        "Reset your password",
        ["Someone asked to reset the password for <b>your</b> account."],
        [("Requested", "21 Oct 2026, 14:12 UTC"), ("From", "Mumbai \"office\"")],
        ("Choose a new password", "https://asterra.yojana.example/reset#t=a&b"),
        ["The link works once."],
        "Sent by Yojana for Asterra Compute Works.");

    [Fact]
    public void Every_value_is_encoded_so_no_caller_can_put_markup_into_a_message()
    {
        var (html, _) = AccountMailLayout.Render(Content());

        Assert.Contains("&lt;b&gt;your&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>your</b>", html);
        Assert.Contains("Mumbai &quot;office&quot;", html);
        Assert.Contains("href=\"https://asterra.yojana.example/reset#t=a&amp;b\"", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_plain_text_part_carries_the_same_facts_and_the_full_link()
    {
        var (_, text) = AccountMailLayout.Render(Content());

        Assert.StartsWith("Reset your password\n\n", text);
        Assert.Contains("Requested: 21 Oct 2026, 14:12 UTC\n", text);
        Assert.Contains("Choose a new password:\nhttps://asterra.yojana.example/reset#t=a&b\n", text);
        Assert.Contains("The link works once.", text);
        Assert.EndsWith("Sent by Yojana for Asterra Compute Works.\n", text);
    }

    [Theory]
    [InlineData("Asterra Compute Works", "AC")]
    [InlineData("nandira", "N")]
    [InlineData("  ", "Y")]
    [InlineData("(Gridloom) Systems", "GS")]
    public void The_monogram_is_up_to_two_initials_of_the_tenant_name(string name, string expected) =>
        Assert.Equal(expected, AccountMailLayout.Monogram(name));
}
