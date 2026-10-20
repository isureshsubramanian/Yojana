using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Relays;

public sealed class RelayAuthentication(IntegrationDbContext db, ITenantContextSetter setter, IClock clock,
    RelaySigningKeyProtection keys, RelayRegistry registry)
{
    public async Task<SiteRelay?> AuthenticateAsync(HttpRequest request, byte[] body, CancellationToken ct)
    {
        var tenantText = request.Headers["X-Relay-Tenant"].ToString();
        if (!TenantId.TryParse(tenantText, out var tenant)
            || !Guid.TryParseExact(request.Headers["X-Relay-Id"].ToString(), "D", out var relayId)
            || !long.TryParse(request.Headers["X-Relay-Timestamp"].ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var timestamp)) return null;
        var timestampText = request.Headers["X-Relay-Timestamp"].ToString();
        var nonce = request.Headers["X-Relay-Nonce"].ToString();
        if (nonce.Length != 48 || !nonce.All(char.IsAsciiHexDigit) || !IsTimely(timestamp, clock.UtcNow)) return null;
        setter.Set(tenant);
        var relay = await db.SiteRelays.SingleOrDefaultAsync(x => x.TenantId == tenant && x.RelayId == relayId
            && x.Status == "active" && db.Connections.Any(connection => connection.TenantId == x.TenantId
                && connection.ConnectionId == x.ConnectionId && connection.Status == ConnectionStatus.Active), ct);
        if (relay is null) return null;
        var path = request.PathBase.Add(request.Path).ToUriComponent();
        var envelope = relay.SigningKeyEnvelope;
        if (!await VerifyEnvelopeAsync(envelope)
            && !(relay.PreviousSecretExpiresAt > clock.UtcNow && relay.PreviousSigningKeyEnvelope is { } previous
                && await VerifyEnvelopeAsync(previous))) return null;
        _ = await registry.ProfileAsync(relay.ConnectionId, ct);

        // TIGHT COUPLING (relay nonce admission): RelayRegistry and RelayAuthentication share the
        // tenant/relay "relay-auth" lock only for nonce admission and credential mutation. Authentication
        // verifies outside it, then rechecks the envelope under it; separating the locks admits revoked keys.
        await using var operation = await ConnectionOperationLock.AcquireAsync(db, tenant, $"relay-auth:{relayId:N}", ct);
        // No decryption or signature computation under the lock: only the admission check and nonce insert.
        if (!await db.SiteRelays.AsNoTracking().AnyAsync(x => x.TenantId == tenant && x.RelayId == relayId
            && x.Status == "active" && (x.SigningKeyEnvelope == envelope
                || x.PreviousSigningKeyEnvelope == envelope && x.PreviousSecretExpiresAt > clock.UtcNow), ct)) return null;
        if (await db.RelayNonces.AnyAsync(x => x.TenantId == tenant && x.RelayId == relayId && x.Nonce == nonce, ct)) return null;
        db.RelayNonces.Add(new() { TenantId = tenant, RelayId = relayId, Nonce = nonce, SeenAt = clock.UtcNow,
            ExpiresAt = clock.UtcNow.AddMinutes(11) });
        await db.SaveChangesAsync(ct);
        return relay;

        async Task<bool> VerifyEnvelopeAsync(string candidate)
        {
            var key = await keys.DecryptAsync(relay, candidate, ct);
            try
            {
                var valid = Verify(key, relayId, timestampText, nonce, request.Method, path, body,
                    request.Headers["X-Relay-Signature"].ToString(), tenantText);
                if (valid) envelope = candidate;
                return valid;
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
    }

    public static bool IsTimely(long timestamp, DateTime now) =>
        Math.Abs((double)timestamp - new DateTimeOffset(now).ToUnixTimeSeconds()) <= 300d;

    public static bool Verify(byte[] key, Guid relayId, string timestamp, string nonce, string method,
        string path, ReadOnlySpan<byte> body, string signature, string tenant = "")
    {
        byte[] presented;
        try { presented = Convert.FromBase64String(signature); }
        catch (FormatException) { return false; }
        var canonical = Encoding.UTF8.GetBytes(string.Join('\n', tenant, relayId.ToString("D"), timestamp, nonce, method, path,
            Convert.ToHexStringLower(SHA256.HashData(body))));
        var expected = HMACSHA256.HashData(key, canonical);
        return presented.Length == expected.Length && CryptographicOperations.FixedTimeEquals(presented, expected);
    }
}
