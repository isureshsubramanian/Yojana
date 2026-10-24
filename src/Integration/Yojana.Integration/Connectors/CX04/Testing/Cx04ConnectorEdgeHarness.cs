using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DatabaseTests.ConnectorEdges;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX04.Testing;

public sealed class Cx04ConnectorEdgeHarness : IConnectorEdgeHarness
{
    private static readonly IReadOnlyDictionary<string, string> Exclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ConnectorEdgeScenarioIds.CancellationAfterShipment] =
                "The verified CX04 contract reads cancellations but exposes no documented cancellation write action for this fixture.",
            [ConnectorEdgeScenarioIds.RemoteOwnerEdit] =
                "The verified CX04 contract has no installed-base owner resource suitable for the shared owner-edit scenario.",
            [ConnectorEdgeScenarioIds.CompanionLocationCheck] =
                "The verified CX04 REST contract has no direct on-hand balance resource; the adapter derives balances from adjustment details.",
        };

    public static ConnectorEdgeHarnessManifest Manifest { get; } = new(Cx04Capabilities.ConnectorCode,
        Cx04Capabilities.For(new(new Uri("https://source.invalid/"), "fixture",
            new Uri("https://yojana.invalid/callback"), Connectors.CX04.Cx04LicenceTier.Essentials)).Messages,
        Exclusions);

    private IConnectorEdgeHost? edgeHost;
    private Cx04Simulator? simulator;
    private const string NotificationSecret = "fictional-cx04-edge-secret";

    public string ConnectorCode => Cx04Capabilities.ConnectorCode;
    public IReadOnlyDictionary<string, string> Capabilities => Manifest.Capabilities;
    public string OutboundValidationRejectionText => "remote system rejected";
    public bool NotificationAcceptancesAreHarnessSupplied => true;
    private Cx04Simulator Simulator => simulator
        ?? throw new InvalidOperationException("Start the connector edge harness before using it.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        simulator ??= new Cx04Simulator(new()
        {
            WebhookSecretHeader = Cx04Protocol.NotificationSecretHeader,
            WebhookSecret = NotificationSecret,
        });
        Simulator.SeedEntity("Default", Cx04Protocol.EndpointVersion, "Shipment", """
            {"id":"edge-delivery","ShipmentNbr":"FIC-DELIVERY","CustomerID":"FIC-CUSTOMER",
             "ShipToID":"FIC-CUSTOMER-SITE","ShipmentDate":"2026-09-01T08:01:00Z",
             "LastModifiedDateTime":"2026-09-01T08:01:00Z","Details":[
               {"LineNbr":1,"InventoryID":"HW-EDGE-400","ShippedQty":2,"UOM":"EA","LocationID":"WH-A",
                "Allocations":[{"LotSerialNbr":"FIC-SERIAL-400-A","Qty":1},{"LotSerialNbr":"FIC-SERIAL-400-B","Qty":1}]}
             ]}
            """);
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
        var connectionId = await edgeHost.AddConnectionAsync(tenant, ConnectorCode);
        using (var scope = edgeHost.ScopeFor(tenant))
        {
            var configured = new Cx04ConnectionSettings(Simulator.BaseUri, "fictional-edge-application",
                new Uri("https://yojana.invalid/cx04/callback"),
                Connectors.CX04.Cx04LicenceTier.Essentials,
                NotificationSecretSha256: Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(NotificationSecret))).ToLowerInvariant());
            await scope.ServiceProvider.GetRequiredKeyedService<IConnectionLifecycleAdapter>(ConnectorCode)
                .ConfigureAsync(connectionId, Cx04Settings.ToJson(configured), cancellationToken);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var connection = await db.Connections.SingleAsync(value => value.ConnectionId == connectionId,
                cancellationToken);
            connection.RegistrationCompleted = true;
            var gateway = await db.ConnectionGatewaySettings.SingleAsync(value =>
                value.ConnectionId == connectionId, cancellationToken);
            if (outboundDocuments) gateway.OutboundMode = "documents";
            db.ConnectionNotificationSources.Add(new()
            {
                TenantId = tenant, ConnectionId = connectionId, Source = "shipments", Enabled = true,
                UpdatedAt = edgeHost.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        Cx04ConsentStart start;
        using (var scope = edgeHost.ScopeFor(tenant))
            start = await scope.ServiceProvider.GetRequiredService<Cx04Authorisation>()
                .BeginAsync(connectionId, cancellationToken);
        var callback = await FollowConsentAsync(start, cancellationToken);
        using (var scope = edgeHost.ScopeFor(tenant))
            await scope.ServiceProvider.GetRequiredService<Cx04Authorisation>()
                .CompleteAsync(connectionId, callback, "user:edge-operator", cancellationToken);
        return new(tenant, connectionId);
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
            "FIC-CUSTOMER", [], Lines(), new(total, "EUR")), nameof(InvoicePosted),
        new(2026, 9, 1, 8, 2, 0, TimeSpan.Zero)));

    public async Task<HttpStatusCode> SendDeliveryNotificationAsync(ConnectorEdgeConnection connection, Uri receiverBaseUri,
        bool cancellation, CancellationToken cancellationToken)
    {
        if (cancellation) throw NotCoveredException();
        var body = new Cx04PushNotification(
            [new Dictionary<string, object?> { ["ShipmentNbr"] = "FIC-NOTIFICATION-DELIVERY" }], [],
            "FictionalShipmentChanges", connection.TenantId.ToString(), "cx04-edge-delivery", 1,
            new Dictionary<string, object?>());
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/integration/connections/{connection.ConnectionId}/notifications/cx04")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add(IntegrationNotificationEndpoints.TenantHeader, connection.TenantId.ToString());
        request.Headers.Add(Cx04Protocol.NotificationSecretHeader, NotificationSecret);
        using var client = new HttpClient { BaseAddress = receiverBaseUri };
        using var response = await client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }
    public Task CancelShipmentAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken) => NotCovered();
    public Task<InboundAcceptance> CancellationAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => NotCovered<InboundAcceptance>();
    public void ScriptRemoteUnavailable() => Simulator.Faults.ServiceUnavailableResponses = 10_000;
    public void RestoreRemote() => Simulator.Faults.ServiceUnavailableResponses = 0;
    public void ScriptClosedPeriodRejection() => Simulator.Faults.RejectNextWriteAsValidationError = true;
    public void SeedRemoteOwner() => throw NotCoveredException();
    public void EditRemoteOwner() => throw NotCoveredException();
    public void ScriptDroppedCreateReply() => Simulator.Faults.DropNextCreateReply = true;
    public ConnectorEdgeFailureGate BlockNextCreateWithFailure()
    {
        var gate = new ConnectorEdgeFailureGate();
        Simulator.Faults.FailNextWriteAfterHook = true;
        Simulator.BeforeNextWrite = async () =>
        {
            gate.MarkEntered();
            await gate.Release;
            // The shared HTTP pipeline retries idempotent PUT requests three times. The fixture holds the first
            // failure in flight, then supplies the remaining failures so the relay—not the HTTP client—owns retry.
            Simulator.Faults.ServiceUnavailableResponses = 3;
        };
        return gate;
    }
    public Task SetRemoteOnHandAsync(ConnectorEdgeConnection connection, Guid moduleItemId, Guid moduleLocationId,
        decimal quantity, CancellationToken cancellationToken) => NotCovered();
    public Task<ConnectorEdgeRemoteState> ReadRemoteAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => Task.FromResult(new ConnectorEdgeRemoteState(
            Simulator.EntityCount("Default", Cx04Protocol.EndpointVersion, "Shipment") - 1,
            false, null, 0));
    public IOutboundCommand ReplacementCommand(string logicalReference) => new CreateReplacementDelivery(
        Guid.CreateVersion7(), "document/" + Guid.CreateVersion7().ToString("N"), logicalReference,
        "FIC-CUSTOMER", "FIC-CUSTOMER-SITE", new(Cx04FixtureFacts.UnitId, "FIC-SERIAL-400-A"),
        OpenEnum.Known("zero"), "FIC-INVOICE", "warranty");
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

    private static async Task<Cx04ConsentCallback> FollowConsentAsync(Cx04ConsentStart start,
        CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(start.ConsentUrl, cancellationToken);
        if (response.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException("The fictional consent endpoint did not redirect.");
        var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        return new(query["code"]!, query["state"]!);
    }
}

public sealed class Cx04FixtureFacts : ICx04OutboundFacts
{
    public static readonly Guid UnitId = Guid.Parse("44444444-4444-4444-8444-444444444444");

    public Task<Cx04OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, string? suppliedSerial,
        CancellationToken ct) => Task.FromResult<Cx04OutboundUnit?>(
        new("HW-EDGE-400", suppliedSerial ?? "FIC-SERIAL-400-A"));

    public Task<string?> ReferenceAsync(Guid connectionId, string kind, string reference,
        CancellationToken ct) => Task.FromResult<string?>(reference);
}
