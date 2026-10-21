using System.Security.Cryptography;
using System.Text;
using Xunit;
using Yojana.Integration.Security.Cryptography;

namespace Yojana.Integration.Tests;

public sealed class UserSecretProtectorTests
{
    [Fact]
    public async Task Envelope_round_trips_binds_purpose_and_uses_previous_kek_after_rotation()
    {
        var old = new LocalKeyEncryptionKey("local", "1", RandomNumberGenerator.GetBytes(32));
        var current = new LocalKeyEncryptionKey("local", "2", RandomNumberGenerator.GetBytes(32));
        var first = new UserSecretProtector(new LocalKeyEncryptionKeyProvider(new LocalKeyEncryptionOptions
        {
            CurrentKey = old,
        }));
        var plaintext = Encoding.UTF8.GetBytes("fictional authenticator secret");
        var envelope = await first.ProtectAsync(plaintext, "authenticator", default);
        Assert.DoesNotContain(Convert.ToBase64String(plaintext), Encoding.UTF8.GetString(envelope));

        var rotated = new UserSecretProtector(new LocalKeyEncryptionKeyProvider(new LocalKeyEncryptionOptions
        {
            CurrentKey = current, PreviousKeys = [old],
        }));
        Assert.Equal(plaintext, await rotated.UnprotectAsync(envelope, "authenticator", default));
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            rotated.UnprotectAsync(envelope, "different-purpose", default));
    }
}
