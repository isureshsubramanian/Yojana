using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Coexistence;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Integration.DatabaseTests;

public sealed partial class IntegrationEdgeCaseTests
{
    [DatabaseFact]
    public async Task Created_cancellation_after_shipment_reverses_the_original_events_once()
    {
        // ADR-0002 AI 15: a cancellation document created after shipping reverses custody, including re-poll.
        await using var simulator = new Cx01Simulator();
        var (tenant, connection) = await Cx01Async(simulator);
        using var setup = fixture.ScopeFor(tenant);
        var adapter = setup.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var inbox = setup.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var item = await inbox.Accept(await Cx01EdgeScenarios.ItemAsync(adapter, connection), default);
        await WaitIdAsync(tenant, item.InboundMessageId);
        var receipt = await inbox.Accept(await Cx01EdgeScenarios.OpeningReceiptAsync(adapter, connection), default);
        await WaitIdAsync(tenant, receipt.InboundMessageId);
        await using var host = await NotificationEndpointTests.WebProcess.StartAsync();
        using var client = new HttpClient { BaseAddress = host.BaseAddress };
        using (var request = Cx01EdgeScenarios.Notification(connection, tenant.ToString(), SigningSecret))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitAsync(tenant, Cx01EdgeScenarios.Delivery(connection).Key, InboundTranslationStatus.Completed);
        using (var before = fixture.ScopeFor(tenant))
        {
            var db = before.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(2, await db.Events.CountAsync(x => x.EventType == UnitEventType.Shipped));
            Assert.All(await db.Current.ToListAsync(), x => Assert.Equal(LocationKind.InTransit, x.LocationKind));
        }
        await Cx01EdgeScenarios.CancelAsync(setup.ServiceProvider.GetRequiredService<Cx01Client>(), connection);
        using (var request = Cx01EdgeScenarios.Notification(connection, tenant.ToString(), SigningSecret, cancellation: true))
        using (var response = await client.SendAsync(request)) Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        await WaitAsync(tenant, Cx01EdgeScenarios.Delivery(connection, true).Key, InboundTranslationStatus.Completed);
        var poll = await adapter.PollAsync(new(tenant, connection, "deliveries", null, TimeSpan.FromMinutes(2), 3), default);
        var repeatedCancellation = Assert.Single(poll.Events, x => x.MessageType == nameof(DocumentCancelled));
        Assert.Equal(InboundAcceptanceResult.AlreadyProcessed, (await inbox.Accept(repeatedCancellation, default)).Outcome);
        Assert.DoesNotContain(poll.Events, x => x.Envelope.ErpExternalId == Cx01EdgeScenarios.Delivery(connection).Key);
        using var final = fixture.ScopeFor(tenant);
        var serials = final.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var reversals = await serials.Events.Where(x => x.EventType == UnitEventType.ShipmentReversed).ToListAsync();
        Assert.Equal(2, reversals.Count);
        var shippedIds = await serials.Events.Where(x => x.EventType == UnitEventType.Shipped).Select(x => x.EventId).ToListAsync();
        Assert.All(reversals, x => Assert.Contains(x.ReversesEventId!.Value, shippedIds));
        Assert.All(await serials.Current.ToListAsync(), x => Assert.Equal(LocationKind.Warehouse, x.LocationKind));
        Assert.Equal(2, await serials.Units.CountAsync());
    }

