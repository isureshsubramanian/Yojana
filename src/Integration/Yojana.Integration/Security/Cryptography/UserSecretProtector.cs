using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yojana.Platform.Contracts.Security;

namespace Yojana.Integration.Security.Cryptography;

/// <summary>
/// Protects global user secrets with a fresh AES-256 data key per value. This intentionally does not use tenant data
/// keys: one user may belong to several tenants before any tenant is selected. Platform sees only the narrow protector
/// port; moving the envelope implementation does not change authentication or persistence.
/// </summary>
public sealed class UserSecretProtector(IKeyEncryptionKeyProvider keyEncryption) : IUserSecretProtector
{
    private const int FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public async Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose,
        CancellationToken cancellationToken)
    {
        ValidatePurpose(purpose);
        var dataKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var wrapped = await keyEncryption.WrapAsync(dataKey, cancellationToken);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];
            using (var aes = new AesGcm(dataKey, TagSize))
                aes.Encrypt(nonce, plaintext.Span, ciphertext, tag, AssociatedData(purpose));
            return JsonSerializer.SerializeToUtf8Bytes(new Envelope(FormatVersion, wrapped.KeyId,
                wrapped.KeyVersion, wrapped.Algorithm, wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag,
                nonce, ciphertext, tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    public async Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> sealedValue, string purpose,
        CancellationToken cancellationToken)
    {
        ValidatePurpose(purpose);
        Envelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<Envelope>(sealedValue.Span)
                ?? throw new CryptographicException("The user-secret envelope is empty.");
        }
        catch (JsonException error)
        {
            throw new CryptographicException("The user-secret envelope is invalid.", error);
        }
        if (envelope.FormatVersion != FormatVersion || envelope.Nonce.Length != NonceSize ||
            envelope.Tag.Length != TagSize)
            throw new CryptographicException("The user-secret envelope version or shape is unsupported.");

        var dataKey = await keyEncryption.UnwrapAsync(new WrappedDataKey(envelope.KeyId, envelope.KeyVersion,
            envelope.KeyAlgorithm, envelope.WrappedKeyNonce, envelope.WrappedKeyCiphertext,
            envelope.WrappedKeyTag), cancellationToken);
        try
        {
            var plaintext = new byte[envelope.Ciphertext.Length];
            using (var aes = new AesGcm(dataKey, TagSize))
                aes.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plaintext,
                    AssociatedData(purpose));
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
    }

    private static byte[] AssociatedData(string purpose) =>
        Encoding.UTF8.GetBytes($"yojana:user-secret:v1\n{purpose}");

    private static void ValidatePurpose(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Length > 100 || purpose.Any(char.IsControl))
            throw new ArgumentException("A short printable user-secret purpose is required.", nameof(purpose));
    }

    private sealed record Envelope(
        int FormatVersion,
        string KeyId,
        string KeyVersion,
        string KeyAlgorithm,
        byte[] WrappedKeyNonce,
        byte[] WrappedKeyCiphertext,
        byte[] WrappedKeyTag,
        byte[] Nonce,
        byte[] Ciphertext,
        byte[] Tag);
}
