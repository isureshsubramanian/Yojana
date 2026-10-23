using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.DatabaseTests.ConnectorEdges;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX01.Testing;

public sealed class Cx01LiveConnectorEdgeHarness : IConnectorEdgeHarness
{
    private static readonly HashSet<string> FaultControlledScenarios =
    [
        ConnectorEdgeScenarioIds.RemoteOutage,
        ConnectorEdgeScenarioIds.ClosedPeriod,
        ConnectorEdgeScenarioIds.RemoteOwnerEdit,
        ConnectorEdgeScenarioIds.DroppedCreateReply,
        ConnectorEdgeScenarioIds.CompanionLocationCheck,
        ConnectorEdgeScenarioIds.StopDuringFailure,
    ];

    private IConnectorEdgeHost? host;
    private LiveFixture? fixture;
    private bool shipmentCancelled;

    public static ConnectorEdgeHarnessManifest Manifest
    {
        get
        {
            var configured = LiveFixture.TryLoad();
            var exclusions = configured is null ? null : ConnectorEdgeScenarios.All
                .Where(scenario => !configured.SupportedScenarios.Contains(scenario.Id, StringComparer.Ordinal)
                    || FaultControlledScenarios.Contains(scenario.Id) && configured.FaultControllerUri is null)
                .ToDictionary(scenario => scenario.Id, scenario =>
                    configured.SupportedScenarios.Contains(scenario.Id, StringComparer.Ordinal)
                        ? $"The live CX01 harness needs the sandbox fault controller to drive {scenario.Id}."
                        : $"The live fixture does not declare {scenario.Id} safe for this sandbox.",
                    StringComparer.Ordinal);
            return new(Cx01Capabilities.ConnectorCode,
                Cx01Capabilities.For(configured?.Release ?? "unknown", true, "documents").Messages,
                exclusions);
        }
    }

