using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Inbox;
using Yojana.Integration.Gateway;
using Yojana.Integration.Notifications;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class Cx01SimulatorDatabaseTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Simulator_product_tree_syncs_through_the_gateway_into_the_BOM_projection()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxbom");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using (var configure = fixture.ScopeFor(tenant))
            await ConfigureAsync(configure.ServiceProvider, tenant, connection, simulator, webhooks: false);

        InboundAcceptance acceptance;
        using (var capture = fixture.ScopeFor(tenant))
            acceptance = (await capture.ServiceProvider.GetRequiredService<Cx01Adapter>()
                .CaptureKeyAsync(connection, "ProductTrees", "FIC-SRV-TWIN", default))!.Acceptance;
        using (var translate = fixture.ScopeFor(tenant))
        {
            var accepted = await translate.ServiceProvider.GetRequiredService<InboundMessageStore>()
                .Accept(acceptance, default);
            var result = await translate.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync<GatewayTranslationResult>(new TranslateInboundMessage(accepted.InboundMessageId),
                    new DeliveryOptions { TenantId = tenant.ToString() });
            if (!result.Translated)
            {
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    using var statusScope = fixture.ScopeFor(tenant);
                    var status = await statusScope.ServiceProvider.GetRequiredService<IntegrationDbContext>()
                        .InboundMessages.AsNoTracking().Where(row => row.InboundMessageId == accepted.InboundMessageId)
                        .Select(row => row.TranslationStatus).SingleAsync();
                    if (status == InboundTranslationStatus.Completed) break;
                    if (attempt == 99) Assert.Fail($"{accepted.InboundMessageId:N} did not complete translation.");
                    await Task.Delay(20);
                }
            }
        }

        using var check = fixture.ScopeFor(tenant);
        var db = check.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var header = await db.BomProjections.SingleAsync(row => row.ParentItemExternalId == "FIC-SRV-TWIN");
        var lines = await db.BomProjectionLines.Where(row => row.BomProjectionId == header.BomProjectionId)
            .OrderBy(row => row.LineNo).ToListAsync();
        Assert.Equal(8, lines.Count);
        Assert.Equal(7, lines.Count(row => row.Kind == "item"));
        Assert.Single(lines, row => row.Kind == "text");
    }

    [DatabaseFact]
    public async Task Simulator_receipt_then_cancellation_leaves_its_serialised_unit_reversed()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxrc");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using (var configure = fixture.ScopeFor(tenant))
            await ConfigureAsync(configure.ServiceProvider, tenant, connection, simulator, webhooks: false);

        async Task TranslateAsync(InboundAcceptance acceptance)
        {
            using var translate = fixture.ScopeFor(tenant);
            var accepted = await translate.ServiceProvider.GetRequiredService<InboundMessageStore>()
                .Accept(acceptance, CancellationToken.None);
            var result = await translate.ServiceProvider.GetRequiredService<IMessageBus>()
                .InvokeAsync<GatewayTranslationResult>(new TranslateInboundMessage(accepted.InboundMessageId),
                    new DeliveryOptions { TenantId = tenant.ToString() });
            if (result.Translated) return;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                using var statusScope = fixture.ScopeFor(tenant);
                var status = await statusScope.ServiceProvider.GetRequiredService<IntegrationDbContext>()
                    .InboundMessages.AsNoTracking().Where(x => x.InboundMessageId == accepted.InboundMessageId)
                    .Select(x => x.TranslationStatus).SingleAsync();
                if (status == InboundTranslationStatus.Completed) return;
                await Task.Delay(20);
            }
            Assert.Fail($"{accepted.InboundMessageId:N} did not complete translation.");
        }

        using (var capture = fixture.ScopeFor(tenant))
        {
            var adapter = capture.ServiceProvider.GetRequiredService<Cx01Adapter>();
            await TranslateAsync((await adapter.CaptureKeyAsync(connection, "Items", "HW-EDGE-100",
                CancellationToken.None))!.Acceptance);
            await TranslateAsync((await adapter.CaptureKeyAsync(connection, "PurchaseDeliveryNotes", "3101",
                CancellationToken.None))!.Acceptance);
            using var response = await capture.ServiceProvider.GetRequiredService<Cx01Client>().SendJsonAsync(connection,
                HttpMethod.Post, Cx01Protocol.Root + "PurchaseDeliveryNotes(3101)/Cancel", new { }, true,
                CancellationToken.None);
            var cancellation = await adapter.CaptureKeyAsync(connection, "PurchaseDeliveryNotes", "3102",
                CancellationToken.None);
            Assert.Equal(nameof(DocumentCancelled), cancellation!.Acceptance.MessageType);
            await TranslateAsync(cancellation.Acceptance);
        }

        using var check = fixture.ScopeFor(tenant);
        var db = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = await db.Identifiers.Where(x => x.RawValue == "FICTIONAL-RECEIPT-SN-1001")
            .Select(x => x.UnitId).SingleAsync();
        Assert.Equal(LifecycleStatus.Reversed, (await db.Current.SingleAsync(x => x.UnitId == unit)).LifecycleStatus);
        Assert.Equal(1, await db.Events.CountAsync(x => x.UnitId == unit
            && x.EventType == UnitEventType.ReceiptReversed));
    }

    [DatabaseFact]
    public async Task Webhook_then_poll_accepts_one_document_change_and_duplicate_event_id_is_processed_once()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxidentity");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await ConfigureAsync(scope.ServiceProvider, tenant, connection, simulator, webhooks: true);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var inbox = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var tracker = scope.ServiceProvider.GetRequiredService<Cx01WebhookEventTracker>();
        var hint = new Cx01WebhookHint(connection, "DeliveryNotes", "Created", "4101", "evt-same-0001");

        await Cx01WebhookHintHandler.Handle(hint, adapter, inbox, tracker, CancellationToken.None);
        await Cx01WebhookHintHandler.Handle(hint, adapter, inbox, tracker, CancellationToken.None);
        var poll = await adapter.PollAsync(new(tenant, connection, "deliveries", null,
            TimeSpan.FromMinutes(2), 1), CancellationToken.None);
        var pollOutcome = await inbox.Accept(Assert.Single(poll.Events), CancellationToken.None);

        Assert.Equal(InboundAcceptanceResult.AlreadyProcessed, pollOutcome.Outcome);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(1, await db.InboundMessages.CountAsync(row => row.ConnectionId == connection));
        Assert.Equal(1, await db.ConnectionNotificationReceipts.CountAsync(row =>
            row.ConnectionId == connection && row.ProcessedAt != null));
        var stored = await db.InboundMessages.SingleAsync(row => row.ConnectionId == connection);
        using var raw = JsonDocument.Parse(stored.RawPayload);
        Assert.Equal(Cx01Mapper.ChangeMarker(raw.RootElement, "4101"), stored.ErpChangeMarker);
        Assert.DoesNotContain("webhook:", stored.ErpChangeMarker, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Watchdog_recovers_each_document_change_once_even_when_the_key_repeats()
    {
        await using var simulator = new Cx01Simulator();
        simulator.EventNotifications.Add(new("evt-failed-1001", "Failed", "DeliveryNotes", "4101",
            "2026-10-20T00:00:00Z", "03:25:24.237"));
        var tenant = await fixture.NewTenantAsync("cxwchg");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await ConfigureAsync(scope.ServiceProvider, tenant, connection, simulator, webhooks: true);
        var watchdog = scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>();

        Assert.Equal(1, await watchdog.WatchdogConnectionAsync(connection, CancellationToken.None));
        simulator.SimulateLaterDeliveryEdit("082000");
        simulator.EventNotifications.Add(new("evt-failed-1002", "Failed", "DeliveryNotes", "4101",
            "2026-10-20T00:00:00Z", "03:26:24.237"));
        Assert.Equal(1, await watchdog.WatchdogConnectionAsync(connection, CancellationToken.None));
        Assert.Equal(0, await watchdog.WatchdogConnectionAsync(connection, CancellationToken.None));

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(2, await db.InboundMessages.CountAsync(row => row.ConnectionId == connection));
        Assert.Equal(2, await db.ConnectionNotificationReceipts.CountAsync(row =>
            row.ConnectionId == connection && row.ProcessedAt != null));
    }

    [DatabaseFact]
    public async Task Watchdog_recovers_new_without_delivery_details_only_after_a_previous_run()
    {
        await using var simulator = new Cx01Simulator();
        simulator.EventNotifications.Add(new("evt-new-2001", "New", "DeliveryNotes", "4101",
            "2026-10-20T00:00:00Z", "03:25:24.237", EmptyDeliveryDetails: true));
        var tenant = await fixture.NewTenantAsync("cxwatchnew");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await ConfigureAsync(scope.ServiceProvider, tenant, connection, simulator, webhooks: true);
        var watchdog = scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>();

        Assert.Equal(0, await watchdog.WatchdogConnectionAsync(connection, CancellationToken.None));
        Assert.Equal(1, await watchdog.WatchdogConnectionAsync(connection, CancellationToken.None));
        Assert.Equal(0, await watchdog.WatchdogConnectionAsync(connection, CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Watchdog_pages_newest_first_and_reuses_a_one_day_watermark_overlap()
    {
        await using var simulator = new Cx01Simulator { EventNotificationPageSize = 1 };
        simulator.EventNotifications.Add(new("evt-old-3001", "Failed", "DeliveryNotes", "4101",
            "2026-10-18T00:00:00Z", "23:59:59.000"));
        simulator.EventNotifications.Add(new("evt-new-3002", "Failed", "DeliveryNotes", "4102",
            "2026-10-20T00:00:00Z", "00:00:01.000"));
        var tenant = await fixture.NewTenantAsync("cxwatchpage");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await ConfigureAsync(scope.ServiceProvider, tenant, connection, simulator, webhooks: true);
        var watchdog = scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>();

        Assert.Equal(2, await watchdog.WatchdogConnectionAsync(connection, CancellationToken.None));
        Assert.True(simulator.EventNotificationQueries.Count >= 2);
        Assert.Contains("CreateTime desc", Uri.UnescapeDataString(simulator.EventNotificationQueries[0]),
            StringComparison.Ordinal);
        _ = await watchdog.WatchdogConnectionAsync(connection, CancellationToken.None);
        Assert.Contains("CreateDate ge '2026-10-19'",
            Uri.UnescapeDataString(simulator.EventNotificationQueries[^1]), StringComparison.Ordinal);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(new DateOnly(2026, 10, 20), (await db.ConnectionNotificationWatchdogs
            .SingleAsync(row => row.ConnectionId == connection)).WatermarkDate);
    }

    [DatabaseFact]
    public async Task Poll_first_seen_after_cancellation_emits_original_before_reversal_then_skips_the_original()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxcancel");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await ConfigureAsync(scope.ServiceProvider, tenant, connection, simulator, webhooks: false);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var inbox = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        using (await scope.ServiceProvider.GetRequiredService<Cx01Client>().SendJsonAsync(connection,
            HttpMethod.Post, Cx01Protocol.Root + "DeliveryNotes(4101)/Cancel", new { }, true,
            CancellationToken.None)) { }

        var firstAfterCancellation = await adapter.PollAsync(new(tenant, connection, "deliveries", null,
            TimeSpan.FromMinutes(2), 3), CancellationToken.None);
        var original = Assert.Single(firstAfterCancellation.Events, item =>
            item.Envelope.ErpExternalId == "4101" && item.MessageType == nameof(DeliveryPosted));
        var reversal = Assert.Single(firstAfterCancellation.Events, item =>
            item.Envelope.ErpExternalId == "4103" && item.MessageType == nameof(DocumentCancelled));
        var ordered = firstAfterCancellation.Events.ToList();
        Assert.True(ordered.IndexOf(original) < ordered.IndexOf(reversal));
        _ = await inbox.AcceptMany(firstAfterCancellation.Events, CancellationToken.None);

        var repeated = await adapter.PollAsync(new(tenant, connection, "deliveries", null,
            TimeSpan.FromMinutes(2), 3), CancellationToken.None);
        Assert.DoesNotContain(repeated.Events, item => item.Envelope.ErpExternalId == "4101");
        Assert.Contains(repeated.Events, item => item.Envelope.ErpExternalId == "4103");
    }

    [DatabaseFact]
    public async Task Capture_key_skips_a_cancelled_original_that_was_captured_before_cancellation()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxcanpre");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await ConfigureAsync(scope.ServiceProvider, tenant, connection, simulator, webhooks: false);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var inbox = scope.ServiceProvider.GetRequiredService<InboundMessageStore>();
        var before = await adapter.CaptureKeyAsync(connection, "DeliveryNotes", "4101", CancellationToken.None);
        Assert.Equal(nameof(DeliveryPosted), before!.Acceptance.MessageType);
        _ = await inbox.Accept(before.Acceptance, CancellationToken.None);
        using (await scope.ServiceProvider.GetRequiredService<Cx01Client>().SendJsonAsync(connection,
            HttpMethod.Post, Cx01Protocol.Root + "DeliveryNotes(4101)/Cancel", new { }, true,
            CancellationToken.None)) { }

        Assert.Null(await adapter.CaptureKeyAsync(connection, "DeliveryNotes", "4101", CancellationToken.None));
        var reversal = await adapter.CaptureKeyAsync(connection, "DeliveryNotes", "4103",
            CancellationToken.None);
        Assert.Equal(nameof(DocumentCancelled), reversal!.Acceptance.MessageType);
    }

    [DatabaseFact]
    public async Task Simulator_poll_is_accepted_through_the_transactional_inbox()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cx01");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, new(simulator.BaseUri, "FICTIONAL_DEMO", "10.0 FP 2308"),
            CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        var credential = await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose, plaintext,
            null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None);
        await store.SaveAsync(credential, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(
            new(tenant, connection), Cx01Capabilities.For("10.0 FP 2308", false).Throttle, CancellationToken.None);

        var result = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>().PollAsync(
            new(tenant, connection, "deliveries", null, TimeSpan.FromMinutes(2), 1), CancellationToken.None);
        var accepted = await scope.ServiceProvider.GetRequiredService<InboundMessageStore>()
            .Accept(result.Events.Single(), CancellationToken.None);

        Assert.Equal(InboundAcceptanceResult.Accepted, accepted.Outcome);
        var row = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().InboundMessages
            .AsNoTracking().SingleAsync(x => x.TenantId == tenant && x.InboundMessageId == accepted.InboundMessageId);
        Assert.Equal(nameof(DeliveryPosted), row.MessageType);
        Assert.DoesNotContain("not-logged", row.RawPayload, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Simulator_reconciliation_returns_gateway_digests_and_honours_warehouse_status_mapping()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxrecon");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(simulator.BaseUri, "FICTIONAL_DEMO", "10.0 FP 2308"), CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose,
            plaintext, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(
            new(tenant, connection), Cx01Capabilities.For("10.0 FP 2308", false).Throttle,
            CancellationToken.None);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var day = new DateOnly(2026, 9, 1);

        var digests = await adapter.EntityDayDigestsAsync(connection, day, day, CancellationToken.None);
        var delivery = Assert.Single(digests, value => value.Entity == "DeliveryNotes");
        Assert.Equal(2, delivery.Count);
        Assert.NotEmpty(delivery.KeyFieldHash);
        Assert.Empty(await adapter.OnHandAsync(new(connection,
            new Dictionary<string, bool> { ["WH-A"] = false }), CancellationToken.None));
        var balance = Assert.Single(await adapter.OnHandAsync(new(connection,
            new Dictionary<string, bool> { ["WH-A"] = true }), CancellationToken.None));
        Assert.Equal("HW-EDGE-100", balance.ItemCode);
        Assert.Equal("WH-A", balance.LocationCode);
        Assert.Equal(2m, balance.OnHand);
        Assert.NotEqual(Guid.Empty, balance.ItemId);
        Assert.NotEqual(Guid.Empty, balance.LocationId);
    }

    [DatabaseFact]
    public async Task Webhook_subscription_and_failed_delivery_watchdog_use_the_simulator()
    {
        await using var simulator = new Cx01Simulator { FailedNotificationAvailable = true };
        var tenant = await fixture.NewTenantAsync("cxhook");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, new(simulator.BaseUri, "FICTIONAL_DEMO", "10.0 FP 2602",
            WebhooksAvailable: true), CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose,
            login, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection,
            Cx01WebhookEndpoints.SigningCredentialPurpose, "simulator-signing-secret"u8.ToArray(), null,
            fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(
            new(tenant, connection), Cx01Capabilities.For("10.0 FP 2602", true).Throttle, CancellationToken.None);
        var maintenance = scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>();

        await maintenance.SubscribeAsync(connection, new Uri("https://connector.invalid/notifications"),
            CancellationToken.None);
        await maintenance.SubscribeAsync(connection, new Uri("https://connector.invalid/notifications"),
            CancellationToken.None);
        Assert.Equal(1, simulator.SubscriptionCreateCount);
        Assert.False(simulator.WebhookSubscriptionPaused);
        await maintenance.SubscribeAsync(connection, new Uri("https://connector.invalid/updated-notifications"),
            CancellationToken.None);
        Assert.Equal(2, simulator.SubscriptionCreateCount);
        var accepted = await maintenance.WatchdogConnectionAsync(connection, CancellationToken.None);

        Assert.True(simulator.WebhookSubscriptionCreated);
        Assert.True(simulator.WebhookSubscriptionPaused);
        Assert.True(simulator.WebhookSubscriptionDeleted);
        var subscription = simulator.LastSubscriptionBody!.RootElement;
        Assert.Equal("HMAC", subscription.GetProperty("AuthenticationType").GetString());
        Assert.Equal("tYES", subscription.GetProperty("Handshake").GetString());
        Assert.Equal("tYES", subscription.GetProperty("VerifyCertificate").GetString());
        Assert.Equal("Active", subscription.GetProperty("State").GetString());
        Assert.Equal(20, subscription.GetProperty("WebhookID").GetString()!.Length);
        Assert.True(subscription.GetProperty("EventCollection").GetArrayLength() > 0);
        using var authentication = JsonDocument.Parse(subscription.GetProperty("AuthenticationCred").GetString()!);
        Assert.Equal("HMACSha256", authentication.RootElement.GetProperty("Algorithm").GetString());
        Assert.Equal(1, accepted);
    }

    [DatabaseFact]
    public async Task Read_only_survey_is_persisted_with_udfs_webhooks_and_owner_answers()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxsurvey");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, new(simulator.BaseUri, "FICTIONAL_DEMO", "1000340",
            AllowlistedTrackingTables: Cx01Simulator.TrackingTables), CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose,
            login, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        _ = await scope.ServiceProvider.GetRequiredService<Cx01Onboarding>().ProbeAndApplyAsync(connection,
            CancellationToken.None);
        _ = await scope.ServiceProvider.GetRequiredService<Cx01Onboarding>().ProbeAndApplyAsync(connection,
            CancellationToken.None);
        Assert.Equal(1, simulator.SerialQueryCreateCount);
        var survey = await scope.ServiceProvider.GetRequiredService<Cx01SurveyService>().RunAsync(connection,
            new(true, "Reviewed fictional procedure", false, "No screen-only checks"), CancellationToken.None);

        Assert.True(survey.Webhooks.ReleaseSupportsWebhooks);
        Assert.True(survey.Webhooks.Enabled);
        Assert.Equal(3, survey.Webhooks.RetryTimes);
        Assert.Equal(CapabilitySupport.Native, survey.BillOfMaterialsSupport);
        Assert.Contains(survey.UserFields, field => field.MandatoryWithoutDefault && field.WrittenObject);
        Assert.All(Cx01Simulator.TrackingTables, table => Assert.Equal("allowed", survey.TrackingTableAllowlist[table].State));
        Assert.True(simulator.SerialQueryCreated);
        Assert.Equal(1, simulator.SerialQueryCreateCount);
        Assert.True(survey.OwnerAnswers.ValidationProcedureReviewed);
        Assert.NotNull(await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().Cx01SurveyRecords
            .SingleOrDefaultAsync(row => row.ConnectionId == connection));
    }

    [DatabaseFact]
    public async Task Reference_only_outbound_updates_and_finds_the_configured_reference_field()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("cxout");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        await settings.SetAsync(connection, new(simulator.BaseUri, "FICTIONAL_DEMO", "10.0 FP 2308",
            RmaReferenceField: "U_YojanaRma",
            MandatoryUserFieldDefaults: new Dictionary<string, string>
            {
                ["ORDN.U_RETURN_REASON"] = "FICTIONAL-DEFAULT",
            }), CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await store.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx01Client.CredentialPurpose,
            login, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None), CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(
            new(tenant, connection), Cx01Capabilities.For("10.0 FP 2308", false).Throttle,
            CancellationToken.None);
        _ = await scope.ServiceProvider.GetRequiredService<Cx01SurveyService>().RunAsync(connection, new(),
            CancellationToken.None);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx01Adapter>();
        var reference = "RMA-FICTIONAL-1001";

        var created = await adapter.CreateAsync(new(connection, Guid.CreateVersion7(),
            nameof(CreateServiceOrder), reference, "{\"target_record_ref\":\"return:5101\"}", null),
            CancellationToken.None);
        var found = await adapter.FindByReferenceAsync(new(connection, nameof(CreateServiceOrder), reference),
            CancellationToken.None);

        Assert.Equal(OutboundConnectorOutcome.Created, created.Outcome);
        Assert.Equal("YOJANA:" + reference, simulator.LastReferenceValue);
        Assert.Equal("FICTIONAL-DEFAULT",
            simulator.LastPatchBody!.RootElement.GetProperty("U_RETURN_REASON").GetString());
        Assert.Equal("5101", found?.ErpExternalId);

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.OutboundCommands.Add(new OutboundCommandRow
        {
            TenantId = tenant,
            OutboundCommandId = Guid.CreateVersion7(),
            ConnectionId = connection,
            CommandType = nameof(CreateServiceOrder),
            IdempotencyKey = Guid.CreateVersion7(),
            ModuleDocumentRef = "fictional/own-write",
            ErpReferenceText = reference,
            Payload = "{}",
            Status = OutboundCommandStatus.Done,
            CreatedAt = fixture.Clock.UtcNow,
            CompletedAt = fixture.Clock.UtcNow,
            ErpExternalId = created.Document!.ErpExternalId,
            ErpChangeMarker = created.Document.ErpChangeMarker,
            Outcome = OutboundConnectorOutcome.Created,
        });
        await db.SaveChangesAsync();
        var ownPoll = await adapter.PollAsync(new(tenant, connection, "returns", null,
            TimeSpan.FromMinutes(2), 10), CancellationToken.None);
        Assert.Empty(ownPoll.Events);

        simulator.SimulateLaterUserEdit();
        var laterPoll = await adapter.PollAsync(new(tenant, connection, "returns", null,
            TimeSpan.FromMinutes(2), 10), CancellationToken.None);
        var later = Assert.Single(laterPoll.Events);
        Assert.Contains("U_FICTIONAL_POST_WRITE", later.RawPayload, StringComparison.Ordinal);
    }

    [DatabaseFact]
    public async Task Customer_validation_rejection_creates_manual_task_with_verbatim_text_and_no_retry()
    {
        await using var simulator = new Cx01Simulator { RejectNextPatchWith = System.Net.HttpStatusCode.BadRequest };
        var tenant = await fixture.NewTenantAsync("cxreject");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(simulator.BaseUri, "FICTIONAL_DEMO", "1000340", RmaReferenceField: "U_YojanaRma"),
            CancellationToken.None);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var credentials = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await credentials.SaveAsync(await protector.EncryptAsync(tenant, connection,
            Cx01Client.CredentialPurpose, login, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None),
            CancellationToken.None);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var commandId = Guid.CreateVersion7();
        db.OutboundCommands.Add(new OutboundCommandRow
        {
            TenantId = tenant,
            OutboundCommandId = commandId,
            ConnectionId = connection,
            CommandType = nameof(CreateServiceOrder),
            IdempotencyKey = Guid.CreateVersion7(),
            ModuleDocumentRef = "fictional/rejected",
            ErpReferenceText = "RMA-FICTIONAL-REJECTED",
            Payload = "{\"target_record_ref\":\"return:5101\"}",
            Status = OutboundCommandStatus.Pending,
            CreatedAt = fixture.Clock.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection, CancellationToken.None);

        Assert.Equal(1, result.Parked);
        Assert.Equal(0, result.Retrying);
        Assert.Equal(1, simulator.PatchAttempts);
        var task = await db.ManualOutboundTasks.SingleAsync(row => row.OutboundCommandId == commandId);
        var deadLetter = await db.DeadLetterItems.SingleAsync(row => row.DeadLetterItemId == task.DeadLetterItemId);
        Assert.Equal("(custom) Return needs a reason code", deadLetter.Failure);
        Assert.Equal(1, (await db.OutboundCommands.SingleAsync(row => row.OutboundCommandId == commandId)).AttemptCount);
    }

    private async Task ConfigureAsync(IServiceProvider services, TenantId tenant, Guid connection,
        Cx01Simulator simulator, bool webhooks)
    {
        await services.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(simulator.BaseUri, "FICTIONAL_DEMO", webhooks ? "10.0 FP 2602" : "10.0 FP 2308",
                WebhooksAvailable: webhooks), CancellationToken.None);
        var protector = services.GetRequiredService<ConnectionCredentialProtector>();
        var credentials = services.GetRequiredService<IConnectionCredentialStore>();
        var login = JsonSerializer.SerializeToUtf8Bytes(new Cx01Credential("integration-user", "not-logged"),
            IntegrationContracts.Json);
        await credentials.SaveAsync(await protector.EncryptAsync(tenant, connection,
            Cx01Client.CredentialPurpose, login, null, fixture.Clock.UtcNow.AddDays(30), CancellationToken.None),
            CancellationToken.None);
        await services.GetRequiredService<ConnectionThrottleProfiles>().OnboardAsync(new(tenant, connection),
            Cx01Capabilities.For(webhooks ? "10.0 FP 2602" : "10.0 FP 2308", webhooks).Throttle,
            CancellationToken.None);
    }
}
