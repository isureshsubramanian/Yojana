namespace Yojana.Serialisation.Contracts;

/// <summary>An item label supplied by the item-owning module or a connector.</summary>
public sealed record ItemLabel(Guid ItemId, string Code, string? Name);

/// <summary>
/// Serialisation's read contract for item names. The item-owning module or a connector implements it; Serialisation
/// keeps only item references and never reads the item master's storage.
/// </summary>
public interface IItemNamePort
{
    Task<IReadOnlyDictionary<Guid, ItemLabel>> NamesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<ItemLabel>> SearchAsync(string text, int take, CancellationToken cancellationToken);
}

/// <summary>
/// Service keys for ports the Serialisation module registers itself. A connector that replaces
/// <see cref="IItemNamePort"/> resolves <see cref="ItemNameCatalogue"/> for items it does not hold.
/// </summary>
public static class SerialisationPortKeys
{
    public const string ItemNameCatalogue = "serialisation:item-name-catalogue";
}

/// <summary>An item-family label supplied by the item-owning module or a connector.</summary>
public sealed record ItemFamilyLabel(string Code, string Name);

/// <summary>
/// Serialisation's read contract for item-family names. Item-family codes remain the stable values stored on
/// templates and policies; this port keeps their user-facing names outside those documents.
/// </summary>
public interface IItemFamilyNamePort
{
    Task<IReadOnlyList<ItemFamilyLabel>> ListAsync(CancellationToken cancellationToken);
}

public sealed record ClaimCauseLabel(string Code, string Label, bool Active);

/// <summary>Readable tenant claim-cause labels without exposing the claims catalogue persistence model.</summary>
public interface IClaimCauseNamePort
{
    Task<IReadOnlyList<ClaimCauseLabel>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>A business-partner label supplied by the party-owning module or a connector.</summary>
public sealed record PartyLabel(Guid PartyRef, string Code, string? Name, string? PartyType);

/// <summary>
/// Serialisation's read contract for business-partner names. The party-owning module or a connector implements it;
/// Serialisation keeps opaque party references and never reads partner storage.
/// </summary>
public interface IPartyNamePort
{
    Task<IReadOnlyDictionary<Guid, PartyLabel>> NamesAsync(IReadOnlyCollection<Guid> partyRefs, CancellationToken cancellationToken);

    Task<IReadOnlyList<PartyLabel>> SearchAsync(string text, int take, CancellationToken cancellationToken);
}

/// <summary>A business partner's contact person with an e-mail address, supplied by the party-owning module or a connector.</summary>
public sealed record PartyContactLabel(Guid PartyRef, string Name, string Email, string? Role = null, string? Language = null);

/// <summary>
/// Serialisation's read contract for the people at a business partner that customer e-mail may go to (P9-T20). The party
/// owner (the ERP through a connector) holds contacts; Serialisation never stores them and only offers them as recipients.
/// </summary>
public interface IPartyContactPort
{
    Task<IReadOnlyList<PartyContactLabel>> ContactsAsync(Guid partyRef, CancellationToken cancellationToken);
}

/// <summary>A stock-location label supplied by the location-owning module or a connector.</summary>
public sealed record LocationLabel(Guid LocationId, string Code, string? Name, string? Kind);

/// <summary>
/// Serialisation's narrow read contract for location names. Inventory owns the location master; Serialisation keeps
/// only opaque location references on custody episodes.
/// </summary>
public interface ILocationNamePort
{
    Task<IReadOnlyDictionary<Guid, LocationLabel>> NamesAsync(IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<LocationLabel>> SearchAsync(string text, int take, string? kind, CancellationToken cancellationToken);
}

/// <summary>A document identity without its optional line number.</summary>
public sealed record DocumentKey(string Type, Guid Id)
{
    public static DocumentKey From(DocumentRef document) => new(document.Type, document.Id);
}

/// <summary>A document label supplied by the document-owning module or a connector.</summary>
public sealed record DocumentLabel(DocumentRef Document, string Number, string? Status = null, Guid? ItemId = null);

/// <summary>
/// Serialisation's read contract for document numbers. Each document-owning module or connector contributes through
/// this one boundary; Serialisation stores document references and never reads another module's document tables.
/// </summary>
public interface IDocumentNumberPort
{
    Task<IReadOnlyDictionary<DocumentKey, DocumentLabel>> NumbersAsync(IReadOnlyCollection<DocumentRef> documents, CancellationToken cancellationToken);

    Task<IReadOnlyList<DocumentLabel>> FindAsync(string type, string numberText, int take, CancellationToken cancellationToken);
}
