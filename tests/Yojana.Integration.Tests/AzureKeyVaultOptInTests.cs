using System.Security.Cryptography;
using Yojana.Integration.Security.Cryptography;

namespace Yojana.Integration.Tests;

public sealed class AzureKeyVaultOptInTests
{
    [Fact]
    public async Task Wrap_and_unwrap_against_configured_vault_is_opt_in()
    {
        var configured = Environment.GetEnvironmentVariable("YOJANA_TEST_KEYVAULT_URI");
        if (string.IsNullOrWhiteSpace(configured)) return;

        var keyUri = new Uri(configured, UriKind.Absolute);
        var segments = keyUri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 || !StringComparer.OrdinalIgnoreCase.Equals(segments[0], "keys"))
            throw new InvalidOperationException(
                "YOJANA_TEST_KEYVAULT_URI must be a versioned key URI: https://<vault>/keys/<name>/<version>.");
        var options = new AzureKeyVaultKeyEncryptionOptions
        {
            VaultUri = new Uri(keyUri.GetLeftPart(UriPartial.Authority)),
            KeyName = segments[1],
            KeyVersion = segments[2],
        };
        var provider = new AzureKeyVaultKeyEncryptionKeyProvider(options,
            new AzureSdkKeyVaultCryptography(options, AzureCredentialFactory.Create(options)));
        var plaintext = RandomNumberGenerator.GetBytes(32);

        var wrapped = await provider.WrapAsync(plaintext, CancellationToken.None);

        Assert.Equal(plaintext, await provider.UnwrapAsync(wrapped, CancellationToken.None));
    }
}
