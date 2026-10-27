using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Outbox;
using Yojana.Integration.Ordering;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class GatewayEndToEndTests(IntegrationHostFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [DatabaseFact]
    public async Task Canonical_delivery_moves_custody_once_and_cancellation_appends_a_reversal()
    {
        var tenant = await fixture.NewTenantAsync("gateway");
        var legalEntity = Guid.NewGuid();
        var connection = await fixture.AddConnectionAsync(tenant, legalEntity);
        var itemExternal = "ITEM-" + Guid.NewGuid().ToString("N")[..6];
        var serial = "FIC-" + Guid.NewGuid().ToString("N")[..8];

        await AcceptAndTranslate(tenant, connection, "item", itemExternal, "001", new ItemUpserted(itemExternal,
            "Fictional controller", OpenEnum.Known("serial"), "EA", false));
        await AcceptAndTranslate(tenant, connection, "receipt", "GR-1", "001", new GoodsReceiptPosted(
            Doc("receipt", "GR-1"), "VENDOR-1", [Line(itemExternal, serial, "WH-1")]));
        var delivery = new DeliveryPosted(Doc("delivery", "D-1"), "CUSTOMER-1", "SITE-1",
            [Line(itemExternal, serial, "WH-1")]);
        var first = await AcceptAndTranslate(tenant, connection, "delivery", "D-1", "001", delivery);
        Assert.True(first.Translated || await TranslationStatusAsync(tenant, "delivery", "D-1") == InboundTranslationStatus.Completed);

        var duplicate = await Accept(tenant, connection, "delivery", "D-1", "001", delivery);
        Assert.Equal(InboundAcceptanceResult.AlreadyProcessed, duplicate.Outcome);

        using (var check = fixture.ScopeFor(tenant))
        {
            var serialisation = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var unit = await serialisation.Identifiers.Where(x => x.RawValue == serial).Select(x => x.UnitId).SingleAsync();
            var current = await serialisation.Current.SingleAsync(x => x.UnitId == unit);
            Assert.Equal(LocationKind.CustomerSite, current.LocationKind);
            Assert.Equal(1, await serialisation.Events.CountAsync(x => x.UnitId == unit && x.EventType == UnitEventType.Shipped));
        }

        await AcceptAndTranslate(tenant, connection, "delivery", "D-1", "002",
            new DocumentCancelled(Doc("delivery", "D-1"), OpenEnum.Known("cancelled")), At.AddDays(1));
        using var final = fixture.ScopeFor(tenant);
        var db = final.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var finalUnit = await db.Identifiers.Where(x => x.RawValue == serial).Select(x => x.UnitId).SingleAsync();
        var reversal = await db.Events.SingleAsync(x => x.UnitId == finalUnit && x.EventType == UnitEventType.ShipmentReversed);
        Assert.NotNull(reversal.ReversesEventId);
        Assert.Equal(LocationKind.Warehouse, (await db.Current.SingleAsync(x => x.UnitId == finalUnit)).LocationKind);
    }

    [DatabaseFact]
    public async Task Canonical_component_issue_is_held_once_and_the_production_receipt_links_it_to_the_parent()
    {
        var tenant = await fixture.NewTenantAsync("cmpissue");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var componentItem = "FIC-NIC-" + Guid.NewGuid().ToString("N")[..6];
        var parentItem = "FIC-SRV-" + Guid.NewGuid().ToString("N")[..6];
        var componentSerial = "NIC-" + Guid.NewGuid().ToString("N")[..10];
        var parentSerial = "SRV-" + Guid.NewGuid().ToString("N")[..10];
        await AcceptAndTranslate(tenant, connection, "item", componentItem, "001", new ItemUpserted(
            componentItem, "Fictional network adapter", OpenEnum.Known("serial"), "EA", false));
        await AcceptAndTranslate(tenant, connection, "item", parentItem, "001", new ItemUpserted(
            parentItem, "Fictional rack server", OpenEnum.Known("serial"), "EA", false));
        await AcceptAndTranslate(tenant, connection, "bom", parentItem, "001", new BomUpserted(parentItem, At,
            false, [new BomUpsertedLine(1, OpenEnum.Known("item"), componentItem,
                "Fictional network adapter", 1, "EA")]));
        await AcceptAndTranslate(tenant, connection, "receipt", "GR-COMP-1", "001", new GoodsReceiptPosted(
            Doc("goods_receipt", "GR-COMP-1"), "FIC-VENDOR-1", [Line(componentItem, componentSerial, "WH-1")]));
        var order = Doc("production_order", "PO-1001");
        var issuedLine = Line(componentItem, componentSerial, "WH-1") with
        {
            BaseDocumentRef = order with { Type = "202", Line = 1 },
        };
        var issued = new ComponentsIssued(Doc("component_issue", "GI-1001"), order, [issuedLine]);

        await AcceptAndTranslate(tenant, connection, "component_issue", "GI-1001", "001", issued,
            At.AddHours(1));
        var duplicate = await Accept(tenant, connection, "component_issue", "GI-1001", "001", issued,
            At.AddHours(1));
        Assert.Equal(InboundAcceptanceResult.AlreadyProcessed, duplicate.Outcome);
        using (var held = fixture.ScopeFor(tenant))
        {
            var serialisation = held.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Single(await serialisation.ProductionComponentHolds.Where(row => row.LinkedAt == null).ToListAsync());
            var component = await serialisation.Identifiers.Where(row => row.RawValue == componentSerial)
                .Select(row => row.UnitId).SingleAsync();
            Assert.Equal(LocationKind.InProduction,
                (await serialisation.Current.SingleAsync(row => row.UnitId == component)).LocationKind);
        }

        var producedLine = Line(parentItem, parentSerial, "WH-1") with { BaseDocumentRef = order };
        await AcceptAndTranslate(tenant, connection, "production_receipt", "PR-1001", "001",
            new ProductionCompleted(Doc("production_receipt", "PR-1001"), order, parentItem,
                [producedLine], []), At.AddHours(2));

        using var final = fixture.ScopeFor(tenant);
        var db = final.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var parent = await db.Identifiers.Where(row => row.RawValue == parentSerial)
            .Select(row => row.UnitId).SingleAsync();
        Assert.Single(await db.AssemblyLinks.Where(row => row.ParentUnitId == parent && row.RemovedAt == null)
            .ToListAsync());
        var hold = await db.ProductionComponentHolds.SingleAsync();
        Assert.Equal(parent, hold.ParentUnitId);
        Assert.NotNull(hold.LinkedAt);
    }

    [DatabaseFact]
    public async Task Cancellation_waits_for_its_original_then_reverses_the_receipt()
    {
        var tenant = await fixture.NewTenantAsync("cancelwait");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var itemExternal = "HW-SERVER-" + Guid.NewGuid().ToString("N")[..6];
        var serial = "SRV-CANCEL-" + Guid.NewGuid().ToString("N")[..8];
        var document = Doc("receipt", "GR-WAIT-1");
        await AcceptAndTranslate(tenant, connection, "item", itemExternal, "001", new ItemUpserted(itemExternal,
            "Fictional rack server", OpenEnum.Known("serial"), "EA", false));
        var cancellation = await Accept(tenant, connection, "receipt-cancellation", "GR-CANCEL-1", "001",
            new DocumentCancelled(document, OpenEnum.Known("cancelled")), At.AddHours(1));
        // Acceptance also queues a background translation, so the direct call may find the message already held;
        // the held status is the assertion.
        using (var hold = fixture.ScopeFor(tenant))
            _ = await hold.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<GatewayTranslationResult>(
                new TranslateInboundMessage(cancellation.InboundMessageId),
                new DeliveryOptions { TenantId = tenant.ToString() });
        await WaitForStatusAsync(tenant, cancellation.InboundMessageId, InboundTranslationStatus.Held);

        await AcceptAndTranslate(tenant, connection, "receipt", "GR-WAIT-1", "001", new GoodsReceiptPosted(
            document, "FIC-VENDOR-1", [Line(itemExternal, serial, "WH-1")]), At);
        await WaitForStatusAsync(tenant, cancellation.InboundMessageId, InboundTranslationStatus.Completed);

        using var check = fixture.ScopeFor(tenant);
        var serialisation = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = await serialisation.Identifiers.Where(x => x.RawValue == serial).Select(x => x.UnitId).SingleAsync();
        Assert.Equal(LifecycleStatus.Reversed,
            (await serialisation.Current.SingleAsync(x => x.UnitId == unit)).LifecycleStatus);
        Assert.Equal(1, await serialisation.Events.CountAsync(x => x.UnitId == unit
            && x.EventType == UnitEventType.ReceiptReversed));
    }

    [DatabaseFact]
    public async Task Cancellation_delivered_again_after_it_was_applied_completes_without_waiting()
    {
        var tenant = await fixture.NewTenantAsync("cancelagain");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var itemExternal = "HW-SWITCH-" + Guid.NewGuid().ToString("N")[..6];
        var serial = "SW-CANCEL-" + Guid.NewGuid().ToString("N")[..8];
        var document = Doc("receipt", "GR-AGAIN-1");
        await AcceptAndTranslate(tenant, connection, "item", itemExternal, "001", new ItemUpserted(itemExternal,
            "Fictional network switch", OpenEnum.Known("serial"), "EA", false));
        await AcceptAndTranslate(tenant, connection, "receipt", "GR-AGAIN-1", "001", new GoodsReceiptPosted(
            document, "FIC-VENDOR-3", [Line(itemExternal, serial, "WH-3")]), At);
        await AcceptAndTranslate(tenant, connection, "receipt-cancellation", "GR-CANCEL-3", "001",
            new DocumentCancelled(document, OpenEnum.Known("cancelled")), At.AddHours(1));

        var again = await AcceptAndTranslate(tenant, connection, "receipt-cancellation", "GR-CANCEL-3", "002",
            new DocumentCancelled(document, OpenEnum.Known("cancelled")), At.AddHours(2));

        await WaitForStatusAsync(tenant, again.InboundMessageId, InboundTranslationStatus.Completed);
        using var check = fixture.ScopeFor(tenant);
        var serialisation = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = await serialisation.Identifiers.Where(x => x.RawValue == serial).Select(x => x.UnitId).SingleAsync();
        Assert.Equal(LifecycleStatus.Reversed,
            (await serialisation.Current.SingleAsync(x => x.UnitId == unit)).LifecycleStatus);
        Assert.Equal(1, await serialisation.Events.CountAsync(x => x.UnitId == unit
            && x.EventType == UnitEventType.ReceiptReversed));
        Assert.False(await check.ServiceProvider.GetRequiredService<IntegrationDbContext>().ResequenceItems
            .AnyAsync(x => x.ConnectionId == connection));
    }

    [DatabaseFact]
    public async Task Original_arriving_after_its_cancellation_expired_is_completed_without_application()
    {
        fixture.Clock.UtcNow = At.AddDays(5).UtcDateTime;
        var tenant = await fixture.NewTenantAsync("cancelfirst");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var itemExternal = "HW-STORAGE-" + Guid.NewGuid().ToString("N")[..6];
        var serial = "ARRAY-CANCEL-" + Guid.NewGuid().ToString("N")[..8];
        var document = Doc("receipt", "GR-LATE-1");
        await AcceptAndTranslate(tenant, connection, "item", itemExternal, "001", new ItemUpserted(itemExternal,
            "Fictional storage array", OpenEnum.Known("serial"), "EA", false));
        var cancellation = await Accept(tenant, connection, "receipt-cancellation", "GR-CANCEL-2", "001",
            new DocumentCancelled(document, OpenEnum.Known("cancelled")), At.AddHours(1));
        using (var hold = fixture.ScopeFor(tenant))
            _ = await hold.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<GatewayTranslationResult>(
                new TranslateInboundMessage(cancellation.InboundMessageId),
                new DeliveryOptions { TenantId = tenant.ToString() });
        await WaitForStatusAsync(tenant, cancellation.InboundMessageId, InboundTranslationStatus.Held);
        using (var expire = fixture.ScopeFor(tenant))
        {
            var db = expire.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var wait = await db.ResequenceItems.SingleAsync(x => x.InboundMessageId == cancellation.InboundMessageId);
            wait.HoldUntil = fixture.Clock.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            Assert.Equal(1, await expire.ServiceProvider.GetRequiredService<BoundedWaitResequencer>()
                .ReleaseExpiredAsync(CancellationToken.None));
        }
        await WaitForStatusAsync(tenant, cancellation.InboundMessageId, InboundTranslationStatus.Completed);

        var original = await AcceptAndTranslate(tenant, connection, "receipt", "GR-LATE-1", "001",
            new GoodsReceiptPosted(document, "FIC-VENDOR-2", [Line(itemExternal, serial, "WH-2")]), At.AddDays(2));
        await WaitForStatusAsync(tenant, original.InboundMessageId, InboundTranslationStatus.Completed);
        using var check = fixture.ScopeFor(tenant);
        Assert.False(await check.ServiceProvider.GetRequiredService<SerialisationDbContext>().Identifiers
            .AnyAsync(x => x.RawValue == serial));
        var integration = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(1, await integration.ExceptionTasks.CountAsync(x =>
            x.InboundMessageId == cancellation.InboundMessageId && x.Kind == "cancellation_original_missing"));
        var originalRow = await integration.InboundMessages.SingleAsync(x => x.ErpExternalId == "GR-LATE-1");
        Assert.Equal(InboundTranslationStatus.Completed, originalRow.TranslationStatus);
        Assert.Equal(1, await integration.ExceptionTasks.CountAsync(x =>
            x.InboundMessageId == originalRow.InboundMessageId && x.Kind == "cancelled_before_applied"));
    }

    [DatabaseFact]
    public async Task Late_delivery_with_an_unknown_serial_creates_a_provisional_unit_and_exception_task()
    {
        var tenant = await fixture.NewTenantAsync("provisional");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var itemExternal = "ITEM-" + Guid.NewGuid().ToString("N")[..6];
        var serial = "FIC-LATE-" + Guid.NewGuid().ToString("N")[..8];
        await AcceptAndTranslate(tenant, connection, "item", itemExternal, "001", new ItemUpserted(itemExternal,
            "Fictional late item", OpenEnum.Known("serial"), "EA", false));
        var accepted = await Accept(tenant, connection, "delivery", "D-LATE", "001",
            new DeliveryPosted(Doc("delivery", "D-LATE"), "CUSTOMER-LATE", "SITE-LATE",
                [Line(itemExternal, serial, "WH-LATE")]));

        await WaitForStatusAsync(tenant, accepted.InboundMessageId, InboundTranslationStatus.Held);

        using (var markLate = fixture.ScopeFor(tenant))
        {
            var integration = markLate.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var row = await integration.InboundMessages.SingleAsync(x => x.InboundMessageId == accepted.InboundMessageId);
            row.TranslationStatus = InboundTranslationStatus.Late;
            await integration.SaveChangesAsync();
        }
        using (var translate = fixture.ScopeFor(tenant))
        {
            var result = await translate.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<GatewayTranslationResult>(
                new TranslateInboundMessage(accepted.InboundMessageId), new DeliveryOptions { TenantId = tenant.ToString() });
            if (!result.Translated)
                await WaitForStatusAsync(tenant, accepted.InboundMessageId, InboundTranslationStatus.Completed);
        }
        using var check = fixture.ScopeFor(tenant);
        var serialisation = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var identifier = await serialisation.Identifiers.SingleAsync(x => x.RawValue == serial);
        Assert.Equal(IdentifierType.Provisional, identifier.Type);
        var integrationDb = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.True(await integrationDb.ExceptionTasks.AnyAsync(x => x.InboundMessageId == accepted.InboundMessageId
            && x.Kind == "provisional_unit"));
    }

    [DatabaseFact]
    public async Task Foreign_invoice_without_a_local_amount_waits_for_a_person()
    {
        var tenant = await fixture.NewTenantAsync("fxamount");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var item = "HW-STORAGE-" + Guid.NewGuid().ToString("N")[..6];
        var invoice = new InvoicePosted(Doc("invoice", "INV-FX-1"), "CUSTOMER-1", [],
            [new CanonicalLine(1, "1", item, 1m, "EA", [], [], Price: 120m, Currency: "USD")],
            new Money(120m, "USD"));
        var accepted = await Accept(tenant, connection, "invoice", "INV-FX-1", "001", invoice);
        await WaitForStatusAsync(tenant, accepted.InboundMessageId, InboundTranslationStatus.NeedsAttention);

        using var check = fixture.ScopeFor(tenant);
        var row = await check.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages
            .SingleAsync(value => value.InboundMessageId == accepted.InboundMessageId);
        Assert.Equal(InboundTranslationStatus.NeedsAttention, row.TranslationStatus);
        Assert.Equal("currency_not_home", row.AttentionReasonCode);
        Assert.Contains("amount in USD; the ERP's EUR amount is needed", row.LastTranslationError);
        Assert.True(await check.ServiceProvider.GetRequiredService<IntegrationDbContext>().ExceptionTasks
            .AnyAsync(value => value.InboundMessageId == accepted.InboundMessageId
                && value.Kind == "currency_not_home"));
    }

    [DatabaseFact]
    public async Task Published_replacement_event_stages_one_idempotent_canonical_command()
    {
        var tenant = await fixture.NewTenantAsync("outbound");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var eventId = Guid.CreateVersion7();
        var unit = Guid.CreateVersion7();
        var header = new PublishedEventHeader(ReplacementShipped.Schema, ReplacementShipped.Version, eventId, unit,
            At.UtcDateTime, At.AddSeconds(1).UtcDateTime, 1, Guid.NewGuid(), "integration-test", null);
        var message = new ReplacementShipped(header, Guid.NewGuid(), "REP-1", Guid.NewGuid(), Guid.NewGuid(), unit,
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "claim", Guid.NewGuid(), "CLAIM-1",
            "approved replacement", "zero", 0, "GBP", "standard", new DateOnly(2027, 9, 1), "new", false, 0);
        using var scope = fixture.ScopeFor(tenant);
        var stager = scope.ServiceProvider.GetRequiredService<OutboundCommandStager>();
        Assert.Equal(1, await PublishedEventOutboundHandler.Handle(message, stager, CancellationToken.None));
        Assert.Equal(0, await PublishedEventOutboundHandler.Handle(message, stager, CancellationToken.None));
        var row = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().OutboundCommands.SingleAsync();
        Assert.Equal(connection, row.ConnectionId);
        Assert.Equal(nameof(CreateReplacementDelivery), row.CommandType);
        var command = JsonSerializer.Deserialize<CreateReplacementDelivery>(row.Payload, IntegrationContracts.Json);
        Assert.NotNull(command);
        Assert.Equal(OutboundCommandStager.Derive(eventId, nameof(CreateReplacementDelivery)), command.IdempotencyKey);
        Assert.False(string.IsNullOrWhiteSpace(command.ModuleDocumentRef));
        Assert.False(string.IsNullOrWhiteSpace(command.ErpReferenceText));
    }

    [DatabaseFact]
    public async Task Unsupported_outbound_capability_raises_a_prefilled_manual_task()
    {
        fixture.Clock.UtcNow = new DateTime(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        var tenant = await fixture.NewTenantAsync("unsupported");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var capability = await db.ConnectionCapabilities.SingleAsync(x => x.ConnectionId == connection
            && x.CanonicalMessage == nameof(CreateServiceOrder));
        capability.SupportLevel = CapabilitySupport.Unsupported;
        await db.SaveChangesAsync();
        var eventId = Guid.NewGuid();
        var header = new PublishedEventHeader("test.unsupported", "1.0", eventId, Guid.NewGuid(),
            fixture.Clock.UtcNow, fixture.Clock.UtcNow, 1, Guid.NewGuid(), "integration-test", null);
        var moduleId = Guid.NewGuid();
        var command = new CreateServiceOrder(Guid.NewGuid(), $"service/{moduleId:N}",
            $"YOJ-{eventId:N}", "FIC-CUSTOMER", new(Guid.NewGuid(), "FIC-UNIT"), "approved",
            "Fictional repair");

        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboundCommandStager>()
            .StageAsync(header, command, CancellationToken.None));
        var row = await db.OutboundCommands.SingleAsync(x => x.ConnectionId == connection);
        Assert.Equal(OutboundCommandStatus.Parked, row.Status);
        var manual = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == row.OutboundCommandId);
        Assert.Contains("Fictional repair", manual.Draft, StringComparison.Ordinal);
    }

    private async Task<GatewayTranslationResult> AcceptAndTranslate<T>(TenantId tenant, Guid connection, string entity,
        string external, string marker, T payload, DateTimeOffset? at = null)
    {
        var accepted = await Accept(tenant, connection, entity, external, marker, payload, at);
        using var scope = fixture.ScopeFor(tenant);
        var result = await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<GatewayTranslationResult>(
            new TranslateInboundMessage(accepted.InboundMessageId), new DeliveryOptions { TenantId = tenant.ToString() });
        if (!result.Translated)
            await WaitForStatusAsync(tenant, accepted.InboundMessageId, InboundTranslationStatus.Completed);
        return result;
    }

    private async Task<InboundAcceptanceResult> Accept<T>(TenantId tenant, Guid connection, string entity,
        string external, string marker, T payload, DateTimeOffset? at = null)
    {
        var envelope = new CanonicalEnvelope("1.0", Guid.CreateVersion7(), tenant.Value, connection, "CX01", entity, external,
            new(marker, OpenEnum.Known("sequence")), at ?? At, (at ?? At).AddSeconds(1), OpenEnum.Known("poll"),
            OpenEnum.Known("complete"), $"raw/{entity}/{external}/{marker}");
        var message = new CanonicalMessage<T>(envelope, typeof(T).Name, payload);
        var json = JsonSerializer.Serialize(message, IntegrationContracts.Json);
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<InboundMessageStore>().Accept(
            new(envelope, typeof(T).Name, json, "{\"raw\":true}"), CancellationToken.None);
    }

    private static ExternalDocumentRef Doc(string type, string number) => new(type, number, DateOnly.FromDateTime(At.UtcDateTime), number);
    private static CanonicalLine Line(string item, string serial, string location) => new(1, "1", item, 1, "EA",
        [new(serial)], [], location);

    private async Task<string> TranslationStatusAsync(TenantId tenant, string entity, string external)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages.AsNoTracking()
            .Where(x => x.ErpEntity == entity && x.ErpExternalId == external && x.IsLatest)
            .Select(x => x.TranslationStatus).SingleAsync();
    }

    private async Task WaitForStatusAsync(TenantId tenant, Guid inboundId, string expected)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var scope = fixture.ScopeFor(tenant);
            var status = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages.AsNoTracking()
                .Where(x => x.InboundMessageId == inboundId).Select(x => x.TranslationStatus).SingleAsync();
            if (status == expected) return;
            await Task.Delay(20);
        }
        using var final = fixture.ScopeFor(tenant);
        var row = await final.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages.AsNoTracking()
            .SingleAsync(x => x.InboundMessageId == inboundId);
        throw new TimeoutException($"Inbound message {inboundId:N} did not reach {expected}; status={row.TranslationStatus}; error={row.LastTranslationError}.");
    }
}
