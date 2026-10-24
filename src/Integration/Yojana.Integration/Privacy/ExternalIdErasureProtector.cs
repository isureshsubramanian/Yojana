using System.Security.Cryptography;
using System.Text;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Privacy;

public sealed class ExternalIdErasureProtector(ITenantSettings settings)
{
    public const string SettingKey = "privacy.external_id_hmac_key";
    private const string Prefix = "hmac256:";

    public async Task<string> ProtectAsync(string value, CancellationToken cancellationToken)
    {
        if (value.StartsWith(Prefix, StringComparison.Ordinal)) return value;
        var encoded = await settings.GetAsync(SettingKey, cancellationToken);
        if (encoded is null)
        {
            encoded = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await settings.SetAsync(SettingKey, encoded, cancellationToken);
        }
        return Protect(value, encoded);
    }

    public async Task<string?> ProtectedIfConfiguredAsync(string value, CancellationToken cancellationToken)
    {
        var encoded = await settings.GetAsync(SettingKey, cancellationToken);
        return encoded is null ? null : Protect(value, encoded);
    }

    private static string Protect(string value, string encodedKey)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(encodedKey));
        return Prefix + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
