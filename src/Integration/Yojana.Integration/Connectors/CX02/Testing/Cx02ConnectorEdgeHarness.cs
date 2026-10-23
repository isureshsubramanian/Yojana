using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DatabaseTests.ConnectorEdges;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX02.Testing;

public sealed class Cx02ConnectorEdgeHarness : IConnectorEdgeHarness
{
    private static readonly Guid DeliveryId = Guid.Parse("20000000-0000-4000-8000-000000000001");
    private static readonly Guid SecondDeliveryId = Guid.Parse("20000000-0000-4000-8000-000000000007");
    private static readonly Guid CancellationId = Guid.Parse("20000000-0000-4000-8000-000000000002");
    private static readonly Guid SecondCancellationId = Guid.Parse("20000000-0000-4000-8000-000000000008");
    private const string ClientState = "fictional-cx02-edge-client-state";
    private Cx02Simulator? simulator;
    private IConnectorEdgeHost? host;
    private bool shipmentCancelled;
    private string? remoteOwner;
    private decimal onHand = 2m;

    public static ConnectorEdgeHarnessManifest Manifest { get; } = new(
        Cx02Capabilities.ConnectorCode, Cx02Capabilities.Declaration.Messages);

    public string ConnectorCode => Cx02Capabilities.ConnectorCode;
    public IReadOnlyDictionary<string, string> Capabilities => Manifest.Capabilities;
    public bool NotificationAcceptancesAreHarnessSupplied => true;
    public bool SplitDeliveryShapeAcrossOpening => true;
    public string DeliveryLocationKind => Yojana.Serialisation.Contracts.LocationKind.CustomerSite;
    public int RemoteOwnerConflictCount => 2;
    private Cx02Simulator Simulator => simulator
        ?? throw new InvalidOperationException("Start the connector edge harness before using it.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        simulator ??= new();
        Simulator.Seed("items", Cx02SimulatorEntity.From(new
        {
            number = "HW-SERVER-100", displayName = "Fictional rack server",
            itemTrackingCode = "serial", baseUnitOfMeasureCode = "EA",
            lastModifiedDateTime = "2026-09-01T08:00:00Z",
        }));
        SeedDelivery();
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
        this.host = host;
        host.UtcNow = new(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        var tenant = tenantId ?? await host.CreateTenantAsync("edgec2");
        var connection = await host.AddConnectionAsync(tenant, ConnectorCode);
        using var scope = host.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx02Settings>().SetAsync(connection,
            new(Simulator.BaseUri, Cx02Simulator.DefaultTenant, Cx02Simulator.DefaultTenant,
                Cx02Simulator.DefaultEnvironment, Cx02Simulator.CompanyId, "1.0", TokenUri: Simulator.TokenUri(),
                Outbound: new(AccountMap: new Dictionary<string, string>
                {
                    ["warranty"] = "FIC-6100", ["reserve"] = "FIC-2100",
                }, LocationMap: new Dictionary<string, string>
                {
                    ["store"] = "WH-A", ["repair"] = "WH-R", ["hold"] = "WH-H",
                })), cancellationToken);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        await scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>().SaveAsync(
            await protector.EncryptAsync(tenant, connection, Cx02Client.CredentialPurpose,
                JsonSerializer.SerializeToUtf8Bytes(new Cx02ClientCredential(
                    "fictional-edge-client", Simulator.Options.ClientSecret, ClientState), IntegrationContracts.Json),
                null, host.UtcNow.AddDays(30), cancellationToken), cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = await db.Connections.SingleAsync(value => value.ConnectionId == connection, cancellationToken);
        row.RegistrationCompleted = true;
        if (outboundDocuments)
        {
            var gateway = await db.ConnectionGatewaySettings.SingleAsync(value => value.ConnectionId == connection,
                cancellationToken);
            gateway.OutboundMode = "documents";
        }
        await db.SaveChangesAsync(cancellationToken);
        return new(tenant, connection);
    }

    public async Task<InboundAcceptance> ItemAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        using var scope = Scope(connection);
        var result = await scope.ServiceProvider.GetRequiredService<Cx02Adapter>().PollAsync(
            new(connection.TenantId, connection.ConnectionId, "items", null, TimeSpan.FromMinutes(5), 10),
            cancellationToken);
        return AssertOne(result.Events, "The fictional edge item was not found.");
    }

    public Task<InboundAcceptance> OpeningReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lines = Lines();
        return Task.FromResult(Acceptance(connection, "receipt", "FIC-OPENING", nameof(GoodsReceiptPosted),
            new GoodsReceiptPosted(new("receipt", "FIC-OPENING", new(2026, 9, 1), "FIC-OPENING"),
                "FIC-SUPPLIER", lines)));
    }

