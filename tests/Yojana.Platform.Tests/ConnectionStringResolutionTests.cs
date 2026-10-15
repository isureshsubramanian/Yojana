using Xunit;
using Yojana.Platform;

namespace Yojana.Platform.Tests;

public sealed class ConnectionStringResolutionTests
{
    private const string LoginWithoutPassword = "Server=localhost,1433;Database=yojana;User Id=sa;Encrypt=True;TrustServerCertificate=True";

    [Fact]
    public void A_login_without_a_password_takes_it_from_the_environment()
    {
        var resolved = PlatformOptions.Resolve(LoginWithoutPassword, name => name == PlatformOptions.PasswordVariable ? "s3cret!A1" : null);
        Assert.Contains("Password=s3cret!A1", resolved, StringComparison.Ordinal);
        Assert.Contains("User ID=sa", resolved, StringComparison.Ordinal);
    }

    [Fact]
    public void A_login_without_a_password_and_no_variable_fails_instead_of_defaulting()
    {
        var error = Assert.Throws<InvalidOperationException>(() => PlatformOptions.Resolve(LoginWithoutPassword, _ => null));
        Assert.Contains(PlatformOptions.PasswordVariable, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_complete_string_is_left_untouched()
    {
        const string withPassword = "Server=localhost;Database=x;User Id=sa;Password=given;Encrypt=True";
        Assert.Equal(withPassword, PlatformOptions.Resolve(withPassword, _ => "other"));
        const string integrated = "Server=localhost;Database=x;Integrated Security=True";
        Assert.Equal(integrated, PlatformOptions.Resolve(integrated, _ => null));
    }
}
