using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>Test-side consumer of the module's published events: proves they leave through the outbox after commit.</summary>
public static class PublishedEventSink
{
    public static readonly ConcurrentQueue<IPublishedUnitEvent> Received = new();
    public static readonly ConcurrentQueue<IPublishedSummaryEvent> Summaries = new();
    public static readonly ConcurrentQueue<AssetTagPrintJobRequested> PrintJobRequests = new();

    public static Task<IReadOnlyList<T>> WaitForAsync<T>(Func<T, bool> match, int count, TimeSpan timeout) where T : IPublishedUnitEvent
    {
        return Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var hits = Received.OfType<T>().Where(match).ToList();
                if (hits.Count >= count) return (IReadOnlyList<T>)hits;
                await Task.Delay(100);
            }

            return (IReadOnlyList<T>)Received.OfType<T>().Where(match).ToList();
        });
    }

    public static Task<IReadOnlyList<T>> WaitForSummaryAsync<T>(Func<T, bool> match, int count, TimeSpan timeout) where T : IPublishedSummaryEvent
    {
        return Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var hits = Summaries.OfType<T>().Where(match).ToList();
                if (hits.Count >= count) return (IReadOnlyList<T>)hits;
                await Task.Delay(100);
            }

            return (IReadOnlyList<T>)Summaries.OfType<T>().Where(match).ToList();
        });
    }
}

public static class PublishedEventSinkHandler
{
    public static void Handle(UnitCommissioned e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitReleased e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitIdentifierCorrected e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(OwnershipOpened e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(OwnershipClosed e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(CustodyChanged e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementCreated e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementRebased e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementTransferred e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementExtendedSold e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementAmended e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementSuperseded e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementBackfilled e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementClosed e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementCoverageChanged e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitIdentifiersSwapped e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitItemCorrected e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitAsBuiltCorrected e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitVoided e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitVoidReversed e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitMerged e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitIdentifiersAssigned e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(UnitAllocationReassigned e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(DocumentSerialsCorrected e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(OwnershipSourceDocumentCorrected e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(UnitProductionOrderReconciled e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(AssetTagPrintJobRequested e) => PublishedEventSink.PrintJobRequests.Enqueue(e);
}
