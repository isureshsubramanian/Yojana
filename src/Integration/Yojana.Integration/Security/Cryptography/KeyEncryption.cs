using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Yojana.Integration.Security.Cryptography;

public sealed record KeyEncryptionKeyReference(string KeyId, string KeyVersion);

public sealed record WrappedDataKey(
    string KeyId,
    string KeyVersion,
    string Algorithm,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag);

public interface IKeyEncryptionKeyProvider
{
    KeyEncryptionKeyReference CurrentKey { get; }
    Task<WrappedDataKey> WrapAsync(ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken);
    Task<byte[]> UnwrapAsync(WrappedDataKey wrappedKey, CancellationToken cancellationToken);
}

public sealed record LocalKeyEncryptionKey(string KeyId, string KeyVersion, byte[] Key);

public sealed class LocalKeyEncryptionOptions
{
    public const string SectionName = "Integration:Security:LocalKeyEncryption";

    public required LocalKeyEncryptionKey CurrentKey { get; init; }
    public IReadOnlyList<LocalKeyEncryptionKey> PreviousKeys { get; init; } = [];
    public bool AllowInProduction { get; init; }
    public string? ProductionOverrideReason { get; init; }

    public static LocalKeyEncryptionOptions From(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(SectionName);
        var current = ReadKey(section.GetSection("Current"), $"{SectionName}:Current");
        var previous = section.GetSection("Previous").GetChildren()
            .Select(item => ReadKey(item, $"{SectionName}:Previous:{item.Key}"))
            .ToList();
        var duplicates = previous.Append(current)
            .GroupBy(item => (item.KeyId, item.KeyVersion))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicates is not null)
            throw new InvalidOperationException($"{SectionName} contains the duplicate key version '{duplicates.Key}'.");

        var allowInProduction = bool.TryParse(section["AllowInProduction"], out var allowed) && allowed;
        var reason = section["ProductionOverrideReason"];
        if (environment.IsProduction() && (!allowInProduction || string.IsNullOrWhiteSpace(reason)))
            throw new InvalidOperationException(
                "The local key-encryption provider is disabled in Production. An exceptional deployment must set " +
                $"{SectionName}:AllowInProduction=true and provide {SectionName}:ProductionOverrideReason.");

        return new LocalKeyEncryptionOptions
        {
            CurrentKey = current,
            PreviousKeys = previous,
            AllowInProduction = allowInProduction,
            ProductionOverrideReason = reason,
        };
    }

    private static LocalKeyEncryptionKey ReadKey(IConfiguration section, string path)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(section["KeyBase64"] ?? string.Empty);
        }
        catch (FormatException error)
        {
            throw new InvalidOperationException($"{path}:KeyBase64 must be a base64-encoded 256-bit key.", error);
        }

        if (key.Length != 32)
            throw new InvalidOperationException($"{path}:KeyBase64 must decode to exactly 32 bytes.");
        var keyId = section["KeyId"];
        var keyVersion = section["KeyVersion"];
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(keyVersion))
            throw new InvalidOperationException($"{path}:KeyId and KeyVersion are required.");
        return new LocalKeyEncryptionKey(keyId, keyVersion, key);
    }
}

/// <summary>A BCL-only development provider. The key belongs in configuration supplied by user-secrets, never source.</summary>
public sealed class LocalKeyEncryptionKeyProvider(LocalKeyEncryptionOptions options) : IKeyEncryptionKeyProvider
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Dictionary<(string KeyId, string KeyVersion), byte[]> keys = options.PreviousKeys
        .Append(options.CurrentKey)
        .ToDictionary(item => (item.KeyId, item.KeyVersion), item => item.Key);

    public KeyEncryptionKeyReference CurrentKey { get; } =
        new(options.CurrentKey.KeyId, options.CurrentKey.KeyVersion);

    public Task<WrappedDataKey> WrapAsync(ReadOnlyMemory<byte> dataKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[dataKey.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(options.CurrentKey.Key, TagSize);
        aes.Encrypt(nonce, dataKey.Span, ciphertext, tag,
            AssociatedData(options.CurrentKey.KeyId, options.CurrentKey.KeyVersion));
        return Task.FromResult(new WrappedDataKey(options.CurrentKey.KeyId, options.CurrentKey.KeyVersion,
            "A256GCM", nonce, ciphertext, tag));
    }

    public Task<byte[]> UnwrapAsync(WrappedDataKey wrappedKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!StringComparer.Ordinal.Equals(wrappedKey.Algorithm, "A256GCM") ||
            !keys.TryGetValue((wrappedKey.KeyId, wrappedKey.KeyVersion), out var key))
            throw new CryptographicException("The wrapped data key uses an unavailable key-encryption key version or algorithm.");

        var plaintext = new byte[wrappedKey.Ciphertext.Length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(wrappedKey.Nonce, wrappedKey.Ciphertext, wrappedKey.Tag, plaintext,
            AssociatedData(wrappedKey.KeyId, wrappedKey.KeyVersion));
        return Task.FromResult(plaintext);
    }

    private static byte[] AssociatedData(string keyId, string keyVersion) =>
        Encoding.UTF8.GetBytes($"yojana:kek:v1\n{keyId}\n{keyVersion}");
}
