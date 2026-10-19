using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Keys;
using Azure.Security.KeyVault.Keys.Cryptography;
using Microsoft.Extensions.Configuration;

namespace Yojana.Integration.Security.Cryptography;

public sealed class AzureKeyVaultKeyEncryptionOptions
{
    public const string SectionName = "Integration:Security:AzureKeyVault";

    public required Uri VaultUri { get; init; }
    public required string KeyName { get; init; }
    public required string KeyVersion { get; init; }
    public string Credential { get; init; } = "DefaultAzureCredential";
    public string? ManagedIdentityClientId { get; init; }
    public string? TenantId { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }

    public KeyEncryptionKeyReference CurrentKey => new(
        $"{VaultUri.AbsoluteUri.TrimEnd('/')}/keys/{KeyName}", KeyVersion);

    public static AzureKeyVaultKeyEncryptionOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!Uri.TryCreate(section["VaultUri"], UriKind.Absolute, out var vaultUri) ||
            !StringComparer.OrdinalIgnoreCase.Equals(vaultUri.Scheme, Uri.UriSchemeHttps))
            throw new InvalidOperationException($"{SectionName}:VaultUri must be an absolute HTTPS URI.");
        var keyName = section["KeyName"];
        var keyVersion = section["KeyVersion"];
        if (string.IsNullOrWhiteSpace(keyName) || string.IsNullOrWhiteSpace(keyVersion))
            throw new InvalidOperationException($"{SectionName}:KeyName and KeyVersion are required.");

        return new AzureKeyVaultKeyEncryptionOptions
        {
            VaultUri = vaultUri,
            KeyName = keyName,
            KeyVersion = keyVersion,
            Credential = section["Credential"] ?? "DefaultAzureCredential",
            ManagedIdentityClientId = section["ManagedIdentityClientId"],
            TenantId = section["TenantId"],
            ClientId = section["ClientId"],
            ClientSecret = section["ClientSecret"],
        };
    }
}

internal sealed record AzureKeyVaultWrapResult(
    string KeyId, string KeyVersion, string Algorithm, byte[] Ciphertext);

internal interface IAzureKeyVaultCryptography
{
    Task<AzureKeyVaultWrapResult> WrapAsync(string keyName, string keyVersion, ReadOnlyMemory<byte> plaintext,
        CancellationToken cancellationToken);
    Task<byte[]> UnwrapAsync(string keyId, string keyVersion, string algorithm, ReadOnlyMemory<byte> ciphertext,
        CancellationToken cancellationToken);
}

internal sealed class AzureSdkKeyVaultCryptography(
    AzureKeyVaultKeyEncryptionOptions options,
    TokenCredential credential) : IAzureKeyVaultCryptography
{
    private readonly KeyClient keys = new(options.VaultUri, credential);

    public async Task<AzureKeyVaultWrapResult> WrapAsync(string keyName, string keyVersion,
        ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken)
    {
        var key = (await keys.GetKeyAsync(keyName, keyVersion, cancellationToken)).Value;
        var algorithm = AlgorithmFor(key.KeyType);
        var cryptography = new CryptographyClient(key.Id, credential);
        var wrapped = await cryptography.WrapKeyAsync(algorithm, plaintext.ToArray(), cancellationToken);
        return new AzureKeyVaultWrapResult(KeyIdWithoutVersion(key.Id), key.Properties.Version,
            algorithm.ToString(), wrapped.EncryptedKey);
    }

    public async Task<byte[]> UnwrapAsync(string keyId, string keyVersion, string algorithm,
        ReadOnlyMemory<byte> ciphertext, CancellationToken cancellationToken)
    {
        var keyUri = new Uri($"{keyId.TrimEnd('/')}/{keyVersion}");
        var cryptography = new CryptographyClient(keyUri, credential);
        var result = await cryptography.UnwrapKeyAsync(new KeyWrapAlgorithm(algorithm), ciphertext.ToArray(),
            cancellationToken);
        return result.Key;
    }

    private static KeyWrapAlgorithm AlgorithmFor(KeyType keyType)
    {
        if (keyType == KeyType.Rsa || keyType == KeyType.RsaHsm) return KeyWrapAlgorithm.RsaOaep256;
        if (keyType == KeyType.Oct || keyType == KeyType.OctHsm) return KeyWrapAlgorithm.A256KW;
        throw new NotSupportedException($"Key Vault key type '{keyType}' cannot wrap tenant data keys.");
    }

    private static string KeyIdWithoutVersion(Uri keyId)
    {
        var text = keyId.AbsoluteUri.TrimEnd('/');
        var slash = text.LastIndexOf('/');
        return slash < 0 ? text : text[..slash];
    }
}

