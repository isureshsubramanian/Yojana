using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Relays;

/// <summary>Transient HTTP envelopes may contain cookies or cloud-held credentials. Reuse tenant key management.</summary>
public sealed class RelayPayloadProtection(ConnectionCredentialProtector protector)
{
    public async Task<string> ProtectAsync<T>(TenantId tenant, Guid connection, Guid request, string direction,
        T value, CancellationToken ct)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);
        if (plaintext.Length > RelayRequestQueue.WireLimit) throw new InvalidDataException("relay_body_too_large");
        try
        {
            using var compressed = new MemoryStream();
            // Compress the JSON's base64 before encryption so an 8 MB binary body still fits the bounded SQL envelope.
            await using (var compressor = new BrotliStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                await compressor.WriteAsync(plaintext, ct);
            var bytes = compressed.ToArray();
            try
            {
                var encrypted = await protector.EncryptAsync(tenant, connection, Purpose(request, direction), bytes,
                    null, DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), ct);
                var result = JsonSerializer.Serialize(encrypted, JsonSerializerOptions.Web);
                if (result.Length > RelayRequestQueue.WireLimit) throw new InvalidDataException("relay_body_too_large");
                return result;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task<T> UnprotectAsync<T>(TenantId tenant, Guid connection, Guid request, string direction,
        string value, CancellationToken ct)
    {
        var encrypted = JsonSerializer.Deserialize<ConnectionCredentialRecord>(value, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("relay_payload_invalid");
        if (encrypted.TenantId != tenant || encrypted.ConnectionId != connection || encrypted.Purpose != Purpose(request, direction))
            throw new InvalidDataException("relay_payload_identity_invalid");
        var compressed = await protector.DecryptAsync(encrypted, ct);
        try
        {
            using var source = new MemoryStream(compressed, false);
            await using var decompressor = new BrotliStream(source, CompressionMode.Decompress);
            var plaintext = await RelayRequestQueue.ReadBoundedAsync(decompressor, RelayRequestQueue.WireLimit, ct);
            try
            {
                return JsonSerializer.Deserialize<T>(plaintext, JsonSerializerOptions.Web)
                    ?? throw new InvalidDataException("relay_payload_invalid");
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        finally { CryptographicOperations.ZeroMemory(compressed); }
    }

    private static string Purpose(Guid request, string direction) => $"relay_{direction}_{request:N}";
}
