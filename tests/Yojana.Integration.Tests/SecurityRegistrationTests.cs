using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Cryptography;

namespace Yojana.Integration.Tests;

public sealed class SecurityRegistrationTests
{
    [Fact]
    public async Task Missing_provider_refuses_only_when_key_operations_are_used()
    {
        var builder = Builder("Production", []);
        builder.AddIntegrationModule();
        await using var services = builder.Services.BuildServiceProvider();
        var provider = services.GetRequiredService<IKeyEncryptionKeyProvider>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.WrapAsync(new byte[32], CancellationToken.None));
        Assert.Contains("No key-encryption provider is configured", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Azure_adapters_resolve_without_contacting_Azure()
    {
        var builder = Builder("Production", new Dictionary<string, string?>
        {
            ["Integration:Security:KeyEncryption:Provider"] = "AzureKeyVault",
            ["Integration:Security:AzureKeyVault:VaultUri"] = "https://vault.invalid/",
            ["Integration:Security:AzureKeyVault:KeyName"] = "tenant-kek",
            ["Integration:Security:AzureKeyVault:KeyVersion"] = "version-1",
            ["Integration:Security:Audit:Provider"] = "AzureBlob",
            ["Integration:Security:Audit:AzureBlob:ServiceUri"] = "https://storage.invalid/",
            ["Integration:Security:Audit:AzureBlob:ContainerName"] = "connection-audit",
        });
        builder.AddIntegrationModule();
        await using var services = builder.Services.BuildServiceProvider();

        Assert.IsType<AzureKeyVaultKeyEncryptionKeyProvider>(
            services.GetRequiredService<IKeyEncryptionKeyProvider>());
        Assert.IsType<AzureBlobConnectionAuditSink>(services.GetRequiredService<IConnectionAuditSink>());
    }

    [Fact]
    public void Production_local_provider_requires_explicit_allow_and_reason()
    {
        var builder = Builder("Production", new Dictionary<string, string?>
        {
            ["Integration:Security:KeyEncryption:Provider"] = "Local",
            ["Integration:Security:LocalKeyEncryption:Current:KeyBase64"] = Convert.ToBase64String(new byte[32]),
            ["Integration:Security:LocalKeyEncryption:Current:KeyId"] = "local",
            ["Integration:Security:LocalKeyEncryption:Current:KeyVersion"] = "1",
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
            ["Integration:Security:LocalKeyEncryption:Current:KeyBase64"] = Convert.ToBase64String(new byte[32]),
            ["Integration:Security:LocalKeyEncryption:Current:KeyId"] = "local",
            ["Integration:Security:LocalKeyEncryption:Current:KeyVersion"] = "1",
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
