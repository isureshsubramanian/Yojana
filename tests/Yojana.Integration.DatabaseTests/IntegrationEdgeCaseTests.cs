using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Ordering;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Webhooks;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed partial class IntegrationEdgeCaseTests(IntegrationHostFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private const string Item = "FIC-CONTROLLER";
    private const string Serial = "FIC-EDGE-001";
    private const string Actor = "user:edge-operator";

    [DatabaseFact]
    public async Task Duplicate_signed_notification_before_and_after_processing_has_one_effect()
    {
        // ADR-0002 AI 15: duplicate webhook delivery, including replay after processing.
        var (tenant, connection) = await GenericAsync();
        await PollAsync(tenant, connection, "item", Item, new ItemUpserted(Item, "Fictional controller", OpenEnum.Known("serial"), "EA", false));
        var secret = RandomNumberGenerator.GetBytes(32);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.ConnectionNotificationSources.Add(new() { TenantId = tenant, ConnectionId = connection, Source = "edge", Enabled = true, UpdatedAt = fixture.Clock.UtcNow });
            await db.SaveChangesAsync();
            var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
            await scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>().SaveAsync(await protector.EncryptAsync(tenant, connection,
                IntegrationNotificationEndpoints.SigningCredentialPurpose, secret, null, DateTime.UtcNow.AddDays(30), default), default);
        }
        var acceptance = Acceptance(tenant, connection, "delivery", "D-1", new DeliveryPosted(Doc("delivery", "D-1"), "FIC-CUSTOMER", "FIC-SITE", [Line()]));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new WebhookNotificationBatch("edge", [new(
            JsonDocument.Parse(acceptance.CanonicalMessage).RootElement.Clone(), JsonSerializer.SerializeToElement(new { fictional = true }))]), IntegrationContracts.Json);
        await using var host = await NotificationEndpointTests.WebProcess.StartAsync();
        using var client = new HttpClient { BaseAddress = host.BaseAddress };
        var timestamp = DateTime.UtcNow;
        async Task<WebhookNotificationResponse> Send()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/integration/connections/{connection}/notifications") { Content = new ByteArrayContent(bytes) };
            request.Content.Headers.ContentType = new("application/json");
            request.Headers.Add(IntegrationNotificationEndpoints.TenantHeader, tenant.ToString());
            request.Headers.Add(IntegrationNotificationEndpoints.TimestampHeader, timestamp.ToString("O"));
            request.Headers.Add(IntegrationNotificationEndpoints.SignatureHeader, Convert.ToHexString(HmacSha256WebhookSignatureVerifier.Sign(
                IntegrationNotificationEndpoints.SignedContent(timestamp, bytes), secret)));
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            return (await response.Content.ReadFromJsonAsync<WebhookNotificationResponse>())!;
        }
        Assert.Equal(1, (await Send()).Accepted);
        await WaitAsync(tenant, "D-1", InboundTranslationStatus.Held);
        var repeat = await Send();
        Assert.True(repeat.AlreadyProcessed == 1, JsonSerializer.Serialize(repeat) + "\n" + host.Details());
        await PollAsync(tenant, connection, "receipt", "GR-1", new GoodsReceiptPosted(Doc("receipt", "GR-1"), "FIC-SUPPLIER", [Line()]));
        await WaitAsync(tenant, "D-1", InboundTranslationStatus.Completed);
        Assert.Equal(1, (await Send()).AlreadyProcessed);
        using var check = fixture.ScopeFor(tenant);
        var integration = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(1, await integration.InboundMessages.CountAsync(x => x.ErpExternalId == "D-1"));
        Assert.Equal(1, await integration.IdempotencyIndex.CountAsync(x => x.ErpExternalId == "D-1"));
        var serialisation = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(1, await serialisation.Units.CountAsync());
        Assert.Equal(1, await serialisation.Events.CountAsync(x => x.EventType == UnitEventType.Shipped));
        Assert.Equal(1, await serialisation.Episodes.CountAsync(x => x.Kind == EpisodeKind.Ownership));
    }

    [DatabaseFact]
    public async Task Receipt_releases_held_delivery_and_timeout_uses_the_provisional_late_path()
    {
        // ADR-0002 AI 15 / Reliability 7: receipt-before-delivery and bounded late-event fallback.
        var (tenant, connection) = await GenericAsync();
        await PollAsync(tenant, connection, "item", Item, new ItemUpserted(Item, "Fictional controller", OpenEnum.Known("serial"), "EA", false));
        var held = await PollAsync(tenant, connection, "delivery", "D-ORDER", new DeliveryPosted(Doc("delivery", "D-ORDER"), "FIC-CUSTOMER", "FIC-SITE", [Line()]), InboundTranslationStatus.Held);
        using (var check = fixture.ScopeFor(tenant))
        {
            var db = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var hold = await db.ResequenceItems.SingleAsync(x => x.InboundMessageId == held);
            Assert.Equal(TimeSpan.FromMinutes(15), hold.HoldUntil - hold.HeldAt);
            Assert.Equal(0, await check.ServiceProvider.GetRequiredService<SerialisationDbContext>().Units.CountAsync());
            Assert.Equal(0, await check.ServiceProvider.GetRequiredService<BoundedWaitResequencer>().ReleaseExpiredAsync(default));
        }
        await PollAsync(tenant, connection, "receipt", "GR-ORDER", new GoodsReceiptPosted(Doc("receipt", "GR-ORDER"), "FIC-SUPPLIER", [Line()]), at: At.AddMinutes(-1));
        await WaitAsync(tenant, "D-ORDER", InboundTranslationStatus.Completed);
        using (var check = fixture.ScopeFor(tenant))
        {
            var db = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(LocationKind.CustomerSite, (await db.Current.SingleAsync()).LocationKind);
            var events = await db.Events.OrderBy(x => x.EventTime).ThenBy(x => x.RecordedTime).ThenBy(x => x.Seq).ToListAsync();
            Assert.True(events.FindIndex(x => x.EventType == UnitEventType.Released) < events.FindIndex(x => x.EventType == UnitEventType.Shipped));
        }
        var late = await PollAsync(tenant, connection, "delivery", "D-LATE", new DeliveryPosted(Doc("delivery", "D-LATE"), "FIC-CUSTOMER", "FIC-SITE", [Line("FIC-LATE")]), InboundTranslationStatus.Held);
        fixture.Clock.UtcNow += TimeSpan.FromMinutes(16);
        using (var release = fixture.ScopeFor(tenant))
            Assert.Equal(1, await release.ServiceProvider.GetRequiredService<BoundedWaitResequencer>().ReleaseExpiredAsync(default));
        await WaitAsync(tenant, "D-LATE", InboundTranslationStatus.Completed);
        using var final = fixture.ScopeFor(tenant);
        var integration = final.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.True((await integration.ResequenceItems.SingleAsync(x => x.InboundMessageId == late)).Late);
        Assert.Single(await integration.ExceptionTasks.Where(x => x.InboundMessageId == late && x.Kind == "provisional_unit").ToListAsync());
        var serials = final.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var identifier = await serials.Identifiers.SingleAsync(x => x.RawValue == "FIC-LATE");
        Assert.Equal(IdentifierType.Provisional, identifier.Type);
        var opening = await serials.Events.SingleAsync(x => x.UnitId == identifier.UnitId && x.EventType == UnitEventType.Commissioned);
        Assert.Equal(At.AddMilliseconds(-1).UtcDateTime, opening.EventTime);
        Assert.Equal(fixture.Clock.UtcNow, opening.RecordedTime);
        Assert.True(await integration.ExceptionTasks.AnyAsync(x => x.InboundMessageId == late && x.Kind == "consumed_event_exception"));
    }

    [DatabaseFact]
    public async Task Repeated_serial_in_one_remote_receipt_creates_one_unit_and_a_review_task()
    {
        // ADR-0002 AI 15 / degradation 6: non-validating source repeats a serial, never a second unit.
        var (tenant, connection) = await GenericAsync();
        await PollAsync(tenant, connection, "item", Item, new ItemUpserted(Item, "Fictional controller", OpenEnum.Known("serial"), "EA", false));
        var inbound = await PollAsync(tenant, connection, "receipt", "GR-DOUBLE", new GoodsReceiptPosted(Doc("receipt", "GR-DOUBLE"), "FIC-SUPPLIER",
            [Line() with { Quantity = 2, Serials = [new(Serial), new(Serial)] }]));
        using var scope = fixture.ScopeFor(tenant);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Units.CountAsync());
        var task = Assert.Single(await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ExceptionTasks.Where(x => x.InboundMessageId == inbound && x.Kind == "duplicate_serial").ToListAsync());
        Assert.Contains("serial", task.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("open", task.Status);
        var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var retained = await integration.InboundMessages.SingleAsync(x => x.InboundMessageId == inbound);
        Assert.Equal(2, JsonSerializer.Deserialize<GoodsReceiptPosted>(retained.RawPayload, IntegrationContracts.Json)!.Lines.Single().Serials.Count);
        var projected = await integration.CanonicalDocuments.SingleAsync(x => x.SourceInboundMessageId == inbound);
        var receipt = JsonSerializer.Deserialize<GoodsReceiptPosted>(projected.Payload, IntegrationContracts.Json)!;
        Assert.Single(receipt.Lines.Single().Serials);
        Assert.Equal(2, receipt.Lines.Single().Quantity);
    }

    [DatabaseFact]
    public async Task Overlapping_connections_share_a_mapped_invoice_without_reapplying_the_business_fact()
    {
        // ADR-0002 AI 15 / dual-run rule: incumbent document wins; overlapping mapped fact is retained and diffed.
        var (tenant, first) = await GenericAsync();
        var second = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        await PollAsync(tenant, first, "item", Item, new ItemUpserted(Item, "Fictional controller", OpenEnum.Known("serial"), "EA", false));
        await PollAsync(tenant, first, "receipt", "GR-1", new GoodsReceiptPosted(Doc("receipt", "GR-1"), "FIC-SUPPLIER", [Line()]));
        var invoice = new InvoicePosted(Doc("invoice", "INV-1"), "FIC-CUSTOMER", [], [Line()], new Money(25, "GBP"));
        var incumbent = await PollAsync(tenant, first, "invoice", "INV-1", invoice);
        int facts;
        using (var before = fixture.ScopeFor(tenant)) facts = await before.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events.CountAsync();
        using (var map = fixture.ScopeFor(tenant))
        {
            var db = map.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var original = await db.ExternalIdMaps.SingleAsync(x => x.ConnectionId == first && x.EntityType == "document" && x.ExternalId == "INV-1");
            db.ExternalIdMaps.Add(new() { TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(), ConnectionId = second, EntityType = "document", ExternalId = "INV-B", ModuleId = Guid.NewGuid(),
                NaturalKeyHash = original.NaturalKeyHash, ValidFrom = fixture.Clock.UtcNow.AddDays(-30), ValidTo = fixture.Clock.UtcNow.AddDays(-1), MappedAt = fixture.Clock.UtcNow.AddDays(-30), MappedBy = Actor, MappingSource = ExternalIdMappingSource.Operator });
            db.ExternalIdMaps.Add(new() { TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(), ConnectionId = second, EntityType = "document", ExternalId = "INV-B", ModuleId = original.ModuleId,
                NaturalKeyHash = original.NaturalKeyHash, ValidFrom = fixture.Clock.UtcNow, MappedAt = fixture.Clock.UtcNow, MappedBy = Actor, MappingSource = ExternalIdMappingSource.Operator });
            await db.SaveChangesAsync();
        }
        var duplicate = await PollAsync(tenant, second, "invoice", "INV-B", invoice with { DocumentRef = Doc("invoice", "INV-B"), Totals = new Money(26, "GBP") });
        using var check = fixture.ScopeFor(tenant);
        var serialisation = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(1, await serialisation.Units.CountAsync());
        Assert.Equal(facts, await serialisation.Events.CountAsync());
        var dbFinal = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(2, await dbFinal.InboundMessages.CountAsync(x => x.MessageType == nameof(InvoicePosted)));
        var projection = await dbFinal.CanonicalDocuments.SingleAsync(x => x.MessageType == nameof(InvoicePosted));
        Assert.Equal(first, projection.ConnectionId);
        Assert.Equal(incumbent, projection.SourceInboundMessageId);
        Assert.Single(await dbFinal.ExceptionTasks.Where(x => x.InboundMessageId == duplicate && x.Kind == "cross_connection_document_conflict").ToListAsync());
        Assert.Equal(InboundTranslationStatus.Completed, (await dbFinal.InboundMessages.SingleAsync(x => x.InboundMessageId == duplicate)).TranslationStatus);
    }

    [DatabaseFact]
    public async Task Companion_location_check_reports_one_difference_per_key_and_clears_when_fixed()
    {
        // ADR-0001 case 34 companion half / ADR-0002 Reliability 4: unit count versus remote on-hand.
        var (tenant, connection) = await GenericAsync();
        await PollAsync(tenant, connection, "item", Item, new ItemUpserted(Item, "Fictional controller", OpenEnum.Known("serial"), "EA", false));
        await PollAsync(tenant, connection, "receipt", "GR-1", new GoodsReceiptPosted(Doc("receipt", "GR-1"), "FIC-SUPPLIER", [Line()]));
        using var scope = fixture.ScopeFor(tenant);
        var current = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Current.SingleAsync();
        fixture.ReconciliationSource.SetOnHand(connection, new ConnectorOnHandBalance(current.ItemId, Item, "Fictional controller", current.LocationId!.Value, "FIC-STORE", "Fictional store", 2));
        var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
        for (var run = 0; run < 2; run++)
        {
            var detail = (await service.DetailAsync(await service.RunLocationChecksAsync(connection, "user:test-operator", default), default))!;
            var diff = Assert.Single(detail.LocationChecks);
            Assert.Equal(ReconciliationResultStatus.Mismatch, diff.Status);
            Assert.Equal(1, diff.ModuleUnitCount);
            Assert.Equal(2, diff.SourceOnHand);
        }
        fixture.ReconciliationSource.SetOnHand(connection, new ConnectorOnHandBalance(current.ItemId, Item, "Fictional controller", current.LocationId.Value, "FIC-STORE", "Fictional store", 1));
        var clean = (await service.DetailAsync(await service.RunLocationChecksAsync(connection, "user:test-operator", default), default))!;
        Assert.Equal(ReconciliationResultStatus.Clean, Assert.Single(clean.LocationChecks).Status);
    }

    private async Task<(TenantId Tenant, Guid Connection)> GenericAsync()
    {
        fixture.Clock.UtcNow = new(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc);
        var tenant = await fixture.NewTenantAsync("edge");
        return (tenant, await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode));
    }

    private static ExternalDocumentRef Doc(string kind, string key) => new(kind, key, new(2026, 9, 1), key);
    private static CanonicalLine Line(string serial = Serial) => new(1, "1", Item, 1, "EA", [new(serial)], [], "FIC-STORE");
    private static InboundAcceptance Acceptance<T>(TenantId tenant, Guid connection, string entity, string key, T payload, DateTimeOffset? at = null)
    {
        var envelope = new CanonicalEnvelope("1.0", Guid.CreateVersion7(), tenant.Value, connection, IntegrationHostFixture.FakeConnectorCode, entity, key,
            new("1", OpenEnum.Known("sequence")), at ?? At, at ?? At, OpenEnum.Known("poll"), OpenEnum.Known("complete"), "fixture:" + key);
        return new(envelope, typeof(T).Name, JsonSerializer.Serialize(new CanonicalMessage<T>(envelope, typeof(T).Name, payload), IntegrationContracts.Json), JsonSerializer.Serialize(payload, IntegrationContracts.Json));
    }
    private async Task<Guid> PollAsync<T>(TenantId tenant, Guid connection, string entity, string key, T payload,
        string expected = InboundTranslationStatus.Completed, DateTimeOffset? at = null)
    {
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            await db.ConnectionPollWatermarks.ExecuteUpdateAsync(update => update.SetProperty(x => x.NextPollAt, fixture.Clock.UtcNow.AddDays(1)));
            var state = await db.ConnectionPollWatermarks.SingleOrDefaultAsync(x => x.ConnectionId == connection && x.EntityType == entity);
            if (state is null) db.ConnectionPollWatermarks.Add(new() { TenantId = tenant, ConnectionId = connection, EntityType = entity, NextPollAt = fixture.Clock.UtcNow, IntervalSeconds = 60, OverlapSeconds = 120 });
            else state.NextPollAt = fixture.Clock.UtcNow;
            await db.SaveChangesAsync();
            fixture.Poller.NextResult = new([Acceptance(tenant, connection, entity, key, payload, at)], key);
            await scope.ServiceProvider.GetRequiredService<ConnectionPollingService>().PollDueAsync(default);
        }
        return await WaitAsync(tenant, key, expected, connection);
    }
    private async Task<Guid> WaitAsync(TenantId tenant, string key, string expected, Guid? connection = null)
    {
        string? last = null;
        for (var attempt = 0; attempt < 300; attempt++)
        {
            using var scope = fixture.ScopeFor(tenant);
            var row = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ErpExternalId == key && (connection == null || x.ConnectionId == connection));
            if (row?.TranslationStatus == expected) return row.InboundMessageId;
            last = row?.TranslationStatus + ": " + row?.LastTranslationError;
            await Task.Delay(20);
        }
        throw new TimeoutException($"{key} expected {expected}; {last}");
    }
}