    public async Task<InboundAcceptance> DeliveryAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
        => (await DeliveryShapeAsync(connection, cancellationToken))[0];

    public async Task<IReadOnlyList<InboundAcceptance>> DeliveryShapeAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        using var scope = Scope(connection);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx02Adapter>();
        var result = new List<InboundAcceptance>(2);
        foreach (var id in new[] { DeliveryId, SecondDeliveryId })
            result.Add(AssertOne(await adapter.CaptureHintAsync(connection.ConnectionId,
                $"/api/{Cx02Simulator.Publisher}/{Cx02Simulator.Group}/{Cx02Simulator.Version}/companies({Cx02Simulator.CompanyId})/trackedItemLedgerEntries({id})",
                "updated", cancellationToken), "The fictional edge delivery row was not found."));
        return result;
    }

    public async Task<InboundAcceptance> DuplicateSerialReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        var opening = await OpeningReceiptAsync(connection, cancellationToken);
        var source = JsonSerializer.Deserialize<CanonicalMessage<GoodsReceiptPosted>>(
            opening.CanonicalMessage, IntegrationContracts.Json)!;
        var repeated = source.Payload.Lines[0].Serials[0];
        var line = source.Payload.Lines[0] with { Quantity = 2, Serials = [repeated, repeated] };
        return Acceptance(connection, "receipt", "FIC-DUPLICATE", nameof(GoodsReceiptPosted),
            source.Payload with
            {
                DocumentRef = source.Payload.DocumentRef with { ExternalId = "FIC-DUPLICATE" }, Lines = [line],
            });
    }

    public Task<InboundAcceptance> InvoiceAsync(ConnectorEdgeConnection connection, string logicalReference,
        decimal total, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Acceptance(connection, "invoice", logicalReference, nameof(InvoicePosted),
            new InvoicePosted(new("invoice", logicalReference, new(2026, 9, 1), logicalReference),
                "FIC-CUSTOMER", [], Lines(), new(total, "GBP"))));
    }

    public async Task<HttpStatusCode> SendDeliveryNotificationAsync(ConnectorEdgeConnection connection,
        Uri receiverBaseUri, bool cancellation, CancellationToken cancellationToken)
    {
        var missing = cancellation
            ? Guid.Parse("20000000-0000-4000-8000-000000000010")
            : Guid.Parse("20000000-0000-4000-8000-000000000009");
        string[] resources = cancellation
            ? [$"/api/v2.0/companies({Cx02Simulator.CompanyId})/salesCreditMemos({missing})"]
            : [$"/api/{Cx02Simulator.Publisher}/{Cx02Simulator.Group}/{Cx02Simulator.Version}/companies({Cx02Simulator.CompanyId})/trackedItemLedgerEntries({missing})"];
        var body = new
        {
            value = resources.Select((resource, index) => new
            {
                subscriptionId = Guid.Parse("20000000-0000-4000-8000-000000000003"),
                clientState = ClientState, changeType = "updated", resource,
                lastModifiedDateTime = $"2026-09-01T08:05:0{index}Z",
            }).ToArray(),
        };
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/integration/connections/{connection.ConnectionId}/notifications/cx02")
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(IntegrationNotificationEndpoints.TenantHeader, connection.TenantId.ToString());
        using var client = new HttpClient { BaseAddress = receiverBaseUri };
        using var response = await client.SendAsync(request, cancellationToken);
        // The edge contract describes durable acceptance rather than the connector-specific acknowledgement code.
        return response.StatusCode == HttpStatusCode.OK ? HttpStatusCode.Accepted : response.StatusCode;
    }

    public Task CancelShipmentAsync(ConnectorEdgeConnection connection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        shipmentCancelled = true;
        Simulator.Seed("salesCreditMemos",
            CancellationRow(CancellationId, "FIC-CANCEL-A", "FIC-DELIVERY-A", "2026-09-02T08:00:00Z"),
            CancellationRow(SecondCancellationId, "FIC-CANCEL-B", "FIC-DELIVERY-B", "2026-09-02T08:00:01Z"));
        return Task.CompletedTask;
    }

    public async Task<InboundAcceptance> CancellationAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
        => (await CancellationShapeAsync(connection, cancellationToken))[0];

    public async Task<IReadOnlyList<InboundAcceptance>> CancellationShapeAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        using var scope = Scope(connection);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx02Adapter>();
        var result = new List<InboundAcceptance>(2);
        foreach (var id in new[] { CancellationId, SecondCancellationId })
            result.Add(AssertOne(await adapter.CaptureHintAsync(connection.ConnectionId,
                $"/api/v2.0/companies({Cx02Simulator.CompanyId})/salesCreditMemos({id})", "updated",
                cancellationToken), "The fictional cancellation row was not found."));
        return result;
    }

    public void ScriptRemoteUnavailable() => Simulator.ServiceUnavailableResponses = 10_000;
    public void RestoreRemote() => Simulator.ServiceUnavailableResponses = 0;
    public void ScriptClosedPeriodRejection() => Simulator.RejectNextPostAsClosedPeriod = true;
    public void SeedRemoteOwner()
    {
        remoteOwner = "FIC-CUSTOMER";
        SeedServiceItems(remoteOwner);
    }
    public void EditRemoteOwner()
    {
        remoteOwner = "FIC-OTHER";
        SeedServiceItems(remoteOwner);
    }
    public void ScriptDroppedCreateReply() => Simulator.DropNextPostReply = true;
    public void ScriptThrottleStorm(int requests) => Simulator.StartThrottleStorm(requests);
    public void ScriptTokenExpiryDuringPost() => Simulator.ExpireTokenBeforeNextPost = true;

    public ConnectorEdgeFailureGate BlockNextCreateWithFailure()
    {
        var gate = new ConnectorEdgeFailureGate();
        Simulator.FailNextDocumentCreateAfterHook = true;
        Simulator.BeforeNextDocumentCreate = async () =>
        {
            gate.MarkEntered();
            await gate.Release;
        };
        return gate;
    }

    public async Task SetRemoteOnHandAsync(ConnectorEdgeConnection connection, Guid moduleItemId,
        Guid moduleLocationId, decimal quantity, CancellationToken cancellationToken)
    {
        onHand = quantity;
        Simulator.Seed("trackedItemLedgerEntries", Cx02SimulatorEntity.From(new
        {
            id = Guid.Parse("20000000-0000-4000-8000-000000000004"), entryNumber = 7001,
            itemNumber = "HW-SERVER-100", postingDate = "2026-09-01", entryType = "Positive Adjmt.",
            documentNumber = "FIC-ON-HAND", documentType = "Positive Adjmt.", quantity,
            serialNumber = "FIC-SERIAL-001", locationCode = "WH-A",
            lastModifiedDateTime = "2026-09-01T08:00:00Z",
        }));
        using var scope = Scope(connection);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var item = await db.ExternalIdMaps.SingleAsync(row => row.ConnectionId == connection.ConnectionId
            && row.EntityType == "item" && row.ExternalId == "HW-SERVER-100", cancellationToken);
        item.ModuleId = moduleItemId;
        var location = await db.ExternalIdMaps.SingleOrDefaultAsync(row => row.ConnectionId == connection.ConnectionId
            && row.EntityType == "location" && row.ExternalId == "WH-A", cancellationToken);
        if (location is null)
        {
            var now = host!.UtcNow;
            db.ExternalIdMaps.Add(new()
            {
                TenantId = connection.TenantId, ExternalIdMapId = Guid.CreateVersion7(now),
                ConnectionId = connection.ConnectionId, EntityType = "location", ExternalId = "WH-A",
                ModuleId = moduleLocationId, NaturalKeyHash = Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes("LOCATION\nWH-A"))).ToLowerInvariant(), ValidFrom = now, MappedAt = now,
                MappedBy = "system:connector-edge-harness", MappingSource = ExternalIdMappingSource.Operator,
            });
        }
        else location.ModuleId = moduleLocationId;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<ConnectorEdgeRemoteState> ReadRemoteAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ConnectorEdgeRemoteState(Simulator.PostedDocumentCount, shipmentCancelled,
            remoteOwner, onHand));
    }

    public IOutboundCommand ReplacementCommand(string logicalReference) => new CreateReplacementDelivery(
        Guid.CreateVersion7(), "document/" + Guid.CreateVersion7().ToString("N"), logicalReference,
        "FIC-CUSTOMER", "FIC-SHIP-TO", new(Cx02FixtureFacts.UnitId, "FIC-SERIAL-001"),
        OpenEnum.Known("zero"), "FIC-INVOICE", "warranty");

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    private IServiceScope Scope(ConnectorEdgeConnection connection) =>
        (host ?? throw new InvalidOperationException("Create a connection before using the harness."))
        .ScopeFor(connection.TenantId);

    private void SeedDelivery() => Simulator.Seed("trackedItemLedgerEntries",
        DeliveryRow(DeliveryId, 6001, "FIC-DELIVERY-A", "FIC-SERIAL-001", "2026-09-01T08:05:00Z"),
        DeliveryRow(SecondDeliveryId, 6002, "FIC-DELIVERY-B", "FIC-SERIAL-002", "2026-09-01T08:05:01Z"));

    private static Cx02SimulatorEntity DeliveryRow(Guid id, int entry, string document, string serial,
        string modified) =>
        Cx02SimulatorEntity.From(new
        {
            id, entryNumber = entry, itemNumber = "HW-SERVER-100", postingDate = "2026-09-02",
            entryType = "Sale", sourceNumber = "FIC-CUSTOMER", shipToCode = "FIC-SHIP-TO",
            documentNumber = document, documentType = "Sales Shipment", quantity = -1m,
            serialNumber = serial, locationCode = "WH-A", lastModifiedDateTime = modified,
        });

    private static Cx02SimulatorEntity CancellationRow(Guid id, string number, string document,
        string modified) => Cx02SimulatorEntity.From(new
        {
            id, number, cancelledDocumentNumber = document, cancelledDocumentType = "delivery",
            postingDate = "2026-09-03", lastModifiedDateTime = modified,
        });

    private void SeedServiceItems(string owner) => Simulator.Seed("serviceItems",
        Cx02SimulatorEntity.From(new { id = Guid.Parse("20000000-0000-4000-8000-000000000005"),
            number = "FIC-SERVICE-001", serialNumber = "FIC-SERIAL-001", itemNumber = "HW-SERVER-100",
            customerNumber = owner, status = "Active", lastModifiedDateTime = "2026-09-01T08:00:00Z" }),
        Cx02SimulatorEntity.From(new { id = Guid.Parse("20000000-0000-4000-8000-000000000006"),
            number = "FIC-SERVICE-002", serialNumber = "FIC-SERIAL-002", itemNumber = "HW-SERVER-100",
            customerNumber = owner, status = "Active", lastModifiedDateTime = "2026-09-01T08:00:00Z" }));

    private static IReadOnlyList<CanonicalLine> Lines() =>
    [
        new(10, "10", "HW-SERVER-100", 1, "EA", [new("FIC-SERIAL-001")], [], "WH-A"),
        new(20, "20", "HW-SERVER-100", 1, "EA", [new("FIC-SERIAL-002")], [], "WH-A"),
    ];

    private static InboundAcceptance Acceptance<T>(ConnectorEdgeConnection connection, string entity,
        string externalId, string messageType, T payload)
    {
        var envelope = new CanonicalEnvelope("1.0", Guid.CreateVersion7(), connection.TenantId.Value,
            connection.ConnectionId, Cx02Capabilities.ConnectorCode, entity, externalId,
            new("2026-09-01T08:00:00Z", OpenEnum.Known("timestamp")),
            new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero), new(2026, 9, 10, 9, 0, 0, TimeSpan.Zero),
            OpenEnum.Known("poll"), OpenEnum.Known("complete"), "fixture:" + externalId);
        return new(envelope, messageType,
            JsonSerializer.Serialize(new CanonicalMessage<T>(envelope, messageType, payload),
                IntegrationContracts.Json), JsonSerializer.Serialize(payload, IntegrationContracts.Json));
    }

    private static T AssertOne<T>(IReadOnlyList<T> values, string message) => values.Count == 1
        ? values[0] : throw new InvalidOperationException(message);
}