public sealed class AzureKeyVaultKeyEncryptionKeyProvider : IKeyEncryptionKeyProvider
{
    private readonly AzureKeyVaultKeyEncryptionOptions options;
    private readonly IAzureKeyVaultCryptography cryptography;

    internal AzureKeyVaultKeyEncryptionKeyProvider(
        AzureKeyVaultKeyEncryptionOptions options,
        IAzureKeyVaultCryptography cryptography)
    {
        this.options = options;
        this.cryptography = cryptography;
    }

    public KeyEncryptionKeyReference CurrentKey => options.CurrentKey;

    public async Task<WrappedDataKey> WrapAsync(ReadOnlyMemory<byte> dataKey,
        CancellationToken cancellationToken)
    {
        var result = await cryptography.WrapAsync(options.KeyName, options.KeyVersion, dataKey, cancellationToken);
        return new WrappedDataKey(result.KeyId, result.KeyVersion, result.Algorithm, [], result.Ciphertext, []);
    }

    public Task<byte[]> UnwrapAsync(WrappedDataKey wrappedKey, CancellationToken cancellationToken)
    {
        if (wrappedKey.Nonce.Length != 0 || wrappedKey.Tag.Length != 0)
            throw new System.Security.Cryptography.CryptographicException(
                "The wrapped data key is not a Key Vault key-wrap payload.");
        return cryptography.UnwrapAsync(wrappedKey.KeyId, wrappedKey.KeyVersion, wrappedKey.Algorithm,
            wrappedKey.Ciphertext, cancellationToken);
    }
}

internal static class AzureCredentialFactory
{
    public static TokenCredential Create(AzureKeyVaultKeyEncryptionOptions options) => options.Credential switch
    {
        "DefaultAzureCredential" => new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = options.ManagedIdentityClientId,
            ExcludeInteractiveBrowserCredential = true,
            ExcludeVisualStudioCredential = true,
            ExcludeVisualStudioCodeCredential = true,
            ExcludeAzureCliCredential = true,
            ExcludeAzurePowerShellCredential = true,
            ExcludeAzureDeveloperCliCredential = true,
            ExcludeBrokerCredential = true,
        }),
        "ManagedIdentity" => string.IsNullOrWhiteSpace(options.ManagedIdentityClientId)
            ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
            : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(options.ManagedIdentityClientId)),
        "ClientSecret" when !string.IsNullOrWhiteSpace(options.TenantId) &&
                            !string.IsNullOrWhiteSpace(options.ClientId) &&
                            !string.IsNullOrWhiteSpace(options.ClientSecret) =>
            new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret),
        "ClientSecret" => throw new InvalidOperationException(
            $"{AzureKeyVaultKeyEncryptionOptions.SectionName}:TenantId, ClientId and ClientSecret are required for ClientSecret."),
        _ => throw new InvalidOperationException(
            $"{AzureKeyVaultKeyEncryptionOptions.SectionName}:Credential must be DefaultAzureCredential, ManagedIdentity or ClientSecret."),
    };
}

internal sealed class MissingKeyEncryptionKeyProvider : IKeyEncryptionKeyProvider
{
    private const string Reason =
        "No key-encryption provider is configured. Set Integration:Security:KeyEncryption:Provider to Local or AzureKeyVault.";

    public KeyEncryptionKeyReference CurrentKey => throw new InvalidOperationException(Reason);

    public Task<WrappedDataKey> WrapAsync(ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken) =>
        Task.FromException<WrappedDataKey>(new InvalidOperationException(Reason));

    public Task<byte[]> UnwrapAsync(WrappedDataKey wrappedKey, CancellationToken cancellationToken) =>
        Task.FromException<byte[]>(new InvalidOperationException(Reason));
}