    public string ConnectorCode => Cx01Capabilities.ConnectorCode;
    public IReadOnlyDictionary<string, string> Capabilities => Manifest.Capabilities;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fixture = LiveFixture.Load();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fixture = null;
        return Task.CompletedTask;
    }

    public async Task<ConnectorEdgeConnection> CreateConnectionAsync(IConnectorEdgeHost host,
        TenantId? tenantId, bool outboundDocuments, CancellationToken cancellationToken)
    {
        this.host = host;
        var live = Fixture;
        host.UtcNow = new(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        var tenant = tenantId ?? await host.CreateTenantAsync("edgelive");
        var connection = await host.AddConnectionAsync(tenant, ConnectorCode);
        using var scope = host.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(new Uri(Environment.GetEnvironmentVariable("YOJANA_CX01_URL")!), live.CompanyDatabase,
                live.Release, WebhooksAvailable: true), cancellationToken);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var credentials = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var user = Environment.GetEnvironmentVariable("YOJANA_CX01_USER")!;
        await credentials.SaveAsync(await protector.EncryptAsync(tenant, connection,
            Cx01Client.CredentialPurpose,
            JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential(user, Environment.GetEnvironmentVariable(
                "YOJANA_CX01_PASSWORD")!), IntegrationContracts.Json), null,
            host.UtcNow.AddDays(1), cancellationToken), cancellationToken);
        await credentials.SaveAsync(await protector.EncryptAsync(tenant, connection,
            Cx01WebhookEndpoints.SigningCredentialPurpose, Convert.FromBase64String(live.SigningSecretBase64),
            null, host.UtcNow.AddDays(1), cancellationToken), cancellationToken);
        var db = scope.ServiceProvider.GetRequiredService<Yojana.Integration.Data.IntegrationDbContext>();
        var row = await db.Connections.SingleAsync(candidate => candidate.ConnectionId == connection,
            cancellationToken);
        row.RegistrationCompleted = true;
        await db.SaveChangesAsync(cancellationToken);
        if (outboundDocuments)
            await scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>().SetAsync(connection,
                new("documents", Cx01DocumentScenarios.Options, live.DocumentPostingEvidence),
                "user:live-edge-operator", cancellationToken);
        return new(tenant, connection);
    }

    public async Task<InboundAcceptance> ItemAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => await CaptureAsync(connection, "Items", Fixture.ItemKey,
        cancellationToken);

    public async Task<InboundAcceptance> DeliveryAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken) => await CaptureAsync(connection, "DeliveryNotes",
        Fixture.DeliveryKey, cancellationToken);

    public async Task<InboundAcceptance> OpeningReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        var delivery = await DeliveryAsync(connection, cancellationToken);
        var canonical = JsonSerializer.Deserialize<CanonicalMessage<DeliveryPosted>>(
            delivery.CanonicalMessage, IntegrationContracts.Json)!;
        var envelope = delivery.Envelope with
        {
            MessageId = Guid.CreateVersion7(), ErpEntity = "receipt",
            ErpExternalId = Fixture.OpeningReceiptReference,
            OccurredAt = canonical.Envelope.OccurredAt.AddMinutes(-1),
        };
        var receipt = new GoodsReceiptPosted(new("receipt", Fixture.OpeningReceiptReference,
            DateOnly.FromDateTime(envelope.OccurredAt.UtcDateTime), Fixture.OpeningReceiptReference),
            Fixture.SupplierReference, canonical.Payload.Lines);
        return Acceptance(envelope, receipt);
    }

    public async Task<InboundAcceptance> DuplicateSerialReceiptAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        var opening = await OpeningReceiptAsync(connection, cancellationToken);
        var canonical = JsonSerializer.Deserialize<CanonicalMessage<GoodsReceiptPosted>>(
            opening.CanonicalMessage, IntegrationContracts.Json)!;
        var line = canonical.Payload.Lines[0];
        var receipt = canonical.Payload with
        {
            DocumentRef = canonical.Payload.DocumentRef with { ExternalId = Fixture.DuplicateReceiptReference },
            Lines = [line with { Quantity = 2, Serials = [line.Serials[0], line.Serials[0]] }],
        };
        var envelope = opening.Envelope with
        {
            MessageId = Guid.CreateVersion7(), ErpExternalId = Fixture.DuplicateReceiptReference,
            PayloadRef = "live-edge:" + Fixture.DuplicateReceiptReference,
        };
        return Acceptance(envelope, receipt);
    }

    public async Task<InboundAcceptance> InvoiceAsync(ConnectorEdgeConnection connection,
        string logicalReference, decimal total, CancellationToken cancellationToken)
    {
        var opening = await OpeningReceiptAsync(connection, cancellationToken);
        var canonical = JsonSerializer.Deserialize<CanonicalMessage<GoodsReceiptPosted>>(
            opening.CanonicalMessage, IntegrationContracts.Json)!;
        var envelope = opening.Envelope with
        {
            MessageId = Guid.CreateVersion7(), ConnectionId = connection.ConnectionId,
            ErpEntity = "invoice", ErpExternalId = logicalReference,
            PayloadRef = "live-edge:" + logicalReference,
        };
        return Acceptance(envelope, new InvoicePosted(new("invoice", logicalReference,
            DateOnly.FromDateTime(envelope.OccurredAt.UtcDateTime), logicalReference),
            Fixture.CustomerReference, [], canonical.Payload.Lines, new(total, "GBP")));
    }

    public async Task<HttpStatusCode> SendDeliveryNotificationAsync(ConnectorEdgeConnection connection,
        Uri receiverBaseUri, bool cancellation, CancellationToken cancellationToken)
    {
        var key = cancellation ? Fixture.CancellationKey : Fixture.DeliveryKey;
        var eventId = cancellation ? Fixture.CancellationEventId : Fixture.DeliveryEventId;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id = eventId, source = "/default/sap.b1/" + Fixture.CompanyDatabase,
            type = "sap.b1.DeliveryNotes.Created.v1", data = new { DocEntry = key },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"/api/integration/connections/{connection.ConnectionId}/notifications/cx01")
        {
            Content = new ByteArrayContent(bytes),
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Add(IntegrationNotificationEndpoints.TenantHeader, connection.TenantId.ToString());
        request.Headers.Add(Cx01Protocol.SignatureHeader, Convert.ToBase64String(HMACSHA256.HashData(
            Convert.FromBase64String(Fixture.SigningSecretBase64), bytes)));
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
            Cx01Protocol.Root + "DeliveryNotes(" + Fixture.DeliveryKey + ")/Cancel", new { }, true,
            cancellationToken);
        shipmentCancelled = true;
    }

    public async Task<InboundAcceptance> CancellationAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        using var scope = Scope(connection);
        var poll = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>().PollAsync(
            new(connection.TenantId, connection.ConnectionId, "deliveries", null,
                TimeSpan.FromMinutes(2), 3), cancellationToken);
        return poll.Events.Single(candidate => candidate.MessageType == nameof(DocumentCancelled));
    }

    public IOutboundCommand ReplacementCommand(string logicalReference) =>
        Cx01DocumentScenarios.Command("delivery", logicalReference);

    public async Task<ConnectorEdgeRemoteState> ReadRemoteAsync(ConnectorEdgeConnection connection,
        CancellationToken cancellationToken)
    {
        if (Fixture.FaultControllerUri is null)
            return new(0, shipmentCancelled, null, 0);
        using var client = ControllerClient();
        return await client.GetFromJsonAsync<ConnectorEdgeRemoteState>("state", cancellationToken)
            ?? throw new InvalidOperationException("The sandbox fault controller returned no remote state.");
    }

    public void ScriptRemoteUnavailable() => Control("remote/unavailable");
    public void RestoreRemote() => Control("remote/available");
    public void ScriptClosedPeriodRejection() => Control("outbound/reject-closed-period");
    public void SeedRemoteOwner() => Control("owner/seed");
    public void EditRemoteOwner() => Control("owner/edit");
    public void ScriptDroppedCreateReply() => Control("outbound/drop-next-create-reply");

    public ConnectorEdgeFailureGate BlockNextCreateWithFailure()
    {
        Control("outbound/block-next-create-failure");
        return new(WaitForControllerAsync("outbound/create-block-entered", CancellationToken.None),
            () => Control("outbound/release-create-failure"));
    }

    public async Task SetRemoteOnHandAsync(ConnectorEdgeConnection connection, Guid moduleItemId, Guid moduleLocationId, decimal quantity,
        CancellationToken cancellationToken)
    {
        using (var client = ControllerClient())
        using (var response = await client.PostAsJsonAsync("inventory/on-hand", new { quantity },
                   cancellationToken))
            response.EnsureSuccessStatusCode();
        using var scope = Scope(connection);
        var db = scope.ServiceProvider.GetRequiredService<Yojana.Integration.Data.IntegrationDbContext>();
        var item = await db.ExternalIdMaps.SingleAsync(row => row.ConnectionId == connection.ConnectionId
            && row.EntityType == "item" && row.ExternalId == Fixture.ItemKey, cancellationToken);
        item.ModuleId = moduleItemId;
        var location = await db.ExternalIdMaps.SingleOrDefaultAsync(row => row.ConnectionId == connection.ConnectionId
            && row.EntityType == "location" && row.ExternalId == Fixture.WarehouseKey, cancellationToken);
        if (location is null)
        {
            var now = host!.UtcNow;
            location = new()
            {
                TenantId = connection.TenantId, ExternalIdMapId = Guid.CreateVersion7(now),
                ConnectionId = connection.ConnectionId, EntityType = "location",
                ExternalId = Fixture.WarehouseKey, ModuleId = moduleLocationId,
                NaturalKeyHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    "LOCATION\n" + Fixture.WarehouseKey.ToUpperInvariant()))).ToLowerInvariant(),
                ValidFrom = now, MappedAt = now, MappedBy = "system:live-connector-edge-harness",
                MappingSource = Yojana.Integration.Data.Entities.ExternalIdMappingSource.Operator,
            };
            db.ExternalIdMaps.Add(location);
        }
        else location.ModuleId = moduleLocationId;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None);

    private LiveFixture Fixture => fixture
        ?? throw new InvalidOperationException("Start the live edge harness before using it.");

    private IServiceScope Scope(ConnectorEdgeConnection connection) =>
        (host ?? throw new InvalidOperationException("Create a connection before using the live harness."))
        .ScopeFor(connection.TenantId);

    private async Task<InboundAcceptance> CaptureAsync(ConnectorEdgeConnection connection,
        string collection, string key, CancellationToken cancellationToken)
    {
        using var scope = Scope(connection);
        var capture = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>()
            .CaptureKeyAsync(connection.ConnectionId, collection, key, cancellationToken);
        return capture?.Acceptance ?? throw new InvalidOperationException(
            $"The configured live edge fixture {collection} record was not found.");
    }

    private static InboundAcceptance Acceptance<T>(CanonicalEnvelope envelope, T payload) => new(
        envelope, typeof(T).Name,
        JsonSerializer.Serialize(new CanonicalMessage<T>(envelope, typeof(T).Name, payload),
            IntegrationContracts.Json), JsonSerializer.Serialize(payload, IntegrationContracts.Json));

    private HttpClient ControllerClient() => new() { BaseAddress = Fixture.FaultControllerUri
        ?? throw new InvalidOperationException("The live fixture needs fault_controller_uri for this scenario.") };

    private void Control(string operation)
    {
        using var client = ControllerClient();
        using var response = client.PostAsync(operation, new StringContent("{}", System.Text.Encoding.UTF8,
            "application/json")).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
    }

    private async Task WaitForControllerAsync(string operation, CancellationToken cancellationToken)
    {
        using var client = ControllerClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await client.GetAsync(operation, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK) return;
            if (response.StatusCode != HttpStatusCode.Conflict) response.EnsureSuccessStatusCode();
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("The sandbox fault controller did not observe the blocked create.");
    }

    private sealed record LiveFixture(
        string CompanyDatabase,
        string Release,
        string ItemKey,
        string DeliveryKey,
        string CancellationKey,
        string DeliveryEventId,
        string CancellationEventId,
        string OpeningReceiptReference,
        string DuplicateReceiptReference,
        string SupplierReference,
        string CustomerReference,
        string WarehouseKey,
        string SigningSecretBase64,
        string DocumentPostingEvidence,
        IReadOnlyList<string> SupportedScenarios,
        Uri? FaultControllerUri = null)
    {
        public static LiveFixture Load() => TryLoad() ?? throw new InvalidOperationException(
            "YOJANA_CX01_EDGE_SCENARIO must name a live edge fixture JSON file.");

        public static LiveFixture? TryLoad()
        {
            var path = Environment.GetEnvironmentVariable("YOJANA_CX01_EDGE_SCENARIO");
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            return JsonSerializer.Deserialize<LiveFixture>(File.ReadAllText(path), IntegrationContracts.Json)
                ?? throw new InvalidOperationException("The live edge fixture JSON is invalid.");
        }
    }
}