public sealed class Cx02FixtureFacts(IntegrationDbContext? db = null, ITenantContext? tenant = null)
    : ICx02OutboundFacts
{
    public static readonly Guid UnitId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    public Task<Cx02OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, string? suppliedSerial,
        CancellationToken cancellationToken) => Task.FromResult<Cx02OutboundUnit?>(new(
        "HW-SERVER-100", suppliedSerial ?? "FIC-SERIAL-001", "WH-A"));

    public async Task<string?> ReferenceAsync(Guid connectionId, string entityType, string reference,
        CancellationToken cancellationToken)
    {
        if (db is not null && tenant is not null && Guid.TryParse(reference.Split('/').Last(), out var id))
            return await db.ExternalIdMaps.Where(row => row.TenantId == tenant.TenantId
                    && row.ConnectionId == connectionId && row.EntityType == entityType && row.ModuleId == id
                    && row.ValidTo == null)
                .Select(row => row.ExternalId).FirstOrDefaultAsync(cancellationToken);
        return reference;
    }
}

public sealed class Cx02FixtureContent : ICx02AttachmentContent
{
    public byte[] Bytes { get; set; } = "%PDF-fictional"u8.ToArray();
    public Task<Yojana.Integration.Outbox.OutboundAttachmentContent?> ReadAsync(string contentRef,
        long maximumBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Bytes.LongLength > maximumBytes) throw new Yojana.Integration.Outbox.OutboundAttachmentTooLargeException();
        return Task.FromResult<Yojana.Integration.Outbox.OutboundAttachmentContent?>(new(Bytes, "application/pdf"));
    }
}
