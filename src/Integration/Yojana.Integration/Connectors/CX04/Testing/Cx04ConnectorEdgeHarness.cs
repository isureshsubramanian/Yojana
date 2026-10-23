using System.Net;
using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Integration.DatabaseTests.ConnectorEdges;
using Yojana.Integration.Inbox;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX04.Testing;

public sealed class Cx04ConnectorEdgeHarness : IConnectorEdgeHarness
{
    private static readonly IReadOnlyDictionary<string, string> Exclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConnectorEdgeScenarioIds.DuplicateNotification] =
                "CX04 push messages are non-authoritative hints; their hosted ingress and re-GET path have a dedicated database acceptance test.",
            [ConnectorEdgeScenarioIds.RemoteOutage] =
                "The shared edge runner requires a day-long synchronous outage, while the CX04 licence lane serialises and delays calls.",
            [ConnectorEdgeScenarioIds.CancellationAfterShipment] =
                "The verified CX04 contract reads cancellations but exposes no documented cancellation write action for this fixture.",
            [ConnectorEdgeScenarioIds.ClosedPeriod] =
                "The verified CX04 REST errors do not identify a portable closed-period code for deterministic simulation.",
            [ConnectorEdgeScenarioIds.RemoteOwnerEdit] =
                "The verified CX04 contract has no installed-base owner resource suitable for the shared owner-edit scenario.",
            [ConnectorEdgeScenarioIds.DroppedCreateReply] =
                "CX04 dropped-reply recovery is exercised per outbound type because the shared runner assumes a replacement-only document fixture.",
            [ConnectorEdgeScenarioIds.CompanionLocationCheck] =
                "The verified CX04 REST contract has no direct on-hand balance resource; the adapter derives balances from adjustment details.",
            [ConnectorEdgeScenarioIds.StopDuringFailure] =
                "The shared stop gate assumes an abortable create hook that is outside the verified CX04 REST contract.",
        };

    public static ConnectorEdgeHarnessManifest Manifest { get; } = new(Cx04Capabilities.ConnectorCode,
        Cx04Capabilities.For(new(new Uri("https://source.invalid/"), "fixture",
            new Uri("https://yojana.invalid/callback"), Connectors.CX04.Cx04LicenceTier.Essentials)).Messages,
        Exclusions);

    private IConnectorEdgeHost? edgeHost;
    private Cx04Simulator? simulator;

    public string ConnectorCode => Cx04Capabilities.ConnectorCode;
    public IReadOnlyDictionary<string, string> Capabilities => Manifest.Capabilities;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        simulator ??= new Cx04Simulator();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (simulator is null) return;
        await simulator.DisposeAsync();
        simulator = null;
    }

    public async Task<ConnectorEdgeConnection> CreateConnectionAsync(IConnectorEdgeHost host,
        TenantId? tenantId, bool outboundDocuments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        edgeHost = host;
        edgeHost.UtcNow = new(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        var tenant = tenantId ?? await edgeHost.CreateTenantAsync("edgec4");
        return new(tenant, await edgeHost.AddConnectionAsync(tenant, ConnectorCode));
    }

    public Task<InboundAcceptance> ItemAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        using var raw = JsonDocument.Parse("""
            {"id":"item-edge","InventoryID":"HW-EDGE-400","Description":"Fictional edge server",
             "TrackingMode":"serial","LastModifiedDateTime":"2026-09-01T08:00:00Z"}
            """);
        return Task.FromResult(Cx04Mapper.Map(connection.TenantId, connection.ConnectionId,
            Cx04Protocol.Entity("items"), raw.RootElement, "poll", edgeHost!.UtcNow,
            Cx04AllocationQuantityMode.LineAndAllocations));
    }

    public Task<InboundAcceptance> OpeningReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => Task.FromResult(Acceptance(connection, "FIC-OPENING",
            new GoodsReceiptPosted(new("receipt", "FIC-OPENING", new(2026, 9, 1), "FIC-OPENING"),
                "FIC-SUPPLIER", Lines()), nameof(GoodsReceiptPosted), new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero)));

    public Task<InboundAcceptance> DeliveryAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => Task.FromResult(Acceptance(connection, "FIC-DELIVERY",
            new DeliveryPosted(new("delivery", "FIC-DELIVERY", new(2026, 9, 1), "FIC-DELIVERY"),
                "FIC-CUSTOMER", "FIC-CUSTOMER-SITE", Lines()), nameof(DeliveryPosted),
            new(2026, 9, 1, 8, 1, 0, TimeSpan.Zero)));

    public Task<InboundAcceptance> DuplicateSerialReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        var repeated = new CanonicalSerial("FIC-SERIAL-400-A");
        var line = Lines()[0] with { Quantity = 2, Serials = [repeated, repeated] };
        return Task.FromResult(Acceptance(connection, "FIC-DUPLICATE",
            new GoodsReceiptPosted(new("receipt", "FIC-DUPLICATE", new(2026, 9, 1), "FIC-DUPLICATE"),
                "FIC-SUPPLIER", [line]), nameof(GoodsReceiptPosted),
            new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero)));
    }

    public Task<InboundAcceptance> InvoiceAsync(ConnectorEdgeConnection connection, string logicalReference,
        decimal total, CancellationToken cancellationToken) => Task.FromResult(Acceptance(connection,
        logicalReference, new InvoicePosted(new("invoice", logicalReference, new(2026, 9, 1), logicalReference),
            "FIC-CUSTOMER", [], Lines(), new(total, "GBP")), nameof(InvoicePosted),
        new(2026, 9, 1, 8, 2, 0, TimeSpan.Zero)));

    public Task<HttpStatusCode> SendDeliveryNotificationAsync(ConnectorEdgeConnection connection, Uri receiverBaseUri,
        bool cancellation, CancellationToken cancellationToken) => NotCovered<HttpStatusCode>();
    public Task CancelShipmentAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken) => NotCovered();
    public Task<InboundAcceptance> CancellationAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => NotCovered<InboundAcceptance>();
    public void ScriptRemoteUnavailable() => throw NotCoveredException();
    public void RestoreRemote() => throw NotCoveredException();
    public void ScriptClosedPeriodRejection() => throw NotCoveredException();
    public void SeedRemoteOwner() => throw NotCoveredException();
    public void EditRemoteOwner() => throw NotCoveredException();
    public void ScriptDroppedCreateReply() => throw NotCoveredException();
    public ConnectorEdgeFailureGate BlockNextCreateWithFailure() => throw NotCoveredException();
    public Task SetRemoteOnHandAsync(ConnectorEdgeConnection connection, Guid moduleItemId, Guid moduleLocationId,
        decimal quantity, CancellationToken cancellationToken) => NotCovered();
    public Task<ConnectorEdgeRemoteState> ReadRemoteAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => NotCovered<ConnectorEdgeRemoteState>();
    public IOutboundCommand ReplacementCommand(string logicalReference) => throw NotCoveredException();
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    private static IReadOnlyList<CanonicalLine> Lines() =>
    [
        new(1, "line-1", "HW-EDGE-400", 2, "EA",
            [new("FIC-SERIAL-400-A"), new("FIC-SERIAL-400-B")], [], "WH-A"),
    ];

    private InboundAcceptance Acceptance<T>(ConnectorEdgeConnection connection, string externalId, T payload,
        string messageType, DateTimeOffset occurred)
    {
        var envelope = new CanonicalEnvelope(IntegrationContracts.CurrentVersion, Guid.CreateVersion7(edgeHost!.UtcNow),
            connection.TenantId.Value, connection.ConnectionId, ConnectorCode, messageType, externalId,
            new(externalId, OpenEnum.Known("fixture")), occurred, new(edgeHost.UtcNow, TimeSpan.Zero),
            OpenEnum.Known("poll"), OpenEnum.Known("complete"), "fixture:" + externalId);
        return new(envelope, messageType, JsonSerializer.Serialize(new CanonicalMessage<T>(envelope,
            messageType, payload), IntegrationContracts.Json),
            JsonSerializer.Serialize(payload, IntegrationContracts.Json));
    }

    private static InvalidOperationException NotCoveredException() =>
        new("This edge scenario is excluded by the CX04 harness manifest.");
    private static Task NotCovered() => Task.FromException(NotCoveredException());
    private static Task<T> NotCovered<T>() => Task.FromException<T>(NotCoveredException());
}
