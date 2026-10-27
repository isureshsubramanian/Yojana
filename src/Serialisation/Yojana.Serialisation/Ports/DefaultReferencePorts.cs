using Yojana.Platform.Contracts.Privacy;
using Microsoft.EntityFrameworkCore;
using Yojana.Serialisation.Contracts;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Ports;

/// <summary>Prevents every party-name consumer from redisplaying a name after its Platform tombstone is committed.</summary>
public sealed class ErasureAwarePartyNamePort(
    IPartyNamePort inner,
    IPersonalDataErasureReadPort erasures) : IPartyNamePort, ISerialisationPortComposition
{
    public IReadOnlyCollection<object> Sources => [inner];

    public async Task<IReadOnlyDictionary<Guid, PartyLabel>> NamesAsync(IReadOnlyCollection<Guid> partyRefs,
        CancellationToken cancellationToken)
    {
        var labels = (await inner.NamesAsync(partyRefs, cancellationToken)).ToDictionary();
        foreach (var partyRef in labels.Keys.ToList())
            if (await erasures.ErasedPartyAsync(partyRef, cancellationToken) is { } erased)
                labels[partyRef] = new PartyLabel(partyRef, ErasedCode(partyRef), erased.Token, null);
        return labels;
    }

    public async Task<IReadOnlyList<PartyLabel>> SearchAsync(string text, int take,
        CancellationToken cancellationToken)
    {
        var labels = await inner.SearchAsync(text, take, cancellationToken);
        var result = new List<PartyLabel>(labels.Count);
        foreach (var label in labels)
        {
            var erased = await erasures.ErasedPartyAsync(label.PartyRef, cancellationToken);
            result.Add(erased is null ? label
                : new PartyLabel(label.PartyRef, ErasedCode(label.PartyRef), erased.Token, null));
        }
        return result;
    }

    private static string ErasedCode(Guid partyRef) =>
        $"ERASED-{partyRef.ToString("N")[..4].ToUpperInvariant()}";
}

/// <summary>A location reference observed on one of this tenant's custody episodes.</summary>
public sealed record RecordedLocationReference(Guid LocationId, string? Kind);

/// <summary>Tenant-scoped location references that Serialisation has recorded in custody history.</summary>
public interface IRecordedLocationReferenceQuery
{
    Task<IReadOnlyDictionary<Guid, string?>> KindsAsync(IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecordedLocationReference>> CandidatesAsync(
        string idPrefix, string? kind, int take, CancellationToken cancellationToken);
}

/// <summary>
/// Production-safe location-name fallback over references already observed in custody history.
/// </summary>
public sealed class RecordedLocationNamePort(IRecordedLocationReferenceQuery recorded) : ILocationNamePort
{
    public async Task<IReadOnlyDictionary<Guid, LocationLabel>> NamesAsync(
        IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken)
    {
        var ids = locationIds.Distinct().ToList();
        IReadOnlyDictionary<Guid, string?> recordedKinds = ids.Count == 0
            ? new Dictionary<Guid, string?>()
            : await recorded.KindsAsync(ids, cancellationToken);

        return ids.ToDictionary(id => id, id => new LocationLabel(id, ReferenceFallback.LocationCode(id), null,
            recordedKinds.TryGetValue(id, out var kind) ? kind : null));
    }

