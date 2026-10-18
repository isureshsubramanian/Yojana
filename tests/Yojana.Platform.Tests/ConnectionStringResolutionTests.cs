using Xunit;
using Yojana.Platform;
using Microsoft.Data.SqlClient;

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
    public void A_complete_string_keeps_its_authentication_and_gets_the_deliberate_pool_size()
    {
        const string withPassword = "Server=localhost;Database=x;User Id=sa;Password=given;Encrypt=True";
        var password = new SqlConnectionStringBuilder(PlatformOptions.Resolve(withPassword, _ => "other"));
        Assert.Equal("given", password.Password);
        Assert.Equal(200, password.MaxPoolSize);
        const string integrated = "Server=localhost;Database=x;Integrated Security=True";
        var trusted = new SqlConnectionStringBuilder(PlatformOptions.Resolve(integrated, _ => null, 240));
        Assert.True(trusted.IntegratedSecurity);
        Assert.Equal(240, trusted.MaxPoolSize);
    }
}
