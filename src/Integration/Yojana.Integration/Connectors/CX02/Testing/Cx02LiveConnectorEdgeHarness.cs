using System.Net;
using Yojana.Integration.Contracts;
using Yojana.Integration.DatabaseTests.ConnectorEdges;
using Yojana.Integration.Inbox;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX02.Testing;

/// <summary>Customer-sandbox gate. Scenarios run only when the fixture explicitly declares them safe.</summary>
public sealed class Cx02LiveConnectorEdgeHarness : IConnectorEdgeHarness
{
    private static readonly HashSet<string> SupportedScenarios =
        (Environment.GetEnvironmentVariable("YOJANA_CX02_EDGE_SCENARIOS") ?? "")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToHashSet(StringComparer.Ordinal);

    public static ConnectorEdgeHarnessManifest Manifest
    {
        get
        {
            var configured = Required("YOJANA_CX02_URL") && Required("YOJANA_CX02_CLIENT_ID")
                && Required("YOJANA_CX02_CLIENT_SECRET") && Required("YOJANA_CX02_EDGE_SCENARIOS");
            var exclusions = configured ? ConnectorEdgeScenarios.All.Where(scenario => !SupportedScenarios.Contains(scenario.Id))
                .ToDictionary(scenario => scenario.Id,
                    scenario => $"The customer fixture does not declare {scenario.Id} safe for this sandbox.",
                    StringComparer.Ordinal) : null;
            return new(Cx02Capabilities.ConnectorCode, Cx02Capabilities.Declaration.Messages, exclusions);
        }
    }

    public string ConnectorCode => Cx02Capabilities.ConnectorCode;
    public IReadOnlyDictionary<string, string> Capabilities => Manifest.Capabilities;

    public Task StartAsync(CancellationToken cancellationToken) => throw FixtureRequired();
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<ConnectorEdgeConnection> CreateConnectionAsync(IConnectorEdgeHost host, TenantId? tenantId,
        bool outboundDocuments, CancellationToken cancellationToken) => throw FixtureRequired();
    public Task<InboundAcceptance> ItemAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken) => throw FixtureRequired();
    public Task<InboundAcceptance> OpeningReceiptAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken) => throw FixtureRequired();
    public Task<InboundAcceptance> DeliveryAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken) => throw FixtureRequired();
    public Task<InboundAcceptance> DuplicateSerialReceiptAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken) => throw FixtureRequired();
    public Task<InboundAcceptance> InvoiceAsync(ConnectorEdgeConnection connection, string logicalReference, decimal total, CancellationToken cancellationToken) => throw FixtureRequired();
    public Task<HttpStatusCode> SendDeliveryNotificationAsync(ConnectorEdgeConnection connection, Uri receiverBaseUri, bool cancellation, CancellationToken cancellationToken) => throw FixtureRequired();
    public Task CancelShipmentAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken) => throw FixtureRequired();
    public Task<InboundAcceptance> CancellationAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken) => throw FixtureRequired();
    public void ScriptRemoteUnavailable() => throw FixtureRequired();
    public void RestoreRemote() => throw FixtureRequired();
    public void ScriptClosedPeriodRejection() => throw FixtureRequired();
    public void SeedRemoteOwner() => throw FixtureRequired();
    public void EditRemoteOwner() => throw FixtureRequired();
    public void ScriptDroppedCreateReply() => throw FixtureRequired();
    public ConnectorEdgeFailureGate BlockNextCreateWithFailure() => throw FixtureRequired();
    public Task SetRemoteOnHandAsync(ConnectorEdgeConnection connection, Guid moduleItemId, Guid moduleLocationId,
        decimal quantity, CancellationToken cancellationToken) => throw FixtureRequired();
    public Task<ConnectorEdgeRemoteState> ReadRemoteAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => throw FixtureRequired();
    public IOutboundCommand ReplacementCommand(string logicalReference) => throw FixtureRequired();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static InvalidOperationException FixtureRequired() => new(
        "The CX02 live edge harness needs the customer-sandbox fixture named by YOJANA_CX02_EDGE_SCENARIOS.");
    private static bool Required(string name) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
}
