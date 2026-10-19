using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Tests;

internal sealed class TestClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
}

internal sealed class TestTenantSettings : ITenantSettings
{
    private readonly Dictionary<string, string> values = [];

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(values.GetValueOrDefault(key));

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        values[key] = value;
        return Task.CompletedTask;
    }
}

internal sealed class FakeAzureKeyVaultCryptography : IAzureKeyVaultCryptography
{
    public List<(string KeyId, string KeyVersion, string Algorithm)> Unwraps { get; } = [];

    public Task<AzureKeyVaultWrapResult> WrapAsync(string keyName, string keyVersion,
        ReadOnlyMemory<byte> plaintext, CancellationToken cancellationToken) =>
        Task.FromResult(new AzureKeyVaultWrapResult("https://vault.invalid/keys/tenant-kek", keyVersion,
            "RSA-OAEP-256", plaintext.ToArray().Reverse().ToArray()));

    public Task<byte[]> UnwrapAsync(string keyId, string keyVersion, string algorithm,
        ReadOnlyMemory<byte> ciphertext, CancellationToken cancellationToken)
    {
        Unwraps.Add((keyId, keyVersion, algorithm));
        return Task.FromResult(ciphertext.ToArray().Reverse().ToArray());
    }
}

internal sealed class FakeAppendBlobStore : IAppendBlobStore
{
    private readonly Dictionary<string, List<byte>> blobs = [];
    public int ConflictsRemaining { get; set; }

    public async IAsyncEnumerable<string> ListAsync(string prefix,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var name in blobs.Keys.Where(item => item.StartsWith(prefix, StringComparison.Ordinal)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return name;
            await Task.Yield();
        }
    }

    public Task<AppendBlobContent?> ReadAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(blobs.TryGetValue(name, out var content)
            ? new AppendBlobContent(content.ToArray(), content.Count)
            : null);
    }

    public Task<bool> AppendAsync(string name, ReadOnlyMemory<byte> content, long expectedLength,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ConflictsRemaining-- > 0) return Task.FromResult(false);
        if (!blobs.TryGetValue(name, out var existing)) blobs[name] = existing = [];
        if (existing.Count != expectedLength) return Task.FromResult(false);
        existing.AddRange(content.ToArray());
        return Task.FromResult(true);
    }
}
