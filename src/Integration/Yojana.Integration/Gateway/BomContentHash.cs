using System.Globalization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Yojana.Integration.Contracts;

namespace Yojana.Integration.Gateway;

/// <summary>The stable projection contract used by CX01 change detection and the BOM projection writer.</summary>
internal static class BomContentHash
{
    public static string Compute(IEnumerable<BomUpsertedLine> lines)
    {
        using var content = new MemoryStream();
        foreach (var line in lines.OrderBy(value => value.LineNo))
        {
            Field(content, line.LineNo.ToString(CultureInfo.InvariantCulture));
            Field(content, line.Kind.RawValue ?? line.Kind.Value);
            Field(content, line.ItemExternalId);
            Field(content, line.Description);
            Field(content, line.Quantity.ToString("G29", CultureInfo.InvariantCulture));
            Field(content, line.Uom);
        }
        return Convert.ToHexString(SHA256.HashData(content.ToArray())).ToLowerInvariant();
    }

    private static void Field(Stream target, string? value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? "");
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        target.Write(length);
        target.Write(bytes);
    }
}
