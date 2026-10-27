using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Ports;

/// <summary>In-memory item labels for tests and the development catalogue.</summary>
[DevelopmentOnly]
public sealed class InMemoryItemNamePort(IItemQueryPort items) : IItemNamePort
{
    private readonly ConcurrentDictionary<Guid, ItemLabel> labels = new();

    public void Register(ItemLabel label) => labels[label.ItemId] = label;

    public async Task<IReadOnlyDictionary<Guid, ItemLabel>> NamesAsync(
        IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        var answer = new Dictionary<Guid, ItemLabel>();
        var ids = itemIds.Distinct().ToList();
        var missing = ids.Where(itemId => !labels.ContainsKey(itemId)).ToList();
        var profiles = missing.Count == 0
            ? new Dictionary<Guid, ItemSerialisationProfile?>()
            : await items.GetManyAsync(missing, cancellationToken);
        foreach (var itemId in ids)
        {
            if (labels.TryGetValue(itemId, out var registered))
            {
                answer[itemId] = registered;
                continue;
            }

            profiles.TryGetValue(itemId, out var profile);
            answer[itemId] = new ItemLabel(itemId, profile?.ItemCode ?? ReferenceFallback.ItemCode(itemId), null);
        }

        return answer;
    }

    public Task<IReadOnlyList<ItemLabel>> SearchAsync(string text, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ItemLabel>>(ReferenceFallback.Ranked(labels.Values, text, take,
            label => label.Code, label => label.Name));
}

/// <summary>In-memory item-family labels for tests and the development catalogue.</summary>
[DevelopmentOnly]
public sealed class InMemoryItemFamilyNamePort : IItemFamilyNamePort
{
    private readonly ConcurrentDictionary<string, ItemFamilyLabel> labels = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ItemFamilyLabel label) => labels[label.Code] = label;

    public Task<IReadOnlyList<ItemFamilyLabel>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ItemFamilyLabel>>(labels.Values
            .OrderBy(label => label.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(label => label.Code, StringComparer.OrdinalIgnoreCase).ToList());
}

/// <summary>In-memory partner labels for tests and the development catalogue.</summary>
[DevelopmentOnly]
public sealed class InMemoryPartyNamePort : IPartyNamePort
{
    private readonly ConcurrentDictionary<Guid, PartyLabel> labels = new();

    public void Register(PartyLabel label) => labels[label.PartyRef] = label;

