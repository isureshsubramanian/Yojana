using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Backfills;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Gateway;
using Yojana.Integration.Inbox;
using Yojana.Integration.Notifications;
using Yojana.Integration.Outbox;
using Yojana.Integration.Reconciliation;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ConnectionLifecycleTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-operator";

    [DatabaseFact]
    public async Task Registration_is_read_only_idempotent_and_preserves_survey_and_encrypted_secrets()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await NewTenant();
        using var scope = fixture.ScopeFor(tenant);
        var commands = scope.ServiceProvider.GetRequiredService<ConnectionCommands>();
        var request = simulator.RegistrationRequest();
        var result = await commands.RegisterAsync(request, Actor, default);
        var calls = simulator.RequestCount;
        var repeat = await commands.RegisterAsync(request, Actor, default);
        Assert.Equal(result.ConnectionId, repeat.ConnectionId);
        Assert.Equal(calls, simulator.RequestCount);
        Assert.Equal("degraded", result.HealthStatus);
        Assert.Contains(result.Checklist, item => item.Code == "smoke_read" && item.Result == "pass");
        Assert.Contains(result.Checklist, item => item.Result == "needs_owner");
        Assert.NotNull(result.SurveySummary);
        var detail = await scope.ServiceProvider.GetRequiredService<ConnectionQueries>().DetailAsync(result.ConnectionId, default);
        Assert.Equal("reference_only", detail.Settings.OutboundMode);
        Assert.True(detail.Settings.ReadOnly);
        Assert.False(detail.Connection.IsLive);
        Assert.NotEmpty(detail.Capabilities);
        Assert.All(detail.Credentials, credential => Assert.True(credential.IsSet));
        Assert.DoesNotContain(request.Credentials[0].Secret, JsonSerializer.Serialize(detail));
        Assert.False(simulator.SerialQueryCreated);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(1, await db.Connections.CountAsync());
        Assert.Contains(await db.ConnectionEvents.ToListAsync(), item => item.EventType == "ConnectionRegistered" && item.ActorRef == Actor);
    }

    [DatabaseFact]
    public async Task Failed_smoke_read_is_committed_as_down_and_can_be_retried()
    {
        await using var simulator = new Cx01Simulator { FailMetadataRead = true };
        var tenant = await NewTenant();
        using var scope = fixture.ScopeFor(tenant);
        var commands = scope.ServiceProvider.GetRequiredService<ConnectionCommands>();
        var result = await commands.RegisterAsync(simulator.RegistrationRequest(), Actor, default);
        Assert.Equal("down", result.HealthStatus);
        var queries = scope.ServiceProvider.GetRequiredService<ConnectionQueries>();
        Assert.Equal("authorisation_refused", (await queries.HealthAsync(result.ConnectionId, default)).Health.LastErrorCode);
        simulator.FailMetadataRead = false;
        var retried = await commands.SmokeReadAsync(result.ConnectionId, default);
        Assert.Equal("degraded", retried.HealthStatus);
        Assert.Contains(retried.Checklist, item => item.Code == "smoke_read" && item.Result == "pass");
        Assert.Null((await queries.HealthAsync(result.ConnectionId, default)).Health.LastErrorMessage);
    }

    [DatabaseFact]
    public async Task Every_archive_precondition_blocks_and_force_requires_admin_and_reason()
    {
        foreach (var kind in new[] { "outbox", "dead_letter", "backfill", "manual" })
        {
            await using var simulator = new Cx01Simulator();
            var tenant = await NewTenant();
            using var scope = fixture.ScopeFor(tenant);
            var commands = scope.ServiceProvider.GetRequiredService<ConnectionCommands>();
            var result = await commands.RegisterAsync(simulator.RegistrationRequest(), Actor, default);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var id = result.ConnectionId;
            var letter = Letter(tenant, id);
            if (kind == "outbox") db.OutboundCommands.Add(Outbound(tenant, id));
            if (kind == "dead_letter") db.DeadLetterItems.Add(letter);
            if (kind == "backfill") db.BackfillCursors.Add(Backfill(tenant, id));
            if (kind == "manual")
            {
                letter.Status = DeadLetterStatus.Resolved;
                db.DeadLetterItems.Add(letter);
                db.ManualOutboundTasks.Add(new()
                {
                    TenantId = tenant, ConnectionId = id, ManualOutboundTaskId = Guid.CreateVersion7(),
                    OutboundCommandId = Guid.CreateVersion7(), DeadLetterItemId = letter.DeadLetterItemId,
                    CommandType = "CreateCreditMemo", Desk = "finance", Draft = "{}", Status = "open", CreatedAt = fixture.Clock.UtcNow,
                });
            }
            await db.SaveChangesAsync();
            var blocked = await commands.ArchiveAsync(id, new(), Actor, false, default);
            Assert.False(blocked.Archived);
            Assert.True(blocked.Preconditions.Blocked);
            Assert.Equal(1, blocked.Preconditions.WaitingOutbox + blocked.Preconditions.OpenDeadLetters
                + blocked.Preconditions.RunningBackfills + blocked.Preconditions.OpenManualTasks);
            Assert.Equal("force_archive_forbidden", (await Assert.ThrowsAsync<ConnectionRuleException>(() =>
                commands.ArchiveAsync(id, new(true, "Fictional review"), Actor, false, default))).Rule);
            Assert.Equal("force_archive_reason_required", (await Assert.ThrowsAsync<ConnectionRuleException>(() =>
                commands.ArchiveAsync(id, new(true), Actor, true, default))).Rule);
            Assert.True((await commands.ArchiveAsync(id, new(true, "Fictional review"), Actor, true, default)).Archived);
        }
    }

    [DatabaseFact]
    public async Task Archive_closes_maps_retires_secrets_and_all_scheduled_remote_work_skips_it()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await NewTenant();
        using var scope = fixture.ScopeFor(tenant);
        var commands = scope.ServiceProvider.GetRequiredService<ConnectionCommands>();
        var registered = await commands.RegisterAsync(simulator.RegistrationRequest(), Actor, default);
        var id = registered.ConnectionId;
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ExternalIdMaps.Add(new()
        {
            TenantId = tenant, ConnectionId = id, ExternalIdMapId = Guid.CreateVersion7(), EntityType = "item",
            ExternalId = "FICTIONAL-1", ModuleId = Guid.CreateVersion7(), ValidFrom = fixture.Clock.UtcNow.AddDays(-1),
            NaturalKeyHash = new string('a', 64), MappingSource = "operator", MappedBy = Actor, MappedAt = fixture.Clock.UtcNow,
        });
        db.BackfillCursors.Add(Backfill(tenant, id));
        db.OutboundCommands.Add(Outbound(tenant, id));
        await db.SaveChangesAsync();
        var archive = await commands.ArchiveAsync(id, new(true, "Fictional replacement connection ready"), Actor, true, default);
        Assert.True(archive.Archived);
        var before = simulator.RequestCount;
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ConnectionPollingService>().PollDueAsync(default));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>().WatchdogConnectionAsync(id, default));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>().WatchdogTenantAsync(default));
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<ReconciliationService>().RunScheduledAsync(default));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<BackfillService>().RunPendingAsync(default));
        await scope.ServiceProvider.GetRequiredService<BackfillService>().ProcessAsync(
            (await db.BackfillCursors.AsNoTracking().SingleAsync()).BackfillCursorId, 1, default);
        Assert.Equal(new OutboundRelayResult(0, 0, 0), await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(id, default));
        Assert.Equal(before, simulator.RequestCount);
        Assert.NotNull((await db.ExternalIdMaps.AsNoTracking().SingleAsync()).ValidTo);
        Assert.All(await db.ConnectionCredentials.AsNoTracking().ToListAsync(), row => Assert.NotNull(row.RetiredAt));
        Assert.Null(await scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>()
            .GetCurrentAsync(tenant, id, simulator.RegistrationRequest().Credentials[0].Purpose, default));
        Assert.Equal("archived", (await scope.ServiceProvider.GetRequiredService<ConnectionQueries>().HealthAsync(id, default)).Health.Status);
        await Assert.ThrowsAsync<ConnectionRuleException>(() => commands.SmokeReadAsync(id, default));
    }

    [DatabaseFact]
    public async Task Health_counts_capture_waiting_work_resolution_and_does_not_duplicate_connection_attention()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await NewTenant();
        using var scope = fixture.ScopeFor(tenant);
        var registered = await scope.ServiceProvider.GetRequiredService<ConnectionCommands>()
            .RegisterAsync(simulator.RegistrationRequest(), Actor, default);
        var id = registered.ConnectionId;
        var captured = await scope.ServiceProvider.GetRequiredService<Cx01Adapter>().PollAsync(
            new(tenant, id, "deliveries", null, TimeSpan.FromMinutes(2), 1), default);
        await scope.ServiceProvider.GetRequiredService<InboundMessageStore>().AcceptMany(captured.Events, default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var outbound = Outbound(tenant, id);
        var letter = Letter(tenant, id);
        db.OutboundCommands.Add(outbound);
        db.DeadLetterItems.Add(letter);
        await db.SaveChangesAsync();
        var projector = scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>();
        var row = await projector.RefreshAsync(id, default);
        Assert.Equal(1, row.ReceivedToday);
        Assert.Equal(1, row.OutboxDepth);
        Assert.Equal(1, row.OpenDeadLetters);
        Assert.NotNull(row.LastInboundAt);
        Assert.Equal(2, (await scope.ServiceProvider.GetRequiredService<DeadLetterService>().AttentionAsync(default)).NeedsAttention);
        letter.Status = DeadLetterStatus.Resolved;
        outbound.Status = OutboundCommandStatus.Done;
        await db.SaveChangesAsync();
        row = await projector.RefreshAsync(id, default);
        Assert.Equal(0, row.OutboxDepth);
        Assert.Equal(0, row.OpenDeadLetters);
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<DeadLetterService>().AttentionAsync(default)).NeedsAttention);
        var queries = scope.ServiceProvider.GetRequiredService<ConnectionQueries>();
        var statusJson = JsonSerializer.Serialize(await queries.StatusAsync(null, 10, default));
        Assert.DoesNotContain("Error", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Credential", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ReplaceBy", statusJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fictional-operator", JsonSerializer.Serialize(await queries.EventsAsync(id, null, 1, default)));
        await scope.ServiceProvider.GetRequiredService<Yojana.Integration.Security.Audit.IConnectionAuditSink>().AppendAsync(
            new(tenant, Guid.CreateVersion7(), id, Yojana.Integration.Security.Audit.ConnectionAuditEventType.ConnectionWrite,
                Actor, fixture.Clock.UtcNow, "{}"), default);
        var events = await queries.EventsAsync(id, null, 1, default);
        Assert.NotNull(events.NextCursor);
        var nextEvents = await queries.EventsAsync(id, events.NextCursor, 1, default);
        Assert.NotEqual(events.Items[0].Sequence, nextEvents.Items[0].Sequence);
    }

    [DatabaseFact]
    public async Task Projection_row_is_rls_isolated_and_connection_and_status_lists_are_keyset_paged()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await NewTenant();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var commands = scope.ServiceProvider.GetRequiredService<ConnectionCommands>();
            await commands.RegisterAsync(simulator.RegistrationRequest(), Actor, default);
            await commands.RegisterAsync(simulator.RegistrationRequest(), Actor, default);
            var queries = scope.ServiceProvider.GetRequiredService<ConnectionQueries>();
            var first = await queries.ListAsync(null, 1, false, default);
            Assert.Single(first.Items);
            Assert.NotNull(first.NextCursor);
            var next = await queries.ListAsync(first.NextCursor, 1, false, default);
            Assert.Single(next.Items);
            Assert.NotEqual(first.Items[0].ConnectionId, next.Items[0].ConnectionId);
        }
        var other = await NewTenant();
        using var otherScope = fixture.ScopeFor(other);
        var db = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Empty(await db.ConnectionHealth.IgnoreQueryFilters().ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM integration.connection_health").SingleAsync());
        db.ConnectionHealth.Add(new() { TenantId = tenant, ConnectionId = Guid.CreateVersion7() });
        var denied = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(33504, Assert.IsType<Microsoft.Data.SqlClient.SqlException>(denied.InnerException).Number);
    }

    [DatabaseFact]
    public async Task Survey_keeps_unknowns_and_records_only_the_table_named_by_an_observed_denial()
    {
        await using var simulator = new Cx01Simulator { SameWarehouseReturnPosted = true, TrackingTablesAllowed = false };
        var tenant = await NewTenant();
        using var scope = fixture.ScopeFor(tenant);
        var request = simulator.RegistrationRequest();
        var configured = request.ConnectorSettings.Deserialize<Cx01ConnectionSettings>(IntegrationContracts.Json)!;
        request = request with { ConnectorSettings = JsonSerializer.SerializeToElement(configured with
            { AllowlistedTrackingTables = Cx01TrackingPermissions.Tables }, IntegrationContracts.Json) };
        var result = await scope.ServiceProvider.GetRequiredService<ConnectionCommands>().RegisterAsync(request, Actor, default);
        Assert.False(simulator.SerialQueryCreated);
        var surveyService = scope.ServiceProvider.GetRequiredService<Cx01SurveyService>();
        var before = await surveyService.LatestAsync(result.ConnectionId, default);
        Assert.All(before!.TrackingTableAllowlist.Values, value => Assert.Equal("owner_declared", value.Source));
        await scope.ServiceProvider.GetRequiredService<Cx01Adapter>().PollAsync(new(tenant, result.ConnectionId,
            "deliveries", null, TimeSpan.FromMinutes(2), 1), default);
        var after = await surveyService.LatestAsync(result.ConnectionId, default);
        var denied = Assert.Single(after!.TrackingTableAllowlist.Values, value => value.State == "not_allowed");
        Assert.Equal("remote_table_denied", denied.Source);
        Assert.NotNull(denied.ObservedAt);
        Assert.Equal(2, after.TrackingTableAllowlist.Values.Count(value => value.State == "allowed"));
        var refreshed = await surveyService.RunAsync(result.ConnectionId, new(), default);
        Assert.Single(refreshed.TrackingTableAllowlist.Values, value => value.State == "not_allowed");
        Assert.Equal(2, refreshed.TrackingTableAllowlist.Values.Count(value => value.State == "unknown"));
        Assert.False(simulator.SerialQueryCreated);
    }

    [DatabaseFact]
    public async Task Watchdog_records_stopped_and_resumed_streams_and_archive_deletes_only_its_subscription()
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await NewTenant();
        using var scope = fixture.ScopeFor(tenant);
        var commands = scope.ServiceProvider.GetRequiredService<ConnectionCommands>();
        var request = simulator.RegistrationRequest();
        var registered = await commands.RegisterAsync(request, Actor, default);
        var id = registered.ConnectionId;
        var signing = await scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>().EncryptAsync(
            tenant, id, Cx01WebhookEndpoints.SigningCredentialPurpose, new byte[32], null,
            fixture.Clock.UtcNow.AddDays(90), default);
        await scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>().SaveAsync(signing, default);
        var notifications = scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>();
        await notifications.SubscribeAsync(id, new Uri("https://fictional.invalid/notifications"), default);
        simulator.SubscriptionStopped = true;
        await notifications.WatchdogConnectionAsync(id, default);
        var state = await scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>().RefreshAsync(id, default);
        Assert.Equal("resumed_by_watchdog", state.NotificationState);
        var events = await scope.ServiceProvider.GetRequiredService<ConnectionQueries>().EventsAsync(id, null, 50, default);
        Assert.Contains(events.Items, item => item.Kind == "StreamStopped");
        Assert.Contains(events.Items, item => item.Kind == "StreamResumedByWatchdog");
        var archive = await commands.ArchiveAsync(id, new(), Actor, false, default);
        Assert.True(archive.Archived);
        Assert.All(simulator.DeletedSubscriptionPaths, path => Assert.Contains(
            simulator.OwnSubscriptionId(tenant.Value, id), path, StringComparison.Ordinal));
        Assert.NotEmpty(simulator.DeletedSubscriptionPaths);
    }

    [DatabaseFact]
    public Task Normal_archive_with_unsubscribe_failure_is_retryable_without_local_changes() =>
        AssertUnsubscribeFailureAsync(false);

    [DatabaseFact]
    public Task Force_archive_with_unsubscribe_failure_records_audit_and_integration_cleanup_task() =>
        AssertUnsubscribeFailureAsync(true);

    private async Task AssertUnsubscribeFailureAsync(bool force)
    {
        await using var simulator = new Cx01Simulator();
        var tenant = await NewTenant();
        using var scope = fixture.ScopeFor(tenant);
        var commands = scope.ServiceProvider.GetRequiredService<ConnectionCommands>();
        var registered = await commands.RegisterAsync(simulator.RegistrationRequest(), Actor, default);
        var id = registered.ConnectionId;
        var signing = await scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>().EncryptAsync(
            tenant, id, Cx01WebhookEndpoints.SigningCredentialPurpose, new byte[32], null,
            fixture.Clock.UtcNow.AddDays(90), default);
        await scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>().SaveAsync(signing, default);
        await scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>()
            .SubscribeAsync(id, new Uri("https://fictional.invalid/notifications"), default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var before = await db.ConnectionHealth.AsNoTracking().SingleAsync();
        var eventCount = await db.ConnectionEvents.CountAsync();
        simulator.FailUnsubscribe = true;
        var request = new ArchiveConnection(force, force ? "Remote access has been removed by the customer." : null);
        var archived = await commands.ArchiveAsync(id, request, Actor, force, default);
        Assert.Equal(force, archived.Archived);
        Assert.False(simulator.WebhookSubscriptionDeleted);
        var connection = await db.Connections.AsNoTracking().SingleAsync();
        if (!force)
        {
            Assert.Equal("unsubscribe_failed", archived.CleanupErrorCode);
            Assert.Equal(ConnectionStatus.Active, connection.Status);
            Assert.Null(connection.ArchivedAt);
            Assert.All(await db.ConnectionCredentials.AsNoTracking().ToListAsync(), row => Assert.Null(row.RetiredAt));
            Assert.All(await db.ConnectionNotificationSources.AsNoTracking().ToListAsync(), row => Assert.True(row.Enabled));
            Assert.Equal(eventCount, await db.ConnectionEvents.CountAsync());
            Assert.Empty(await db.ManualOutboundTasks.ToListAsync());
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await db.ConnectionHealth.AsNoTracking().SingleAsync()));
            simulator.FailUnsubscribe = false;
            Assert.True((await commands.ArchiveAsync(id, new(), Actor, false, default)).Archived);
            return;
        }
        Assert.Equal(ConnectionStatus.Archived, connection.Status);
        Assert.NotNull(connection.ArchivedAt);
        Assert.All(await db.ConnectionCredentials.AsNoTracking().ToListAsync(), row => Assert.NotNull(row.RetiredAt));
        Assert.All(await db.ConnectionNotificationSources.AsNoTracking().ToListAsync(), row => Assert.False(row.Enabled));
        var audit = await db.ConnectionEvents.SingleAsync(row => row.EventType == "RemoteSubscriptionLeft");
        Assert.Contains("unsubscribe_failed", audit.DetailsJson);
        Assert.DoesNotContain("Fictional remote detail", audit.DetailsJson);
        Assert.Equal(Actor, audit.ActorRef);
        var task = await db.ManualOutboundTasks.SingleAsync();
        Assert.Equal("integration", task.Desk);
        Assert.Equal(ManualTaskCommandType.RemoveRemoteSubscription, task.CommandType);
        Assert.Equal(ManualTaskStatus.Open, task.Status);
        Assert.Null(task.OutboundCommandId);
        Assert.Null(task.DeadLetterItemId);
        Assert.Contains("customer's administrator", task.Draft);
        Assert.Contains("remove it there", task.Draft);
        Assert.DoesNotContain("Fictional remote detail", task.Draft);
        Assert.True((await commands.ArchiveAsync(id, request, Actor, true, default)).Archived);
        Assert.Equal(1, await db.ManualOutboundTasks.CountAsync());
        Assert.Equal(1, await db.ConnectionEvents.CountAsync(row => row.EventType == "RemoteSubscriptionLeft"));
        var desk = scope.ServiceProvider.GetRequiredService<DeadLetterService>();
        Assert.Single((await desk.ManualTasksAsync("open", "integration", null, 10, default)).Items);
        var completed = await desk.CompleteManualTaskAsync(task.ManualOutboundTaskId, new(), Actor, default);
        Assert.Equal(ManualTaskStatus.Completed, completed.Status);
        Assert.Equal(Actor, completed.CompletedBy);
        Assert.Empty(await db.OutboundCommands.ToListAsync());
        Assert.Empty(await db.ExternalIdMaps.ToListAsync());
    }

    [DatabaseFact]
    public async Task Minute_refresh_preserves_folded_days_and_rollover_folds_the_whole_previous_day_once()
    {
        var originalNow = fixture.Clock.UtcNow;
        try
        {
            fixture.Clock.UtcNow = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
            var today = fixture.Clock.UtcNow.Date;
            var tenant = await fixture.NewTenantAsync("daily-health");
            var id = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            InboundMessage Message(DateTime acceptedAt) => new()
            {
                TenantId = tenant, ConnectionId = id, InboundMessageId = Guid.CreateVersion7(), MessageId = Guid.CreateVersion7(),
                ConnectorCode = IntegrationHostFixture.FakeConnectorCode, ErpEntity = "fictional-record",
                ErpExternalId = Guid.NewGuid().ToString(), ErpChangeMarker = "1", MarkerKind = "version",
                ContractVersion = "1.0", MessageType = "ItemUpserted", RawPayload = "{}", CanonicalMessage = "{}",
                AcceptedAt = acceptedAt, OccurredAt = acceptedAt, ObservedAt = acceptedAt,
                TranslationStatus = InboundTranslationStatus.Completed, IsLatest = true,
            };
            db.InboundMessages.AddRange(Message(today.AddDays(-30)), Message(today.AddDays(-29)),
                Message(today.AddDays(-1)), Message(today.AddHours(1)), Message(today.AddDays(1)));
            await db.SaveChangesAsync();
            var projector = scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>();
            var first = await projector.RefreshAsync(id, default);
            Assert.Equal(1, first.ReceivedToday);
            Assert.Equal(3, first.ReceivedLast30Days);
            var folded = first.CompletedInboundDaysJson;
            // Removing old operational rows must not make the minute job recount completed days.
            await db.InboundMessages.Where(row => row.AcceptedAt < today).ExecuteDeleteAsync();
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            var minute = await projector.RefreshAsync(id, default);
            Assert.Equal(folded, minute.CompletedInboundDaysJson);
            Assert.Equal(3, minute.ReceivedLast30Days);
            db.InboundMessages.Add(Message(today.AddHours(23)));
            await db.SaveChangesAsync();
            fixture.Clock.UtcNow = today.AddDays(1).AddMinutes(1);
            var rollover = await projector.RefreshAsync(id, default);
            Assert.Equal(1, rollover.ReceivedToday);
            Assert.Equal(4, rollover.ReceivedLast30Days);
            var rolled = rollover.CompletedInboundDaysJson;
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            await projector.RefreshAsync(id, default);
            Assert.Equal(rolled, rollover.CompletedInboundDaysJson);
            Assert.Equal(4, rollover.ReceivedLast30Days);
        }
        finally { fixture.Clock.UtcNow = originalNow; }
    }

    [DatabaseFact]
    public async Task Tenant_refresh_updates_archived_health_once_then_skips_it_and_keeps_active_connections_fresh()
    {
        var originalNow = fixture.Clock.UtcNow;
        try
        {
            var tenant = await fixture.NewTenantAsync("arch-health");
            var archivedId = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
            var activeId = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
            var missingHealthId = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var projector = scope.ServiceProvider.GetRequiredService<ConnectionHealthProjection>();
            await projector.RefreshAsync(archivedId, default);
            await db.Connections.Where(row => row.ConnectionId == archivedId || row.ConnectionId == missingHealthId)
                .ExecuteUpdateAsync(update => update.SetProperty(row => row.Status, ConnectionStatus.Archived));
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            await projector.RefreshTenantAsync(default);
            var archived = await db.ConnectionHealth.SingleAsync(row => row.ConnectionId == archivedId);
            Assert.Equal("archived", archived.Status);
            var initiallyMissing = await db.ConnectionHealth.SingleAsync(row => row.ConnectionId == missingHealthId);
            Assert.Equal("archived", initiallyMissing.Status);
            var finalRefresh = archived.UpdatedAt;
            var eventCount = await db.ConnectionEvents.CountAsync(row => row.ConnectionId == archivedId);
            fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
            await projector.RefreshTenantAsync(default);
            Assert.Equal(finalRefresh, archived.UpdatedAt);
            Assert.Equal(finalRefresh, initiallyMissing.UpdatedAt);
            Assert.Equal(eventCount, await db.ConnectionEvents.CountAsync(row => row.ConnectionId == archivedId));
            Assert.Equal(fixture.Clock.UtcNow, (await db.ConnectionHealth.SingleAsync(row => row.ConnectionId == activeId)).UpdatedAt);
        }
        finally { fixture.Clock.UtcNow = originalNow; }
    }

    private async Task<TenantId> NewTenant()
    {
        var tenant = await fixture.NewTenantAsync("health");
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>().SeedGenericEntriesAsync(default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.ConnectorCatalogue.ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, true));
        return tenant;
    }

    private OutboundCommandRow Outbound(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, ConnectionId = id, OutboundCommandId = Guid.CreateVersion7(), IdempotencyKey = Guid.CreateVersion7(),
        CommandType = "AttachDocument", ModuleDocumentRef = "fictional-document", ErpReferenceText = "fictional-reference",
        Payload = "{}", Status = OutboundCommandStatus.Pending, CreatedAt = fixture.Clock.UtcNow,
    };
    private DeadLetterItem Letter(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, ConnectionId = id, DeadLetterItemId = Guid.CreateVersion7(), SourceId = Guid.CreateVersion7(),
        SourceKind = "inbound_message", Direction = "inbound", RecordType = "DeliveryPosted", ReasonCode = "fictional_failure",
        RawPayload = "{}", CanonicalAttempt = "{}", Failure = "Fictional failure", Status = DeadLetterStatus.NeedsAttention,
        CreatedAt = fixture.Clock.UtcNow, UpdatedAt = fixture.Clock.UtcNow,
    };
    private BackfillCursor Backfill(TenantId tenant, Guid id) => new()
    {
        TenantId = tenant, ConnectionId = id, BackfillCursorId = Guid.CreateVersion7(), Entity = "deliveries",
        RangeStart = new(2026, 9, 1), RangeEnd = new(2026, 9, 2), PageSize = 10,
        Status = BackfillStatus.Running, RequestedBy = Actor, LastChangedBy = Actor, CreatedAt = fixture.Clock.UtcNow,
    };
}
