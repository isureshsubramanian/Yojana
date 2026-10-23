using Microsoft.Data.SqlClient;
using Yojana.Serialisation.Contracts;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Yojana.Serialisation.Search;

/// <summary>Maps only published facts that can change the unit or document search representation.</summary>
public static class SearchProjectionRequestMapper
{
    // TIGHT COUPLING with PublishedEventMapper.PublishAsync: the ledger publisher must enqueue this request in the
    // same outbox as the public fact. Restoring event subscribers would put slow search work back on every public
    // handler chain; omitting this call would leave only the nightly healer to make the index current.
    public static SearchProjectionRequested? Map(IPublishedUnitEvent message) => message switch
    {
        UnitCommissioned or UnitReleased or UnitDispositionChanged or UnitIdentifierCorrected or OwnershipOpened or OwnershipClosed
            or CustodyChanged or UnitIdentifiersSwapped or UnitItemCorrected or UnitAsBuiltCorrected
            or UnitVoided or UnitVoidReversed or UnitMerged =>
            new([message.Header.UnitId], message.Header.Document is { } document ? [document] : null,
                message.Header.Seq, message.Header.RecordedTime),
        _ => null,
    };

    public static SearchProjectionRequested? Combine(IReadOnlyList<SearchProjectionRequested> requests)
    {
        if (requests.Count == 0) return null;
        var unitIds = requests.SelectMany(request => request.UnitIds).Distinct().Order().ToArray();
        var documents = requests.SelectMany(request => request.Documents ?? [])
            .DistinctBy(document => (document.Type, document.Id))
            .Select(document => new DocumentRef(document.Type, document.Id))
            .ToArray();
        var eventSeq = unitIds.Length == 1
            ? requests.Select(request => request.EventSeq).OfType<long>().DefaultIfEmpty().Max()
            : 0;
        var recordedAt = requests.Select(request => request.RecordedAt).OfType<DateTime>().DefaultIfEmpty().Max();
        return new SearchProjectionRequested(unitIds, documents.Length == 0 ? null : documents,
            eventSeq == 0 ? null : eventSeq, recordedAt == default ? null : recordedAt);
    }
}

/// <summary>Narrow internal message that moves asynchronous published-event projection work to the search queue.</summary>
public sealed record SearchProjectionRequested(
    IReadOnlyList<Guid> UnitIds,
    IReadOnlyList<DocumentRef>? Documents,
    long? EventSeq = null,
    DateTime? RecordedAt = null);

public static class SearchProjectionHandler
{
    public static void Configure(HandlerChain chain) => SearchProjectionRetry.Configure(chain);

    public static async Task Handle(SearchProjectionRequested request, SearchIndexer indexer, CancellationToken cancellationToken)
    {
        var unitIsCurrent = request.EventSeq is { } seq && request.UnitIds.Count == 1
            && await indexer.IsUnitCurrentAsync(request.UnitIds[0], seq, cancellationToken);
        if (!unitIsCurrent) await indexer.ReindexUnitsAsync(request.UnitIds, cancellationToken);
        foreach (var document in request.Documents ?? [])
        {
            await indexer.ReindexDocumentAsync(document.Type, document.Id, request.RecordedAt, cancellationToken);
        }
    }
}

/// <summary>Processes bulk-ledger re-index requests on their bounded, durable local queue.</summary>
public static class SearchReindexHandler
{
    public static void Configure(HandlerChain chain)
    {
        SearchProjectionRetry.Configure(chain);
        chain.OnException<OperationCanceledException>().RequeueIndefinitely();
    }

    public static async Task Handle(SearchReindexRequested request, SearchBackfill backfill, SearchIndexer indexer, CancellationToken ct)
    {
        await backfill.ReindexUnitsAsync(request.UnitIds, ct);
        foreach (var doc in request.Documents ?? []) await indexer.ReindexDocumentAsync(doc.Type, doc.Id, ct);
    }
}

/// <summary>Transient SQL failures that are safe to replay because search writes rebuild whole entries.</summary>
public static class SearchProjectionRetry
{
    private static readonly HashSet<int> TransientNumbers = [1205, 1222, 3960];
    private static readonly TimeSpan[] Delays =
        [TimeSpan.FromMilliseconds(75), TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1)];

    public static void Configure(HandlerChain chain) => chain
        .OnException(IsTransient, "SQL Server deadlock, lock timeout, or snapshot update conflict")
        .RetryWithCooldown(Delays)
        .WithBoundedJitter(0.5);

    public static bool IsTransient(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqlException sql && sql.Errors.Cast<SqlError>().Any(error => TransientNumbers.Contains(error.Number)))
            {
                return true;
            }

            if (current.InnerException is null) break;
        }

        return false;
    }
}
