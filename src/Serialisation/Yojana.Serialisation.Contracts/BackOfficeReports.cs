namespace Yojana.Serialisation.Contracts;

/// <summary>One physical unit in a selection-set identifier cross-reference, ordered beneath its selected root.</summary>
public sealed record IdentifierCrossReferenceRow(
    string RootSerial,
    string? ParentSerial,
    int Depth,
    string? Slot,
    string InternalSerial,
    string? ManufacturerSerial,
    string? VendorSerial,
    string? CustomerSerial,
    string? AssetTag,
    string Item,
    string? CurrentOwner);

/// <summary>Keyset page. The cursor is opaque and advances by selected root, so a component tree is never split.</summary>
public sealed record IdentifierCrossReferencePage(
    IReadOnlyList<IdentifierCrossReferenceRow> Items,
    string? NextCursor,
    int SelectedRoots);

public static class BackOfficeHolding
{
    public const string InstalledBase = "installed_base";
    public const string Stock = "stock";
    public const string All = "all";
    public static readonly IReadOnlySet<string> Values = new HashSet<string>([InstalledBase, Stock, All], StringComparer.Ordinal);
}

public sealed record InstalledBaseStockFilter(
    IReadOnlyList<Guid>? ItemIds = null,
    string Holding = BackOfficeHolding.All,
    Guid? LocationId = null);

public sealed record InstalledBaseStockRow(
    string Serial,
    string Item,
    string Holding,
    string? CurrentOwner,
    string? Location,
    string? LocationKind,
    string LifecycleStatus,
    string Disposition,
    string? Condition);

public sealed record InstalledBaseStockPage(IReadOnlyList<InstalledBaseStockRow> Items, string? NextCursor);

public sealed record AsBuiltComponentReportRow(
    string Slot,
    string ComponentItem,
    string? ComponentSerial,
    string Outcome,
    int Depth = 1);

public sealed record AsBuiltCompletenessRoot(
    string RootSerial,
    string RootItem,
    string? CustomerItemReference,
    string? CurrentOwner,
    string Outcome,
    IReadOnlyList<AsBuiltComponentReportRow> Components);

public sealed record AsBuiltCompletenessPage(IReadOnlyList<AsBuiltCompletenessRoot> Items, string? NextCursor);

/// <summary>Raw document-chain request passed to the Sales-owned read boundary.</summary>
public sealed record InternalSaleChainRequest(Guid OwnershipEpisodeId, DocumentRef OpeningDocument);

public sealed record InternalWarrantyLineFact(
    int Line,
    Guid ItemId,
    decimal Price,
    string Currency,
    string? PolicyCode,
    Guid? PolicyVersionId,
    string? InternalNote = null);

/// <summary>Sales facts only. Report presentation resolves every document, item and party name through its name port.</summary>
public sealed record InternalSaleChainFact(
    Guid OwnershipEpisodeId,
    DocumentRef? Order,
    DocumentRef? Delivery,
    DocumentRef? Invoice,
    DateTime? DeliveredAt,
    DateTime? InvoicedAt,
    IReadOnlyList<InternalWarrantyLineFact> WarrantyLines,
    string? InternalNote = null);

/// <summary>
/// Batch boundary for the internal sale chain. Sales or a connector owns document storage; Serialisation supplies only
/// ownership episode ids and opening document references and never depends on the other module's schema.
/// </summary>
public interface IInternalSaleDocumentQueryPort
{
    Task<IReadOnlyList<InternalSaleChainFact>> ReadAsync(
        IReadOnlyCollection<InternalSaleChainRequest> requests,
        CancellationToken cancellationToken);
}

public sealed record InternalIdentifier(string Type, string Value, bool Current);

public sealed record InternalWarrantyLine(
    int Line,
    string Item,
    decimal Price,
    string Currency,
    string? PolicyCode,
    string? InternalNote);

public sealed record InternalSaleChain(
    string? Order,
    string? Delivery,
    string? Invoice,
    DateTime? DeliveredAt,
    DateTime? InvoicedAt,
    IReadOnlyList<InternalWarrantyLine> WarrantyLines,
    string? InternalNote);

public sealed record InternalOwnershipSale(
    string Owner,
    DateTime ValidFrom,
    DateTime? ValidTo,
    bool Current,
    string? CustomerPoReference,
    string? CustomerItemReference,
    IReadOnlyList<InternalSaleChain> SaleChains);

public sealed record InternalServiceCost(
    string Kind,
    string Reference,
    string Status,
    DateTime RecordedAt,
    string? InternalNote,
    decimal? Cost,
    string? Currency);

public sealed record InternalUnitLookup(
    string Serial,
    string Item,
    string LifecycleStatus,
    string Disposition,
    IReadOnlyList<InternalIdentifier> Identifiers,
    IReadOnlyList<InternalOwnershipSale> Ownership,
    IReadOnlyList<InternalServiceCost> ServiceHistory);
