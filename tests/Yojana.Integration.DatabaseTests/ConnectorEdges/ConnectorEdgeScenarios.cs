using Yojana.Integration.Contracts;

namespace Yojana.Integration.DatabaseTests.ConnectorEdges;

public sealed record ConnectorEdgeScenario(
    string Id,
    string Description,
    IReadOnlyList<string> RequiredCapabilities);

public static class ConnectorEdgeScenarioIds
{
    public const string DuplicateNotification = "duplicate-notification";
    public const string OutOfOrder = "out-of-order-receipt-delivery";
    public const string RemoteOutage = "remote-outage-beyond-retry-window";
    public const string CancellationAfterShipment = "cancellation-after-shipment";
    public const string DuplicateSerial = "duplicate-serial-not-validated-remotely";
    public const string ClosedPeriod = "closed-period-outbound-rejection";
    public const string OverlappingConnections = "overlapping-connections-same-invoice";
    public const string RemoteOwnerEdit = "remote-owner-edit-during-parallel-run";
    public const string DroppedCreateReply = "dropped-reply-after-create";
    public const string CompanionLocationCheck = "companion-location-count-check";
    public const string StopDuringFailure = "stop-during-inflight-failure";
}

public static class ConnectorEdgeScenarios
{
    public static IReadOnlyList<ConnectorEdgeScenario> All { get; } =
    [
        new(ConnectorEdgeScenarioIds.DuplicateNotification,
            "A duplicate delivery notification before and after processing has one effect.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(DeliveryPosted)]),
        new(ConnectorEdgeScenarioIds.OutOfOrder,
            "A delivery waits for its receipt and the bounded late path remains deterministic.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(DeliveryPosted)]),
        new(ConnectorEdgeScenarioIds.RemoteOutage,
            "An outage beyond the retry window reaches the dead-letter queue and recovers once.",
            [nameof(CreateReplacementDelivery)]),
        new(ConnectorEdgeScenarioIds.CancellationAfterShipment,
            "A cancellation after shipment reverses the original shipment exactly once.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(DeliveryPosted), nameof(DocumentCancelled)]),
        new(ConnectorEdgeScenarioIds.DuplicateSerial,
            "A repeated serial in one remote receipt creates one unit and a review task.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted)]),
        new(ConnectorEdgeScenarioIds.ClosedPeriod,
            "A closed-period outbound rejection is parked once and completed by manual mapping.",
            [nameof(CreateReplacementDelivery)]),
        new(ConnectorEdgeScenarioIds.OverlappingConnections,
            "Two active connections retain but do not reapply the same mapped invoice.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(InvoicePosted)]),
        new(ConnectorEdgeScenarioIds.RemoteOwnerEdit,
            "A remote owner edit during parallel run raises one conflict without changing unit state.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(DeliveryPosted)]),
        new(ConnectorEdgeScenarioIds.DroppedCreateReply,
            "A dropped reply after create is recovered by query-before-create without a duplicate.",
            [nameof(CreateReplacementDelivery)]),
        new(ConnectorEdgeScenarioIds.CompanionLocationCheck,
            "The companion on-hand check reports one difference per key and clears when fixed.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted)]),
        new(ConnectorEdgeScenarioIds.StopDuringFailure,
            "Stopping during an in-flight failure leaves work queued until resume and posts once.",
            [nameof(CreateReplacementDelivery)]),
    ];

    public static ConnectorEdgeScenario Get(string id) =>
        All.Single(scenario => scenario.Id == id);
}
