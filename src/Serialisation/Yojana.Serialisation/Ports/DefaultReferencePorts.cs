using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Yojana.Serialisation.Contracts;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ports;

/// <summary>In-memory item labels for tests and the recorded-message harness; a real item module replaces the port.</summary>
public sealed class InMemoryItemNamePort(IItemQueryPort items) : IItemNamePort
{
    private readonly ConcurrentDictionary<Guid, ItemLabel> labels = new();

    public void Register(ItemLabel label) => labels[label.ItemId] = label;

    public async Task<IReadOnlyDictionary<Guid, ItemLabel>> NamesAsync(IReadOnlyCollection<Guid> itemIds, CancellationToken cancellationToken)
    {
        var answer = new Dictionary<Guid, ItemLabel>();
        foreach (var itemId in itemIds.Distinct())
        {
            if (labels.TryGetValue(itemId, out var registered))
            {
                answer[itemId] = registered;
                continue;
            }

            var profile = await items.GetAsync(itemId, cancellationToken);
            answer[itemId] = new ItemLabel(itemId, profile?.ItemCode ?? ReferenceFallback.ItemCode(itemId), null);
        }

        return answer;
    }

    public Task<IReadOnlyList<ItemLabel>> SearchAsync(string text, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ItemLabel>>(ReferenceFallback.Ranked(labels.Values, text, take,
            label => label.Code, label => label.Name));
}

/// <summary>In-memory partner labels for tests and the recorded-message harness; a real party module replaces the port.</summary>
public sealed class InMemoryPartyNamePort : IPartyNamePort
{
    private readonly ConcurrentDictionary<Guid, PartyLabel> labels = new();

    public void Register(PartyLabel label) => labels[label.PartyRef] = label;

