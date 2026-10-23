using Yojana.Integration.Connectors.CX04;
using Yojana.Integration.Connectors.CX04.Testing;

namespace Yojana.Integration.DatabaseTests.ConnectorEdges;

[Collection("integration host")]
public sealed class Cx04ConnectorEdgeScenarioTests(IntegrationHostFixture fixture)
{
    private Task RunAsync(string id) => new ConnectorEdgeScenarioRunner(fixture).RunAsync(
        ConnectorEdgeScenarios.Get(id), new Cx04ConnectorEdgeHarness(), CancellationToken.None);

    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.DuplicateNotification)]
    public Task Duplicate_notification() => RunAsync(ConnectorEdgeScenarioIds.DuplicateNotification);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.OutOfOrder)]
    public Task Out_of_order() => RunAsync(ConnectorEdgeScenarioIds.OutOfOrder);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.RemoteOutage)]
    public Task Remote_outage() => RunAsync(ConnectorEdgeScenarioIds.RemoteOutage);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.CancellationAfterShipment)]
    public Task Cancellation_after_shipment() => RunAsync(ConnectorEdgeScenarioIds.CancellationAfterShipment);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.DuplicateSerial)]
    public Task Duplicate_serial() => RunAsync(ConnectorEdgeScenarioIds.DuplicateSerial);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.ClosedPeriod)]
    public Task Closed_period() => RunAsync(ConnectorEdgeScenarioIds.ClosedPeriod);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.OverlappingConnections)]
    public Task Overlapping_connections() => RunAsync(ConnectorEdgeScenarioIds.OverlappingConnections);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.RemoteOwnerEdit)]
    public Task Remote_owner_edit() => RunAsync(ConnectorEdgeScenarioIds.RemoteOwnerEdit);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.DroppedCreateReply)]
    public Task Dropped_create_reply() => RunAsync(ConnectorEdgeScenarioIds.DroppedCreateReply);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.CompanionLocationCheck)]
    public Task Companion_location_check() => RunAsync(ConnectorEdgeScenarioIds.CompanionLocationCheck);
    [ConnectorEdgeFact(typeof(Cx04ConnectorEdgeHarness), ConnectorEdgeScenarioIds.StopDuringFailure)]
    public Task Stop_during_failure() => RunAsync(ConnectorEdgeScenarioIds.StopDuringFailure);
}

public sealed class Cx04LiveConnectorEdgeHarness
{
    public static ConnectorEdgeHarnessManifest Manifest { get; } = new(Cx04Capabilities.ConnectorCode,
        Cx04ConnectorEdgeHarness.Manifest.Capabilities);
}

public sealed class Cx04LiveConnectorEdgeScenarioTests
{
    private static Task LiveOnly(string scenarioId)
    {
        ConnectorEdgeResultWriter.Record(Cx04Capabilities.ConnectorCode, scenarioId, "failed", null,
            DateTime.UtcNow, DateTime.UtcNow);
        throw new InvalidOperationException(
            "The CX04 customer sandbox harness requires an explicitly provisioned acceptance fixture.");
    }

    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.DuplicateNotification, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Duplicate_notification() => LiveOnly(ConnectorEdgeScenarioIds.DuplicateNotification);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.OutOfOrder, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Out_of_order() => LiveOnly(ConnectorEdgeScenarioIds.OutOfOrder);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.RemoteOutage, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Remote_outage() => LiveOnly(ConnectorEdgeScenarioIds.RemoteOutage);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.CancellationAfterShipment, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Cancellation_after_shipment() => LiveOnly(ConnectorEdgeScenarioIds.CancellationAfterShipment);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.DuplicateSerial, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Duplicate_serial() => LiveOnly(ConnectorEdgeScenarioIds.DuplicateSerial);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.ClosedPeriod, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Closed_period() => LiveOnly(ConnectorEdgeScenarioIds.ClosedPeriod);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.OverlappingConnections, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Overlapping_connections() => LiveOnly(ConnectorEdgeScenarioIds.OverlappingConnections);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.RemoteOwnerEdit, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Remote_owner_edit() => LiveOnly(ConnectorEdgeScenarioIds.RemoteOwnerEdit);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.DroppedCreateReply, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Dropped_create_reply() => LiveOnly(ConnectorEdgeScenarioIds.DroppedCreateReply);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.CompanionLocationCheck, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Companion_location_check() => LiveOnly(ConnectorEdgeScenarioIds.CompanionLocationCheck);
    [ConnectorEdgeFact(typeof(Cx04LiveConnectorEdgeHarness), ConnectorEdgeScenarioIds.StopDuringFailure, live: true)]
    [Trait("Category", "Cx04Live")]
    public Task Stop_during_failure() => LiveOnly(ConnectorEdgeScenarioIds.StopDuringFailure);
}
