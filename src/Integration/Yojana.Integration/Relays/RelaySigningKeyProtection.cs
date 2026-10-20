using System.Security.Cryptography;
using System.Text.Json;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Relays;

public sealed class RelaySigningKeyProtection(ConnectionCredentialProtector protector)
{
    public async Task<string> EncryptAsync(TenantId tenant, Guid connection, Guid relay, string secret, CancellationToken ct)
    {
        var key = Convert.FromBase64String(secret);
        try
        {
            return JsonSerializer.Serialize(await protector.EncryptAsync(tenant, connection, Purpose(relay), key,
                null, DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), ct), JsonSerializerOptions.Web);
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    // Only authentication opens a signing key. The stored envelope is never itself a signing credential.
    internal async Task<byte[]> DecryptAsync(SiteRelay relay, string envelope, CancellationToken ct)
    {
        var record = JsonSerializer.Deserialize<ConnectionCredentialRecord>(envelope, JsonSerializerOptions.Web)
            ?? throw new CryptographicException("relay_key_invalid");
        if (record.TenantId != relay.TenantId || record.ConnectionId != relay.ConnectionId || record.Purpose != Purpose(relay.RelayId))
            throw new CryptographicException("relay_key_identity_invalid");
        return await protector.DecryptAsync(record, ct);
    }

    private static string Purpose(Guid relay) => $"relay_signing_key_{relay:N}";
}
