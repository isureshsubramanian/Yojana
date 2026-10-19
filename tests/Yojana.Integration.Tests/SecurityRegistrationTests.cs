using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Yojana.Integration.Tests;

public sealed class SecurityRegistrationTests
{
    [Fact]
    public void Production_local_provider_requires_explicit_allow_and_reason()
    {
        var builder = Builder("Production", new Dictionary<string, string?>
        {
            ["Integration:Security:KeyEncryption:Provider"] = "Local",
            ["Integration:Security:LocalKeyEncryption:KeyBase64"] = Convert.ToBase64String(new byte[32]),
            ["Integration:Security:LocalKeyEncryption:KeyId"] = "local",
            ["Integration:Security:LocalKeyEncryption:KeyVersion"] = "1",
        });

        var error = Assert.Throws<InvalidOperationException>(() => builder.AddIntegrationModule());
        Assert.Contains("ProductionOverrideReason", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_local_provider_accepts_named_exception()
    {
        var builder = Builder("Production", new Dictionary<string, string?>
        {
            ["Integration:Security:KeyEncryption:Provider"] = "Local",
            ["Integration:Security:LocalKeyEncryption:KeyBase64"] = Convert.ToBase64String(new byte[32]),
            ["Integration:Security:LocalKeyEncryption:KeyId"] = "local",
            ["Integration:Security:LocalKeyEncryption:KeyVersion"] = "1",
            ["Integration:Security:LocalKeyEncryption:AllowInProduction"] = "true",
            ["Integration:Security:LocalKeyEncryption:ProductionOverrideReason"] = "Isolated recovery deployment",
        });

        Assert.Same(builder, builder.AddIntegrationModule());
    }

    private static HostApplicationBuilder Builder(string environment, Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environment,
        });
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }
}