    public async Task<IReadOnlyList<LocationLabel>> SearchAsync(
        string text, int take, string? kind, CancellationToken cancellationToken)
    {
        if (ReferenceFallback.LocationIdPrefix(text) is not { } idPrefix) return [];
        var candidates = await recorded.CandidatesAsync(idPrefix, kind, take, cancellationToken);
        return candidates
            .DistinctBy(candidate => candidate.LocationId)
            .Select(candidate => new LocationLabel(candidate.LocationId,
                ReferenceFallback.LocationCode(candidate.LocationId), null, candidate.Kind))
            .OrderByDescending(label => label.Code.Equals(text.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenBy(label => label.Code, StringComparer.OrdinalIgnoreCase)
            .Take(take).ToList();
    }
}

/// <summary>Reads only tenant-filtered custody history for location fallback labels.</summary>
public sealed class RecordedLocationReferenceQuery(SerialisationDbContext db, ITenantContext tenant) : IRecordedLocationReferenceQuery
{
    public async Task<IReadOnlyDictionary<Guid, string?>> KindsAsync(
        IReadOnlyCollection<Guid> locationIds, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var ids = locationIds.Distinct().ToList();
        var rows = await db.Episodes.AsNoTracking()
            .Where(episode => episode.TenantId == tenantId && episode.LocationId != null
                && ids.Contains(episode.LocationId.Value))
            .OrderByDescending(episode => episode.ValidFrom)
            .Select(episode => new { LocationId = episode.LocationId!.Value, episode.LocationKind })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(row => row.LocationId)
            .ToDictionary(group => group.Key, group => group.Select(row => row.LocationKind).FirstOrDefault());
    }

    public async Task<IReadOnlyList<RecordedLocationReference>> CandidatesAsync(
        string idPrefix, string? kind, int take, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = await db.Episodes.AsNoTracking()
            .Where(episode => episode.TenantId == tenantId && episode.LocationId != null
                && episode.LocationId.Value.ToString().StartsWith(idPrefix)
                && (kind == null || episode.LocationKind == kind))
            .OrderByDescending(episode => episode.ValidFrom)
            .Select(episode => new { LocationId = episode.LocationId!.Value, episode.LocationKind })
            .Take(take * 3)
            .ToListAsync(cancellationToken);
        return rows.DistinctBy(row => row.LocationId).Take(take)
            .Select(row => new RecordedLocationReference(row.LocationId, row.LocationKind)).ToList();
    }
}

/// <summary>Tenant-scoped references that Serialisation has itself recorded.</summary>
public interface IRecordedDocumentReferenceQuery
{
    /// <summary>Recorded references of this type whose id starts with these hex digits (empty: any), at most <paramref name="take"/>.</summary>
    Task<IReadOnlyList<DocumentRef>> CandidatesAsync(string type, string idPrefix, int take, CancellationToken cancellationToken);
}

/// <summary>
/// Production-safe document-number fallback over references already recorded by this module.
/// </summary>
public sealed class RecordedDocumentNumberPort(IRecordedDocumentReferenceQuery recorded) : IDocumentNumberPort
{
    public Task<IReadOnlyDictionary<DocumentKey, DocumentLabel>> NumbersAsync(IReadOnlyCollection<DocumentRef> documents, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<DocumentKey, DocumentLabel>>(documents
            .Select(document => new { Document = document, Key = DocumentKey.From(document) })
            .DistinctBy(row => row.Key)
            .ToDictionary(row => row.Key, row => new DocumentLabel(row.Document, ReferenceFallback.DocumentNumber(row.Document))));

    public async Task<IReadOnlyList<DocumentLabel>> FindAsync(string type, string numberText, int take, CancellationToken cancellationToken)
    {
        if (ReferenceFallback.IdPrefix(type, numberText) is not { } idPrefix) return [];
        var candidates = await recorded.CandidatesAsync(type, idPrefix, take, cancellationToken);
        return candidates
            .Select(document => new DocumentLabel(document, ReferenceFallback.DocumentNumber(document)))
            .Where(label => label.Document.Id.ToString("N").StartsWith(idPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(label => label.Number.Equals(numberText.Trim(), StringComparison.OrdinalIgnoreCase))
            .ThenBy(label => label.Number, StringComparer.OrdinalIgnoreCase)
            .Take(take).ToList();
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

    public static string LocationCode(Guid locationId) => "LOC-" + ShortHex(locationId);

    public static string? LocationIdPrefix(string text)
    {
        var value = text.Trim();
        const string prefix = "LOC-";
        if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) value = value[prefix.Length..];
        return value.Length <= 8 && value.All(Uri.IsHexDigit) ? value.ToUpperInvariant() : null;
    }

    public static string DocumentNumber(DocumentRef document) => DocumentPrefix(document.Type) + ShortHex(document.Id);

    public static string DocumentPrefix(string type) => type switch
    {
        "production_order" => "MO-",
        "receipt" => "GR-",
        "delivery" => "DN-",
        "shipment" => "DN-",
        "invoice" => "IN-",
        "transfer" => "TR-",
        "return" => "RT-",
        "lot" => "LOT-",
        _ => "DOC-",
    };

    public static IReadOnlyList<T> Ranked<T>(IEnumerable<T> source, string text, int take, params Func<T, string?>[] values)
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