    public Task<IReadOnlyDictionary<Guid, PartyLabel>> NamesAsync(IReadOnlyCollection<Guid> partyRefs, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, PartyLabel>>(partyRefs.Distinct().ToDictionary(
            partyRef => partyRef,
            partyRef => labels.TryGetValue(partyRef, out var label)
                ? label
                : new PartyLabel(partyRef, ReferenceFallback.PartyCode(partyRef), null, null)));

    public Task<IReadOnlyList<PartyLabel>> SearchAsync(string text, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PartyLabel>>(ReferenceFallback.Ranked(labels.Values, text, take,
            label => label.Code, label => label.Name));
}

/// <summary>Registered document labels shared by default document-port scopes in tests and recorded scenarios.</summary>
public sealed class InMemoryDocumentNumberRegistry
{
    private readonly ConcurrentDictionary<DocumentKey, DocumentLabel> labels = new();

    public void Register(DocumentLabel label) => labels[DocumentKey.From(label.Document)] = label;

    public bool TryGet(DocumentKey key, out DocumentLabel label) => labels.TryGetValue(key, out label!);

    public IReadOnlyList<DocumentLabel> Find(string type, string numberText, int take)
    {
        var query = numberText.Trim();
        return labels.Values
            .Where(label => label.Document.Type == type && label.Number.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(label => label.Number.Equals(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(label => label.Number, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .ToList();
    }
}

/// <summary>Tenant-scoped references that Serialisation has itself recorded.</summary>
public interface IRecordedDocumentReferenceQuery
{
    /// <summary>Recorded references of this type whose id starts with these hex digits (empty: any), at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<DocumentRef>> CandidatesAsync(string type, string idPrefix, int take, CancellationToken cancellationToken);
}

/// <summary>
/// Default document-number port. Registered numbers win; fallback numbers cover references already recorded by this
/// module until the document-owning module or connector replaces the port.
/// </summary>
public sealed class DefaultDocumentNumberPort(InMemoryDocumentNumberRegistry registry, IRecordedDocumentReferenceQuery recorded) : IDocumentNumberPort
{
    public Task<IReadOnlyDictionary<DocumentKey, DocumentLabel>> NumbersAsync(IReadOnlyCollection<DocumentRef> documents, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<DocumentKey, DocumentLabel>>(documents
            .Select(document => new { Document = document, Key = DocumentKey.From(document) })
            .DistinctBy(row => row.Key)
            .ToDictionary(row => row.Key, row => registry.TryGet(row.Key, out var label)
                ? label
                : new DocumentLabel(row.Document, ReferenceFallback.DocumentNumber(row.Document))));

    public async Task<IReadOnlyList<DocumentLabel>> FindAsync(string type, string numberText, int take, CancellationToken cancellationToken)
    {
        var registered = registry.Find(type, numberText, take);
        if (registered.Count >= take) return registered;

        var registeredKeys = registered.Select(label => DocumentKey.From(label.Document)).ToHashSet();
        if (ReferenceFallback.IdPrefix(type, numberText) is not { } idPrefix) return registered;
        var candidates = await recorded.CandidatesAsync(type, idPrefix, take + registered.Count, cancellationToken);
        var fallback = candidates
            .Where(document => !registeredKeys.Contains(DocumentKey.From(document)))
            .Select(document => new DocumentLabel(document, ReferenceFallback.DocumentNumber(document)))
            .Where(label => label.Document.Id.ToString("N").StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(label => label.Number.Equals(numberText.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenBy(label => label.Number, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, take - registered.Count));
        return [.. registered, .. fallback];
    }
}

/// <summary>Reads only bounded, indexed sources of document references owned by Serialisation.</summary>
public sealed class RecordedDocumentReferenceQuery(SerialisationDbContext db, ITenantContext tenant) : IRecordedDocumentReferenceQuery
{
    public async Task<IReadOnlyList<DocumentRef>> CandidatesAsync(string type, string idPrefix, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var ids = new HashSet<Guid>();
        if (type == "lot")
        {
            var currentLots = db.Current.AsNoTracking()
                .Where(unit => unit.TenantId == tenantId && unit.LotId != null
                    && unit.LotId.Value.ToString().StartsWith(idPrefix))
                .Select(unit => unit.LotId!.Value);
            var receiptLots = db.Units.AsNoTracking()
                .Where(unit => unit.TenantId == tenantId && unit.LotId != null
                    && unit.LotId.Value.ToString().StartsWith(idPrefix))
                .Select(unit => unit.LotId!.Value);
            var recordedLots = await currentLots.Concat(receiptLots)
                .Distinct()
                .Take(take)
                .ToListAsync(cancellationToken);
            ids.UnionWith(recordedLots);
        }

        if (type == "production_order")
        {
            var allocated = await db.SerialRanges.AsNoTracking()
                .Where(range => range.TenantId == tenantId && range.AllocatedToDocumentType == type && range.AllocatedToDocumentId != null
                    && range.AllocatedToDocumentId.Value.ToString().StartsWith(idPrefix))
                .Select(range => range.AllocatedToDocumentId!.Value)
                .Distinct()
                .Take(take)
                .ToListAsync(cancellationToken);
            ids.UnionWith(allocated);
        }

        if (ids.Count < take)
        {
            // Seeks the ledger's (tenant, document_type, document_id) index to this tenant's documents of the type and
            // tests the id prefix on the index rows: an index range scan, never the table, and exact however many
            // documents exist. A real document module replaces this port and searches its own indexed number column.
            var live = db.Events.AsNoTracking()
                .Where(unitEvent => unitEvent.TenantId == tenantId && unitEvent.DocumentType == type && unitEvent.DocumentId != null
                    && unitEvent.DocumentId.Value.ToString().StartsWith(idPrefix))
                .Select(unitEvent => unitEvent.DocumentId!.Value);
            var archive = db.ArchivedEvents.AsNoTracking()
                .Where(unitEvent => unitEvent.TenantId == tenantId && unitEvent.DocumentType == type && unitEvent.DocumentId != null
                    && unitEvent.DocumentId.Value.ToString().StartsWith(idPrefix))
                .Select(unitEvent => unitEvent.DocumentId!.Value);
            var recorded = await live.Concat(archive)
                .Distinct()
                .Take(take - ids.Count)
                .ToListAsync(cancellationToken);
            ids.UnionWith(recorded);
        }

        return ids.Take(take).Select(id => new DocumentRef(type, id)).ToList();
    }
}

/// <summary>One home for fallback reference formatting, so every endpoint and picker shows the same value.</summary>
public static class ReferenceFallback
{
    /// <summary>
    /// The id hex digits a typed fallback number names ("MO-1A2B" or "1a2b" for a production order gives "1A2B"); empty
    /// when only the prefix was typed; null when the text cannot be a fallback number of this type.
    /// </summary>
    public static string? IdPrefix(string type, string numberText)
    {
        var text = numberText.Trim();
        var prefix = DocumentPrefix(type);
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) text = text[prefix.Length..];
        return text.Length <= 8 && text.All(Uri.IsHexDigit) ? text.ToUpperInvariant() : null;
    }

    public static string ItemCode(Guid itemId) => "ITEM-" + ShortHex(itemId);

    public static string PartyCode(Guid partyRef) => "BP-" + ShortHex(partyRef);

    public static string DocumentNumber(DocumentRef document) => DocumentPrefix(document.Type) + ShortHex(document.Id);

    public static string DocumentPrefix(string type) => type switch
    {
        "production_order" => "MO-",
        "receipt" => "GR-",
        "shipment" => "DN-",
        "invoice" => "IN-",
        "transfer" => "TR-",
        "return" => "RT-",
        "lot" => "LOT-",
        _ => "DOC-",
    };

    internal static IReadOnlyList<T> Ranked<T>(IEnumerable<T> source, string text, int take, params Func<T, string?>[] values)
    {
        var query = text.Trim();
        return source
            .Select(item => new
            {
                Item = item,
                Rank = values.Select(value => value(item)).Where(value => !string.IsNullOrWhiteSpace(value)).Select(value =>
                    value!.Equals(query, StringComparison.OrdinalIgnoreCase) ? 0
                    : value.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 1
                    : value.Contains(query, StringComparison.OrdinalIgnoreCase) ? 2
                    : 3).DefaultIfEmpty(3).Min(),
            })
            .Where(row => row.Rank < 3)
            .OrderBy(row => row.Rank)
            .ThenBy(row => values[0](row.Item), StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(row => row.Item)
            .ToList();
    }

    private static string ShortHex(Guid id) => id.ToString("N")[..8].ToUpperInvariant();
}
