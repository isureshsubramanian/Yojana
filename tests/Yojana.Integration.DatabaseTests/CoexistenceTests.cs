using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Coexistence;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Gateway;
using Yojana.Integration.Notifications;
using Yojana.Integration.Security.Credentials;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class CoexistenceTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-operator";

    [DatabaseFact]
    public async Task Drift_scan_baselines_first_then_parks_unexplained_edits_and_follows_document_moves()
    {
        await using var simulator = new Cx01Simulator { EquipmentCardPageCap = 2 };
        AddCard(simulator, 11, "C-NORTHWIND", "DLN-1");
        AddCard(simulator, 12, "C-NORTHWIND", "DLN-2");
        AddCard(simulator, 13, "C-FABRIKAM", "DLN-3");
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var drift = scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>();

        var first = await drift.ScanAsync(id, default);
        Assert.True(first.Supported);
        Assert.True(first.PassCompleted);
        Assert.Equal(3, first.Records);
        Assert.Equal(2, first.Pages);
        Assert.Equal(12, first.NewBaselines);
        Assert.Equal(0, first.ConflictsRaised);

        Card(simulator, 11)["CustomerCode"] = "C-CONTOSO";            // edited by hand in the remote
        Card(simulator, 12)["CustomerCode"] = "C-CONTOSO";            // moved by a new delivery
        Card(simulator, 12)["DeliveryCode"] = 9002;
        var second = await drift.ScanAsync(id, default);
        Assert.Equal(1, second.ConflictsRaised);
        Assert.Equal(1, second.DocumentExplained);

        Card(simulator, 11)["CustomerCode"] = "C-ADVENTURE";
        var third = await drift.ScanAsync(id, default);
        Assert.Equal(0, third.ConflictsRaised);
        var conflicts = scope.ServiceProvider.GetRequiredService<CoexistenceConflicts>();
        var page = await conflicts.ListAsync(id, null, null, 50, default);
        var conflict = Assert.Single(page.Items);
        Assert.Equal("11", conflict.RecordKey);
        Assert.Equal("CustomerCode", conflict.FieldName);
        Assert.Equal("C-NORTHWIND", conflict.BaselineValue);
        Assert.Equal("C-ADVENTURE", conflict.RemoteValue);
        Assert.Equal(CoexistenceConflictStatus.Open, conflict.Status);
        Assert.Contains("card 11", conflict.RecordLabel);
    }

    [DatabaseFact]
    public async Task Keep_module_raises_a_revert_task_that_the_scan_closes_when_the_value_is_back()
    {
        await using var simulator = new Cx01Simulator();
        AddCard(simulator, 21, "C-NORTHWIND", "DLN-1");
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var drift = scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>();
        await drift.ScanAsync(id, default);
        Card(simulator, 21)["StatusOfSerialNumber"] = "sns_Terminated";
        await drift.ScanAsync(id, default);
        var conflicts = scope.ServiceProvider.GetRequiredService<CoexistenceConflicts>();
        var conflict = Assert.Single((await conflicts.ListAsync(id, null, null, 50, default)).Items);

        Assert.Equal("conflict_note_required", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            conflicts.ResolveAsync(conflict.ConflictId, new(CoexistenceConflictDecision.KeepModule, " "), Actor, default))).Rule);
        var kept = await conflicts.ResolveAsync(conflict.ConflictId,
            new(CoexistenceConflictDecision.KeepModule, "Unit is still under contract in the module."), Actor, default);
        Assert.Equal(CoexistenceConflictStatus.AwaitingRemoteRevert, kept.Status);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.ManualOutboundTaskId == kept.ManualTaskId);
        Assert.Equal(ManualTaskCommandType.RevertRemoteChange, task.CommandType);
        Assert.Contains("sns_Active", task.Draft);

        await drift.ScanAsync(id, default);
        Assert.Equal(CoexistenceConflictStatus.AwaitingRemoteRevert, (await conflicts.GetAsync(conflict.ConflictId, default)).Status);
        Card(simulator, 21)["StatusOfSerialNumber"] = "sns_Active";
        var closed = await drift.ScanAsync(id, default);
        Assert.Equal(1, closed.ConflictsClosed);
        var final = await conflicts.GetAsync(conflict.ConflictId, default);
        Assert.Equal(CoexistenceConflictStatus.Closed, final.Status);
        Assert.Equal(CoexistenceConflictDecision.KeepModule, final.Decision);
        db.ChangeTracker.Clear();
        Assert.Equal(ManualTaskStatus.Completed, (await db.ManualOutboundTasks.SingleAsync(x => x.ManualOutboundTaskId == task.ManualOutboundTaskId)).Status);
        Assert.Contains(await db.ConnectionEvents.ToListAsync(), x => x.EventType == "CoexistenceConflictResolved" && x.ActorRef == Actor);
    }

    [DatabaseFact]
    public async Task Accepting_the_remote_value_rebaselines_and_a_remote_revert_closes_an_undecided_conflict()
    {
        await using var simulator = new Cx01Simulator();
        AddCard(simulator, 31, "C-NORTHWIND", "DLN-1");
        AddCard(simulator, 32, "C-NORTHWIND", "DLN-2");
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var drift = scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>();
        var conflicts = scope.ServiceProvider.GetRequiredService<CoexistenceConflicts>();
        await drift.ScanAsync(id, default);
        Card(simulator, 31)["ContractCode"] = 77;
        Card(simulator, 32)["ContractCode"] = 78;
        Assert.Equal(2, (await drift.ScanAsync(id, default)).ConflictsRaised);
        var open = (await conflicts.ListAsync(id, "open", null, 50, default)).Items;

        var accepted = await conflicts.ResolveAsync(open.Single(x => x.RecordKey == "31").ConflictId,
            new(CoexistenceConflictDecision.AcceptRemote, "Contract was renewed in the ERP by finance."), Actor, default);
        Assert.Equal(CoexistenceConflictStatus.Closed, accepted.Status);
        Card(simulator, 32)["ContractCode"] = null;
        var rescan = await drift.ScanAsync(id, default);
        Assert.Equal(0, rescan.ConflictsRaised);
        Assert.Equal(1, rescan.ConflictsClosed);
        var reverted = await conflicts.GetAsync(open.Single(x => x.RecordKey == "32").ConflictId, default);
        Assert.Equal("reverted_remotely", reverted.Decision);
        Assert.Empty((await conflicts.ListAsync(id, null, null, 50, default)).Items);
        Assert.Equal(2, (await conflicts.ListAsync(id, "closed", null, 50, default)).Items.Count);
    }

    [DatabaseFact]
    public async Task Scan_resumes_from_its_cursor_and_conflict_list_is_keyset_paged()
    {
        await using var simulator = new Cx01Simulator { EquipmentCardPageCap = 2 };
        for (var card = 41; card <= 45; card++) AddCard(simulator, card, "C-NORTHWIND", "DLN-" + card);
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<CoexistenceSettings>();
        await settings.UpdateAsync(id, new(NativeWarrantyObjectModes.Frozen, null, null, true, 20, 1), Actor, default);
        var drift = scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>();
        var first = await drift.ScanAsync(id, default);
        Assert.False(first.PassCompleted);
        Assert.Equal(2, first.Records);
        Assert.True((await settings.ViewAsync(id, default)).PassInProgress);
        await drift.ScanAsync(id, default);
        var last = await drift.ScanAsync(id, default);
        Assert.True(last.PassCompleted);
        Assert.Equal(1, last.Records);
        var view = await settings.ViewAsync(id, default);
        Assert.Equal(5, view.LastPassRecords);
        Assert.False(view.PassInProgress);

        for (var card = 41; card <= 45; card++) Card(simulator, card)["CustomerCode"] = "C-CONTOSO";
        for (var run = 0; run < 3; run++) await drift.ScanAsync(id, default);
        var conflicts = scope.ServiceProvider.GetRequiredService<CoexistenceConflicts>();
        var pageOne = await conflicts.ListAsync(id, null, null, 3, default);
        Assert.Equal(3, pageOne.Items.Count);
        Assert.NotNull(pageOne.NextCursor);
        var pageTwo = await conflicts.ListAsync(id, null, pageOne.NextCursor, 3, default);
        Assert.Equal(2, pageTwo.Items.Count);
        Assert.Null(pageTwo.NextCursor);
        Assert.Empty(pageOne.Items.Select(x => x.ConflictId).Intersect(pageTwo.Items.Select(x => x.ConflictId)));
    }

    [DatabaseFact]
    public async Task Stop_the_sync_pauses_every_worker_and_the_subscription_and_resume_restores_both()
    {
        await using var simulator = new Cx01Simulator();
        AddCard(simulator, 51, "C-NORTHWIND", "DLN-1");
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var signing = await scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>().EncryptAsync(
            tenant, id, Cx01WebhookEndpoints.SigningCredentialPurpose, new byte[32], null,
            fixture.Clock.UtcNow.AddDays(90), default);
        await scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>().SaveAsync(signing, default);
        await scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>()
            .SubscribeAsync(id, new Uri("https://fictional.invalid/notifications"), default);
        var control = scope.ServiceProvider.GetRequiredService<SyncControl>();

        Assert.Equal("stop_reason_required", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            control.StopAsync(id, new(""), Actor, default))).Rule);
        var stopped = await control.StopAsync(id, new("Month-end freeze requested by the customer."), Actor, default);
        Assert.Equal("stopped", stopped.SyncState);
        Assert.True(stopped.NotificationsHeld);
        Assert.True(simulator.WebhookSubscriptionPaused);
        Assert.Equal("already_stopped", (await control.StopAsync(id, new("Again"), Actor, default)).Warning);

        var before = simulator.RequestCount;
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<ConnectionPollingService>().PollDueAsync(default));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<Cx01WebhookMaintenance>().WatchdogConnectionAsync(id, default));
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>().ScanScheduledAsync(default));
        Assert.Equal("connection_inactive", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>().ScanAsync(id, default))).Rule);
        Assert.Equal(before, simulator.RequestCount);
        var settings = scope.ServiceProvider.GetRequiredService<CoexistenceSettings>();
        Assert.Equal("stopped", (await settings.ViewAsync(id, default)).SyncState);
        var health = await scope.ServiceProvider.GetRequiredService<ConnectionQueries>().HealthAsync(id, default);
        Assert.Equal("paused", health.Health.Status);

        var resumed = await control.ResumeAsync(id, new("Freeze lifted."), Actor, default);
        Assert.Equal("running", resumed.SyncState);
        Assert.True(resumed.NotificationsHeld);
        Assert.Equal(1, simulator.SubscriptionResumeCount);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>().ScanScheduledAsync(default));
        var events = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ConnectionEvents
            .Where(x => x.ConnectionId == id).Select(x => x.EventType).ToListAsync();
        Assert.Contains("SyncStopped", events);
        Assert.Contains("SyncResumed", events);
    }

    [DatabaseFact]
    public async Task Stop_succeeds_when_the_remote_cannot_hold_notifications()
    {
        await using var simulator = new Cx01Simulator { FailUnsubscribe = true };
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var stopped = await scope.ServiceProvider.GetRequiredService<SyncControl>()
            .StopAsync(id, new("Remote under maintenance."), Actor, default);
        Assert.Equal("stopped", stopped.SyncState);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(ConnectionStatus.Paused, (await db.Connections.SingleAsync(x => x.ConnectionId == id)).Status);
        var audit = await db.ConnectionEvents.SingleAsync(x => x.ConnectionId == id && x.EventType == "SyncStopped");
        Assert.DoesNotContain("Fictional remote detail", audit.DetailsJson);
    }

    [DatabaseFact]
    public async Task Settings_refuse_pushed_mode_and_bad_fields_and_record_changes()
    {
        await using var simulator = new Cx01Simulator();
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<CoexistenceSettings>();
        var view = await settings.ViewAsync(id, default);
        Assert.Equal(NativeWarrantyObjectModes.Frozen, view.NativeWarrantyObjectMode);
        Assert.False(view.PushedModeAvailable);
        Assert.True(view.DriftScanSupported);
        Assert.Contains("CustomerCode", view.WatchedFields);
        Assert.Equal("pushed_mode_unavailable", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            settings.UpdateAsync(id, new(NativeWarrantyObjectModes.Pushed, null, null), Actor, default))).Rule);
        Assert.Equal("coexistence_fields_invalid", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            settings.UpdateAsync(id, new(NativeWarrantyObjectModes.Frozen, ["CustomerCode eq 'x'"], null), Actor, default))).Rule);
        Assert.Equal("coexistence_fields_invalid", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            settings.UpdateAsync(id, new(NativeWarrantyObjectModes.Frozen, ["CustomerCode"], ["CustomerCode"]), Actor, default))).Rule);
        var updated = await settings.UpdateAsync(id, new(NativeWarrantyObjectModes.Frozen, ["CustomerCode", "U_SiteRef"],
            ["DeliveryCode"]), Actor, default);
        Assert.Equal(["CustomerCode", "U_SiteRef"], updated.WatchedFields);
        Assert.Equal(["DeliveryCode"], updated.ExplainingFields);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Contains(await db.ConnectionEvents.ToListAsync(), x => x.EventType == "CoexistenceSettingsChanged");
    }

    [DatabaseFact]
    public async Task Parallel_run_measures_what_the_gateway_records_and_needs_confirmations_with_evidence()
    {
        await using var simulator = new Cx01Simulator();
        AddCard(simulator, 61, "C-NORTHWIND", "DLN-1");
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var run = scope.ServiceProvider.GetRequiredService<ParallelRun>();
        var notStarted = await run.ViewAsync(id, default);
        Assert.Equal("not_started", notStarted.Status);
        Assert.Equal("parallel_run_not_started", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            run.ConfirmAsync(id, "month_end_close", new("Closed 2026-09-30"), Actor, default))).Rule);
        Assert.Equal("parallel_run_days_invalid", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            run.StartAsync(id, new(45), Actor, default))).Rule);

        var today = DateOnly.FromDateTime(fixture.Clock.UtcNow);
        var started = await run.StartAsync(id, new(30, today.AddDays(-40)), Actor, default);
        Assert.Equal("in_progress", started.Status);
        Assert.Equal("pass", started.Items.Single(x => x.Code == "elapsed").Status);
        Assert.Equal("pending", started.Items.Single(x => x.Code == "drift_scan_recent").Status);

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        for (var day = 0; day < 7; day++)
        {
            var at = fixture.Clock.UtcNow.Date.AddDays(-day).AddHours(1);
            db.ReconciliationRuns.Add(new()
            {
                TenantId = tenant, ReconciliationRunId = Guid.CreateVersion7(), ConnectionId = id,
                RangeStart = DateOnly.FromDateTime(at), RangeEnd = DateOnly.FromDateTime(at), Trigger = "scheduled",
                Status = ReconciliationRunStatus.Clean, StartedAt = at, CompletedAt = at,
            });
        }
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>().ScanAsync(id, default);
        Assert.Equal("parallel_run_evidence_required", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            run.ConfirmAsync(id, "month_end_close", new(""), Actor, default))).Rule);
        Assert.Equal("parallel_run_item_not_found", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            run.ConfirmAsync(id, "elapsed", new("Counted"), Actor, default))).Rule);
        ParallelRunView view = started;
        foreach (var code in new[] { "active_phase_complete", "incumbent_read_only", "month_end_close",
                     "rollback_rehearsed", "users_repointed" })
            view = await run.ConfirmAsync(id, code, new("Fictional evidence for " + code), Actor, default);
        Assert.Equal("ready", view.Status);
        Assert.All(view.Items, item => Assert.Contains(item.Status, new[] { "pass", "not_applicable" }));

        db.DeadLetterItems.Add(new()
        {
            TenantId = tenant, DeadLetterItemId = Guid.CreateVersion7(), ConnectionId = id, Direction = "inbound",
            RecordType = "delivery", SourceKind = "inbound_message", SourceId = Guid.CreateVersion7(),
            ReasonCode = "unknown_item", RawPayload = "{}", CanonicalAttempt = "{}", Failure = "Fictional",
            Status = DeadLetterStatus.NeedsAttention, CreatedAt = fixture.Clock.UtcNow.AddDays(-8),
            UpdatedAt = fixture.Clock.UtcNow.AddDays(-8),
        });
        await db.SaveChangesAsync();
        var aged = await run.ViewAsync(id, default);
        Assert.Equal("in_progress", aged.Status);
        Assert.Equal("fail", aged.Items.Single(x => x.Code == "no_aged_dead_letters").Status);
    }

    [DatabaseFact]
    public async Task Coexistence_rows_are_rls_isolated_and_revert_tasks_complete_without_a_remote_id()
    {
        await using var simulator = new Cx01Simulator();
        AddCard(simulator, 71, "C-NORTHWIND", "DLN-1");
        var (tenant, id) = await RegisteredAsync(simulator);
        Guid taskId;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var drift = scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>();
            await drift.ScanAsync(id, default);
            Card(simulator, 71)["CustomerCode"] = "C-CONTOSO";
            await drift.ScanAsync(id, default);
            var conflicts = scope.ServiceProvider.GetRequiredService<CoexistenceConflicts>();
            var conflict = Assert.Single((await conflicts.ListAsync(id, null, null, 50, default)).Items);
            taskId = (await conflicts.ResolveAsync(conflict.ConflictId,
                new(CoexistenceConflictDecision.KeepModule, "Owner is kept in the module."), Actor, default)).ManualTaskId!.Value;
            var completed = await scope.ServiceProvider.GetRequiredService<DeadLetterService>()
                .CompleteManualTaskAsync(taskId, new(null, null), Actor, default);
            Assert.Equal(ManualTaskStatus.Completed, completed.Status);
        }
        var other = await NewTenant();
        using var otherScope = fixture.ScopeFor(other);
        var db = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM integration.coexistence_conflict").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM integration.coexistence_baseline").SingleAsync());
        db.CoexistenceConflicts.Add(new()
        {
            TenantId = tenant, ConflictId = Guid.CreateVersion7(), ConnectionId = id, RecordType = "installed_base_record",
            RecordKey = "9971", RecordLabel = "Fictional", FieldName = "CustomerCode", Status = CoexistenceConflictStatus.Open,
            DetectedAt = fixture.Clock.UtcNow, LastSeenAt = fixture.Clock.UtcNow,
        });
        var denied = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(33504, Assert.IsType<Microsoft.Data.SqlClient.SqlException>(denied.InnerException).Number);
    }

    private async Task<(TenantId Tenant, Guid ConnectionId)> RegisteredAsync(Cx01Simulator simulator)
    {
        var tenant = await NewTenant();
        using var scope = fixture.ScopeFor(tenant);
        var result = await scope.ServiceProvider.GetRequiredService<ConnectionCommands>()
            .RegisterAsync(simulator.RegistrationRequest(), Actor, default);
        return (tenant, result.ConnectionId);
    }

    private async Task<TenantId> NewTenant()
    {
        var tenant = await fixture.NewTenantAsync("coexist");
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>().SeedGenericEntriesAsync(default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.ConnectorCatalogue.ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, true));
        return tenant;
    }

    private static void AddCard(Cx01Simulator simulator, long number, string customer, string delivery) =>
        simulator.EquipmentCards.Add(new()
        {
            ["EquipmentCardNum"] = number, ["InternalSerialNum"] = "SN-" + number, ["ItemCode"] = "HW-EDGE-100",
            ["CustomerCode"] = customer, ["StatusOfSerialNumber"] = "sns_Active", ["ContractCode"] = null,
            ["ContractEndDate"] = null, ["DeliveryCode"] = delivery, ["InvoiceCode"] = null,
        });

    private static Dictionary<string, object?> Card(Cx01Simulator simulator, long number) =>
        simulator.EquipmentCards.Single(card => Convert.ToInt64(card["EquipmentCardNum"]) == number);
}
