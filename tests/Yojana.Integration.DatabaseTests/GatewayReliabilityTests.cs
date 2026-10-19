using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class GatewayReliabilityTests(IntegrationHostFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);

    [DatabaseFact]
    public async Task Sweep_recovers_a_committed_acceptance_and_replay_writes_no_second_unit_fact()
    {
        var tenant = await fixture.NewTenantAsync("sweep");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var item = "ITEM-" + Guid.NewGuid().ToString("N")[..8];
        var acceptance = Acceptance(tenant, connection, "item", item, "1",
            new ItemUpserted(item, "Fictional sweep item", OpenEnum.Known("serial"), "EA", false));
        var inboundId = TimeOrderedId.New(fixture.Clock.UtcNow.AddMinutes(-10));
        using (var seed = fixture.ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.InboundMessages.Add(Row(tenant, inboundId, acceptance, fixture.Clock.UtcNow.AddMinutes(-10)));
            db.IdempotencyIndex.Add(new IdempotencyClaim
            {
                TenantId = tenant, ConnectionId = connection, ErpEntity = "item", ErpExternalId = item,
                ErpChangeMarker = "1", InboundMessageId = inboundId, ClaimedAt = fixture.Clock.UtcNow.AddMinutes(-10),
            });
            await db.SaveChangesAsync();
            Assert.Equal(1, await seed.ServiceProvider.GetRequiredService<InboundTranslationRecovery>()
                .SweepAsync(CancellationToken.None));
        }
        await WaitForStatus(tenant, inboundId, InboundTranslationStatus.Completed);
        using (var check = fixture.ScopeFor(tenant))
            Assert.Single(await check.ServiceProvider.GetRequiredService<IntegrationDbContext>().ReferenceProjections
                .Where(x => x.Code == item).ToListAsync());

        var serial = "SER-" + Guid.NewGuid().ToString("N")[..8];
        await Accept(tenant, connection, "receipt", "GR-REPLAY", "1", new GoodsReceiptPosted(
            Doc("receipt", "GR-REPLAY"), "VENDOR", [Line(item, serial)]));
        var receiptId = await LatestId(tenant, "receipt", "GR-REPLAY");
        await WaitForStatus(tenant, receiptId, InboundTranslationStatus.Completed);
        int before;
        using (var check = fixture.ScopeFor(tenant))
            before = await check.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events
                .CountAsync(x => x.EventType == UnitEventType.Released);
        using (var crash = fixture.ScopeFor(tenant))
        {
            var db = crash.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var row = await db.InboundMessages.SingleAsync(x => x.InboundMessageId == receiptId);
            row.TranslationStatus = InboundTranslationStatus.Pending;
            row.TranslatedAt = null;
            row.AcceptedAt = fixture.Clock.UtcNow.AddMinutes(-10);
            row.NextTranslationAttemptAt = fixture.Clock.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
            await crash.ServiceProvider.GetRequiredService<InboundTranslationRecovery>().SweepAsync(CancellationToken.None);
        }
        await WaitForStatus(tenant, receiptId, InboundTranslationStatus.Completed);
        using var final = fixture.ScopeFor(tenant);
        Assert.Equal(before, await final.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events
            .CountAsync(x => x.EventType == UnitEventType.Released));
    }

    [DatabaseFact]
    public async Task Parallel_duplicate_accepts_produce_one_translation()
    {
        var tenant = await fixture.NewTenantAsync("parallel");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var item = "ITEM-" + Guid.NewGuid().ToString("N")[..8];
        var acceptance = Acceptance(tenant, connection, "item", item, "1",
            new ItemUpserted(item, "Fictional concurrent item", OpenEnum.Known("serial"), "EA", false));
        async Task<InboundAcceptanceResult> Run()
        {
            using var scope = fixture.ScopeFor(tenant);
            return await scope.ServiceProvider.GetRequiredService<InboundMessageStore>().Accept(acceptance, CancellationToken.None);
        }
        var results = await Task.WhenAll(Run(), Run());
        Assert.Single(results, x => x.Outcome == InboundAcceptanceResult.Accepted);
        Assert.Single(results, x => x.Outcome == InboundAcceptanceResult.AlreadyProcessed);
        await WaitForStatus(tenant, results[0].InboundMessageId, InboundTranslationStatus.Completed);
        using var check = fixture.ScopeFor(tenant);
        var db = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Single(await db.InboundMessages.Where(x => x.ErpExternalId == item).ToListAsync());
        Assert.Single(await db.ReferenceProjections.Where(x => x.Code == item).ToListAsync());
    }

    [DatabaseFact]
    public async Task Delivery_is_held_until_receipt_then_custody_is_released_in_order()
    {
        var tenant = await fixture.NewTenantAsync("order");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var item = "ITEM-" + Guid.NewGuid().ToString("N")[..8];
        var serial = "SER-" + Guid.NewGuid().ToString("N")[..8];
        await Accept(tenant, connection, "item", item, "1",
            new ItemUpserted(item, "Fictional ordered item", OpenEnum.Known("serial"), "EA", false));
        var delivery = await Accept(tenant, connection, "delivery", "D-ORDER", "1",
            new DeliveryPosted(Doc("delivery", "D-ORDER"), "CUSTOMER", "SITE", [Line(item, serial)]));
        await WaitForStatus(tenant, delivery.InboundMessageId, InboundTranslationStatus.Held);
        var receipt = await Accept(tenant, connection, "receipt", "GR-ORDER", "1",
            new GoodsReceiptPosted(Doc("receipt", "GR-ORDER"), "VENDOR", [Line(item, serial)]));
        await WaitForStatus(tenant, receipt.InboundMessageId, InboundTranslationStatus.Completed);
        await WaitForStatus(tenant, delivery.InboundMessageId, InboundTranslationStatus.Completed);
        using var check = fixture.ScopeFor(tenant);
        var serialisation = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = await serialisation.Identifiers.Where(x => x.RawValue == serial).Select(x => x.UnitId).SingleAsync();
        Assert.Equal(Yojana.Serialisation.Contracts.LocationKind.CustomerSite,
            (await serialisation.Current.SingleAsync(x => x.UnitId == unit)).LocationKind);
        Assert.Equal(1, await serialisation.Events.CountAsync(x => x.UnitId == unit && x.EventType == UnitEventType.Shipped));
    }

    [DatabaseFact]
    public async Task Newer_marker_supersedes_an_older_processing_row()
    {
        var tenant = await fixture.NewTenantAsync("newer");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var item = "ITEM-" + Guid.NewGuid().ToString("N")[..8];
        var old = Acceptance(tenant, connection, "item", item, "1",
            new ItemUpserted(item, "Fictional old name", OpenEnum.Known("serial"), "EA", false));
        var oldId = TimeOrderedId.New(fixture.Clock.UtcNow);
        using (var seed = fixture.ScopeFor(tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var row = Row(tenant, oldId, old, fixture.Clock.UtcNow);
            row.TranslationStatus = InboundTranslationStatus.Processing;
            row.TranslationStartedAt = fixture.Clock.UtcNow;
            db.InboundMessages.Add(row);
            db.IdempotencyIndex.Add(new IdempotencyClaim { TenantId = tenant, ConnectionId = connection,
                ErpEntity = "item", ErpExternalId = item, ErpChangeMarker = "1", InboundMessageId = oldId,
                ClaimedAt = fixture.Clock.UtcNow });
            await db.SaveChangesAsync();
        }
        var newer = await Accept(tenant, connection, "item", item, "2",
            new ItemUpserted(item, "Fictional current name", OpenEnum.Known("serial"), "EA", false));
        await WaitForStatus(tenant, newer.InboundMessageId, InboundTranslationStatus.Completed);
        using var check = fixture.ScopeFor(tenant);
        var db2 = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(InboundTranslationStatus.Superseded,
            await db2.InboundMessages.Where(x => x.InboundMessageId == oldId).Select(x => x.TranslationStatus).SingleAsync());
        Assert.Equal("Fictional current name", await db2.ReferenceProjections.Where(x => x.Code == item)
            .Select(x => x.DisplayName).SingleAsync());
    }

    [DatabaseFact]
    public async Task Two_connections_record_the_same_invoice_but_apply_its_entitlement_once()
    {
        var tenant = await fixture.NewTenantAsync("invoice");
        var legalEntity = Guid.NewGuid();
        var firstConnection = await fixture.AddConnectionAsync(tenant, legalEntity);
        var secondConnection = await fixture.AddConnectionAsync(tenant, legalEntity);
        var coveredItem = Guid.CreateVersion7();
        var warrantyItem = Guid.CreateVersion7();
        var customer = Guid.CreateVersion7();
        var invoice = Guid.CreateVersion7();
        var coveredExternal = "COVERED-" + Guid.NewGuid().ToString("N")[..6];
        var warrantyExternal = "PLAN-" + Guid.NewGuid().ToString("N")[..6];
        var customerExternal = "CUSTOMER-" + Guid.NewGuid().ToString("N")[..6];
        var invoiceExternal = "INV-" + Guid.NewGuid().ToString("N")[..6];
        var serial = "SER-" + Guid.NewGuid().ToString("N")[..8];
        var policyCode = "PLAN" + Guid.NewGuid().ToString("N")[..6];

        using (var setup = fixture.ScopeFor(tenant))
        {
            var db = setup.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            foreach (var connection in new[] { firstConnection, secondConnection })
            {
                Map(db, tenant, connection, "item", coveredExternal, coveredItem);
                Map(db, tenant, connection, "item", warrantyExternal, warrantyItem);
                Map(db, tenant, connection, "party", customerExternal, customer);
                Map(db, tenant, connection, "document", invoiceExternal, invoice);
            }
            db.ReferenceProjections.AddRange(
                new ReferenceProjection
                {
                    TenantId = tenant, EntityType = "item", ModuleId = coveredItem, Code = coveredExternal,
                    DisplayName = "Fictional covered item", TrackingMode = "serial", Uom = "EA", Deleted = false,
                    AttributesJson = JsonSerializer.Serialize(new ItemReferenceAttributes(false, null), IntegrationContracts.Json),
                    UpdatedAt = fixture.Clock.UtcNow,
                },
                new ReferenceProjection
                {
                    TenantId = tenant, EntityType = "item", ModuleId = warrantyItem, Code = warrantyExternal,
                    DisplayName = "Fictional service plan", TrackingMode = "none", Uom = "EA", Deleted = false,
                    AttributesJson = JsonSerializer.Serialize(new ItemReferenceAttributes(true, policyCode), IntegrationContracts.Json),
                    UpdatedAt = fixture.Clock.UtcNow,
                });
            await db.SaveChangesAsync();
        }
        await Invoke<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily("extended_term", "Extended term", true, AssuranceOrService.Service));
        await Invoke<ReplacementPolicyRow>(tenant,
            new DefineReplacementPolicy("RP-EXT", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = new PolicyTerms(new(12, DurationRule.Months), DurationBasis.Time, [],
            new(StartTrigger.PlanPurchase, [], null), new(100, 100, 0, "return_to_base"), [], ["WORLD"],
            Transferability.Yes, "full", "RP-EXT", [], 30, AssuranceOrService.Service, PolicyTerms.TimeZoneUtc);
        var draft = await Invoke<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), policyCode,
            "Fictional service plan", "extended_term", EntitlementType.Extended, new(2020, 1, 1), terms, "test"));
        await Invoke<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var commissioned = await Invoke<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), coveredItem,
            UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            At.AddDays(-1).UtcDateTime, "test", legalEntity));
        var unit = Assert.Single(commissioned.Lines).UnitId!.Value;

        var messageId = Guid.CreateVersion7();
        var payload = new WarrantyLineSold(Doc("invoice", invoiceExternal), customerExternal, [serial], null,
            warrantyExternal, new(12), new(120, "EUR"),
            new(1, "1", warrantyExternal, 1, "EA", [], [], Price: 120, Currency: "EUR"));
        var first = await AcceptShared(firstConnection);
        await WaitForStatus(tenant, first.InboundMessageId, InboundTranslationStatus.Completed);
        var second = await AcceptShared(secondConnection);
        await WaitForStatus(tenant, second.InboundMessageId, InboundTranslationStatus.Completed);

        using var check = fixture.ScopeFor(tenant);
        var serialisation = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Single(await serialisation.Entitlements.Where(x => x.UnitId == unit
            && x.CoverageFamily == "extended_term").ToListAsync());
        var integration = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(2, await integration.InboundMessages.CountAsync(x => x.MessageId == messageId));

        async Task<InboundAcceptanceResult> AcceptShared(Guid connection)
        {
            var envelope = new CanonicalEnvelope("1.0", messageId, tenant.Value, connection, "CX01", "invoice",
                invoiceExternal, new("1", OpenEnum.Known("sequence")), At, At.AddSeconds(1),
                OpenEnum.Known("webhook"), OpenEnum.Known("complete"), $"raw/invoice/{invoiceExternal}/1");
            var canonical = new CanonicalMessage<WarrantyLineSold>(envelope, nameof(WarrantyLineSold), payload);
            using var scope = fixture.ScopeFor(tenant);
            return await scope.ServiceProvider.GetRequiredService<InboundMessageStore>().Accept(new(envelope,
                nameof(WarrantyLineSold), JsonSerializer.Serialize(canonical, IntegrationContracts.Json), "{}"),
                CancellationToken.None);
        }
    }

    private async Task<InboundAcceptanceResult> Accept<T>(TenantId tenant, Guid connection, string entity,
        string external, string marker, T payload)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<InboundMessageStore>()
            .Accept(Acceptance(tenant, connection, entity, external, marker, payload), CancellationToken.None);
    }

    private async Task<T> Invoke<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static void Map(IntegrationDbContext db, TenantId tenant, Guid connection, string type,
        string external, Guid moduleRef) => db.ExternalIdMaps.Add(new()
        {
            TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(), ConnectionId = connection,
            EntityType = type, ExternalId = external, ModuleRef = moduleRef, ValidFrom = At.UtcDateTime,
            MappingSource = ExternalIdMappingSource.Manual,
        });

    private static InboundAcceptance Acceptance<T>(TenantId tenant, Guid connection, string entity,
        string external, string marker, T payload)
    {
        var envelope = new CanonicalEnvelope("1.0", Guid.CreateVersion7(), tenant.Value, connection, "CX01",
            entity, external, new(marker, OpenEnum.Known("sequence")), At, At.AddSeconds(1),
            OpenEnum.Known("webhook"), OpenEnum.Known("complete"), $"raw/{entity}/{external}/{marker}");
        var message = new CanonicalMessage<T>(envelope, typeof(T).Name, payload);
        return new(envelope, typeof(T).Name, JsonSerializer.Serialize(message, IntegrationContracts.Json), "{}");
    }

    private static InboundMessage Row(TenantId tenant, Guid id, InboundAcceptance acceptance, DateTime acceptedAt) => new()
    {
        TenantId = tenant, InboundMessageId = id, MessageId = acceptance.Envelope.MessageId,
        ConnectionId = acceptance.Envelope.ConnectionId, ConnectorCode = acceptance.Envelope.ConnectorCode,
        ErpEntity = acceptance.Envelope.ErpEntity, ErpExternalId = acceptance.Envelope.ErpExternalId,
        ErpChangeMarker = acceptance.Envelope.ErpChangeMarker.Value, MarkerKind = "sequence", ContractVersion = "1.0",
        MessageType = acceptance.MessageType, RawPayload = acceptance.RawPayload,
        CanonicalMessage = acceptance.CanonicalMessage, OccurredAt = acceptance.Envelope.OccurredAt.UtcDateTime,
        ObservedAt = acceptance.Envelope.ObservedAt.UtcDateTime, AcceptedAt = acceptedAt, IsLatest = true,
        TranslationStatus = InboundTranslationStatus.Pending, NextTranslationAttemptAt = acceptedAt,
    };

    private async Task<Guid> LatestId(TenantId tenant, string entity, string external)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages
            .Where(x => x.ErpEntity == entity && x.ErpExternalId == external && x.IsLatest)
            .Select(x => x.InboundMessageId).SingleAsync();
    }

    private async Task WaitForStatus(TenantId tenant, Guid id, string expected)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var scope = fixture.ScopeFor(tenant);
            var status = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages.AsNoTracking()
                .Where(x => x.InboundMessageId == id).Select(x => x.TranslationStatus).SingleAsync();
            if (status == expected) return;
            await Task.Delay(20);
        }
        using var final = fixture.ScopeFor(tenant);
        var row = await final.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages.AsNoTracking()
            .SingleAsync(x => x.InboundMessageId == id);
        throw new TimeoutException($"Inbound message {id:N} did not reach {expected}; status={row.TranslationStatus}; error={row.LastTranslationError}.");
    }

    private static ExternalDocumentRef Doc(string type, string number) =>
        new(type, number, DateOnly.FromDateTime(At.UtcDateTime), number);
    private static CanonicalLine Line(string item, string serial) =>
        new(1, "1", item, 1, "EA", [new(serial)], [], "WH-1");
}
