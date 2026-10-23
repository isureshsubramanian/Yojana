using Yojana.Integration.Contracts;

namespace Yojana.Integration.Verification;

/// <summary>
/// The production acceptance checklist and the test-side edge harness share these definitions. Changing a scenario
/// therefore changes both surfaces through one explicit contract rather than an ordering or copied-list dependency.
/// </summary>
public static class ConnectorAcceptanceScenarioCatalogue
{
    public static IReadOnlyList<ConnectorEdgeScenarioDefinition> All { get; } =
    [
        new("duplicate-notification", "A duplicate delivery notification before and after processing has one effect.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(DeliveryPosted)]),
        new("out-of-order-receipt-delivery", "A delivery waits for its receipt and the bounded late path remains deterministic.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(DeliveryPosted)]),
        new("remote-outage-beyond-retry-window", "An outage beyond the retry window reaches review and recovers once.",
            [nameof(CreateReplacementDelivery)]),
        new("cancellation-after-shipment", "A cancellation after shipment reverses the original shipment exactly once.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(DeliveryPosted), nameof(DocumentCancelled)]),
        new("duplicate-serial-not-validated-remotely", "A repeated serial in one remote receipt creates one unit and a review task.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted)]),
        new("closed-period-outbound-rejection", "A closed-period outbound rejection is parked once and completed by manual mapping.",
            [nameof(CreateReplacementDelivery)]),
        new("overlapping-connections-same-invoice", "Two active connections retain but do not reapply the same mapped invoice.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(InvoicePosted)]),
        new("remote-owner-edit-during-parallel-run", "A remote owner edit during parallel run raises one conflict without changing unit state.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted), nameof(DeliveryPosted)]),
        new("dropped-reply-after-create", "A dropped reply after create is recovered by query-before-create without a duplicate.",
            [nameof(CreateReplacementDelivery)]),
        new("companion-location-count-check", "The companion on-hand check reports one difference per key and clears when fixed.",
            [nameof(ItemUpserted), nameof(GoodsReceiptPosted)]),
        new("stop-during-inflight-failure", "Stopping during an in-flight failure leaves work queued until resume and posts once.",
            [nameof(CreateReplacementDelivery)]),
    ];

    public static ConnectorEdgeScenarioDefinition Get(string id) =>
        All.Single(scenario => scenario.Id == id);
}