    [DatabaseFact]
    public async Task Closed_period_replacement_is_rejected_once_and_manual_number_completes_mapping()
    {
        // ADR-0002 AI 15: closed posting period rejects replacement; a pre-filled manual draft completes by mapping.
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await Cx01Async(simulator);
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01DocumentsService>().SetAsync(connection,
            new("documents", Cx01DocumentScenarios.Options, "Fictional owner accepts document posting"), Actor, default);
        await scope.ServiceProvider.GetRequiredService<Cx01SurveyService>().RunAsync(connection, new(), default);
        var command = Cx01DocumentScenarios.Command("delivery", "FIC-CLOSED-" + Guid.NewGuid().ToString("N"));
        var header = new PublishedEventHeader("Replacement.Shipped", "1.0", Guid.NewGuid(), Cx01DocumentScenarios.UnitId, At.UtcDateTime, At.UtcDateTime, 1, Guid.NewGuid(), Actor, null);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboundCommandStager>().StageAsync(header, command, default));
        Cx01EdgeScenarios.RejectReplacement(simulator);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = await db.OutboundCommands.SingleAsync();
        Assert.Equal(OutboundConnectorOutcome.Rejected, row.Outcome);
        Assert.Contains("period is closed", row.LastFailure);
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == row.OutboundCommandId);
        Assert.Contains("FIC-CUSTOMER", task.Draft);
        Assert.Contains(command.ModuleDocumentRef, task.Draft);
        var requests = simulator.RequestCount;
        for (var repeat = 0; repeat < 3; repeat++) Assert.Equal(0, (await relay.RelayConnectionAsync(connection, default)).Retrying);
        Assert.Equal(requests, simulator.RequestCount);
        var done = await scope.ServiceProvider.GetRequiredService<DeadLetterService>().CompleteManualTaskAsync(task.ManualOutboundTaskId,
            new("FIC-HAND-1", "1", true), Actor, default);
        Assert.Equal(ManualTaskStatus.Completed, done.Status);
        var mapping = await db.ExternalIdMaps.SingleAsync(x => x.EntityType == "document");
        Assert.Equal("FIC-HAND-1", mapping.ExternalId);
        Assert.Equal(Guid.Parse(command.ModuleDocumentRef.Split('/').Last()), mapping.ModuleId);
        Assert.Empty(simulator.DocumentCreateCounts);
    }

    [DatabaseFact]
    public async Task Parallel_run_remote_owner_edit_is_one_conflict_and_does_not_change_unit_state()
    {
        // ADR-0001 coexistence / T18: remotely edited module-owned field is parked, never applied to module units.
        await using var simulator = new Cx01Simulator();
        Cx01EdgeScenarios.AddCard(simulator);
        var (tenant, connection) = await Cx01Async(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var inbox = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var item = await inbox.Accept(await Cx01EdgeScenarios.ItemAsync(adapter, connection), default);
        await WaitIdAsync(tenant, item.InboundMessageId);
        var receipt = await inbox.Accept(await Cx01EdgeScenarios.OpeningReceiptAsync(adapter, connection), default);
        await WaitIdAsync(tenant, receipt.InboundMessageId);
        var hint = Cx01EdgeScenarios.Delivery(connection);
        var delivery = (await adapter.CaptureKeyAsync(connection, hint.Collection, hint.Key, default))!.Acceptance;
        var shipped = await inbox.Accept(delivery, default);
        await WaitIdAsync(tenant, shipped.InboundMessageId);
        var serials = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.All(await serials.Current.AsNoTracking().ToListAsync(), unit => Assert.NotNull(unit.OwnerPartyRef));
        async Task<string> OwnedStateAsync() => JsonSerializer.Serialize(await serials.Current.AsNoTracking().OrderBy(x => x.UnitId)
            .Select(x => new { x.UnitId, x.ItemId, x.OwnerPartyRef, x.CustodyPartyRef, x.LocationKind, x.LocationId, x.LifecycleStatus, x.Disposition, x.ConditionGrade }).ToListAsync());
        var before = await OwnedStateAsync();
        var count = await serials.Events.CountAsync(x => x.EventType == UnitEventType.OwnershipOpened
            || x.EventType == UnitEventType.OwnershipClosed || x.EventType == UnitEventType.CustodyChanged || x.EventType == UnitEventType.Shipped);
        await scope.ServiceProvider.GetRequiredService<ParallelRun>().StartAsync(connection, new(30, new DateOnly(2026, 9, 1)), Actor, default);
        var drift = scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>();
        Assert.Equal(0, (await drift.ScanAsync(connection, default)).ConflictsRaised);
        Cx01EdgeScenarios.EditOwnedField(simulator);
        Assert.Equal(1, (await drift.ScanAsync(connection, default)).ConflictsRaised);
        Assert.Equal(0, (await drift.ScanAsync(connection, default)).ConflictsRaised);
        var conflict = Assert.Single((await scope.ServiceProvider.GetRequiredService<CoexistenceConflicts>().ListAsync(connection, "open", null, 50, default)).Items);
        Assert.Equal(CoexistenceConflictStatus.Open, conflict.Status);
        Assert.Equal(before, await OwnedStateAsync());
        Assert.Equal(count, await serials.Events.CountAsync(x => x.EventType == UnitEventType.OwnershipOpened
            || x.EventType == UnitEventType.OwnershipClosed || x.EventType == UnitEventType.CustodyChanged || x.EventType == UnitEventType.Shipped));
    }

    private static readonly byte[] SigningSecret = "fictional-edge-signing-secret-32!"u8.ToArray();
    private async Task<(TenantId Tenant, Guid Connection)> Cx01Async(Cx01Simulator simulator)
    {
        fixture.Clock.UtcNow = new(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        var tenant = await fixture.NewTenantAsync("edgecx");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = await db.Connections.SingleAsync(x => x.ConnectionId == connection);
        row.RegistrationCompleted = true;
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection, new(simulator.BaseUri, "FICTIONAL_EDGE", "1000340", WebhooksAvailable: true), default);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose,
            JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("fictional-user", "fictional-password"), IntegrationContracts.Json), null, DateTime.UtcNow.AddDays(30), default), default);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01WebhookEndpoints.SigningCredentialPurpose, SigningSecret, null, DateTime.UtcNow.AddDays(30), default), default);
        return (tenant, connection);
    }
    private async Task WaitIdAsync(TenantId tenant, Guid id)
    {
        using var scope = fixture.ScopeFor(tenant);
        var row = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages.AsNoTracking().SingleAsync(x => x.InboundMessageId == id);
        await WaitAsync(tenant, row.ErpExternalId, InboundTranslationStatus.Completed, row.ConnectionId);
    }
}
