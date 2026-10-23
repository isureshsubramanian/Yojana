using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Connectors.CX02.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class Cx02AdapterDatabaseTests(IntegrationHostFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset At = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
    private DateTime clockBeforeTest;

    public Task InitializeAsync()
    {
        clockBeforeTest = fixture.Clock.UtcNow;
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        fixture.Clock.UtcNow = clockBeforeTest;
        return Task.CompletedTask;
    }

    [DatabaseFact]
    public async Task First_sync_maps_standard_and_extension_resources_through_the_inbound_store()
    {
        await using var simulator = new Cx02Simulator();
        simulator.Seed("serviceItems", Cx02SimulatorEntity.From(new
        {
            id = Guid.NewGuid(), number = "SERVICE-100", serialNumber = "SER-100",
            itemNumber = "HW-SERVER-100", lastModifiedDateTime = "2026-09-01T08:00:00Z",
        }));
        simulator.Seed("trackedItemLedgerEntries", Ledger(Guid.NewGuid(), 1001, "Purchase Receipt", "REC-100",
            "SER-100", 1m, "2026-09-01T08:01:00Z"));
        var setup = await SetupAsync(simulator);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx02Adapter>();
        var inbox = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();

        var item = Assert.Single((await adapter.PollAsync(new(setup.Tenant, setup.Connection, "items", null,
            TimeSpan.FromMinutes(5), 10), default)).Events);
        var receipt = Assert.Single((await adapter.PollAsync(new(setup.Tenant, setup.Connection,
            "tracked_ledger_entries", null, TimeSpan.FromMinutes(5), 10), default)).Events);

        Assert.Equal(InboundAcceptanceResult.Accepted, (await inbox.Accept(item, default)).Outcome);
        Assert.Equal(InboundAcceptanceResult.Accepted, (await inbox.Accept(receipt, default)).Outcome);
        Assert.Contains("SERVICE-100", receipt.CanonicalMessage, StringComparison.Ordinal);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(2, await db.InboundMessages.CountAsync(row => row.ConnectionId == setup.Connection));
    }

    [DatabaseFact]
    public async Task Invoice_read_joins_standard_lines_to_extension_serials_and_service_item_numbers()
    {
        await using var simulator = new Cx02Simulator();
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var relationId = Guid.NewGuid();
        var ledgerId = Guid.NewGuid();
        simulator.Seed("salesInvoices", Cx02SimulatorEntity.From(new
        {
            id = invoiceId, number = "INV-210", customerNumber = "C-210", invoiceDate = "2026-09-01",
            totalAmountIncludingTax = 1600m, currencyCode = "INR",
            lastModifiedDateTime = "2026-09-01T08:03:00Z",
        }));
        simulator.Seed("salesInvoiceLines", Cx02SimulatorEntity.From(new
        {
            id = lineId, documentId = invoiceId, sequence = 10, lineObjectNumber = "HW-STORAGE-210",
            quantity = 1m, unitOfMeasureCode = "EA", unitPrice = 1600m, currencyCode = "INR",
            lastModifiedDateTime = "2026-09-01T08:03:00Z",
        }));
        simulator.Seed("itemEntryRelations", Cx02SimulatorEntity.From(new
        {
            id = relationId, sourceDocumentType = "salesInvoice", sourceDocumentNumber = "INV-210",
            sourceLineNumber = 10, sourceDocumentId = invoiceId,
            itemLedgerEntryNumber = 2101, lastModifiedDateTime = "2026-09-01T08:03:00Z",
        }));
        simulator.Seed("trackedItemLedgerEntries", Ledger(ledgerId, 2101, "Sales Invoice", "INV-210",
            "SER-210", -1m, "2026-09-01T08:03:00Z"));
        simulator.Seed("serviceItems", Cx02SimulatorEntity.From(new
        {
            id = Guid.NewGuid(), number = "SERVICE-210", serialNumber = "SER-210",
            itemNumber = "HW-STORAGE-210", lastModifiedDateTime = "2026-09-01T08:04:00Z",
        }));
        var setup = await SetupAsync(simulator);
        using var scope = fixture.ScopeFor(setup.Tenant);

        var invoice = Assert.Single((await scope.ServiceProvider.GetRequiredService<Cx02Adapter>().PollAsync(
            new(setup.Tenant, setup.Connection, "sales_invoices", null, TimeSpan.FromMinutes(5), 10),
            default)).Events);
        var message = JsonSerializer.Deserialize<CanonicalMessage<InvoicePosted>>(invoice.CanonicalMessage,
            IntegrationContracts.Json)!;

        var serial = Assert.Single(Assert.Single(message.Payload.Lines).Serials);
        Assert.Equal("SER-210", serial.Value);
        Assert.Equal("SERVICE-210", serial.ErpSerialId);
    }

    [DatabaseFact]
    public async Task Incremental_sync_re_reads_the_overlap_but_accepts_only_the_new_change()
    {
        await using var simulator = new Cx02Simulator();
        var firstId = Guid.NewGuid();
        simulator.Seed("items", Item(firstId, "HW-PC-100", "2026-09-01T08:00:00Z"));
        var setup = await SetupAsync(simulator);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx02Adapter>();
        var inbox = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var first = await adapter.PollAsync(new(setup.Tenant, setup.Connection, "items", null,
            TimeSpan.FromMinutes(5), 10), default);
        Assert.Equal(InboundAcceptanceResult.Accepted, (await inbox.Accept(Assert.Single(first.Events), default)).Outcome);
        simulator.Seed("items", Item(firstId, "HW-PC-100", "2026-09-01T08:00:00Z"),
            Item(Guid.NewGuid(), "HW-PC-101", "2026-09-01T08:01:00Z"));

        var incremental = await adapter.PollAsync(new(setup.Tenant, setup.Connection, "items", first.NextWatermark,
            TimeSpan.FromMinutes(5), 10), default);
        var outcomes = await inbox.AcceptMany(incremental.Events, default);

        Assert.Equal(2, incremental.Events.Count);
        Assert.Single(outcomes, outcome => outcome.Outcome == InboundAcceptanceResult.AlreadyProcessed);
        Assert.Single(outcomes, outcome => outcome.Outcome == InboundAcceptanceResult.Accepted);
    }

    [DatabaseFact]
    public async Task Duplicate_and_out_of_order_notification_reads_are_idempotent_and_superseded()
    {
        await using var simulator = new Cx02Simulator();
        var id = Guid.NewGuid();
        simulator.Seed("items", Item(id, "HW-NET-100", "2026-09-01T08:02:00Z"));
        var setup = await SetupAsync(simulator);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx02Adapter>();
        var inbox = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var resource = $"/api/v2.0/companies({Cx02Simulator.CompanyId})/items({id})";
        var newest = Assert.Single(await adapter.CaptureHintAsync(setup.Connection, resource, "updated", default));
        simulator.Seed("items", Item(id, "HW-NET-100", "2026-09-01T08:01:00Z"));
        var older = Assert.Single(await adapter.CaptureHintAsync(setup.Connection, resource, "updated", default));

        var accepted = await inbox.Accept(newest, default);
        var duplicate = await inbox.Accept(newest, default);
        var late = await inbox.Accept(older, default);

        Assert.Equal(InboundAcceptanceResult.Accepted, accepted.Outcome);
        Assert.Equal(InboundAcceptanceResult.AlreadyProcessed, duplicate.Outcome);
        Assert.Equal(InboundAcceptanceResult.Superseded, late.Outcome);
    }

    [DatabaseFact]
    public async Task Lapsed_and_deleted_subscriptions_are_renewed_or_recreated()
    {
        fixture.Clock.UtcNow = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var remoteClock = new MutableCx02TimeProvider(At);
        await using var receiver = new AcceptanceReceiver();
        await using var simulator = new Cx02Simulator(remoteClock);
        var setup = await SetupAsync(simulator, receiver.Uri);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var maintenance = scope.ServiceProvider.GetRequiredService<Cx02WebhookMaintenance>();
        var settings = scope.ServiceProvider.GetRequiredService<Cx02Settings>();
        await EnsureSubscriptionsAsync(maintenance, setup.Connection, simulator);
        var original = await settings.NotificationsAsync(setup.Connection, default);
        Assert.Equal(Cx02Protocol.NotificationEntities.Count, simulator.ActiveSubscriptionCount);

        simulator.LapseSubscriptions();
        remoteClock.Advance(TimeSpan.FromHours(62));
        fixture.Clock.UtcNow += TimeSpan.FromHours(62);
        await EnsureSubscriptionsAsync(maintenance, setup.Connection, simulator);
        var renewed = await settings.NotificationsAsync(setup.Connection, default);
        Assert.All(renewed.Subscriptions, value => Assert.True(value.ExpiresAt > original.Subscriptions
            .Single(old => old.Resource == value.Resource).ExpiresAt));

        var deleted = renewed.Subscriptions[0];
        using (var ignored = await scope.ServiceProvider.GetRequiredService<Cx02Client>().SendJsonAsync(
            setup.Connection, HttpMethod.Delete, simulator.CustomApiUri($"subscriptions({deleted.SubscriptionId})"),
            null, true, null, default)) { }
        await EnsureSubscriptionsAsync(maintenance, setup.Connection, simulator);
        var recreated = await settings.NotificationsAsync(setup.Connection, default);
        Assert.NotEqual(deleted.SubscriptionId,
            recreated.Subscriptions.Single(value => value.Resource == deleted.Resource).SubscriptionId);
        Assert.Equal(Cx02Protocol.NotificationEntities.Count, simulator.ActiveSubscriptionCount);
    }

    [DatabaseFact]
    public async Task Retry_after_429_storm_is_honoured_without_advancing_a_failed_watermark()
    {
        await using var simulator = new Cx02Simulator();
        var setup = await SetupAsync(simulator);
        using var scope = fixture.ScopeFor(setup.Tenant);
        simulator.StartThrottleStorm(3);

        var result = await scope.ServiceProvider.GetRequiredService<Cx02Adapter>().PollAsync(
            new(setup.Tenant, setup.Connection, "items", null, TimeSpan.FromMinutes(5), 10), default);

        Assert.NotEmpty(result.Events);
        Assert.NotNull(result.NextWatermark);
        Assert.True(simulator.Requests.Count(request => request.Path.Contains("/items", StringComparison.Ordinal)) >= 4);
    }

    [DatabaseFact]
    public async Task Token_expiry_mid_run_refreshes_once_and_continues()
    {
        await using var simulator = new Cx02Simulator();
        var setup = await SetupAsync(simulator);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx02Adapter>();
        _ = await adapter.PollAsync(new(setup.Tenant, setup.Connection, "items", null,
            TimeSpan.FromMinutes(5), 10), default);
        simulator.ExpireCurrentTokens();

        var refreshed = await adapter.PollAsync(new(setup.Tenant, setup.Connection, "customers", null,
            TimeSpan.FromMinutes(5), 10), default);

        Assert.NotEmpty(refreshed.Events);
        Assert.True(simulator.Requests.Count(request => request.Path.EndsWith("/oauth2/v2.0/token",
            StringComparison.Ordinal)) >= 2);
    }

    [DatabaseFact]
    public async Task Change_by_unlicensed_actor_is_found_only_by_the_poll_baseline()
    {
        var remoteClock = new MutableCx02TimeProvider(At);
        await using var receiver = new AcceptanceReceiver();
        await using var simulator = new Cx02Simulator(remoteClock);
        var changedId = Guid.NewGuid();
        simulator.Seed("items", Item(changedId, "HW-STORAGE-200", "2026-09-01T08:01:00Z"));
        var setup = await SetupAsync(simulator, receiver.Uri);
        using var scope = fixture.ScopeFor(setup.Tenant);
        await EnsureSubscriptionsAsync(scope.ServiceProvider.GetRequiredService<Cx02WebhookMaintenance>(),
            setup.Connection, simulator);
        simulator.SuppressNextNotificationAsUnlicensedActor = true;
        simulator.QueueWebhookChange("items", changedId);
        remoteClock.Advance(TimeSpan.FromSeconds(31));
        await simulator.DispatchWebhooksAsync();
        Assert.Empty(receiver.Bodies);

        var polled = await scope.ServiceProvider.GetRequiredService<Cx02Adapter>().PollAsync(
            new(setup.Tenant, setup.Connection, "items", null, TimeSpan.FromMinutes(5), 10), default);

        Assert.Contains(polled.Events, value => value.Envelope.ErpExternalId == "HW-STORAGE-200");
    }

    [DatabaseFact]
    public async Task Collection_notification_is_acknowledged_then_re_read_through_the_inbound_store()
    {
        fixture.Clock.UtcNow = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var remoteClock = new MutableCx02TimeProvider(At);
        await using var simulator = new Cx02Simulator(remoteClock, new() { CollectionNotificationThreshold = 0 });
        var entryId = Guid.NewGuid();
        simulator.Seed("trackedItemLedgerEntries", Ledger(entryId, 2001, "Positive Adjmt.", "ADJ-200",
            "SER-200", 1m, "2026-09-01T08:01:00Z"));
        await using var host = await NotificationEndpointTests.WebProcess.StartAsync();
        var setup = await SetupAsync(simulator, host.BaseAddress);
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await EnsureSubscriptionsAsync(scope.ServiceProvider.GetRequiredService<Cx02WebhookMaintenance>(),
                setup.Connection, simulator);
        simulator.QueueWebhookChange("trackedItemLedgerEntries", entryId);
        remoteClock.Advance(TimeSpan.FromSeconds(31));

        await simulator.DispatchWebhooksAsync();
        await WaitForInboundAsync(setup.Tenant, setup.Connection, "trackedItemLedgerEntries", 1);

        Assert.Contains(simulator.DeliveredNotifications, value => value.Channel == "webhook"
            && value.Body.Contains("\"changeType\":\"collection\"", StringComparison.Ordinal));
    }

    [DatabaseFact]
    public async Task Cancellation_appends_a_reversal_for_the_original_delivery()
    {
        await using var simulator = new Cx02Simulator();
        var receiptId = Guid.NewGuid();
        simulator.Seed("items", Item(Guid.NewGuid(), "HW-SERVER-100", "2026-09-01T08:00:00Z"));
        simulator.Seed("trackedItemLedgerEntries", Ledger(receiptId, 3001, "Purchase Receipt", "REC-300",
            "SER-300", 1m, "2026-09-01T08:01:00Z"));
        var setup = await SetupAsync(simulator);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx02Adapter>();
        await TranslateAsync(scope, Assert.Single((await adapter.PollAsync(new(setup.Tenant, setup.Connection,
            "items", null, TimeSpan.FromMinutes(5), 10), default)).Events));
        await TranslateAsync(scope, Assert.Single((await adapter.PollAsync(new(setup.Tenant, setup.Connection,
            "tracked_ledger_entries", null, TimeSpan.FromMinutes(5), 10), default)).Events));
        var deliveryId = Guid.NewGuid();
        simulator.Seed("trackedItemLedgerEntries", Ledger(receiptId, 3001, "Purchase Receipt", "REC-300",
                "SER-300", 1m, "2026-09-01T08:01:00Z"),
            Ledger(deliveryId, 3002, "Sales Shipment", "SHIP-300", "SER-300", -1m,
                "2026-09-01T08:02:00Z"));
        var movements = await adapter.PollAsync(new(setup.Tenant, setup.Connection, "tracked_ledger_entries",
            null, TimeSpan.FromMinutes(5), 10), default);
        await TranslateAsync(scope, Assert.Single(movements.Events, value => value.MessageType == nameof(DeliveryPosted)));
        simulator.Seed("salesCreditMemos", Cx02SimulatorEntity.From(new
        {
            id = Guid.NewGuid(), number = "CRM-300", cancelledDocumentType = "delivery",
            cancelledDocumentNumber = "SHIP-300", postingDate = "2026-09-02",
            lastModifiedDateTime = "2026-09-02T08:00:00Z",
        }));
        var cancellation = Assert.Single((await adapter.PollAsync(new(setup.Tenant, setup.Connection,
            "sales_credit_memos", null, TimeSpan.FromMinutes(5), 10), default)).Events);

        await TranslateAsync(scope, cancellation);

        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = await serialisation.Identifiers.Where(value => value.RawValue == "SER-300")
            .Select(value => value.UnitId).SingleAsync();
        var reversal = await serialisation.Events.SingleAsync(value => value.UnitId == unit
            && value.EventType == UnitEventType.ShipmentReversed);
        Assert.NotNull(reversal.ReversesEventId);
    }

    private async Task<(TenantId Tenant, Guid Connection)> SetupAsync(Cx02Simulator simulator,
        Uri? callback = null)
    {
        fixture.Clock.UtcNow = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        var tenant = await fixture.NewTenantAsync("cx02");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), Cx02Capabilities.ConnectorCode);
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx02Settings>().SetAsync(connection,
            new(simulator.BaseUri, Cx02Simulator.DefaultTenant, Cx02Simulator.DefaultTenant,
                Cx02Simulator.DefaultEnvironment, Cx02Simulator.CompanyId, "1.0", callback,
                simulator.TokenUri(), PollPageSize: 2), default);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx02Client.CredentialPurpose,
            JsonSerializer.SerializeToUtf8Bytes(new Cx02ClientCredential("fictional-client",
                simulator.Options.ClientSecret, "fictional-client-state"), IntegrationContracts.Json), null,
            fixture.Clock.UtcNow.AddDays(30), default), default);
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().UpdateAsync(new(tenant, connection),
            Cx02Capabilities.Declaration.Throttle.DefaultProfile, default);
        return (tenant, connection);
    }

    private static async Task EnsureSubscriptionsAsync(Cx02WebhookMaintenance maintenance, Guid connection,
        Cx02Simulator simulator)
    {
        try { await maintenance.EnsureAsync(connection, default); }
        catch (Exception exception)
        {
            throw new Xunit.Sdk.XunitException(
                $"CX02 subscription setup failed. Simulator: {simulator.LastHandlerFailure ?? "no handler failure"}",
                exception);
        }
    }

    private static Cx02SimulatorEntity Item(Guid id, string number, string changed) =>
        Cx02SimulatorEntity.From(new
        {
            id, number, displayName = "Fictional hardware", itemTrackingCode = "Serial Specific",
            baseUnitOfMeasureCode = "EA", lastModifiedDateTime = changed,
        });

    private static Cx02SimulatorEntity Ledger(Guid id, int entryNumber, string documentType,
        string documentNumber, string serial, decimal quantity, string changed) =>
        Cx02SimulatorEntity.From(new
        {
            id, entryNumber, itemNumber = "HW-SERVER-100", postingDate = "2026-09-01", entryType = documentType,
            sourceNumber = documentType == "Purchase Receipt" ? "V-100" : "C-100", documentNumber, documentType,
            quantity, serialNumber = serial, lotNumber = "", packageNumber = "", locationCode = "WH-A",
            lastModifiedDateTime = changed,
        });

    private static async Task TranslateAsync(IServiceScope scope, InboundAcceptance acceptance)
    {
        var accepted = await scope.ServiceProvider.GetRequiredService<InboundMessageStore>().Accept(acceptance, default);
        if (accepted.Outcome == InboundAcceptanceResult.AlreadyProcessed) return;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var status = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages
                .AsNoTracking().Where(value => value.InboundMessageId == accepted.InboundMessageId)
                .Select(value => value.TranslationStatus).SingleAsync();
            if (status == Yojana.Integration.Data.Entities.InboundTranslationStatus.Completed) return;
            if (status is Yojana.Integration.Data.Entities.InboundTranslationStatus.Failed
                or Yojana.Integration.Data.Entities.InboundTranslationStatus.Held
                or Yojana.Integration.Data.Entities.InboundTranslationStatus.NeedsAttention)
            {
                var error = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages
                    .AsNoTracking().Where(value => value.InboundMessageId == accepted.InboundMessageId)
                    .Select(value => value.LastTranslationError).SingleAsync();
                throw new Xunit.Sdk.XunitException($"CX02 translation ended in {status}: {error}");
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("CX02 inbound translation did not complete.");
    }

    private async Task WaitForInboundAsync(TenantId tenant, Guid connection, string entity, int count)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var scope = fixture.ScopeFor(tenant);
            if (await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages.AsNoTracking()
                .CountAsync(value => value.ConnectionId == connection && value.ErpEntity == entity) >= count) return;
            await Task.Delay(25);
        }
        throw new TimeoutException($"CX02 notification did not capture {entity}.");
    }

    private sealed class MutableCx02TimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan value) => current += value;
    }

    private sealed class AcceptanceReceiver : IAsyncDisposable
    {
        private readonly HttpListener listener;
        private readonly CancellationTokenSource stop = new();
        private readonly Task loop;

        public AcceptanceReceiver()
        {
            for (var attempt = 0; ; attempt++)
            {
                using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
                socket.Start();
                var port = ((IPEndPoint)socket.LocalEndpoint).Port;
                socket.Stop();
                var candidate = new HttpListener();
                var uri = new Uri($"http://127.0.0.1:{port}/");
                candidate.Prefixes.Add(uri.ToString());
                try
                {
                    candidate.Start();
                    listener = candidate;
                    Uri = uri;
                    break;
                }
                catch (HttpListenerException) when (attempt < 19)
                {
                    candidate.Close();
                }
            }
            loop = RunAsync();
        }

        public Uri Uri { get; }
        public List<string> Bodies { get; } = [];

        public async ValueTask DisposeAsync()
        {
            stop.Cancel();
            listener.Stop();
            try { await loop; }
            catch (HttpListenerException) { }
            listener.Close();
            stop.Dispose();
        }

        private async Task RunAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await listener.GetContextAsync(); }
                catch (Exception) when (stop.IsCancellationRequested) { break; }
                _ = Task.Run(() => HandleAsync(context), stop.Token);
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            var validation = context.Request.QueryString["validationToken"];
            if (validation is not null)
            {
                context.Response.ContentType = "text/plain";
                var bytes = Encoding.UTF8.GetBytes(validation);
                context.Response.ContentLength64 = bytes.Length;
                await context.Response.OutputStream.WriteAsync(bytes);
            }
            else
            {
                using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
                var body = await reader.ReadToEndAsync();
                lock (Bodies) Bodies.Add(body);
            }
            context.Response.StatusCode = 200;
            context.Response.Close();
        }
    }
}
