using Yojana.Integration.Contracts;
using Yojana.Integration.Verification;

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
        ConnectorAcceptanceScenarioCatalogue.All.Select(scenario => new ConnectorEdgeScenario(
            scenario.Id, scenario.Description, scenario.RequiredCapabilities)).ToList();

    public static ConnectorEdgeScenario Get(string id) =>
        All.Single(scenario => scenario.Id == id);
}
