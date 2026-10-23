using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Coexistence;
using Yojana.Integration.Connections;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Ordering;
using Yojana.Integration.Outbox;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Xunit;

namespace Yojana.Integration.DatabaseTests.ConnectorEdges;

public sealed class ConnectorEdgeScenarioRunner(IntegrationHostFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private const string Actor = "user:edge-operator";

    public async Task RunAsync(ConnectorEdgeScenario scenario, IConnectorEdgeHarness harness,
        CancellationToken cancellationToken)
    {
        var manifest = new ConnectorEdgeHarnessManifest(harness.ConnectorCode, harness.Capabilities);
        if (manifest.NotApplicable(scenario) is { } reason)
            throw new InvalidOperationException(
                $"The discovery-time capability skip was not applied for {scenario.Id}: {reason}");

        await using (harness)
        {
            await harness.StartAsync(cancellationToken);
            switch (scenario.Id)
            {
                case ConnectorEdgeScenarioIds.DuplicateNotification:
                    await DuplicateNotificationAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.OutOfOrder:
                    await OutOfOrderAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.RemoteOutage:
                    await RemoteOutageAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.CancellationAfterShipment:
                    await CancellationAfterShipmentAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.DuplicateSerial:
                    await DuplicateSerialAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.ClosedPeriod:
                    await ClosedPeriodAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.OverlappingConnections:
                    await OverlappingConnectionsAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.RemoteOwnerEdit:
                    await RemoteOwnerEditAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.DroppedCreateReply:
                    await DroppedCreateReplyAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.CompanionLocationCheck:
                    await CompanionLocationCheckAsync(harness, cancellationToken);
                    break;
                case ConnectorEdgeScenarioIds.StopDuringFailure:
                    await StopDuringFailureAsync(harness, cancellationToken);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }
            await harness.StopAsync(cancellationToken);
        }
    }

    private async Task DuplicateNotificationAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var connection = await ConnectionAsync(harness, false, ct);
        await AcceptAsync(connection, await harness.ItemAsync(connection, ct), InboundTranslationStatus.Completed, ct);
        await using var web = await NotificationEndpointTests.WebProcess.StartAsync();
        Assert.Equal(HttpStatusCode.Accepted,
            await harness.SendDeliveryNotificationAsync(connection, web.BaseAddress, false, ct));
        await WaitAsync(connection, ExternalId(await harness.DeliveryAsync(connection, ct)),
            InboundTranslationStatus.Held, ct);
        Assert.Equal(HttpStatusCode.Accepted,
            await harness.SendDeliveryNotificationAsync(connection, web.BaseAddress, false, ct));
        await AcceptAsync(connection, await harness.OpeningReceiptAsync(connection, ct),
            InboundTranslationStatus.Completed, ct);
        var deliveryId = ExternalId(await harness.DeliveryAsync(connection, ct));
        await WaitAsync(connection, deliveryId, InboundTranslationStatus.Completed, ct);
        Assert.Equal(HttpStatusCode.Accepted,
            await harness.SendDeliveryNotificationAsync(connection, web.BaseAddress, false, ct));
        using var scope = fixture.ScopeFor(connection.TenantId);
        var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(1, await integration.InboundMessages.CountAsync(x => x.ConnectionId == connection.ConnectionId
            && x.ErpExternalId == deliveryId, ct));
        Assert.Equal(1, await integration.IdempotencyIndex.CountAsync(x => x.ConnectionId == connection.ConnectionId
            && x.ErpExternalId == deliveryId, ct));
        var serials = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(2, await serials.Units.CountAsync(ct));
        Assert.Equal(2, await serials.Events.CountAsync(x => x.EventType == UnitEventType.Shipped, ct));
        Assert.Equal(2, await serials.Episodes.CountAsync(x => x.Kind == EpisodeKind.Ownership, ct));
    }

    private async Task OutOfOrderAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var connection = await ConnectionAsync(harness, false, ct);
        await AcceptAsync(connection, await harness.ItemAsync(connection, ct), InboundTranslationStatus.Completed, ct);
        var delivery = await harness.DeliveryAsync(connection, ct);
        var held = await AcceptAsync(connection, delivery, InboundTranslationStatus.Held, ct);
        using (var check = fixture.ScopeFor(connection.TenantId))
        {
            var db = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var hold = await db.ResequenceItems.SingleAsync(x => x.InboundMessageId == held, ct);
            Assert.Equal(TimeSpan.FromMinutes(15), hold.HoldUntil - hold.HeldAt);
            Assert.Equal(0, await check.ServiceProvider.GetRequiredService<SerialisationDbContext>()
                .Units.CountAsync(ct));
            Assert.Equal(0, await check.ServiceProvider.GetRequiredService<BoundedWaitResequencer>()
                .ReleaseExpiredAsync(ct));
        }
        await AcceptAsync(connection, await harness.OpeningReceiptAsync(connection, ct),
            InboundTranslationStatus.Completed, ct);
        await WaitAsync(connection, delivery.Envelope.ErpExternalId, InboundTranslationStatus.Completed, ct);
        using (var check = fixture.ScopeFor(connection.TenantId))
        {
            var db = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.All(await db.Current.ToListAsync(ct), row => Assert.Equal(LocationKind.CustomerSite, row.LocationKind));
            var events = await db.Events.OrderBy(x => x.EventTime).ThenBy(x => x.RecordedTime)
                .ThenBy(x => x.Seq).ToListAsync(ct);
            Assert.True(events.FindIndex(x => x.EventType == UnitEventType.Released)
                < events.FindIndex(x => x.EventType == UnitEventType.Shipped));
        }

        var late = LateDelivery(delivery);
        var lateId = await AcceptAsync(connection, late, InboundTranslationStatus.Held, ct);
        fixture.Clock.UtcNow += TimeSpan.FromMinutes(16);
        using (var release = fixture.ScopeFor(connection.TenantId))
            Assert.Equal(1, await release.ServiceProvider.GetRequiredService<BoundedWaitResequencer>()
                .ReleaseExpiredAsync(ct));
        await WaitAsync(connection, late.Envelope.ErpExternalId, InboundTranslationStatus.Completed, ct);
        using var final = fixture.ScopeFor(connection.TenantId);
        var integration = final.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.True((await integration.ResequenceItems.SingleAsync(x => x.InboundMessageId == lateId, ct)).Late);
        Assert.Single(await integration.ExceptionTasks.Where(x => x.InboundMessageId == lateId
            && x.Kind == "provisional_unit").ToListAsync(ct));
        var serials = final.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var identifier = await serials.Identifiers.SingleAsync(x => x.RawValue == "FIC-LATE", ct);
        Assert.Equal(IdentifierType.Provisional, identifier.Type);
        var opening = await serials.Events.SingleAsync(x => x.UnitId == identifier.UnitId
            && x.EventType == UnitEventType.Commissioned, ct);
        Assert.Equal(late.Envelope.OccurredAt.AddMilliseconds(-1).UtcDateTime, opening.EventTime);
        Assert.Equal(fixture.Clock.UtcNow, opening.RecordedTime);
        Assert.True(await integration.ExceptionTasks.AnyAsync(x => x.InboundMessageId == lateId
            && x.Kind == "consumed_event_exception", ct));
    }

    private async Task DuplicateSerialAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var connection = await ConnectionAsync(harness, false, ct);
        await AcceptAsync(connection, await harness.ItemAsync(connection, ct), InboundTranslationStatus.Completed, ct);
        var inbound = await AcceptAsync(connection, await harness.DuplicateSerialReceiptAsync(connection, ct),
            InboundTranslationStatus.Completed, ct);
        using var scope = fixture.ScopeFor(connection.TenantId);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .Units.CountAsync(ct));
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var task = Assert.Single(await db.ExceptionTasks.Where(x => x.InboundMessageId == inbound
            && x.Kind == "duplicate_serial").ToListAsync(ct));
        Assert.Contains("serial", task.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("open", task.Status);
        var retained = await db.InboundMessages.SingleAsync(x => x.InboundMessageId == inbound, ct);
        Assert.Equal(2, JsonSerializer.Deserialize<GoodsReceiptPosted>(retained.RawPayload,
            IntegrationContracts.Json)!.Lines.Single().Serials.Count);
        var projected = await db.CanonicalDocuments.SingleAsync(x => x.SourceInboundMessageId == inbound, ct);
        var receipt = JsonSerializer.Deserialize<GoodsReceiptPosted>(projected.Payload,
            IntegrationContracts.Json)!;
        Assert.Single(receipt.Lines.Single().Serials);
        Assert.Equal(2, receipt.Lines.Single().Quantity);
    }

    private async Task OverlappingConnectionsAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var first = await ConnectionAsync(harness, false, ct);
        var second = await harness.CreateConnectionAsync(new IntegrationConnectorEdgeHost(fixture),
            first.TenantId, false, ct);
        await AcceptAsync(first, await harness.ItemAsync(first, ct), InboundTranslationStatus.Completed, ct);
        await AcceptAsync(first, await harness.OpeningReceiptAsync(first, ct), InboundTranslationStatus.Completed, ct);
        var incumbent = await AcceptAsync(first, await harness.InvoiceAsync(first, "FIC-INVOICE-A", 25, ct),
            InboundTranslationStatus.Completed, ct);
        int facts;
        using (var before = fixture.ScopeFor(first.TenantId))
            facts = await before.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events.CountAsync(ct);
        using (var map = fixture.ScopeFor(first.TenantId))
        {
            var db = map.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var original = await db.ExternalIdMaps.SingleAsync(x => x.ConnectionId == first.ConnectionId
                && x.EntityType == "document" && x.ExternalId == "FIC-INVOICE-A", ct);
            db.ExternalIdMaps.Add(new()
            {
                TenantId = first.TenantId, ExternalIdMapId = Guid.CreateVersion7(),
                ConnectionId = second.ConnectionId, EntityType = "document", ExternalId = "FIC-INVOICE-B",
                ModuleId = Guid.NewGuid(), NaturalKeyHash = original.NaturalKeyHash,
                ValidFrom = fixture.Clock.UtcNow.AddDays(-30), ValidTo = fixture.Clock.UtcNow.AddDays(-1),
                MappedAt = fixture.Clock.UtcNow.AddDays(-30), MappedBy = Actor,
                MappingSource = ExternalIdMappingSource.Operator,
            });
            db.ExternalIdMaps.Add(new()
            {
                TenantId = first.TenantId, ExternalIdMapId = Guid.CreateVersion7(),
                ConnectionId = second.ConnectionId, EntityType = "document", ExternalId = "FIC-INVOICE-B",
                ModuleId = original.ModuleId, NaturalKeyHash = original.NaturalKeyHash,
                ValidFrom = fixture.Clock.UtcNow, MappedAt = fixture.Clock.UtcNow, MappedBy = Actor,
                MappingSource = ExternalIdMappingSource.Operator,
            });
            await db.SaveChangesAsync(ct);
        }
        var duplicate = await AcceptAsync(second,
            await harness.InvoiceAsync(second, "FIC-INVOICE-B", 26, ct),
            InboundTranslationStatus.Completed, ct);
        using var check = fixture.ScopeFor(first.TenantId);
        var serials = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(2, await serials.Units.CountAsync(ct));
        Assert.Equal(facts, await serials.Events.CountAsync(ct));
        var dbFinal = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(2, await dbFinal.InboundMessages.CountAsync(x => x.MessageType == nameof(InvoicePosted), ct));
        var projection = await dbFinal.CanonicalDocuments.SingleAsync(x => x.MessageType == nameof(InvoicePosted), ct);
        Assert.Equal(first.ConnectionId, projection.ConnectionId);
        Assert.Equal(incumbent, projection.SourceInboundMessageId);
        Assert.Single(await dbFinal.ExceptionTasks.Where(x => x.InboundMessageId == duplicate
            && x.Kind == "cross_connection_document_conflict").ToListAsync(ct));
    }

    private async Task CompanionLocationCheckAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var connection = await ConnectionAsync(harness, false, ct);
        await AcceptAsync(connection, await harness.ItemAsync(connection, ct), InboundTranslationStatus.Completed, ct);
        await AcceptAsync(connection, await harness.OpeningReceiptAsync(connection, ct),
            InboundTranslationStatus.Completed, ct);
        using var scope = fixture.ScopeFor(connection.TenantId);
        var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
        await harness.SetRemoteOnHandAsync(connection, 3, ct);
        for (var run = 0; run < 2; run++)
        {
            var detail = (await service.DetailAsync(await service.RunLocationChecksAsync(
                connection.ConnectionId, Actor, ct), ct))!;
            var difference = Assert.Single(detail.LocationChecks);
            Assert.Equal(ReconciliationResultStatus.Mismatch, difference.Status);
            Assert.Equal(2, difference.ModuleUnitCount);
            Assert.Equal(3, difference.SourceOnHand);
        }
        await harness.SetRemoteOnHandAsync(connection, 2, ct);
        var clean = (await service.DetailAsync(await service.RunLocationChecksAsync(
            connection.ConnectionId, Actor, ct), ct))!;
        Assert.Equal(ReconciliationResultStatus.Clean, Assert.Single(clean.LocationChecks).Status);
    }

    private async Task RemoteOutageAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var connection = await ConnectionAsync(harness, true, ct);
        var commandId = await StageReplacementAsync(connection, harness, "FIC-OUTAGE", ct);
        using var scope = fixture.ScopeFor(connection.TenantId);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>()
            .SetAsync(OutboxRelaySettings.ConnectionKey(connection.ConnectionId), "1", ct);
        await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunAsync(
            connection.ConnectionId, new(2026, 9, 10), new(2026, 9, 10),
            ReconciliationTrigger.Scheduled, false, null, ct);
        Assert.NotEqual("down", (await scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>()
            .RefreshAsync(connection.ConnectionId, ct)).Status);
        var start = fixture.Clock.UtcNow;
        db.ConnectionPollWatermarks.Add(new()
        {
            TenantId = connection.TenantId, ConnectionId = connection.ConnectionId,
            EntityType = "invoice", LastPolledAt = start, NextPollAt = start.AddMinutes(1),
            IntervalSeconds = 60, OverlapSeconds = 120, LastError = "Fictional remote unavailable",
        });
        await db.SaveChangesAsync(ct);
        harness.ScriptRemoteUnavailable();
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        var command = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == commandId, ct);
        var sawDegraded = false;
        var sawDown = false;
        var attempts = 0;
        while (fixture.Clock.UtcNow < start.AddDays(1))
        {
            Assert.Equal(1, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Retrying);
            attempts++;
            await db.Entry(command).ReloadAsync(ct);
            Assert.Equal(OutboundCommandStatus.Retry, command.Status);
            Assert.Equal(0, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Retrying);
            var projected = await scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>()
                .RefreshAsync(connection.ConnectionId, ct);
            sawDegraded |= projected.Status == "degraded";
            sawDown |= projected.Status == "down";
            fixture.Clock.UtcNow = command.NextAttemptAt!.Value;
        }
        Assert.True(attempts > 3);
        Assert.True(sawDegraded);
        Assert.True(sawDown);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Parked);
        var letter = await db.DeadLetterItems.SingleAsync(x => x.SourceId == commandId, ct);
        Assert.Equal("max_outbox_age", letter.ReasonCode);
        harness.RestoreRemote();
        await scope.ServiceProvider.GetRequiredService<DeadLetterService>().MapAndRetryAsync(
            letter.DeadLetterItemId, new("party", "FIC-CUSTOMER", Guid.NewGuid()), Actor, ct);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Completed);
        Assert.Equal(0, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Completed);
        Assert.Equal(1, (await harness.ReadRemoteAsync(connection, ct)).CreatedDocuments);
        Assert.Single(await db.ExternalIdMaps.Where(x => x.ConnectionId == connection.ConnectionId
            && x.EntityType == "document").ToListAsync(ct));
    }

    private async Task CancellationAfterShipmentAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var connection = await ConnectionAsync(harness, false, ct);
        await AcceptAsync(connection, await harness.ItemAsync(connection, ct), InboundTranslationStatus.Completed, ct);
        await AcceptAsync(connection, await harness.OpeningReceiptAsync(connection, ct),
            InboundTranslationStatus.Completed, ct);
        await using var web = await NotificationEndpointTests.WebProcess.StartAsync();
        Assert.Equal(HttpStatusCode.Accepted,
            await harness.SendDeliveryNotificationAsync(connection, web.BaseAddress, false, ct));
        await WaitAsync(connection, ExternalId(await harness.DeliveryAsync(connection, ct)),
            InboundTranslationStatus.Completed, ct);
        using (var before = fixture.ScopeFor(connection.TenantId))
        {
            var db = before.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(2, await db.Events.CountAsync(x => x.EventType == UnitEventType.Shipped, ct));
            Assert.All(await db.Current.ToListAsync(ct), x => Assert.Equal(LocationKind.InTransit, x.LocationKind));
        }
        await harness.CancelShipmentAsync(connection, ct);
        Assert.Equal(HttpStatusCode.Accepted,
            await harness.SendDeliveryNotificationAsync(connection, web.BaseAddress, true, ct));
        var cancellation = await harness.CancellationAsync(connection, ct);
        await WaitAsync(connection, cancellation.Envelope.ErpExternalId, InboundTranslationStatus.Completed, ct);
        using var scope = fixture.ScopeFor(connection.TenantId);
        var inbox = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        Assert.Equal(InboundAcceptanceResult.AlreadyProcessed,
            (await inbox.Accept(cancellation, ct)).Outcome);
        var serials = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var reversals = await serials.Events.Where(x => x.EventType == UnitEventType.ShipmentReversed)
            .ToListAsync(ct);
        Assert.Equal(2, reversals.Count);
        var shipped = await serials.Events.Where(x => x.EventType == UnitEventType.Shipped)
            .Select(x => x.EventId).ToListAsync(ct);
        Assert.All(reversals, reversal => Assert.Contains(reversal.ReversesEventId!.Value, shipped));
        Assert.All(await serials.Current.ToListAsync(ct), x => Assert.Equal(LocationKind.Warehouse, x.LocationKind));
        Assert.True((await harness.ReadRemoteAsync(connection, ct)).ShipmentCancelled);
    }

    private async Task ClosedPeriodAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var connection = await ConnectionAsync(harness, true, ct);
        var command = harness.ReplacementCommand("FIC-CLOSED-" + Guid.NewGuid().ToString("N"));
        var rowId = await StageAsync(connection, command, ct);
        harness.ScriptClosedPeriodRejection();
        using var scope = fixture.ScopeFor(connection.TenantId);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Parked);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == rowId, ct);
        Assert.Equal(OutboundConnectorOutcome.Rejected, row.Outcome);
        Assert.Contains("period is closed", row.LastFailure);
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == rowId, ct);
        Assert.Contains("FIC-CUSTOMER", task.Draft);
        var before = await harness.ReadRemoteAsync(connection, ct);
        for (var repeat = 0; repeat < 3; repeat++)
            Assert.Equal(0, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Retrying);
        Assert.Equal(before, await harness.ReadRemoteAsync(connection, ct));
        await scope.ServiceProvider.GetRequiredService<DeadLetterService>().CompleteManualTaskAsync(
            task.ManualOutboundTaskId, new("FIC-HAND-1", "1", true), Actor, ct);
        Assert.Equal("FIC-HAND-1", (await db.ExternalIdMaps.SingleAsync(x => x.EntityType == "document", ct)).ExternalId);
        Assert.Equal(0, (await harness.ReadRemoteAsync(connection, ct)).CreatedDocuments);
    }

    private async Task RemoteOwnerEditAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        harness.SeedRemoteOwner();
        var connection = await ConnectionAsync(harness, false, ct);
        await AcceptAsync(connection, await harness.ItemAsync(connection, ct), InboundTranslationStatus.Completed, ct);
        await AcceptAsync(connection, await harness.OpeningReceiptAsync(connection, ct),
            InboundTranslationStatus.Completed, ct);
        await AcceptAsync(connection, await harness.DeliveryAsync(connection, ct),
            InboundTranslationStatus.Completed, ct);
        using var scope = fixture.ScopeFor(connection.TenantId);
        var serials = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.All(await serials.Current.AsNoTracking().ToListAsync(ct), unit => Assert.NotNull(unit.OwnerPartyRef));
        async Task<string> OwnedStateAsync() => JsonSerializer.Serialize(await serials.Current.AsNoTracking()
            .OrderBy(x => x.UnitId).Select(x => new
            {
                x.UnitId, x.ItemId, x.OwnerPartyRef, x.CustodyPartyRef, x.LocationKind, x.LocationId,
                x.LifecycleStatus, x.Disposition, x.ConditionGrade,
            }).ToListAsync(ct));
        var before = await OwnedStateAsync();
        var count = await serials.Events.CountAsync(x => x.EventType == UnitEventType.OwnershipOpened
            || x.EventType == UnitEventType.OwnershipClosed || x.EventType == UnitEventType.CustodyChanged
            || x.EventType == UnitEventType.Shipped, ct);
        await scope.ServiceProvider.GetRequiredService<ParallelRun>().StartAsync(connection.ConnectionId,
            new(30, new DateOnly(2026, 9, 1)), Actor, ct);
        var drift = scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>();
        Assert.Equal(0, (await drift.ScanAsync(connection.ConnectionId, ct)).ConflictsRaised);
        harness.EditRemoteOwner();
        Assert.Equal(1, (await drift.ScanAsync(connection.ConnectionId, ct)).ConflictsRaised);
        Assert.Equal(0, (await drift.ScanAsync(connection.ConnectionId, ct)).ConflictsRaised);
        Assert.Single((await scope.ServiceProvider.GetRequiredService<CoexistenceConflicts>()
            .ListAsync(connection.ConnectionId, "open", null, 50, ct)).Items);
        Assert.Equal(before, await OwnedStateAsync());
        Assert.Equal(count, await serials.Events.CountAsync(x => x.EventType == UnitEventType.OwnershipOpened
            || x.EventType == UnitEventType.OwnershipClosed || x.EventType == UnitEventType.CustodyChanged
            || x.EventType == UnitEventType.Shipped, ct));
    }

    private async Task DroppedCreateReplyAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var connection = await ConnectionAsync(harness, true, ct);
        var rowId = await StageReplacementAsync(connection, harness, "FIC-DROPPED-REPLY", ct);
        harness.ScriptDroppedCreateReply();
        using var scope = fixture.ScopeFor(connection.TenantId);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        var result = await relay.RelayConnectionAsync(connection.ConnectionId, ct);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == rowId, ct);
        if (result.Completed == 0)
        {
            Assert.Equal(1, result.Retrying);
            fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
            Assert.Equal(1, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Completed);
        }
        await db.Entry(row).ReloadAsync(ct);
        Assert.Equal(OutboundCommandStatus.Done, row.Status);
        Assert.Equal(1, (await harness.ReadRemoteAsync(connection, ct)).CreatedDocuments);
        Assert.Single(await db.ExternalIdMaps.Where(x => x.ConnectionId == connection.ConnectionId
            && x.EntityType == "document").ToListAsync(ct));
    }

    private async Task StopDuringFailureAsync(IConnectorEdgeHarness harness, CancellationToken ct)
    {
        var connection = await ConnectionAsync(harness, true, ct);
        var rowId = await StageReplacementAsync(connection, harness, "FIC-STOP-INFLIGHT", ct);
        var gate = harness.BlockNextCreateWithFailure();
        using var sending = fixture.ScopeFor(connection.TenantId);
        var inFlight = sending.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection.ConnectionId, ct);
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10), ct);
        using var stopping = fixture.ScopeFor(connection.TenantId);
        var stop = stopping.ServiceProvider.GetRequiredService<SyncControl>().StopAsync(
            connection.ConnectionId, new("Fictional maintenance"), Actor, ct);
        Assert.False(inFlight.IsCompleted);
        gate.Continue();
        await inFlight;
        Assert.Equal("stopped", (await stop).SyncState);
        using var check = fixture.ScopeFor(connection.TenantId);
        var db = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == rowId, ct);
        Assert.Equal(OutboundCommandStatus.Retry, row.Status);
        fixture.Clock.UtcNow = row.NextAttemptAt!.Value.AddSeconds(1);
        var relay = check.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(0, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Completed);
        await check.ServiceProvider.GetRequiredService<SyncControl>().ResumeAsync(
            connection.ConnectionId, new("Maintenance complete"), Actor, ct);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Completed);
        Assert.Equal(0, (await relay.RelayConnectionAsync(connection.ConnectionId, ct)).Completed);
        Assert.Equal(1, (await harness.ReadRemoteAsync(connection, ct)).CreatedDocuments);
    }

    private async Task<ConnectorEdgeConnection> ConnectionAsync(IConnectorEdgeHarness harness,
        bool outboundDocuments, CancellationToken ct) =>
        await harness.CreateConnectionAsync(new IntegrationConnectorEdgeHost(fixture), null,
            outboundDocuments, ct);

    private async Task<Guid> AcceptAsync(ConnectorEdgeConnection connection, InboundAcceptance acceptance,
        string expected, CancellationToken ct)
    {
        using var scope = fixture.ScopeFor(connection.TenantId);
        var result = await scope.ServiceProvider.GetRequiredService<InboundMessageStore>().Accept(acceptance, ct);
        await WaitAsync(connection, acceptance.Envelope.ErpExternalId, expected, ct);
        return result.InboundMessageId;
    }

    private async Task<Guid> WaitAsync(ConnectorEdgeConnection connection, string externalId,
        string expected, CancellationToken ct)
    {
        string? last = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            using var scope = fixture.ScopeFor(connection.TenantId);
            var row = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>()
                .InboundMessages.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.ConnectionId == connection.ConnectionId && x.ErpExternalId == externalId, ct);
            if (row?.TranslationStatus == expected) return row.InboundMessageId;
            last = row?.TranslationStatus + ": " + row?.LastTranslationError;
            await Task.Delay(20, ct);
        }
        throw new TimeoutException($"{externalId} expected {expected}; {last}");
    }

    private async Task<Guid> StageReplacementAsync(ConnectorEdgeConnection connection,
        IConnectorEdgeHarness harness, string reference, CancellationToken ct) =>
        await StageAsync(connection, harness.ReplacementCommand(reference), ct);

    private async Task<Guid> StageAsync(ConnectorEdgeConnection connection, IOutboundCommand command,
        CancellationToken ct)
    {
        using var scope = fixture.ScopeFor(connection.TenantId);
        var header = new PublishedEventHeader("Replacement.Shipped", "1.0", Guid.CreateVersion7(),
            Guid.CreateVersion7(), At.UtcDateTime, At.UtcDateTime, 1, Guid.NewGuid(), Actor, null);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboundCommandStager>()
            .StageAsync(header, command, ct));
        return await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().OutboundCommands
            .Where(x => x.ConnectionId == connection.ConnectionId).Select(x => x.OutboundCommandId)
            .SingleAsync(ct);
    }

    private static string ExternalId(InboundAcceptance acceptance) => acceptance.Envelope.ErpExternalId;

    private static InboundAcceptance LateDelivery(InboundAcceptance source)
    {
        var canonical = JsonSerializer.Deserialize<CanonicalMessage<DeliveryPosted>>(
            source.CanonicalMessage, IntegrationContracts.Json)!;
        var line = canonical.Payload.Lines.Single();
        var payload = canonical.Payload with
        {
            DocumentRef = canonical.Payload.DocumentRef with { ExternalId = "FIC-LATE" },
            Lines = [line with { Serials = [new("FIC-LATE")] }],
        };
        var envelope = source.Envelope with
        {
            MessageId = Guid.CreateVersion7(), ErpExternalId = "FIC-LATE", PayloadRef = "fixture:FIC-LATE",
        };
        return new(envelope, nameof(DeliveryPosted),
            JsonSerializer.Serialize(new CanonicalMessage<DeliveryPosted>(envelope,
                nameof(DeliveryPosted), payload), IntegrationContracts.Json),
            "{\"fictional_late_delivery\":true}");
    }

}
