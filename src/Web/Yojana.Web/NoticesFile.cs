using System.IO.Compression;
using System.Security.Cryptography;

namespace Yojana.Web;

/// <summary>The shipped notices file, compressed once and fingerprinted for caching; it never changes while the host runs.</summary>
internal sealed record NoticesFile(byte[] Gzip, string ETag)
{
    private static readonly Lock Gate = new();
    private static NoticesFile? loaded;

    public static NoticesFile For(string path)
    {
        lock (Gate)
        {
            if (loaded is not null) return loaded;
            var bytes = File.ReadAllBytes(path);
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
            loaded = new NoticesFile(output.ToArray(), $"\"{Convert.ToHexStringLower(SHA256.HashData(bytes))[..32]}\"");
            return loaded;
        }
    }
}
