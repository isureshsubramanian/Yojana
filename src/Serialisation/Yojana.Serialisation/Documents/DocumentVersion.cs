using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Documents;

public static class DocumentVersion
{
    public static string Encode(byte[] rowVersion) => Convert.ToBase64String(rowVersion);

    public static void RequireExpected(string? expectedVersion, byte[] currentVersion, string changedBy, DateTime changedAt)
    {
        if (expectedVersion is null) return;

        byte[] expected;
        try
        {
            expected = Convert.FromBase64String(expectedVersion);
        }
        catch (FormatException)
        {
            throw new CommandRefusedException("document_version_invalid", "The document version is not a valid base64 rowversion.", DocumentVersionRules.Reload);
        }

        if (!expected.AsSpan().SequenceEqual(currentVersion)) throw new DocumentChangedException(changedBy, changedAt);
    }
}
