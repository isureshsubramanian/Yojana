using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests.ConnectorEdges;

public sealed record ConnectorEdgeConnection(TenantId TenantId, Guid ConnectionId);

public sealed record ConnectorEdgeRemoteState(
    int CreatedDocuments,
    bool ShipmentCancelled,
    string? OwnerPartyRef,
    decimal OnHandQuantity);

public sealed class ConnectorEdgeFailureGate
{
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task? externalEntered;
    private readonly Action? externalContinue;

    public ConnectorEdgeFailureGate() { }

    public ConnectorEdgeFailureGate(Task entered, Action continueAction)
    {
        externalEntered = entered;
        externalContinue = continueAction;
    }

    public Task Entered => externalEntered ?? entered.Task;
    public Task Release => release.Task;
    public void MarkEntered() => entered.TrySetResult();
    public void Continue()
    {
        externalContinue?.Invoke();
        release.TrySetResult();
    }
}

public interface IConnectorEdgeHost
{
    DateTime UtcNow { get; set; }
    Task<TenantId> CreateTenantAsync(string prefix);
    Task<Guid> AddConnectionAsync(TenantId tenantId, string connectorCode);
    IServiceScope ScopeFor(TenantId tenantId);
}

public interface IConnectorEdgeHarness : IAsyncDisposable
{
    string ConnectorCode { get; }
    bool IsLive => false;
    IReadOnlyDictionary<string, string> Capabilities { get; }
    string OutboundValidationRejectionText => "period is closed";
    bool NotificationAcceptancesAreHarnessSupplied => false;
    bool SplitDeliveryShapeAcrossOpening => false;
    string DeliveryLocationKind => Yojana.Serialisation.Contracts.LocationKind.InTransit;
    int RemoteOwnerConflictCount => 1;

    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<ConnectorEdgeConnection> CreateConnectionAsync(IConnectorEdgeHost host, TenantId? tenantId,
        bool outboundDocuments, CancellationToken cancellationToken);

    Task<InboundAcceptance> ItemAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken);
    Task<InboundAcceptance> OpeningReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken);
    Task<InboundAcceptance> DeliveryAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken);
    async Task<IReadOnlyList<InboundAcceptance>> DeliveryShapeAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => [await DeliveryAsync(connection, cancellationToken)];
    Task<InboundAcceptance> DuplicateSerialReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken);
    Task<InboundAcceptance> InvoiceAsync(ConnectorEdgeConnection connection, string logicalReference,
        decimal total, CancellationToken cancellationToken);
    Task<HttpStatusCode> SendDeliveryNotificationAsync(ConnectorEdgeConnection connection, Uri receiverBaseUri,
        bool cancellation, CancellationToken cancellationToken);
    Task CancelShipmentAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken);
    Task<InboundAcceptance> CancellationAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken);
    async Task<IReadOnlyList<InboundAcceptance>> CancellationShapeAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => [await CancellationAsync(connection, cancellationToken)];

    void ScriptRemoteUnavailable();
    void RestoreRemote();
    void ScriptClosedPeriodRejection();
    void SeedRemoteOwner();
    void EditRemoteOwner();
    void ScriptDroppedCreateReply();
    ConnectorEdgeFailureGate BlockNextCreateWithFailure();
    // The module item and location ids come from the runner: harnesses live in src/Integration and must not read Serialisation data.
    Task SetRemoteOnHandAsync(ConnectorEdgeConnection connection, Guid moduleItemId, Guid moduleLocationId, decimal quantity,
        CancellationToken cancellationToken);
    Task<ConnectorEdgeRemoteState> ReadRemoteAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken);

    IOutboundCommand ReplacementCommand(string logicalReference);
}

public sealed class IntegrationConnectorEdgeHost(IntegrationHostFixture fixture) : IConnectorEdgeHost
{
    public DateTime UtcNow
    {
        get => fixture.Clock.UtcNow;
        set => fixture.Clock.UtcNow = value;
    }

    public Task<TenantId> CreateTenantAsync(string prefix) => fixture.NewTenantAsync(prefix);

    public Task<Guid> AddConnectionAsync(TenantId tenantId, string connectorCode) =>
        fixture.AddConnectionAsync(tenantId, Guid.CreateVersion7(), connectorCode);

    public IServiceScope ScopeFor(TenantId tenantId) => fixture.ScopeFor(tenantId);
}

public sealed record ConnectorEdgeHarnessManifest(
    string ConnectorCode,
    IReadOnlyDictionary<string, string> Capabilities,
    IReadOnlyDictionary<string, string>? ScenarioExclusions = null)
{
    public string? NotApplicable(ConnectorEdgeScenario scenario)
    {
        if (ScenarioExclusions?.TryGetValue(scenario.Id, out var reason) == true) return reason;
        foreach (var capability in scenario.RequiredCapabilities)
        {
            if (!Capabilities.TryGetValue(capability, out var support))
                return $"{ConnectorCode} does not declare the {capability} capability.";
            if (support == Yojana.Integration.Data.Entities.CapabilitySupport.Unsupported)
                return $"{ConnectorCode} declares {capability} as unsupported.";
        }
        return null;
    }
}
