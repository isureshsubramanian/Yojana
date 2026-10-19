using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class DeadLetterTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Rejected_command_raises_prefilled_manual_task_and_operator_mapping_completes_it()
    {
        ResetClock();
        fixture.Connector.Configure(FakeOutboundBehaviour.Reject);
        var tenant = await fixture.NewTenantAsync("manual");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var (commandId, moduleId) = await AddCommandAsync(tenant, connection, fixture.Clock.UtcNow.AddHours(-1));
        using var scope = fixture.ScopeFor(tenant);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, CancellationToken.None)).Parked);

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == commandId);
        Assert.Contains("Fictional repair", task.Draft, StringComparison.Ordinal);
        Assert.Equal("service", task.Desk);
        var actor = Guid.NewGuid().ToString("D");
        var service = scope.ServiceProvider.GetRequiredService<DeadLetterService>();
        var warning = await Assert.ThrowsAsync<DeadLetterRuleException>(() => service
            .CompleteManualTaskAsync(task.ManualOutboundTaskId, new("ERP-MANUAL-1", "v9"), actor,
                CancellationToken.None));
        Assert.Equal("manual_task_external_id_unverified", warning.Rule);
        var completed = await service
            .CompleteManualTaskAsync(task.ManualOutboundTaskId, new("ERP-MANUAL-1", "v9", true), actor,
                CancellationToken.None);
        Assert.Equal(ManualTaskStatus.Completed, completed.Status);
        var command = await db.OutboundCommands.SingleAsync(x => x.OutboundCommandId == commandId);
        Assert.Equal(OutboundCommandStatus.Done, command.Status);
        Assert.Equal(OutboundConnectorOutcome.Manual, command.Outcome);
        var mapping = await db.ExternalIdMaps.SingleAsync(x => x.ExternalId == "ERP-MANUAL-1");
        Assert.Equal(moduleId, mapping.ModuleId);
        Assert.Equal(ExternalMappingSource.Manual, mapping.MappingSource);
        Assert.Equal(actor, mapping.MappedBy);
        Assert.True(await db.DeadLetterEvents.AnyAsync(x => x.DeadLetterItemId == task.DeadLetterItemId
            && x.Action == DeadLetterAction.ManualMapping && x.ActorRef == actor));
    }

    [DatabaseFact]
    public async Task Discard_requires_reason_and_appends_an_event_without_deleting_the_item()
    {
        ResetClock();
        var tenant = await fixture.NewTenantAsync("discard");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var inboundId = await AddInboundAsync(tenant, connection);
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<DeadLetterService>();
        var itemId = await service.ParkInboundAsync(inboundId, "party_not_mapped", "Party mapping failed.",
            CancellationToken.None);
        var refused = await Assert.ThrowsAsync<DeadLetterRuleException>(() => service.DiscardAsync(itemId, " ",
            Guid.NewGuid().ToString("D"), CancellationToken.None));
        Assert.Equal("discard_reason_required", refused.Rule);

        var actor = Guid.NewGuid().ToString("D");
        await service.DiscardAsync(itemId, "Confirmed duplicate source document", actor, CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var item = await db.DeadLetterItems.SingleAsync(x => x.DeadLetterItemId == itemId);
        Assert.Equal(DeadLetterStatus.Discarded, item.Status);
        Assert.True(await db.DeadLetterEvents.AnyAsync(x => x.DeadLetterItemId == itemId
            && x.Action == DeadLetterAction.Discard && x.ActorRef == actor
            && x.Reason == "Confirmed duplicate source document"));
    }

    [DatabaseFact]
    public async Task Connection_maximum_age_override_parks_old_command_and_filtered_list_finds_it()
    {
        ResetClock();
        fixture.Connector.Configure(FakeOutboundBehaviour.Succeed);
        var tenant = await fixture.NewTenantAsync("age");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        await AddCommandAsync(tenant, connection, fixture.Clock.UtcNow.AddDays(-2));
        using (var settingsScope = fixture.ScopeFor(tenant))
            await settingsScope.ServiceProvider.GetRequiredService<ITenantSettings>()
                .SetAsync(OutboxRelaySettings.ConnectionKey(connection), "1", CancellationToken.None);
        using var scope = fixture.ScopeFor(tenant);
        var result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection, CancellationToken.None);
        Assert.Equal(1, result.Parked);
        Assert.Equal(0, fixture.Connector.CreateCallsFor(connection));
        var page = await scope.ServiceProvider.GetRequiredService<DeadLetterService>().ListAsync(
            new(connection, "max_outbox_age", "CreateServiceOrder", null, 1,
                DeadLetterStatus.NeedsAttention, null, null, 1), CancellationToken.None);
        Assert.Single(page.Items);
        Assert.Equal("max_outbox_age", page.Items[0].ReasonCode);
        Assert.Equal(1, page.Summary.CouldNotSend);
        Assert.Equal(1, page.Total);
    }

    [DatabaseFact]
    public async Task Tenant_defined_desks_route_new_tasks_and_are_returned_with_labels()
    {
        ResetClock();
        fixture.Connector.Configure(FakeOutboundBehaviour.Reject);
        var tenant = await fixture.NewTenantAsync("desks");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        await AddCommandAsync(tenant, connection, fixture.Clock.UtcNow.AddHours(-2));
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<ManualTaskDeskSettings>();
        await settings.SetAsync(new([new("operations", "Operations desk")],
            [new("CreateServiceOrder", "operations")], ITenantSettings.MissingVersion), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection, CancellationToken.None);
        var page = await scope.ServiceProvider.GetRequiredService<DeadLetterService>()
            .ManualTasksAsync(ManualTaskStatus.Open, "operations", null, 10, CancellationToken.None);
        var task = Assert.Single(page.Items);
        Assert.Equal("operations", task.Desk);
        var desk = Assert.Single(page.Desks);
        Assert.Equal("Operations desk", desk.Label);
    }

    [DatabaseFact]
    public async Task Mapping_actions_write_audit_and_gateway_mappings_before_scheduling_retry()
    {
        ResetClock();
        var tenant = await fixture.NewTenantAsync("map-act");
        var mappedConnection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var provisionalConnection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var mappedInbound = await AddInboundAsync(tenant, mappedConnection);
        var provisionalInbound = await AddInboundAsync(tenant, provisionalConnection);
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<DeadLetterService>();
        var mappedItem = await service.ParkInboundAsync(mappedInbound, "item_not_mapped", "Item mapping failed.",
            CancellationToken.None);
        var provisionalItem = await service.ParkInboundAsync(provisionalInbound, "party_not_mapped",
            "Party mapping failed.", CancellationToken.None);
        var actor = Guid.NewGuid().ToString("D");
        var moduleId = Guid.NewGuid();
        await service.MapAndRetryAsync(mappedItem, new("item", "ITEM-FIC-1", moduleId), actor,
            CancellationToken.None);
        var provisional = await service.CreateProvisionalAndRetryAsync(provisionalItem,
            new("party", "PARTY-FIC-2"), actor, CancellationToken.None);

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(DeadLetterStatus.RetryPending,
            (await db.DeadLetterItems.SingleAsync(x => x.DeadLetterItemId == mappedItem)).Status);
        Assert.Equal(moduleId, (await db.ExternalIdMaps.SingleAsync(x => x.ConnectionId == mappedConnection
            && x.ExternalId == "ITEM-FIC-1")).ModuleId);
        var provisionalReference = await db.ReferenceProjections.SingleAsync(x =>
            x.ConnectionId == provisionalConnection && x.ExternalId == "PARTY-FIC-2");
        Assert.True(provisionalReference.Provisional);
        Assert.Equal(provisional.ModuleId, provisionalReference.ModuleId);
        Assert.Equal(2, await db.DeadLetterEvents.CountAsync(x =>
            (x.DeadLetterItemId == mappedItem || x.DeadLetterItemId == provisionalItem)
            && x.ActorRef == actor
            && (x.Action == DeadLetterAction.MapAndRetry
                || x.Action == DeadLetterAction.CreateProvisionalAndRetry)));
    }

    private async Task<(Guid CommandId, Guid ModuleId)> AddCommandAsync(TenantId tenant, Guid connection,
        DateTime createdAt)
    {
        var id = TimeOrderedId.New(createdAt);
        var moduleId = Guid.NewGuid();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.OutboundCommands.Add(new OutboundCommandRow
        {
            TenantId = tenant,
            OutboundCommandId = id,
            ConnectionId = connection,
            CommandType = "CreateServiceOrder",
            IdempotencyKey = Guid.NewGuid(),
            ModuleDocumentRef = $"service/{moduleId:N}",
            ErpReferenceText = $"YOJ-{Guid.NewGuid():N}",
            Payload = "{\"customer_ref\":\"FIC-CUSTOMER\",\"problem_description\":\"Fictional repair\"}",
            Status = OutboundCommandStatus.Pending,
            CreatedAt = createdAt,
        });
        await db.SaveChangesAsync();
        return (id, moduleId);
    }

    private async Task<Guid> AddInboundAsync(TenantId tenant, Guid connection)
    {
        var id = TimeOrderedId.New(fixture.Clock.UtcNow.AddDays(-1));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.InboundMessages.Add(new InboundMessage
        {
            TenantId = tenant,
            InboundMessageId = id,
            MessageId = Guid.NewGuid(),
            ConnectionId = connection,
            ConnectorCode = "CX01",
            ErpEntity = "party",
            ErpExternalId = "PARTY-FIC-1",
            ErpChangeMarker = "1",
            MarkerKind = "sequence",
            ContractVersion = "1.0",
            MessageType = "PartyUpserted",
            RawPayload = "{\"name\":\"Fictional Trading\"}",
            CanonicalMessage = "{\"party_external_id\":\"PARTY-FIC-1\"}",
            OccurredAt = fixture.Clock.UtcNow.AddDays(-1),
            ObservedAt = fixture.Clock.UtcNow.AddDays(-1),
            AcceptedAt = fixture.Clock.UtcNow.AddDays(-1),
            IsLatest = true,
            TranslationStatus = InboundTranslationStatus.Pending,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private void ResetClock() => fixture.Clock.UtcNow = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
}
