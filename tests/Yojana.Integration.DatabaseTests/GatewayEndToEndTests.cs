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
        Assert.Equal(1, first.UnitsAffected);

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
            Assert.True(result.Translated);
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
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<GatewayTranslationResult>(
            new TranslateInboundMessage(accepted.InboundMessageId), new DeliveryOptions { TenantId = tenant.ToString() });
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
}
