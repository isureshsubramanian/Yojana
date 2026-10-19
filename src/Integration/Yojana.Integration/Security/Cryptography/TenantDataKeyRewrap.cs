using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Security.Cryptography;

public sealed record TenantDataKeyRewrapPosition(TenantId TenantId, int DataKeyVersion);

public sealed record TenantDataKeyRewrapCheckpoint(string OperationId, TenantDataKeyRewrapPosition Position);

public interface ITenantDataKeyRewrapCheckpointStore
{
    Task<TenantDataKeyRewrapCheckpoint?> GetAsync(string operationId, CancellationToken cancellationToken);
    Task SaveAsync(TenantDataKeyRewrapCheckpoint checkpoint, CancellationToken cancellationToken);
    Task CompleteAsync(string operationId, CancellationToken cancellationToken);
}

public sealed class InMemoryTenantDataKeyRewrapCheckpointStore : ITenantDataKeyRewrapCheckpointStore
{
    private readonly object sync = new();
    private readonly Dictionary<string, TenantDataKeyRewrapCheckpoint> checkpoints = [];

    public Task<TenantDataKeyRewrapCheckpoint?> GetAsync(string operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) return Task.FromResult(checkpoints.GetValueOrDefault(operationId));
    }

    public Task SaveAsync(TenantDataKeyRewrapCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) checkpoints[checkpoint.OperationId] = checkpoint;
        return Task.CompletedTask;
    }

    public Task CompleteAsync(string operationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync) checkpoints.Remove(operationId);
        return Task.CompletedTask;
    }
}

public sealed record TenantDataKeyRewrapResult(int Examined, int Rewrapped, bool IsComplete);

/// <summary>
/// Rewraps existing tenant data keys without rotating those keys or touching credential ciphertext. The checkpoint is
/// advanced after each successful record, so an interrupted maintenance run can safely resume with the same id.
/// </summary>
public sealed class TenantDataKeyRewrapService(
    ITenantDataKeyRewrapStore store,
    ITenantDataKeyRewrapCheckpointStore checkpoints,
    IKeyEncryptionKeyProvider keyEncryption)
{
    public async Task<TenantDataKeyRewrapResult> RunBatchAsync(string operationId, int batchSize,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("A rewrap operation id is required.", nameof(operationId));
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var checkpoint = await checkpoints.GetAsync(operationId, cancellationToken);
        var records = await store.ListForRewrapAsync(checkpoint?.Position, batchSize, cancellationToken);
        var rewrapped = 0;
        foreach (var record in records)
        {
            if (!StringComparer.Ordinal.Equals(record.KeyEncryptionKeyId, keyEncryption.CurrentKey.KeyId) ||
                !StringComparer.Ordinal.Equals(record.KeyEncryptionKeyVersion, keyEncryption.CurrentKey.KeyVersion))
            {
                var plaintext = await keyEncryption.UnwrapAsync(ToWrapped(record), cancellationToken);
                try
                {
                    var replacement = await keyEncryption.WrapAsync(plaintext, cancellationToken);
                    await store.ReplaceWrappingAsync(record, replacement, cancellationToken);
                    rewrapped++;
                }
                finally
                {
                    System.Security.Cryptography.CryptographicOperations.ZeroMemory(plaintext);
                }
            }

            await checkpoints.SaveAsync(new TenantDataKeyRewrapCheckpoint(operationId,
                new TenantDataKeyRewrapPosition(record.TenantId, record.Version)), cancellationToken);
        }

        var isComplete = records.Count < batchSize;
        if (isComplete) await checkpoints.CompleteAsync(operationId, cancellationToken);
        return new TenantDataKeyRewrapResult(records.Count, rewrapped, isComplete);
    }

    private static WrappedDataKey ToWrapped(TenantDataKeyRecord record) => new(record.KeyEncryptionKeyId,
        record.KeyEncryptionKeyVersion, record.KeyEncryptionAlgorithm, record.WrappedKeyNonce,
        record.WrappedKeyCiphertext, record.WrappedKeyTag);
}
