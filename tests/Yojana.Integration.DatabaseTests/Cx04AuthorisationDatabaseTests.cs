using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors;
using Yojana.Integration.Connectors.CX04;
using Yojana.Integration.Connectors.CX04.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class Cx04AuthorisationDatabaseTests(IntegrationHostFixture fixture)
{
    private const string Actor = "user:fictional-integration-admin";

    [DatabaseFact]
    public async Task Sign_in_actions_admit_both_administrator_roles_and_refuse_other_roles()
    {
        var tenant = await fixture.NewTenantAsync("c4roles-");
        using var scope = fixture.ScopeFor(tenant);
        var authorisation = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        Assert.True((await authorisation.AuthorizeAsync(Principal(YojanaRole.TenantAdministrator), null,
            YojanaPolicy.IntegrationOperations)).Succeeded);
        Assert.True((await authorisation.AuthorizeAsync(Principal(YojanaRole.IntegrationOperator), null,
            YojanaPolicy.IntegrationOperations)).Succeeded);
        Assert.False((await authorisation.AuthorizeAsync(Principal(YojanaRole.ServiceManager), null,
            YojanaPolicy.IntegrationOperations)).Succeeded);

        ClaimsPrincipal Principal(string role) => new(new ClaimsIdentity(
        [
            new Claim(YojanaClaim.TenantId, tenant.Value.ToString("D")),
            new Claim(YojanaClaim.Role, role),
        ], "test"));
    }

    [DatabaseFact]
    public async Task Disable_validates_confirmation_and_trimmed_reason_and_returns_the_full_read_model()
    {
        await using var simulator = new Cx04Simulator();
        var connection = await ConnectionAsync(simulator);
        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        await ConsentAsync(connection);
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var service = scope.ServiceProvider.GetRequiredService<Cx04Authorisation>();
            Assert.Equal("disable_confirmation_required", (await Assert.ThrowsAsync<Cx04AuthorisationException>(
                () => service.DisableAsync(connection.ConnectionId, new("review", false), Actor, default))).Rule);
            Assert.Equal("disable_reason_invalid", (await Assert.ThrowsAsync<Cx04AuthorisationException>(
                () => service.DisableAsync(connection.ConnectionId, new("   ", true), Actor, default))).Rule);
            Assert.Equal("disable_reason_invalid", (await Assert.ThrowsAsync<Cx04AuthorisationException>(
                () => service.DisableAsync(connection.ConnectionId, new(new string('x', 201), true), Actor, default))).Rule);

            var result = await service.DisableAsync(connection.ConnectionId,
                new("  Fictional access review  ", true), Actor, default);
            Assert.Equal("Fictional access review", result.Reason);
            var view = await service.ReadAsync(connection.ConnectionId, null, 10, default);
            Assert.Equal(Cx04AuthorisationStatus.Disabled, view.Status);
            Assert.Equal("Fictional access review", view.DisabledReason);
            Assert.Equal("Unknown user", view.DisabledBy);
            Assert.Equal("Unknown user", view.SignedInBy);
            Assert.NotNull(view.SignedInAt);
            Assert.NotNull(view.DisabledAt);
            Assert.NotNull(view.LastCallAt);
            Assert.Equal("essentials", view.LicenceTier);
            Assert.True(view.LicensedRequestsPerMinute > 0);
            Assert.True(view.GovernedRequestsPerMinute > 0);
            Assert.Equal(view.LicensedRequestsPerMinute - view.GovernedRequestsPerMinute,
                view.RequestHeadroomPerMinute);
            Assert.False(view.CanDisable);
            Assert.True(view.RequiresFreshConsent);
            Assert.Contains(view.History.Items, item => item.Kind == "disabled"
                && item.Reason == "Fictional access review" && item.By == "Unknown user");
            var firstPage = await service.ReadAsync(connection.ConnectionId, null, 1, default);
            Assert.Single(firstPage.History.Items);
            Assert.NotNull(firstPage.History.NextCursor);
            var secondPage = await service.ReadAsync(connection.ConnectionId,
                firstPage.History.NextCursor, 1, default);
            Assert.Single(secondPage.History.Items);
            Assert.NotEqual(firstPage.History.Items[0].Sequence, secondPage.History.Items[0].Sequence);
        }

        var events = await AuditEventsAsync(connection.Tenant);
        var disabled = Assert.Single(events, value => value.EventType == ConnectionAuditEventType.ConsentDisabled);
        Assert.Contains("Fictional access review", disabled.DetailsJson, StringComparison.Ordinal);
        var pause = Assert.Single(events, value => value.EventType == ConnectionAuditEventType.ConsentPauseNotified);
        using var details = JsonDocument.Parse(pause.DetailsJson);
        Assert.Equal("disabled", details.RootElement.GetProperty("reason").GetString());
        Assert.Equal("Fictional access review", details.RootElement.GetProperty("disabled_reason").GetString());
        Assert.Contains("Unknown user", details.RootElement.GetProperty("detail").GetString());
        Assert.Contains(YojanaRole.TenantAdministrator, details.RootElement.GetProperty("recipient_roles")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Contains(YojanaRole.IntegrationOperator, details.RootElement.GetProperty("recipient_roles")
            .EnumerateArray().Select(value => value.GetString()));
    }

    [DatabaseFact]
    public async Task Reminder_settings_are_validated_audited_and_do_not_resend_a_day_after_change()
    {
        await using var simulator = new Cx04Simulator();
        var connection = await ConnectionAsync(simulator);
        await ConsentAsync(connection);
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<Cx04ReminderSettings>();
            var defaults = await settings.GetAsync(default);
            Assert.Equal([14, 3], defaults.DaysBeforeExpiry);
            Assert.Equal([YojanaRole.IntegrationOperator], defaults.RecipientRoles);
            Assert.Equal(["in_app", "email"], defaults.Channels);
            foreach (var invalid in new[]
                     {
                         new UpdateCx04ReminderSettings([], [YojanaRole.IntegrationOperator], defaults.SettingsVersion),
                         new UpdateCx04ReminderSettings([14, 14], [YojanaRole.IntegrationOperator], defaults.SettingsVersion),
                         new UpdateCx04ReminderSettings([91], [YojanaRole.IntegrationOperator], defaults.SettingsVersion),
                         new UpdateCx04ReminderSettings([14], [], defaults.SettingsVersion),
                         new UpdateCx04ReminderSettings([14], ["fictional_role"], defaults.SettingsVersion),
                     })
                await Assert.ThrowsAsync<Cx04AuthorisationException>(() =>
                    settings.SetAsync(connection.ConnectionId, invalid, Actor, default));

            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var state = await db.Cx04AuthorisationStates.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId);
            state.RefreshChainExpiresAt = fixture.Clock.UtcNow.AddDays(10);
            await db.SaveChangesAsync();
            var reminders = scope.ServiceProvider.GetRequiredService<Cx04ConsentExpiryReminders>();
            Assert.Equal(1, await reminders.EvaluateAsync(default));
            Assert.Equal(0, await reminders.EvaluateAsync(default));

            var changed = await settings.SetAsync(connection.ConnectionId,
                new([14, 5], [YojanaRole.IntegrationOperator, YojanaRole.TenantAdministrator],
                    defaults.SettingsVersion), Actor, default);
            Assert.Equal([14, 5], changed.DaysBeforeExpiry);
            Assert.Equal(0, await reminders.EvaluateAsync(default));
            fixture.Clock.UtcNow += TimeSpan.FromDays(6);
            Assert.Equal(1, await reminders.EvaluateAsync(default));
            Assert.Equal(0, await reminders.EvaluateAsync(default));
            var view = await scope.ServiceProvider.GetRequiredService<Cx04Authorisation>()
                .ReadAsync(connection.ConnectionId, default);
            Assert.Equal([14, 5], view.ReminderDays);
            Assert.Equal([14, 5], view.RemindersSent);
            Assert.Null(view.NextReminderDays);
        }

        var events = await AuditEventsAsync(connection.Tenant);
        Assert.Single(events, value => value.EventType == ConnectionAuditEventType.ConsentReminderSettingsChanged);
        Assert.Single(events, value => value.EventType == ConnectionAuditEventType.ConsentExpiry14Days);
        Assert.Single(events, value => value.EventType == ConnectionAuditEventType.ConsentExpiryReminder);
    }

    [DatabaseFact]
    public async Task Expiry_pause_notice_fires_once_per_pause_and_a_fresh_sign_in_starts_a_new_pause()
    {
        await using var simulator = new Cx04Simulator();
        var connection = await ConnectionAsync(simulator);
        await ConsentAsync(connection);
        await ExpireAsync(connection);
        Assert.Single((await AuditEventsAsync(connection.Tenant)), value =>
            value.EventType == ConnectionAuditEventType.ConsentPauseNotified);
        using (var scope = fixture.ScopeFor(connection.Tenant))
            Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<Cx04ConsentExpiryReminders>()
                .EvaluateAsync(default));
        Assert.Single((await AuditEventsAsync(connection.Tenant)), value =>
            value.EventType == ConnectionAuditEventType.ConsentPauseNotified);

        fixture.Clock.UtcNow += TimeSpan.FromSeconds(1);
        await ConsentAsync(connection);
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var state = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>()
                .Cx04AuthorisationStates.SingleAsync(value => value.ConnectionId == connection.ConnectionId);
            Assert.Null(state.PauseNotifiedAt);
        }
        await ExpireAsync(connection);
        var pauses = (await AuditEventsAsync(connection.Tenant)).Where(value =>
            value.EventType == ConnectionAuditEventType.ConsentPauseNotified).ToList();
        Assert.Equal(2, pauses.Count);
        Assert.All(pauses, value =>
        {
            using var details = JsonDocument.Parse(value.DetailsJson);
            Assert.Equal("expired", details.RootElement.GetProperty("reason").GetString());
            var roles = details.RootElement.GetProperty("recipient_roles").EnumerateArray()
                .Select(role => role.GetString()).ToList();
            Assert.Contains(YojanaRole.TenantAdministrator, roles);
            Assert.Contains(YojanaRole.IntegrationOperator, roles);
        });
    }

    [DatabaseFact]
    public async Task Every_verified_outbound_type_maps_and_query_before_create_recovers_a_dropped_reply()
    {
        await using var simulator = new Cx04Simulator();
        simulator.SeedEntity("Default", Cx04Protocol.EndpointVersion, "InventoryItem",
            """{"id":"item-file","InventoryID":"FIC-PC-640","Description":"Fictional workstation","LastModifiedDateTime":"2026-09-01T08:00:00Z"}""");
        var connection = await ConnectionAsync(simulator);
        await ConsentAsync(connection);
        using var scope = fixture.ScopeFor(connection.Tenant);
        var documents = new Cx04OutboundDocuments(scope.ServiceProvider.GetRequiredService<Cx04Settings>(),
            scope.ServiceProvider.GetRequiredService<Cx04Client>(), new OutboundFacts(), new RenderedDocument());
        var unit = new UnitReference(Guid.CreateVersion7(), "FIC-SERIAL-640");
        var commands = new IOutboundCommand[]
        {
            new CreateReplacementDelivery(Guid.CreateVersion7(), "replacement:640", "FIC-REPLACEMENT-640",
                "FIC-CUSTOMER", "FIC-SITE", unit, OpenEnum.Known("warranty"), "FIC-INVOICE-640", "fault"),
            new CreateCreditMemo(Guid.CreateVersion7(), "credit:640", "FIC-CREDIT-640", "FIC-CUSTOMER",
                [new(null, "Returned storage", 1, new(25, "GBP"), "FIC-STORAGE-640",
                    [new("FIC-SERIAL-640")])], null, "FIC-INVOICE-640"),
            new PostWarrantyExpense(Guid.CreateVersion7(), "expense:640", "FIC-EXPENSE-640",
                [new("FIC-WARRANTY", 25, 0, "GBP"), new("FIC-RESERVE", 0, 25, "GBP")], "FIC-COHORT", false),
            new ChangeStockStatusOrLocation(Guid.CreateVersion7(), "move:640", "FIC-MOVE-640", unit,
                "QUARANTINE", "AVAILABLE", "inspection complete"),
            new AttachDocument(Guid.CreateVersion7(), "attachment:640", "FIC-ATTACHMENT-640",
                "items:item-file", "Inspection", OutboundDocumentContentReference.ForDocument(RenderedDocument.Id), null),
        };

        foreach (var command in commands)
        {
            var result = await documents.CreateAsync(Request(connection.ConnectionId, command), default);
            Assert.Equal(OutboundConnectorOutcome.Created, result.Outcome);
        }
        Assert.Single(simulator.Files);
        var serviceOrder = new CreateServiceOrder(Guid.CreateVersion7(), "service:640", "FIC-SERVICE-640",
            "FIC-CUSTOMER", unit, "covered", "does not start");
        Assert.Equal(OutboundConnectorOutcome.Unsupported,
            (await documents.CreateAsync(Request(connection.ConnectionId, serviceOrder), default)).Outcome);

        var dropped = new CreateCreditMemo(Guid.CreateVersion7(), "credit:dropped", "FIC-CREDIT-DROPPED",
            "FIC-CUSTOMER", [new(null, "Media retention", 1, new(10, "GBP"), "FIC-MEDIA")], null, null);
        simulator.Faults.DropNextCreateReply = true;
        await Assert.ThrowsAnyAsync<Exception>(() => documents.CreateAsync(Request(connection.ConnectionId, dropped), default));
        var recovered = await documents.FindByReferenceAsync(new(connection.ConnectionId,
            nameof(CreateCreditMemo), dropped.ErpReferenceText), default);
        Assert.NotNull(recovered);
        Assert.NotNull(await documents.FindByReferenceAsync(new(connection.ConnectionId,
            nameof(CreateReplacementDelivery), commands[0].ErpReferenceText), default));

        var rejected = new CreateCreditMemo(Guid.CreateVersion7(), "credit:rejected", "FIC-CREDIT-REJECTED",
            "FIC-CUSTOMER", Enumerable.Range(1, 1_001).Select(index => new CreditMemoLine(null,
                "Fictional rejected line " + index, 1, new(1, "GBP"), "FIC-PC-640")).ToArray(), null, null);
        Assert.Equal(OutboundConnectorOutcome.Rejected,
            (await documents.CreateAsync(Request(connection.ConnectionId, rejected), default)).Outcome);
        Assert.Equal(0, simulator.PasswordGrantRequestCount);
    }

    [DatabaseFact]
    public async Task Poll_covers_the_two_day_push_purge_window_and_push_hints_reget_the_record()
    {
        await using var simulator = new Cx04Simulator();
        simulator.SeedEntity("Default", Cx04Protocol.EndpointVersion, "InventoryItem",
            """{"id":"item-purge","InventoryID":"FIC-STORAGE-640","Description":"Fictional storage array","LastModifiedDateTime":"2026-09-02T00:00:00Z"}""");
        var connection = await ConnectionAsync(simulator);
        await ConsentAsync(connection);
        var cursor = new Cx04Watermark(new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero), "later").ToString();
        using var scope = fixture.ScopeFor(connection.Tenant);
        var adapter = scope.ServiceProvider.GetRequiredService<Cx04Adapter>();

        var poll = await adapter.PollAsync(new(connection.Tenant, connection.ConnectionId, "items", cursor,
            TimeSpan.FromDays(2), 100), default);
        var polled = Assert.Single(poll.Events);
        Assert.Equal("FIC-STORAGE-640", polled.Envelope.ErpExternalId);
        Assert.Equal("poll", polled.Envelope.CaptureMode.Value);

        var before = simulator.ProcessedRequestCount;
        var hinted = Assert.Single(await adapter.CaptureHintAsync(connection.ConnectionId,
            "items", "item-purge", default));
        Assert.True(simulator.ProcessedRequestCount > before);
        Assert.Equal("FIC-STORAGE-640", hinted.Envelope.ErpExternalId);
        Assert.Equal("webhook", hinted.Envelope.CaptureMode.Value);
        Assert.Equal(0, simulator.PasswordGrantRequestCount);
    }

    [DatabaseFact]
    public async Task Consent_rejects_bad_replayed_and_expired_state_without_password_grant()
    {
        await using var simulator = new Cx04Simulator();
        var connection = await ConnectionAsync(simulator);
        Cx04ConsentStart start;
        using (var scope = fixture.ScopeFor(connection.Tenant))
            start = await scope.ServiceProvider.GetRequiredService<Cx04Authorisation>()
                .BeginAsync(connection.ConnectionId, default);

        var callback = await FollowConsentAsync(start);
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var service = scope.ServiceProvider.GetRequiredService<Cx04Authorisation>();
            var bad = await Assert.ThrowsAsync<Cx04AuthorisationException>(() => service.CompleteAsync(
                connection.ConnectionId, callback with { State = "not-the-issued-state" }, Actor, default));
            Assert.Equal("oauth_state_invalid", bad.Rule);
            var result = await service.CompleteAsync(connection.ConnectionId, callback, Actor, default);
            Assert.Equal(Cx04AuthorisationStatus.Active, result.Status);
            var replayed = await Assert.ThrowsAsync<Cx04AuthorisationException>(() => service.CompleteAsync(
                connection.ConnectionId, callback, Actor, default));
            Assert.Equal("oauth_state_invalid", replayed.Rule);
        }

        var expiredConnection = await ConnectionAsync(simulator);
        using (var scope = fixture.ScopeFor(expiredConnection.Tenant))
        {
            var service = scope.ServiceProvider.GetRequiredService<Cx04Authorisation>();
            var expiredStart = await service.BeginAsync(expiredConnection.ConnectionId, default);
            var expiredCallback = await FollowConsentAsync(expiredStart);
            fixture.Clock.UtcNow = expiredStart.ExpiresAt.AddSeconds(1);
            var expired = await Assert.ThrowsAsync<Cx04AuthorisationException>(() => service.CompleteAsync(
                expiredConnection.ConnectionId, expiredCallback, Actor, default));
            Assert.Equal("oauth_state_invalid", expired.Rule);
        }

        var otherTenant = await fixture.NewTenantAsync("c4iso-");
        using (var scope = fixture.ScopeFor(otherTenant))
        {
            var isolated = await Assert.ThrowsAsync<Cx04AuthorisationException>(() => scope.ServiceProvider
                .GetRequiredService<Cx04Authorisation>().ReadAsync(connection.ConnectionId, default));
            Assert.Equal("connection_not_found", isolated.Rule);
        }
        Assert.Equal(1, simulator.AuthorisationCodeGrantRequestCount);
        Assert.Equal(0, simulator.PasswordGrantRequestCount);
    }

    [DatabaseFact]
    public async Task Refresh_is_serialised_and_invalid_grant_preserves_queued_work_until_reconsent()
    {
        await using var simulator = new Cx04Simulator();
        simulator.SeedEntity("Default", Cx04Protocol.EndpointVersion, "InventoryItem",
            """{"id":"item-id","InventoryID":"FIC-SERVER-640","Description":"Fictional rack server","LastModifiedDateTime":"2026-09-01T08:00:00Z"}""");
        var connection = await ConnectionAsync(simulator);
        await ConsentAsync(connection);
        fixture.Clock.UtcNow += TimeSpan.FromHours(2);
        var target = new Uri(simulator.BaseUri,
            $"entity/Default/{Cx04Protocol.EndpointVersion}/InventoryItem/item-id");

        async Task ReadAsync()
        {
            using var scope = fixture.ScopeFor(connection.Tenant);
            using var _ = await scope.ServiceProvider.GetRequiredService<Cx04Client>()
                .GetAsync(connection.ConnectionId, target, default);
        }
        await Task.WhenAll(ReadAsync(), ReadAsync(), ReadAsync());
        Assert.Equal(1, simulator.RefreshGrantRequestCount);
        using (var read = fixture.ScopeFor(connection.Tenant))
        {
            var history = (await read.ServiceProvider.GetRequiredService<Cx04Authorisation>()
                .ReadAsync(connection.ConnectionId, default)).History.Items;
            var renewed = Assert.Single(history, item => item.Kind == "access_renewed");
            Assert.Equal(1, renewed.Count);
        }
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Single(await db.ConnectionCredentials.Where(value => value.ConnectionId == connection.ConnectionId
                && value.Purpose == Cx04Client.TokenPurpose && value.RotatedAt == null
                && value.RetiredAt == null).ToListAsync());
            Assert.Single(await db.ConnectionCredentials.Where(value => value.ConnectionId == connection.ConnectionId
                && value.Purpose == Cx04Client.TokenPurpose && value.RotatedAt != null).ToListAsync());
            db.ConnectionPollWatermarks.Add(new()
            {
                TenantId = connection.Tenant, ConnectionId = connection.ConnectionId, EntityType = "items",
                Watermark = "FIC-WATERMARK", IntervalSeconds = 60, OverlapSeconds = 86400,
                NextPollAt = fixture.Clock.UtcNow,
            });
            db.OutboundCommands.Add(new()
            {
                TenantId = connection.Tenant, OutboundCommandId = Guid.CreateVersion7(),
                ConnectionId = connection.ConnectionId, CommandType = "CreateCreditMemo",
                IdempotencyKey = Guid.CreateVersion7(), ModuleDocumentRef = "FIC-CREDIT-640",
                ErpReferenceText = "FIC-CREDIT-640", Payload = "{}", Status = OutboundCommandStatus.Pending,
                CreatedAt = fixture.Clock.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        fixture.Clock.UtcNow += TimeSpan.FromHours(2);
        simulator.Faults.RejectNextRefreshAsInvalidGrant = true;
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var exception = await Assert.ThrowsAsync<Cx04AuthorisationException>(() => scope.ServiceProvider
                .GetRequiredService<Cx04Client>().GetAsync(connection.ConnectionId, target, default));
            Assert.Equal("reconsent_required", exception.Rule);
        }
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Equal(ConnectionStatus.Paused, (await db.Connections.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId)).Status);
            Assert.Equal(Cx04AuthorisationStatus.ConsentRequired, (await db.Cx04AuthorisationStates.SingleAsync(
                value => value.ConnectionId == connection.ConnectionId)).Status);
            Assert.Equal("FIC-WATERMARK", (await db.ConnectionPollWatermarks.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId && value.EntityType == "items")).Watermark);
            Assert.Equal(OutboundCommandStatus.Pending, (await db.OutboundCommands.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId)).Status);
            Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<Cx04Authorisation>()
                .ReadAsync(connection.ConnectionId, default)).WaitingOutbound);
        }
        var refusedPause = Assert.Single((await AuditEventsAsync(connection.Tenant)), value =>
            value.EventType == ConnectionAuditEventType.ConsentPauseNotified);
        using (var details = JsonDocument.Parse(refusedPause.DetailsJson))
            Assert.Equal("refused_by_erp", details.RootElement.GetProperty("reason").GetString());

        await ConsentAsync(connection);
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Equal(ConnectionStatus.Active, (await db.Connections.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId)).Status);
            Assert.Equal("FIC-WATERMARK", (await db.ConnectionPollWatermarks.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId && value.EntityType == "items")).Watermark);
            Assert.Equal(OutboundCommandStatus.Pending, (await db.OutboundCommands.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId)).Status);
            Assert.Null((await db.Cx04AuthorisationStates.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId)).PauseNotifiedAt);
        }
        Assert.Equal(0, simulator.PasswordGrantRequestCount);
    }

    [DatabaseFact]
    public async Task Expiry_reminders_fire_once_and_disable_erases_tokens_stops_sync_and_is_audited()
    {
        await using var simulator = new Cx04Simulator();
        var connection = await ConnectionAsync(simulator);
        await ConsentAsync(connection);
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var state = await db.Cx04AuthorisationStates.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId);
            state.RefreshChainExpiresAt = fixture.Clock.UtcNow.AddDays(10);
            await db.SaveChangesAsync();
            var reminders = scope.ServiceProvider.GetRequiredService<Cx04ConsentExpiryReminders>();
            Assert.Equal(1, await reminders.EvaluateAsync(default));
            Assert.Equal(0, await reminders.EvaluateAsync(default));
        }
        fixture.Clock.UtcNow += TimeSpan.FromDays(8);
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var reminders = scope.ServiceProvider.GetRequiredService<Cx04ConsentExpiryReminders>();
            Assert.Equal(1, await reminders.EvaluateAsync(default));
            Assert.Equal(0, await reminders.EvaluateAsync(default));
            var disabled = await scope.ServiceProvider.GetRequiredService<Cx04Authorisation>()
                .DisableAsync(connection.ConnectionId, new("Fictional access review", true), Actor, default);
            Assert.True(disabled.TokensDeleted);
            Assert.True(disabled.RemoteSignOutAttempted);
        }
        using (var scope = fixture.ScopeFor(connection.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            Assert.Empty(await db.ConnectionCredentials.Where(value => value.ConnectionId == connection.ConnectionId
                && (value.Purpose == Cx04Client.TokenPurpose || value.Purpose == Cx04Client.PkcePurpose)).ToListAsync());
            Assert.Equal(Cx04AuthorisationStatus.Disabled, (await db.Cx04AuthorisationStates.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId)).Status);
            Assert.Equal(ConnectionStatus.Paused, (await db.Connections.SingleAsync(value =>
                value.ConnectionId == connection.ConnectionId)).Status);
        }
        var events = new List<ConnectionAuditEvent>();
        using (var auditScope = fixture.ScopeFor(connection.Tenant))
        {
            var audit = auditScope.ServiceProvider.GetRequiredService<IConnectionAuditSink>();
            await foreach (var item in audit.ReadAsync(connection.Tenant,
                               DateOnly.FromDateTime(fixture.Clock.UtcNow.AddDays(-30)),
                               DateOnly.FromDateTime(fixture.Clock.UtcNow.AddDays(1)), default)) events.Add(item);
        }
        Assert.Single(events, value => value.EventType == ConnectionAuditEventType.ConsentExpiry14Days);
        Assert.Single(events, value => value.EventType == ConnectionAuditEventType.ConsentExpiry3Days);
        Assert.Single(events, value => value.EventType == ConnectionAuditEventType.ConsentDisabled);
        Assert.Equal(1, simulator.LogoutRequestCount);
        Assert.Equal(0, simulator.PasswordGrantRequestCount);
    }

    private async Task ExpireAsync((TenantId Tenant, Guid ConnectionId) connection)
    {
        using var scope = fixture.ScopeFor(connection.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var state = await db.Cx04AuthorisationStates.SingleAsync(value =>
            value.ConnectionId == connection.ConnectionId);
        state.RefreshChainExpiresAt = fixture.Clock.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        _ = await scope.ServiceProvider.GetRequiredService<Cx04ConsentExpiryReminders>().EvaluateAsync(default);
    }

    private async Task<List<ConnectionAuditEvent>> AuditEventsAsync(TenantId tenant)
    {
        var events = new List<ConnectionAuditEvent>();
        using var scope = fixture.ScopeFor(tenant);
        var audit = scope.ServiceProvider.GetRequiredService<IConnectionAuditSink>();
        await foreach (var item in audit.ReadAsync(tenant,
                           DateOnly.FromDateTime(fixture.Clock.UtcNow.AddDays(-120)),
                           DateOnly.FromDateTime(fixture.Clock.UtcNow.AddDays(1)), default))
            events.Add(item);
        return events;
    }

    private async Task<(TenantId Tenant, Guid ConnectionId)> ConnectionAsync(Cx04Simulator simulator)
    {
        var tenant = await fixture.NewTenantAsync("cx04-");
        var connectionId = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(),
            Cx04Capabilities.ConnectorCode);
        using var scope = fixture.ScopeFor(tenant);
        var settings = new Cx04ConnectionSettings(simulator.BaseUri, "fictional-connected-application",
            new Uri("https://yojana.invalid/cx04/callback"),
            Yojana.Integration.Connectors.CX04.Cx04LicenceTier.Essentials);
        await scope.ServiceProvider.GetRequiredKeyedService<IConnectionLifecycleAdapter>(Cx04Capabilities.ConnectorCode)
            .ConfigureAsync(connectionId, Cx04Settings.ToJson(settings), default);
        return (tenant, connectionId);
    }

    private async Task ConsentAsync((TenantId Tenant, Guid ConnectionId) connection)
    {
        Cx04ConsentStart start;
        using (var begin = fixture.ScopeFor(connection.Tenant))
            start = await begin.ServiceProvider.GetRequiredService<Cx04Authorisation>()
                .BeginAsync(connection.ConnectionId, default);
        var callback = await FollowConsentAsync(start);
        using var complete = fixture.ScopeFor(connection.Tenant);
        await complete.ServiceProvider.GetRequiredService<Cx04Authorisation>()
            .CompleteAsync(connection.ConnectionId, callback, Actor, default);
    }

    private static async Task<Cx04ConsentCallback> FollowConsentAsync(Cx04ConsentStart start)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler);
        using var response = await client.GetAsync(start.ConsentUrl);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query);
        return new(query["code"]!, query["state"]!);
    }

    private static OutboundCreateRequest Request(Guid connectionId, IOutboundCommand command) => new(connectionId,
        Guid.CreateVersion7(), command.GetType().Name, command.ErpReferenceText,
        System.Text.Json.JsonSerializer.Serialize(command, command.GetType(), IntegrationContracts.Json),
        command.IdempotencyKey);

    private sealed class OutboundFacts : ICx04OutboundFacts
    {
        public Task<Cx04OutboundUnit?> UnitAsync(Guid connectionId, Guid unitId, string? suppliedSerial,
            CancellationToken ct) => Task.FromResult<Cx04OutboundUnit?>(new("FIC-PC-640",
            suppliedSerial ?? "FIC-SERIAL-640"));
        public Task<string?> ReferenceAsync(Guid connectionId, string kind, string reference,
            CancellationToken ct) => Task.FromResult<string?>(reference);
    }

    private sealed class RenderedDocument : IRenderedOutboundDocumentPort
    {
        public static readonly Guid Id = Guid.Parse("64000000-0000-0000-0000-000000000640");
        public Task<OutboundDocumentDescription?> DescribeAsync(Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult<OutboundDocumentDescription?>(new("inspection.txt", "text/plain"));
        public Task<RenderedOutboundDocument?> ReadAsync(Guid documentId, CancellationToken cancellationToken) =>
            Task.FromResult<RenderedOutboundDocument?>(new("inspection.txt", "text/plain", "fictional"u8.ToArray()));
    }
}
