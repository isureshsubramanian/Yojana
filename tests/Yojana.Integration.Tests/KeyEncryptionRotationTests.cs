using System.Security.Cryptography;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class KeyEncryptionRotationTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId TenantOne = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly TenantId TenantTwo = new(Guid.Parse("10000000-0000-0000-0000-000000000002"));

    [Fact]
    public async Task Local_key_ring_wraps_with_current_and_unwraps_previous_versions()
    {
        var previous = Key("1", 1);
        var provider = Provider(Key("2", 33), previous);
        var oldProvider = Provider(previous);
        var plaintext = RandomNumberGenerator.GetBytes(32);
        var old = await oldProvider.WrapAsync(plaintext, CancellationToken.None);
        var current = await provider.WrapAsync(plaintext, CancellationToken.None);

        Assert.Equal("2", current.KeyVersion);
        Assert.Equal(plaintext, await provider.UnwrapAsync(old, CancellationToken.None));
    }

    [Fact]
    public async Task Rewrap_operation_resumes_after_each_batch_and_preserves_data_keys()
    {
        var store = new InMemoryTenantDataKeyStore();
        var oldProvider = Provider(Key("1", 1));
        var clock = new TestClock(Now);
        var oldService = new TenantDataKeyService(store, oldProvider, clock);
        await oldService.GetOrCreateAsync(TenantOne, CancellationToken.None);
        await oldService.GetOrCreateAsync(TenantTwo, CancellationToken.None);
        var before = await oldService.UnwrapAsync(TenantOne, 1, CancellationToken.None);

        var currentProvider = Provider(Key("2", 33), Key("1", 1));
        var checkpoints = new InMemoryTenantDataKeyRewrapCheckpointStore();
        var operation = new TenantDataKeyRewrapService(store, checkpoints, currentProvider);
        var first = await operation.RunBatchAsync("rotate-2026-09", 1, CancellationToken.None);
        var resumed = await operation.RunBatchAsync("rotate-2026-09", 10, CancellationToken.None);

        Assert.False(first.IsComplete);
        Assert.Equal(1, first.Rewrapped);
        Assert.True(resumed.IsComplete);
        Assert.Equal(1, resumed.Rewrapped);
        Assert.All(await store.ListAsync(TenantOne, CancellationToken.None),
            item => Assert.Equal("2", item.KeyEncryptionKeyVersion));
        Assert.Equal(before, await new TenantDataKeyService(store, currentProvider, clock)
            .UnwrapAsync(TenantOne, 1, CancellationToken.None));
    }

    [Fact]
    public async Task Azure_provider_uses_recorded_key_version_for_unwrap()
    {
        var fake = new FakeAzureKeyVaultCryptography();
        var options = new AzureKeyVaultKeyEncryptionOptions
        {
            VaultUri = new Uri("https://vault.invalid/"),
            KeyName = "tenant-kek",
            KeyVersion = "current-version",
        };
        var provider = new AzureKeyVaultKeyEncryptionKeyProvider(options, fake);
        var wrapped = await provider.WrapAsync("data-key"u8.ToArray(), CancellationToken.None);
        var recorded = wrapped with { KeyVersion = "older-version" };

        Assert.Equal("data-key"u8.ToArray(), await provider.UnwrapAsync(recorded, CancellationToken.None));
        Assert.Equal("older-version", Assert.Single(fake.Unwraps).KeyVersion);
    }

    private static LocalKeyEncryptionKey Key(string version, int start) => new("local", version,
        Enumerable.Range(start, 32).Select(item => (byte)item).ToArray());

    private static LocalKeyEncryptionKeyProvider Provider(LocalKeyEncryptionKey current,
        params LocalKeyEncryptionKey[] previous) => new(new LocalKeyEncryptionOptions
        {
            CurrentKey = current,
            PreviousKeys = previous,
        });
}
