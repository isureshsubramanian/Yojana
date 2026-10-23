using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Contracts;
using Yojana.Integration.DatabaseTests.ConnectorEdges;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX01.Testing;

public sealed class Cx01ConnectorEdgeHarness : IConnectorEdgeHarness
{
    private static readonly byte[] SigningSecret = "fictional-edge-signing-secret-32!"u8.ToArray();
    private Cx01Simulator? simulator;
    private IConnectorEdgeHost? host;

    public static ConnectorEdgeHarnessManifest Manifest { get; } = new(
        Cx01Capabilities.ConnectorCode,
        Cx01Capabilities.For("10.0 FP 2602", true, "documents").Messages);

    public string ConnectorCode => Cx01Capabilities.ConnectorCode;
    public IReadOnlyDictionary<string, string> Capabilities => Manifest.Capabilities;

    private Cx01Simulator Simulator => simulator
        ?? throw new InvalidOperationException("Start the connector edge harness before using it.");

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        simulator ??= new Cx01Simulator();
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
        var tenant = tenantId ?? await host.CreateTenantAsync("edgecx");
        var connection = await host.AddConnectionAsync(tenant, ConnectorCode);
        using var scope = host.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(Simulator.BaseUri, "FICTIONAL_EDGE", "10.0 FP 2602", WebhooksAvailable: true),
            cancellationToken);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var credentials = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        await credentials.SaveAsync(await protector.EncryptAsync(tenant, connection,
            Cx01Client.CredentialPurpose,
            JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("fictional-user", "fictional-password"),
                IntegrationContracts.Json), null, host.UtcNow.AddDays(30), cancellationToken),
            cancellationToken);
        await credentials.SaveAsync(await protector.EncryptAsync(tenant, connection,
            Cx01WebhookEndpoints.SigningCredentialPurpose, SigningSecret, null,
            host.UtcNow.AddDays(30), cancellationToken), cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<Yojana.Integration.Data.IntegrationDbContext>();
        var row = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.Connections.Where(candidate => candidate.ConnectionId == connection), cancellationToken);
        row.RegistrationCompleted = true;
        await db.SaveChangesAsync(cancellationToken);
        if (outboundDocuments)
        {
            Simulator.EnableDocumentPosts = true;
            await scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>().SetAsync(connection,
                new("documents", Cx01DocumentScenarios.Options,
                    "Fictional owner accepts edge-suite document posting"),
                "user:edge-operator", cancellationToken);
            await scope.ServiceProvider.GetRequiredService<Cx01SurveyService>()
                .RunAsync(connection, new(), cancellationToken);
        }
        return new(tenant, connection);
    }

    public async Task<InboundAcceptance> ItemAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        using var scope = Scope(connection);
        var capture = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>()
            .CaptureKeyAsync(connection.ConnectionId, "Items", "HW-EDGE-100", cancellationToken);
        return capture?.Acceptance ?? throw new InvalidOperationException("The remote edge item was not found.");
    }

    public async Task<InboundAcceptance> OpeningReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        var delivery = await DeliveryAsync(connection, cancellationToken);
        var canonical = JsonSerializer.Deserialize<CanonicalMessage<DeliveryPosted>>(
            delivery.CanonicalMessage, IntegrationContracts.Json)!;
        var envelope = delivery.Envelope with
        {
            MessageId = Guid.CreateVersion7(),
            ErpEntity = "receipt",
            ErpExternalId = "FIC-OPENING",
            OccurredAt = canonical.Envelope.OccurredAt.AddMinutes(-1),
        };
        var receipt = new GoodsReceiptPosted(
            new("receipt", "FIC-OPENING", new(2026, 9, 1), "FIC-OPENING"),
            "FIC-SUPPLIER", canonical.Payload.Lines);
        return Acceptance(envelope, receipt, "{\"fictional_opening\":true}");
    }

    public async Task<InboundAcceptance> DeliveryAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        using var scope = Scope(connection);
        var capture = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>()
            .CaptureKeyAsync(connection.ConnectionId, "DeliveryNotes", "4101", cancellationToken);
        return capture?.Acceptance ?? throw new InvalidOperationException("The remote edge delivery was not found.");
    }

    public async Task<InboundAcceptance> DuplicateSerialReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        var opening = await OpeningReceiptAsync(connection, cancellationToken);
        var canonical = JsonSerializer.Deserialize<CanonicalMessage<GoodsReceiptPosted>>(
            opening.CanonicalMessage, IntegrationContracts.Json)!;
        var line = canonical.Payload.Lines.Single();
        var repeated = line.Serials[0];
        var receipt = canonical.Payload with
        {
            DocumentRef = canonical.Payload.DocumentRef with { ExternalId = "FIC-DUPLICATE" },
            Lines = [line with { Quantity = 2, Serials = [repeated, repeated] }],
        };
        var envelope = opening.Envelope with
        {
            MessageId = Guid.CreateVersion7(),
            ErpExternalId = "FIC-DUPLICATE",
            PayloadRef = "fixture:duplicate-serial",
        };
        return Acceptance(envelope, receipt,
            JsonSerializer.Serialize(receipt, IntegrationContracts.Json));
    }

    public async Task<InboundAcceptance> InvoiceAsync(ConnectorEdgeConnection connection,
        string logicalReference, decimal total, CancellationToken cancellationToken)
    {
        var opening = await OpeningReceiptAsync(connection, cancellationToken);
        var canonical = JsonSerializer.Deserialize<CanonicalMessage<GoodsReceiptPosted>>(
            opening.CanonicalMessage, IntegrationContracts.Json)!;
        var envelope = opening.Envelope with
        {
            MessageId = Guid.CreateVersion7(),
            ConnectionId = connection.ConnectionId,
            ErpEntity = "invoice",
            ErpExternalId = logicalReference,
            PayloadRef = "fixture:" + logicalReference,
        };
        var invoice = new InvoicePosted(
            new("invoice", logicalReference, new(2026, 9, 1), logicalReference),
            "FIC-CUSTOMER", [], canonical.Payload.Lines, new(total, "GBP"));
        return Acceptance(envelope, invoice, "{\"fictional_invoice\":true}");
    }

    public async Task<HttpStatusCode> SendDeliveryNotificationAsync(ConnectorEdgeConnection connection,
        Uri receiverBaseUri, bool cancellation, CancellationToken cancellationToken)
    {
        var hint = DeliveryHint(connection.ConnectionId, cancellation);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = hint.EventId,
            source = "/default/sap.b1/FICTIONAL_EDGE",
            type = "sap.b1.DeliveryNotes.Created.v1",
            data = new { DocEntry = hint.Key },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/integration/connections/{connection.ConnectionId}/notifications/cx01")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(IntegrationNotificationEndpoints.TenantHeader, connection.TenantId.ToString());
        request.Headers.Add(Cx01Protocol.SignatureHeader,
            Convert.ToBase64String(HMACSHA256.HashData(SigningSecret, bytes)));
        using var client = new HttpClient { BaseAddress = receiverBaseUri };
        using var response = await client.SendAsync(request, cancellationToken);
        return response.StatusCode;
    }

    public async Task CancelShipmentAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        using var scope = Scope(connection);
        using var response = await scope.ServiceProvider.GetRequiredService<Cx01Client>().SendJsonAsync(
            connection.ConnectionId, HttpMethod.Post,
            Cx01Protocol.Root + "DeliveryNotes(4101)/Cancel", new { }, true, cancellationToken);
    }

    public async Task<InboundAcceptance> CancellationAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        using var scope = Scope(connection);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var poll = await adapter.PollAsync(new(connection.TenantId, connection.ConnectionId,
            "deliveries", null, TimeSpan.FromMinutes(2), 3), cancellationToken);
        return poll.Events.Single(candidate => candidate.MessageType == nameof(DocumentCancelled));
    }

    public void ScriptRemoteUnavailable() => Simulator.ServiceUnavailableResponses = 10_000;
    public void RestoreRemote() => Simulator.ServiceUnavailableResponses = 0;
    public void ScriptClosedPeriodRejection() =>
        Simulator.RejectNextDocumentCollection = Cx01DocumentScenarios.Collection("delivery");

    public void SeedRemoteOwner() => Simulator.EquipmentCards.Add(new()
    {
        ["EquipmentCardNum"] = 71,
        ["InternalSerialNum"] = "FICTIONAL-SN-1001",
        ["ItemCode"] = "HW-EDGE-100",
        ["CustomerCode"] = "C-FICTIONAL-01",
        ["StatusOfSerialNumber"] = "sns_Active",
        ["ContractCode"] = null,
        ["ContractEndDate"] = null,
        ["DeliveryCode"] = "FIC-DELIVERY",
        ["InvoiceCode"] = null,
    });

    public void EditRemoteOwner() => Simulator.EquipmentCards.Single()["CustomerCode"] = "FIC-OTHER";
    public void ScriptDroppedCreateReply() => Simulator.DropNextCreateReply = true;

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

    public async Task SetRemoteOnHandAsync(ConnectorEdgeConnection connection, Guid moduleItemId, Guid moduleLocationId, decimal quantity,
        CancellationToken cancellationToken)
    {
        Simulator.OnHandQuantity = quantity;
        using var scope = Scope(connection);
        var db = scope.ServiceProvider.GetRequiredService<Yojana.Integration.Data.IntegrationDbContext>();
        var item = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(
            db.ExternalIdMaps.Where(row => row.ConnectionId == connection.ConnectionId
                && row.EntityType == "item" && row.ExternalId == "HW-EDGE-100"), cancellationToken);
        var location = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleOrDefaultAsync(
            db.ExternalIdMaps.Where(row => row.ConnectionId == connection.ConnectionId
                && row.EntityType == "location" && row.ExternalId == "WH-A"), cancellationToken);
        item.ModuleId = moduleItemId;
        if (location is null)
        {
            var now = host!.UtcNow;
            location = new()
            {
                TenantId = connection.TenantId,
                ExternalIdMapId = Guid.CreateVersion7(now),
                ConnectionId = connection.ConnectionId,
                EntityType = "location",
                ExternalId = "WH-A",
                ModuleId = moduleLocationId,
                NaturalKeyHash = Convert.ToHexString(SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes("LOCATION\nWH-A"))).ToLowerInvariant(),
                ValidFrom = now,
                MappedAt = now,
                MappedBy = "system:connector-edge-harness",
                MappingSource = Yojana.Integration.Data.Entities.ExternalIdMappingSource.Operator,
            };
            db.ExternalIdMaps.Add(location);
        }
        else location.ModuleId = moduleLocationId;
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<ConnectorEdgeRemoteState> ReadRemoteAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var owner = Simulator.EquipmentCards.SingleOrDefault()?.GetValueOrDefault("CustomerCode")?.ToString();
        return Task.FromResult(new ConnectorEdgeRemoteState(
            Simulator.DocumentCreateCounts.Values.Sum(), Simulator.DeliveryCancelled, owner,
            Simulator.OnHandQuantity));
    }

    public IOutboundCommand ReplacementCommand(string logicalReference) =>
        Cx01DocumentScenarios.Command("delivery", logicalReference);

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    private IServiceScope Scope(ConnectorEdgeConnection connection) =>
        (host ?? throw new InvalidOperationException("Create a connection before using the harness."))
        .ScopeFor(connection.TenantId);

    private static Cx01WebhookHint DeliveryHint(Guid connectionId, bool cancellation) =>
        new(connectionId, "DeliveryNotes", "Created", cancellation ? "4103" : "4101",
            cancellation ? "fic-cancel" : "fic-delivery");

    private static InboundAcceptance Acceptance<T>(CanonicalEnvelope envelope, T payload, string raw) =>
        new(envelope, typeof(T).Name,
            JsonSerializer.Serialize(new CanonicalMessage<T>(envelope, typeof(T).Name, payload),
                IntegrationContracts.Json), raw);
}