    public Task<IReadOnlyDictionary<Guid, PartyLabel>> NamesAsync(
        IReadOnlyCollection<Guid> partyRefs, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PartyLabel>>(partyRefs.Distinct().ToDictionary(
            partyRef => partyRef,
            partyRef => labels.TryGetValue(partyRef, out var label)
                ? label
                : new PartyLabel(partyRef, ReferenceFallback.PartyCode(partyRef), null, null)));

    public Task<IReadOnlyList<PartyLabel>> SearchAsync(string text, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PartyLabel>>(ReferenceFallback.Ranked(labels.Values, text, take,
            label => label.Code, label => label.Name));
}

/// <summary>Development and test contacts until the party source supplies them.</summary>
[DevelopmentOnly]
public sealed class InMemoryPartyContactPort : IPartyContactPort
{
    private readonly ConcurrentDictionary<Guid, IReadOnlyList<PartyContactLabel>> contacts = new();

    public void Register(PartyContactLabel contact) => contacts.AddOrUpdate(contact.PartyRef, [contact],
        (_, existing) => existing.Where(x => !x.Email.Equals(contact.Email, StringComparison.OrdinalIgnoreCase))
            .Append(contact).ToList());

    public Task<IReadOnlyList<PartyContactLabel>> ContactsAsync(Guid partyRef, CancellationToken cancellationToken) =>
        Task.FromResult(contacts.TryGetValue(partyRef, out var found)
            ? found : (IReadOnlyList<PartyContactLabel>)[]);
}

/// <summary>Registered location labels shared by development and test scopes.</summary>
[DevelopmentOnly]
public sealed class InMemoryLocationNameRegistry
{
    private readonly ConcurrentDictionary<Guid, LocationLabel> labels = new();

    public void Register(LocationLabel label) => labels[label.LocationId] = label;

    public bool TryGet(Guid locationId, out LocationLabel label) => labels.TryGetValue(locationId, out label!);

    public IReadOnlyList<LocationLabel> Search(string text, int take, string? kind) =>
        ReferenceFallback.Ranked(labels.Values.Where(label => kind is null
                || string.Equals(label.Kind, kind, StringComparison.OrdinalIgnoreCase)), text, take,
            label => label.Code, label => label.Name);
}

/// <summary>Development catalogue over the production-safe recorded-reference fallback.</summary>
[DevelopmentOnly]
public sealed class DefaultLocationNamePort(
    InMemoryLocationNameRegistry registry,
    ILocationNamePort recorded) : ILocationNamePort
{
    public DefaultLocationNamePort(InMemoryLocationNameRegistry registry, IRecordedLocationReferenceQuery recorded)
        : this(registry, new RecordedLocationNamePort(recorded)) { }

    public async Task<IReadOnlyDictionary<Guid, LocationLabel>> NamesAsync(
        IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken)
    {
        var ids = locationIds.Distinct().ToList();
        var missing = ids.Where(id => !registry.TryGet(id, out _)).ToList();
        var fallback = missing.Count == 0
            ? new Dictionary<Guid, LocationLabel>()
            : (await recorded.NamesAsync(missing, cancellationToken)).ToDictionary();
        return ids.ToDictionary(id => id, id => registry.TryGet(id, out var label) ? label : fallback[id]);
    }

    public async Task<IReadOnlyList<LocationLabel>> SearchAsync(
        string text, int take, string? kind, CancellationToken cancellationToken)
    {
        var registered = registry.Search(text, take, kind);
        if (registered.Count >= take) return registered;
        var registeredIds = registered.Select(label => label.LocationId).ToHashSet();
        var fallback = await recorded.SearchAsync(text, take + registered.Count, kind, cancellationToken);
        return [.. registered, .. fallback.Where(label => !registeredIds.Contains(label.LocationId))
            .Take(Math.Max(0, take - registered.Count))];
    }
}

/// <summary>Registered document labels shared by development and test scopes.</summary>
[DevelopmentOnly]
public sealed class InMemoryDocumentNumberRegistry
{
    private readonly ConcurrentDictionary<DocumentKey, DocumentLabel> labels = new();

    public void Register(DocumentLabel label) => labels[DocumentKey.From(label.Document)] = label;

    public bool TryGet(DocumentKey key, out DocumentLabel label) => labels.TryGetValue(key, out label!);

    public IReadOnlyList<DocumentLabel> Find(string type, string numberText, int take)
    {
        var query = numberText.Trim();
        return labels.Values
            .Where(label => label.Document.Type == type
                && label.Number.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(label => label.Number.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(label => label.Number, StringComparer.OrdinalIgnoreCase)
            .Take(take).ToList();
    }
}

/// <summary>Development catalogue over the production-safe recorded-reference fallback.</summary>
[DevelopmentOnly]
public sealed class DefaultDocumentNumberPort(
    InMemoryDocumentNumberRegistry registry,
    IDocumentNumberPort recorded) : IDocumentNumberPort
{
    public DefaultDocumentNumberPort(InMemoryDocumentNumberRegistry registry, IRecordedDocumentReferenceQuery recorded)
        : this(registry, new RecordedDocumentNumberPort(recorded)) { }

    public async Task<IReadOnlyDictionary<DocumentKey, DocumentLabel>> NumbersAsync(
        IReadOnlyCollection<DocumentRef> documents, CancellationToken cancellationToken)
    {
        var distinct = documents.Select(document => new { Document = document, Key = DocumentKey.From(document) })
            .DistinctBy(row => row.Key).ToList();
        var missing = distinct.Where(row => !registry.TryGet(row.Key, out _)).Select(row => row.Document).ToList();
        var fallback = missing.Count == 0
            ? new Dictionary<DocumentKey, DocumentLabel>()
            : (await recorded.NumbersAsync(missing, cancellationToken)).ToDictionary();
        return distinct.ToDictionary(row => row.Key, row => registry.TryGet(row.Key, out var label)
            ? label : fallback[row.Key]);
    }

    public async Task<IReadOnlyList<DocumentLabel>> FindAsync(
        string type, string numberText, int take, CancellationToken cancellationToken)
    {
        var registered = registry.Find(type, numberText, take);
        if (registered.Count >= take) return registered;
        var registeredKeys = registered.Select(label => DocumentKey.From(label.Document)).ToHashSet();
        var fallback = await recorded.FindAsync(type, numberText, take + registered.Count, cancellationToken);
        return [.. registered, .. fallback.Where(label => !registeredKeys.Contains(DocumentKey.From(label.Document)))
            .Take(Math.Max(0, take - registered.Count))];
    }
}

/// <summary>Adds recorded claims to facts supplied by development scenarios.</summary>
[DevelopmentOnly]
public sealed class DevelopmentWarrantyClaimsPort(
    IWarrantyClaimsPort recorded,
    InMemoryWarrantyPorts development) : IWarrantyClaimsPort
{
    public async Task<bool> HasOpenClaimsAsync(
        Guid unitId, Guid entitlementId, CancellationToken cancellationToken) =>
        await recorded.HasOpenClaimsAsync(unitId, entitlementId, cancellationToken)
        || await development.HasOpenClaimsAsync(unitId, entitlementId, cancellationToken);

    public async Task<bool> HasOpenClaimsAsync(
        Guid unitId, IReadOnlyList<Guid> entitlementIds, CancellationToken cancellationToken) =>
        await recorded.HasOpenClaimsAsync(unitId, entitlementIds, cancellationToken)
        || await development.HasOpenClaimsAsync(unitId, entitlementIds, cancellationToken);
}
