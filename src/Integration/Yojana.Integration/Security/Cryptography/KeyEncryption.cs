using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Yojana.Integration.Security.Cryptography;

public sealed record WrappedDataKey(string KeyId, string KeyVersion, byte[] Nonce, byte[] Ciphertext, byte[] Tag);

public interface IKeyEncryptionKeyProvider
{
    Task<WrappedDataKey> WrapAsync(ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken);
    Task<byte[]> UnwrapAsync(WrappedDataKey wrappedKey, CancellationToken cancellationToken);
}

public sealed class LocalKeyEncryptionOptions
{
    public const string SectionName = "Integration:Security:LocalKeyEncryption";

    public required string KeyId { get; init; }
    public required string KeyVersion { get; init; }
    public required byte[] Key { get; init; }
    public bool AllowInProduction { get; init; }
    public string? ProductionOverrideReason { get; init; }

    public static LocalKeyEncryptionOptions From(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(SectionName);
        byte[] key;
        try
        {
            key = Convert.FromBase64String(section["KeyBase64"] ?? string.Empty);
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException($"{SectionName}:KeyBase64 must be a base64-encoded 256-bit key.", error);
        }

        if (key.Length != 32)
            throw new InvalidOperationException($"{SectionName}:KeyBase64 must decode to exactly 32 bytes.");

        var keyId = section["KeyId"];
        var keyVersion = section["KeyVersion"];
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(keyVersion))
            throw new InvalidOperationException($"{SectionName}:KeyId and KeyVersion are required.");

        var allowInProduction = bool.TryParse(section["AllowInProduction"], out var allowed) && allowed;
        var reason = section["ProductionOverrideReason"];
        if (environment.IsProduction() && (!allowInProduction || string.IsNullOrWhiteSpace(reason)))
            throw new InvalidOperationException(
                "The local key-encryption provider is disabled in Production. An exceptional deployment must set " +
                $"{SectionName}:AllowInProduction=true and provide {SectionName}:ProductionOverrideReason.");

        return new LocalKeyEncryptionOptions
        {
            KeyId = keyId,
            KeyVersion = keyVersion,
            Key = key,
            AllowInProduction = allowInProduction,
            ProductionOverrideReason = reason,
        };
    }
}

/// <summary>A BCL-only development provider. The key belongs in configuration supplied by user-secrets, never source.</summary>
public sealed class LocalKeyEncryptionKeyProvider(LocalKeyEncryptionOptions options) : IKeyEncryptionKeyProvider
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public Task<WrappedDataKey> WrapAsync(ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[dataKey.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(options.Key, TagSize);
        aes.Encrypt(nonce, dataKey.Span, ciphertext, tag, AssociatedData(options.KeyId, options.KeyVersion));
        return Task.FromResult(new WrappedDataKey(options.KeyId, options.KeyVersion, nonce, ciphertext, tag));
    }

    public Task<byte[]> UnwrapAsync(WrappedDataKey wrappedKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(options.KeyId, wrappedKey.KeyId) ||
            !StringComparer.Ordinal.Equals(options.KeyVersion, wrappedKey.KeyVersion))
            throw new CryptographicException("The wrapped data key belongs to a different key-encryption key version.");

        var plaintext = new byte[wrappedKey.Ciphertext.Length];
        using var aes = new AesGcm(options.Key, TagSize);
        aes.Decrypt(wrappedKey.Nonce, wrappedKey.Ciphertext, wrappedKey.Tag, plaintext,
            AssociatedData(wrappedKey.KeyId, wrappedKey.KeyVersion));
        return Task.FromResult(plaintext);
    }

    private static byte[] AssociatedData(string keyId, string keyVersion) =>
        Encoding.UTF8.GetBytes($"yojana:kek:v1\n{keyId}\n{keyVersion}");
}
