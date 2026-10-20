using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Yojana.Relay.Contracts;

namespace Yojana.Relay;

public static class RelaySigning
{
    public static byte[] KeyBytes(string secret) => Convert.FromBase64String(secret);

    public static string Sign(ReadOnlySpan<byte> key, Guid relayId, string timestamp, string nonce,
        string method, string path, ReadOnlySpan<byte> body, string tenant = "") => Convert.ToBase64String(HMACSHA256.HashData(key,
        Encoding.UTF8.GetBytes(string.Join('\n', tenant, relayId.ToString("D"), timestamp, nonce, method, path,
            Convert.ToHexStringLower(SHA256.HashData(body))))));

    public static void Stamp(HttpRequestMessage request, RelayAgentConfiguration configuration, string secret,
        ReadOnlySpan<byte> body, DateTimeOffset? now = null)
    {
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var key = KeyBytes(secret);
        try
        {
            request.Headers.Add("X-Relay-Tenant", configuration.TenantId.ToString("D"));
            request.Headers.Add("X-Relay-Id", configuration.RelayId.ToString("D"));
            request.Headers.Add("X-Relay-Timestamp", timestamp);
            request.Headers.Add("X-Relay-Nonce", nonce);
            request.Headers.Add("X-Relay-Signature", Sign(key, configuration.RelayId, timestamp, nonce,
                request.Method.Method, request.RequestUri!.AbsolutePath, body, configuration.TenantId.ToString("D")));
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }
}
