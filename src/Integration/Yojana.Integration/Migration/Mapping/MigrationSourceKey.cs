using System.Security.Cryptography;
using System.Text;

namespace Yojana.Integration.LegacyMigration.Mapping;

/// <summary>The indexable form of a serial-master source key: SHA-256 of its UTF-8 text, as 64 upper-case hex characters.</summary>
public static class MigrationSourceKey
{
    public static string Hash(string sourceKey) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceKey)));
}
