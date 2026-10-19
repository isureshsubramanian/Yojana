using System.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Security.Cryptography;

public sealed record TenantDataKeyRecord(
    TenantId TenantId,
    int Version,
    string KeyEncryptionKeyId,
    string KeyEncryptionKeyVersion,
    byte[] WrappedKeyNonce,
    byte[] WrappedKeyCiphertext,
    byte[] WrappedKeyTag,
    DateTime CreatedAt,
    DateTime? RotatedAt,
    DateTime? ExpiresAt,
    bool IsActive);

public interface ITenantDataKeyStore
{
    Task<TenantDataKeyRecord?> GetActiveAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task<TenantDataKeyRecord?> GetVersionAsync(TenantId tenantId, int version, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantDataKeyRecord>> ListAsync(TenantId tenantId, CancellationToken cancellationToken);
    Task AddAsync(TenantDataKeyRecord key, CancellationToken cancellationToken);
    Task ReplaceActiveAsync(TenantId tenantId, int expectedVersion, TenantDataKeyRecord replacement,
        DateTime rotatedAt, CancellationToken cancellationToken);
}

public sealed class InMemoryTenantDataKeyStore : ITenantDataKeyStore
{
    private readonly object sync = new();
    private readonly List<TenantDataKeyRecord> keys = [];

    public Task<TenantDataKeyRecord?> GetActiveAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(keys.SingleOrDefault(x => x.TenantId == tenantId && x.IsActive));
    }

    public Task<TenantDataKeyRecord?> GetVersionAsync(TenantId tenantId, int version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(keys.SingleOrDefault(x => x.TenantId == tenantId && x.Version == version));
    }

    public Task<IReadOnlyList<TenantDataKeyRecord>> ListAsync(TenantId tenantId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult<IReadOnlyList<TenantDataKeyRecord>>(
            keys.Where(x => x.TenantId == tenantId).OrderByDescending(x => x.Version).ToList());
    }

    public Task AddAsync(TenantDataKeyRecord key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            if (keys.Any(x => x.TenantId == key.TenantId && (x.Version == key.Version || x.IsActive)))
                throw new InvalidOperationException("A data-key version or active key already exists for this tenant.");
            keys.Add(Clone(key));
        }
        return Task.CompletedTask;
    }

    public Task ReplaceActiveAsync(TenantId tenantId, int expectedVersion, TenantDataKeyRecord replacement,
        DateTime rotatedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            var index = keys.FindIndex(x => x.TenantId == tenantId && x.Version == expectedVersion && x.IsActive);
            if (index < 0) throw new InvalidOperationException("The active tenant data key was not found.");
            if (replacement.TenantId != tenantId || !replacement.IsActive ||
                keys.Any(x => x.TenantId == tenantId && x.Version == replacement.Version))
                throw new InvalidOperationException("The replacement tenant data key is invalid.");
            keys[index] = keys[index] with { IsActive = false, RotatedAt = rotatedAt };
            keys.Add(Clone(replacement));
        }
        return Task.CompletedTask;
    }

    private static TenantDataKeyRecord Clone(TenantDataKeyRecord key) => key with
    {
        WrappedKeyNonce = key.WrappedKeyNonce.ToArray(),
        WrappedKeyCiphertext = key.WrappedKeyCiphertext.ToArray(),
        WrappedKeyTag = key.WrappedKeyTag.ToArray(),
    };
}

public sealed class TenantDataKeyService(
    ITenantDataKeyStore store,
    IKeyEncryptionKeyProvider keyEncryption,
    IClock clock)
{
    public async Task<TenantDataKeyRecord> GetOrCreateAsync(TenantId tenantId,
        CancellationToken cancellationToken)
    {
        if (tenantId.IsEmpty) throw new ArgumentException("Tenant id is required.", nameof(tenantId));
        return await store.GetActiveAsync(tenantId, cancellationToken)
            ?? await CreateAsync(tenantId, 1, null, cancellationToken);
    }

    public async Task<TenantDataKeyRecord> RotateAsync(TenantId tenantId, DateTime? expiresAt,
        CancellationToken cancellationToken)
    {
        var active = await store.GetActiveAsync(tenantId, cancellationToken);
        if (active is null) return await CreateAsync(tenantId, 1, expiresAt, cancellationToken);

        var replacement = await CreateRecordAsync(tenantId, checked(active.Version + 1), expiresAt,
            cancellationToken);
        await store.ReplaceActiveAsync(tenantId, active.Version, replacement, clock.UtcNow, cancellationToken);
        return replacement;
    }

    public async Task<byte[]> UnwrapAsync(TenantId tenantId, int version, CancellationToken cancellationToken)
    {
        var record = await store.GetVersionAsync(tenantId, version, cancellationToken)
            ?? throw new KeyNotFoundException("The tenant data-key version was not found.");
        return await keyEncryption.UnwrapAsync(ToWrapped(record), cancellationToken);
    }

    private async Task<TenantDataKeyRecord> CreateAsync(TenantId tenantId, int version, DateTime? expiresAt,
        CancellationToken cancellationToken)
    {
        var record = await CreateRecordAsync(tenantId, version, expiresAt, cancellationToken);
        await store.AddAsync(record, cancellationToken);
        return record;
    }

    private async Task<TenantDataKeyRecord> CreateRecordAsync(TenantId tenantId, int version, DateTime? expiresAt,
        CancellationToken cancellationToken)
    {
        var plaintext = RandomNumberGenerator.GetBytes(32);
        try
        {
            var wrapped = await keyEncryption.WrapAsync(plaintext, cancellationToken);
            return new TenantDataKeyRecord(tenantId, version, wrapped.KeyId, wrapped.KeyVersion,
                wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag, clock.UtcNow, null, expiresAt, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static WrappedDataKey ToWrapped(TenantDataKeyRecord record) => new(record.KeyEncryptionKeyId,
        record.KeyEncryptionKeyVersion, record.WrappedKeyNonce, record.WrappedKeyCiphertext, record.WrappedKeyTag);
}
