using System.Security.Cryptography;
using System.Text;

namespace Yojana.Integration.LegacyMigration.Canonicalisation;

internal static class MigrationReviewIdentity
{
    public static string Fingerprint(string sourceSystem, string kind, string canonicalKey,
        IReadOnlyList<string> itemCodes, IReadOnlyList<string> sourceKeys) => Hash(string.Join('\n',
        [sourceSystem, kind, canonicalKey, .. itemCodes, "--", .. sourceKeys]));

    public static string Correlation(string sourceSystem, string kind, string canonicalKey,
        IReadOnlyList<string> itemCodes) => Hash(string.Join('\n',
        [sourceSystem, kind, canonicalKey, .. itemCodes]));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
        .ToLowerInvariant();
}
