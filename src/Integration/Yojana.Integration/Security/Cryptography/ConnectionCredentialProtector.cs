using System.Security.Cryptography;
using System.Text;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Security.Cryptography;

public sealed record ConnectionCredentialRecord(
    TenantId TenantId,
    Guid ConnectionId,
    string Purpose,
    int DataKeyVersion,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag,
    DateTime CreatedAt,
    DateTime? RotatedAt,
    DateTime? ExpiresAt,
    DateTime RotationDueAt);

public interface IConnectionCredentialStore
{
    Task<ConnectionCredentialRecord?> GetCurrentAsync(TenantId tenantId, Guid connectionId, string purpose,
        CancellationToken cancellationToken);
    Task<IReadOnlyList<ConnectionCredentialRecord>> ListVersionsAsync(TenantId tenantId, Guid connectionId,
        string purpose, CancellationToken cancellationToken);
    Task SaveAsync(ConnectionCredentialRecord credential, CancellationToken cancellationToken);
}

public sealed class InMemoryConnectionCredentialStore : IConnectionCredentialStore
{
    private readonly object sync = new();
    private readonly List<ConnectionCredentialRecord> credentials = [];

    public Task<ConnectionCredentialRecord?> GetCurrentAsync(TenantId tenantId, Guid connectionId, string purpose,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(credentials.SingleOrDefault(x => x.TenantId == tenantId &&
            x.ConnectionId == connectionId && StringComparer.Ordinal.Equals(x.Purpose, purpose) &&
            x.RotatedAt is null) is { } current ? Clone(current) : null);
    }

    public Task<IReadOnlyList<ConnectionCredentialRecord>> ListVersionsAsync(TenantId tenantId, Guid connectionId,
        string purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult<IReadOnlyList<ConnectionCredentialRecord>>(credentials
            .Where(x => x.TenantId == tenantId && x.ConnectionId == connectionId &&
                        StringComparer.Ordinal.Equals(x.Purpose, purpose))
            .OrderByDescending(x => x.CreatedAt).Select(Clone).ToList());
    }

    public Task SaveAsync(ConnectionCredentialRecord credential, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var current = credentials.FindIndex(x => x.TenantId == credential.TenantId &&
                x.ConnectionId == credential.ConnectionId &&
                StringComparer.Ordinal.Equals(x.Purpose, credential.Purpose) && x.RotatedAt is null);
            if (current >= 0) credentials[current] = credentials[current] with { RotatedAt = credential.CreatedAt };
            credentials.Add(Clone(credential));
        }
        return Task.CompletedTask;
    }

    private static ConnectionCredentialRecord Clone(ConnectionCredentialRecord credential) => credential with
    {
        Nonce = credential.Nonce.ToArray(),
        Ciphertext = credential.Ciphertext.ToArray(),
        Tag = credential.Tag.ToArray(),
    };
}

public sealed class ConnectionCredentialProtector(TenantDataKeyService dataKeys, IClock clock)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public async Task<ConnectionCredentialRecord> EncryptAsync(TenantId tenantId, Guid connectionId,
        string purpose, ReadOnlyMemory<byte> secret, DateTime? expiresAt, DateTime rotationDueAt,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(tenantId, connectionId, purpose);
        if (secret.IsEmpty) throw new ArgumentException("Credential secret is required.", nameof(secret));
        var keyRecord = await dataKeys.GetOrCreateAsync(tenantId, cancellationToken);
        var key = await dataKeys.UnwrapAsync(tenantId, keyRecord.Version, cancellationToken);
        try
        {
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[secret.Length];
            var tag = new byte[TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, secret.Span, ciphertext, tag, AssociatedData(tenantId, connectionId, purpose));
            return new ConnectionCredentialRecord(tenantId, connectionId, purpose, keyRecord.Version, nonce,
                ciphertext, tag, clock.UtcNow, null, expiresAt, rotationDueAt);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<byte[]> DecryptAsync(ConnectionCredentialRecord credential,
        CancellationToken cancellationToken)
    {
        ValidateIdentity(credential.TenantId, credential.ConnectionId, credential.Purpose);
        var key = await dataKeys.UnwrapAsync(credential.TenantId, credential.DataKeyVersion, cancellationToken);
        try
        {
            var plaintext = new byte[credential.Ciphertext.Length];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(credential.Nonce, credential.Ciphertext, credential.Tag, plaintext,
                AssociatedData(credential.TenantId, credential.ConnectionId, credential.Purpose));
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] AssociatedData(TenantId tenantId, Guid connectionId, string purpose) =>
        Encoding.UTF8.GetBytes($"yojana:connection-credential:v1\n{tenantId}\n{connectionId:D}\n{purpose}");

    private static void ValidateIdentity(TenantId tenantId, Guid connectionId, string purpose)
    {
        if (tenantId.IsEmpty) throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        if (connectionId == Guid.Empty) throw new ArgumentException("Connection id is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(purpose)) throw new ArgumentException("Credential purpose is required.", nameof(purpose));
    }
}
