using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Search;

/// <summary>
/// Keeps the index current from the module's own published events (through the outbox, so a posting never waits
/// for the index). Each handler re-indexes the unit and, when the event names a document, that document.
/// </summary>
public static class SearchIndexHandler
{
    public static Task Handle(UnitCommissioned e, SearchIndexer indexer, CancellationToken ct) => ApplyAsync(e, indexer, ct);
    public static Task Handle(UnitReleased e, SearchIndexer indexer, CancellationToken ct) => ApplyAsync(e, indexer, ct);
    public static Task Handle(UnitIdentifierCorrected e, SearchIndexer indexer, CancellationToken ct) => ApplyAsync(e, indexer, ct);
    public static Task Handle(OwnershipOpened e, SearchIndexer indexer, CancellationToken ct) => ApplyAsync(e, indexer, ct);
    public static Task Handle(OwnershipClosed e, SearchIndexer indexer, CancellationToken ct) => ApplyAsync(e, indexer, ct);
    public static Task Handle(CustodyChanged e, SearchIndexer indexer, CancellationToken ct) => ApplyAsync(e, indexer, ct);

    public static async Task Handle(SearchReindexRequested request, SearchIndexer indexer, CancellationToken ct)
    {
        await indexer.ReindexUnitsAsync(request.UnitIds, ct);
        foreach (var doc in request.Documents ?? []) await indexer.ReindexDocumentAsync(doc.Type, doc.Id, ct);
    }

    private static async Task ApplyAsync(IPublishedUnitEvent e, SearchIndexer indexer, CancellationToken ct)
    {
        await indexer.ReindexUnitsAsync([e.Header.UnitId], ct);
        if (e.Header.Document is { } doc) await indexer.ReindexDocumentAsync(doc.Type, doc.Id, e.Header.RecordedTime, ct);
    }
}
