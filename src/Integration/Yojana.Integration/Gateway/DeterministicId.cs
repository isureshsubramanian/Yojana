using System.Security.Cryptography;
using System.Text;

namespace Yojana.Integration.Gateway;

/// <summary>Name-based deterministic identifiers for work derived from an inbound or published message.</summary>
public static class DeterministicId
{
    // Private namespace allocated to Yojana Integration. The resulting UUIDv8 is stable and carries no source UUIDv7
    // timestamp, avoiding the ordering corruption caused by overwriting bytes in a time-ordered identifier.
    private static readonly Guid NamespaceId = new("a7ed6e44-14d8-8a46-9e5c-8a130d1977d0");

    public static Guid Create(Guid sourceId, string salt) => Create(NamespaceId, sourceId, salt);

    public static Guid Create(Guid namespaceId, Guid sourceId, int salt) =>
        Create(namespaceId, sourceId, salt.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static Guid Create(Guid namespaceId, Guid sourceId, string salt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(salt);
        Span<byte> namespaceBytes = stackalloc byte[16];
        Span<byte> sourceBytes = stackalloc byte[16];
        namespaceId.TryWriteBytes(namespaceBytes, bigEndian: true, out _);
        sourceId.TryWriteBytes(sourceBytes, bigEndian: true, out _);
        var saltBytes = Encoding.UTF8.GetBytes(salt);
        var input = new byte[32 + saltBytes.Length];
        namespaceBytes.CopyTo(input);
        sourceBytes.CopyTo(input.AsSpan(16));
        saltBytes.CopyTo(input.AsSpan(32));
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(input, digest);
        Span<byte> value = digest[..16];
        value[6] = (byte)((value[6] & 0x0f) | 0x80); // RFC 9562 UUID version 8.
        value[8] = (byte)((value[8] & 0x3f) | 0x80); // RFC variant.
        return new Guid(value, bigEndian: true);
    }
}
