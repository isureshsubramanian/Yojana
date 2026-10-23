using Yojana.Integration.Connectors.CX01.Testing;
using Xunit;

namespace Yojana.Integration.DatabaseTests.ConnectorEdges;

[Collection("integration host")]
public sealed class Cx01ConnectorEdgeScenarioTests(IntegrationHostFixture fixture)
{
    private Task RunAsync(string scenarioId) =>
        new ConnectorEdgeScenarioRunner(fixture).RunAsync(
            ConnectorEdgeScenarios.Get(scenarioId), new Cx01ConnectorEdgeHarness(), CancellationToken.None);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.DuplicateNotification)]
    public Task Duplicate_notification() => RunAsync(ConnectorEdgeScenarioIds.DuplicateNotification);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.OutOfOrder)]
    public Task Out_of_order_receipt_and_delivery() => RunAsync(ConnectorEdgeScenarioIds.OutOfOrder);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.RemoteOutage)]
    public Task Remote_outage_beyond_retry_window() => RunAsync(ConnectorEdgeScenarioIds.RemoteOutage);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.CancellationAfterShipment)]
    public Task Cancellation_after_shipment() => RunAsync(ConnectorEdgeScenarioIds.CancellationAfterShipment);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.DuplicateSerial)]
    public Task Duplicate_serial_not_validated_remotely() => RunAsync(ConnectorEdgeScenarioIds.DuplicateSerial);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.ClosedPeriod)]
    public Task Closed_period_outbound_rejection() => RunAsync(ConnectorEdgeScenarioIds.ClosedPeriod);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.OverlappingConnections)]
    public Task Overlapping_connections_deliver_same_invoice() => RunAsync(ConnectorEdgeScenarioIds.OverlappingConnections);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.RemoteOwnerEdit)]
    public Task Remote_owner_edit_during_parallel_run() => RunAsync(ConnectorEdgeScenarioIds.RemoteOwnerEdit);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.DroppedCreateReply)]
    public Task Dropped_reply_after_create() => RunAsync(ConnectorEdgeScenarioIds.DroppedCreateReply);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.CompanionLocationCheck)]
    public Task Companion_location_count_check() => RunAsync(ConnectorEdgeScenarioIds.CompanionLocationCheck);

    [ConnectorEdgeFact(typeof(Cx01ConnectorEdgeHarness), ConnectorEdgeScenarioIds.StopDuringFailure)]
    public Task Stop_during_inflight_failure() => RunAsync(ConnectorEdgeScenarioIds.StopDuringFailure);
}

[Collection("integration host")]
public sealed class Cx01LiveConnectorEdgeScenarioTests(IntegrationHostFixture fixture)
{
    private Task RunAsync(string scenarioId) =>
        new ConnectorEdgeScenarioRunner(fixture).RunAsync(
            ConnectorEdgeScenarios.Get(scenarioId), new Cx01LiveConnectorEdgeHarness(),
            CancellationToken.None);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.DuplicateNotification, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Duplicate_notification() => RunAsync(ConnectorEdgeScenarioIds.DuplicateNotification);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.OutOfOrder, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Out_of_order_receipt_and_delivery() => RunAsync(ConnectorEdgeScenarioIds.OutOfOrder);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.RemoteOutage, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Remote_outage_beyond_retry_window() => RunAsync(ConnectorEdgeScenarioIds.RemoteOutage);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.CancellationAfterShipment, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Cancellation_after_shipment() => RunAsync(ConnectorEdgeScenarioIds.CancellationAfterShipment);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.DuplicateSerial, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Duplicate_serial_not_validated_remotely() => RunAsync(ConnectorEdgeScenarioIds.DuplicateSerial);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.ClosedPeriod, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Closed_period_outbound_rejection() => RunAsync(ConnectorEdgeScenarioIds.ClosedPeriod);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.OverlappingConnections, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Overlapping_connections_deliver_same_invoice() => RunAsync(ConnectorEdgeScenarioIds.OverlappingConnections);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.RemoteOwnerEdit, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Remote_owner_edit_during_parallel_run() => RunAsync(ConnectorEdgeScenarioIds.RemoteOwnerEdit);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.DroppedCreateReply, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Dropped_reply_after_create() => RunAsync(ConnectorEdgeScenarioIds.DroppedCreateReply);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.CompanionLocationCheck, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Companion_location_count_check() => RunAsync(ConnectorEdgeScenarioIds.CompanionLocationCheck);

    [ConnectorEdgeFact(typeof(Cx01LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.StopDuringFailure, live: true)]
    [Trait("Category", "Cx01Live")]
    public Task Stop_during_inflight_failure() => RunAsync(ConnectorEdgeScenarioIds.StopDuringFailure);
}
