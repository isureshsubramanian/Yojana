using System.Security.Cryptography;
using System.Text;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class EnvelopeEncryptionTests
{
    private static readonly TenantId Tenant = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly Guid Connection = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly DateTime Now = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Local_provider_wraps_and_authenticates_data_keys()
    {
        var provider = Provider();
        var plaintext = RandomNumberGenerator.GetBytes(32);
        var wrapped = await provider.WrapAsync(plaintext, CancellationToken.None);

        Assert.Equal(plaintext, await provider.UnwrapAsync(wrapped, CancellationToken.None));
        wrapped.Tag[0] ^= 1;
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() =>
            provider.UnwrapAsync(wrapped, CancellationToken.None));
    }

    [Fact]
    public async Task Data_key_rotation_keeps_old_versions_available_for_decryption()
    {
        var store = new InMemoryTenantDataKeyStore();
        var service = new TenantDataKeyService(store, Provider(), new TestClock(Now));
        var protector = new ConnectionCredentialProtector(service, new TestClock(Now));
        var old = await protector.EncryptAsync(Tenant, Connection, "api-token", "first"u8.ToArray(),
            Now.AddDays(90), Now.AddDays(60), CancellationToken.None);

        var rotated = await service.RotateAsync(Tenant, Now.AddYears(1), CancellationToken.None);
        var current = await protector.EncryptAsync(Tenant, Connection, "api-token", "second"u8.ToArray(),
            Now.AddDays(90), Now.AddDays(60), CancellationToken.None);

        Assert.Equal(2, rotated.Version);
        Assert.Equal(1, old.DataKeyVersion);
        Assert.Equal(2, current.DataKeyVersion);
        Assert.Equal("first", Encoding.UTF8.GetString(
            await protector.DecryptAsync(old, CancellationToken.None)));
        var versions = await store.ListAsync(Tenant, CancellationToken.None);
        Assert.Equal(2, versions.Count);
        Assert.NotNull(versions.Single(x => x.Version == 1).RotatedAt);
    }

    [Fact]
    public async Task Credential_ciphertext_is_bound_to_tenant_connection_and_purpose()
    {
        var store = new InMemoryTenantDataKeyStore();
        var service = new TenantDataKeyService(store, Provider(), new TestClock(Now));
        var protector = new ConnectionCredentialProtector(service, new TestClock(Now));
        var credential = await protector.EncryptAsync(Tenant, Connection, "webhook", "secret"u8.ToArray(),
            null, Now.AddDays(30), CancellationToken.None);

        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => protector.DecryptAsync(
            credential with { ConnectionId = Guid.Parse("20000000-0000-0000-0000-000000000002") },
            CancellationToken.None));
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => protector.DecryptAsync(
            credential with { Purpose = "api-token" }, CancellationToken.None));

        var otherTenant = new TenantId(Guid.Parse("10000000-0000-0000-0000-000000000002"));
        var key = (await store.ListAsync(Tenant, CancellationToken.None)).Single();
        await store.AddAsync(key with { TenantId = otherTenant }, CancellationToken.None);
        await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => protector.DecryptAsync(
            credential with { TenantId = otherTenant }, CancellationToken.None));
    }

    [Fact]
    public async Task Credential_store_keeps_rotated_versions_and_only_one_current_record()
    {
        var store = new InMemoryConnectionCredentialStore();
        var first = CredentialRecord(Now);
        var second = CredentialRecord(Now.AddDays(1));

        await store.SaveAsync(first, CancellationToken.None);
        await store.SaveAsync(second, CancellationToken.None);

        Assert.Equal(second.CreatedAt, (await store.GetCurrentAsync(Tenant, Connection, "api-token",
            CancellationToken.None))!.CreatedAt);
        var versions = await store.ListVersionsAsync(Tenant, Connection, "api-token", CancellationToken.None);
        Assert.Equal(2, versions.Count);
        Assert.Equal(second.CreatedAt, versions.Single(x => x.RotatedAt is not null).RotatedAt);
    }

    private static ConnectionCredentialRecord CredentialRecord(DateTime createdAt) => new(Tenant, Connection,
        "api-token", 1, [1], [2], [3], createdAt, null, createdAt.AddDays(90), createdAt.AddDays(60));

    private static LocalKeyEncryptionKeyProvider Provider() => new(new LocalKeyEncryptionOptions
    {
        CurrentKey = new LocalKeyEncryptionKey("local-test", "1",
            Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()),
    });
}
