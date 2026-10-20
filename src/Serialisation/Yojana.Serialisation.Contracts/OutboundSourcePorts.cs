namespace Yojana.Serialisation.Contracts;

/// <summary>Identity and effective-dated custody, with the document's customer and their last recorded delivery site.</summary>
public sealed record OutboundUnitSource(string? Serial, Guid? LocationId, Guid? CustomerPartyRef, Guid? ShipToLocationId);

public interface IOutboundUnitSourcePort
{
    Task<OutboundUnitSource> ReadAsync(Guid unitId, DocumentRef? document, CancellationToken cancellationToken, Guid? customerPartyRef = null, bool allowOwnershipFallback = true, DateTime? custodyAt = null);
}

public sealed record OutboundDocumentDescription(string FileName, string ContentType);

public sealed record RenderedOutboundDocument(string FileName, string ContentType, byte[] Bytes);

/// <summary>Returns a stored document's rendered content; unavailable documents are never replaced with invented files.</summary>
public interface IRenderedOutboundDocumentPort
{
    Task<OutboundDocumentDescription?> DescribeAsync(Guid documentId, CancellationToken cancellationToken);
    Task<RenderedOutboundDocument?> ReadAsync(Guid documentId, CancellationToken cancellationToken);
}

/// <summary>The content reference shared by document publishers and connector content stores.</summary>
public static class OutboundDocumentContentReference
{
    private const string Prefix = "serialisation/document/";
    public static string ForDocument(Guid documentId) => Prefix + documentId.ToString("N");
    public static bool TryDocumentId(string? reference, out Guid documentId)
    {
        documentId = Guid.Empty;
        return reference?.StartsWith(Prefix, StringComparison.Ordinal) == true
            && Guid.TryParse(reference[Prefix.Length..], out documentId);
    }
}
