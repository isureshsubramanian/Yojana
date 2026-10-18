using System.Collections.Concurrent;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>Test-side consumer of the module's published events: proves they leave through the outbox after commit.</summary>
public static class PublishedEventSink
{
    public static readonly ConcurrentQueue<IPublishedUnitEvent> Received = new();
    public static readonly ConcurrentQueue<IPublishedSummaryEvent> Summaries = new();
    public static readonly ConcurrentQueue<AssetTagPrintJobRequested> PrintJobRequests = new();
    public static readonly ConcurrentQueue<LabelReprintJobRequested> LabelReprintRequests = new();
    public static readonly ConcurrentQueue<CertificateIssueRequested> CertificateRequests = new();

    public static Task<IReadOnlyList<T>> WaitForAsync<T>(Func<T, bool> match, int count, TimeSpan timeout)
    {
        return Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var hits = All<T>().Where(match).ToList();
                if (hits.Count >= count) return (IReadOnlyList<T>)hits;
                await Task.Delay(100);
            }

            return (IReadOnlyList<T>)All<T>().Where(match).ToList();
        });
    }

    private static IEnumerable<T> All<T>() => Received.OfType<T>()
        .Concat(Summaries.OfType<T>())
        .Concat(PrintJobRequests.OfType<T>())
        .Concat(LabelReprintRequests.OfType<T>())
        .Concat(CertificateRequests.OfType<T>());

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
    public static void Handle(UnitGraded e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitIdentifierCorrected e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(OwnershipOpened e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(OwnershipClosed e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(CustodyChanged e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitPartInstalled e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementCreated e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementRebased e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementTransferred e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementExtendedSold e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementAmended e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementSuperseded e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementBackfilled e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementClosed e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(EntitlementCoverageChanged e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(CoverageDecisionChanged e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(ClaimAdjudicated e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(RecoveryClaimRaised e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(RecoveryClaimApproved e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(ChargeableQuoteRequested e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(RmaAuthorised e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(RmaReceived e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(RmaDispositioned e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(ReplacementShipped e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(ReplacementAssetTagPrintRequested e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(ReplacementReturnOverdue e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(RefurbishmentReleased e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(RefurbishmentCostPosted e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(ReleaseRecordReissueRequested e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(ReplacementLinkCorrected e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(CertificateReissueRequested e) => PublishedEventSink.Received.Enqueue(e);
    // P6 certificate publications.
    public static void Handle(CertificateIssued e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(CertificateSuperseded e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(LabelPrinted e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(LabelReprinted e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitIdentifiersSwapped e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitItemCorrected e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitAsBuiltCorrected e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(ItemTrackingModeChanged e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(UnitVoided e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitVoidReversed e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitMerged e) => PublishedEventSink.Received.Enqueue(e);
    public static void Handle(UnitIdentifiersAssigned e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(UnitAllocationReassigned e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(DocumentSerialsCorrected e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(OwnershipSourceDocumentCorrected e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(UnitProductionOrderReconciled e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(CampaignUnitsSelected e) => PublishedEventSink.Summaries.Enqueue(e);
    public static void Handle(AssetTagPrintJobRequested e) => PublishedEventSink.PrintJobRequests.Enqueue(e);
    public static void Handle(LabelReprintJobRequested e) => PublishedEventSink.LabelReprintRequests.Enqueue(e);
    public static void Handle(CertificateIssueRequested e) => PublishedEventSink.CertificateRequests.Enqueue(e);
}
