using System.Security.Cryptography;
using System.Text;

namespace Yojana.Integration.Outbox;

/// <summary>A bounded marker for a reply with several remote documents; keeps exact own-write checks durable.</summary>
public static class OutboundAcknowledgement
{
    // TIGHT COUPLING (own-write recognition): EfCx01OwnWriteStore decodes this durable composite marker.
    // Remote replies must survive restarts; separating the encoding and decoding would hide our own writes
    // or suppress later foreign edits. Per-document receipts cover pending commands as well.
    public const string Prefix = "parts|";
    public static bool Matches(string? marker, string id, string changeMarker) => marker?.StartsWith(Prefix, StringComparison.Ordinal) == true
        && marker[Prefix.Length..].Split('|', StringSplitOptions.RemoveEmptyEntries).Contains(Token(id, changeMarker).TrimEnd('|'), StringComparer.Ordinal);
    public static string? Parts(params OutboundDocument[] documents) => Marker(new("", null, documents));
    public static string? Marker(OutboundDocument document)
    {
        if (document.RelatedDocuments is not { Count: > 0 }) return document.ErpChangeMarker;
        var tokens = string.Concat(new[] { document }.Concat(document.RelatedDocuments)
            .Where(x => x.ErpChangeMarker is not null).Select(x => Token(x.ErpExternalId, x.ErpChangeMarker!)));
        return tokens.Length == 0 ? null : Prefix + tokens;
    }

    public static string Token(string id, string marker) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(id + "\n" + marker))) + "|";
}
