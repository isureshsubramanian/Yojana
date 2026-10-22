using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Coexistence;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Apply;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Integration.Reconciliation;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

/// <summary>P9-T11: the parallel-run dashboard, handover by kind of record and the go-live record (OD-64).</summary>
[Collection("integration host")]
public sealed class ParallelRunDashboardTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-operator";

    [DatabaseFact]
    public async Task Handover_goes_one_kind_at_a_time_and_waits_for_the_contracts_setting_and_the_month_end_close()
    {
        await using var simulator = new Cx01Simulator();
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var dashboard = scope.ServiceProvider.GetRequiredService<ParallelRunDashboardService>();

        var before = await dashboard.ViewAsync(id, default);
        Assert.Equal("not_started", before.Status);
        Assert.Null(before.DaysLeft);
        Assert.All(before.Handover, step => Assert.Contains(step.ConfirmRefusals, x => x.Code == "parallel_run_not_started"));
        Assert.False(before.GoLive.CanIssue);
        Assert.Equal("parallel_run_not_started", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.PlanHandoverAsync(id, HandoverKinds.Returns, new(Today.AddDays(5)), Actor, default))).Rule);

        await scope.ServiceProvider.GetRequiredService<ParallelRun>().StartAsync(id, new(30, Today.AddDays(-11)), Actor, default);
        var started = await dashboard.ViewAsync(id, default);
        Assert.Equal(12, started.DayNumber);
        Assert.Equal(19, started.DaysLeft);
        Assert.Equal(["installed_base_and_cover", "returns", "certificates", "retire_owned_objects"],
            started.Handover.Select(x => x.Kind));
        Assert.Equal(["next", "planned", "planned", "after_close"], started.Handover.Select(x => x.State));

        Assert.Equal("month_end_close_outside_run", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.SetMonthEndCloseAsync(id, new(Today.AddDays(40)), Actor, default))).Rule);
        Assert.Equal(Today.AddDays(10), (await dashboard.SetMonthEndCloseAsync(id, new(Today.AddDays(10)), Actor, default))
            .MonthEndCloseOn);

        await dashboard.PlanHandoverAsync(id, HandoverKinds.Returns, new(Today.AddDays(3)), Actor, default);
        Assert.Equal("handover_plan_out_of_order", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.PlanHandoverAsync(id, HandoverKinds.Certificates, new(Today.AddDays(2)), Actor, default))).Rule);
        Assert.Equal("handover_kind_not_found", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.PlanHandoverAsync(id, "stock", new(Today), Actor, default))).Rule);

        // Installed base waits for the OD-36 setting; the handover reports it and never flips it.
        var first = (await dashboard.ViewAsync(id, default)).Handover[0];
        Assert.False(first.CanConfirm);
        Assert.Contains(first.ConfirmRefusals, x => x.Code == "handover_contracts_setting_incumbent");
        Assert.Equal("handover_contracts_setting_incumbent", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.ConfirmHandoverAsync(id, HandoverKinds.InstalledBaseAndCover, new("Card fields read-only"), Actor, default))).Rule);
        var contracts = scope.ServiceProvider.GetRequiredService<WarrantyContractMaintenanceSettings>();
        await contracts.UpdateAsync(id, new(WarrantyContractsMaintainedIn.Module, (await contracts.GetAsync(default)).Version),
            Actor, default);

        Assert.Equal("handover_previous_kind_open", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.ConfirmHandoverAsync(id, HandoverKinds.Returns, new("Return permissions removed"), Actor, default))).Rule);
        Assert.Equal("parallel_run_evidence_required", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.ConfirmHandoverAsync(id, HandoverKinds.InstalledBaseAndCover, new(" "), Actor, default))).Rule);
        var owned = await dashboard.ConfirmHandoverAsync(id, HandoverKinds.InstalledBaseAndCover,
            new("Card owner, status and contract fields read-only for all ERP roles"), Actor, default);
        Assert.Equal(["module_owns", "next", "planned", "after_close"], owned.Handover.Select(x => x.State));
        Assert.Equal(Today, owned.Handover[0].OwnedSince);
        Assert.NotNull(owned.Handover[0].ConfirmedByName);
        Assert.Equal("handover_already_owned", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.PlanHandoverAsync(id, HandoverKinds.InstalledBaseAndCover, new(Today), Actor, default))).Rule);

        await dashboard.ConfirmHandoverAsync(id, HandoverKinds.Returns, new("Return authorisation read-only"), Actor, default);
        await dashboard.ConfirmHandoverAsync(id, HandoverKinds.Certificates, new("Certificates issued by the module"), Actor, default);
        Assert.Equal("handover_month_end_close_open", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.ConfirmHandoverAsync(id, HandoverKinds.RetireOwnedObjects, new("Retired"), Actor, default))).Rule);
        // The go-live item and the P8 checklist item are the same confirmation.
        await dashboard.ConfirmGoLiveItemAsync(id, GoLiveItemCodes.MonthEndClose, new("October close passed in both"), Actor, default);
        Assert.Equal("pass", (await scope.ServiceProvider.GetRequiredService<ParallelRun>().ViewAsync(id, default))
            .Items.Single(x => x.Code == "month_end_close").Status);
        var retired = await dashboard.ConfirmHandoverAsync(id, HandoverKinds.RetireOwnedObjects,
            new("Owned objects retired after the close"), Actor, default);
        Assert.All(retired.Handover, step => Assert.Equal("module_owns", step.State));
        Assert.Equal("done", retired.GoLive.Items.Single(x => x.Code == GoLiveItemCodes.ReturnsHandover).Status);
    }

    [DatabaseFact]
    public async Task Dashboard_counts_sync_lag_conflicts_failed_messages_and_explained_reconciliations()
    {
        await using var simulator = new Cx01Simulator();
        simulator.EquipmentCards.Add(new()
        {
            ["EquipmentCardNum"] = 81L, ["InternalSerialNum"] = "SN-81", ["ItemCode"] = "HW-EDGE-100",
            ["CustomerCode"] = "C-NORTHWIND", ["StatusOfSerialNumber"] = "sns_Active", ["ContractCode"] = null,
            ["ContractEndDate"] = null, ["DeliveryCode"] = "DLN-1", ["InvoiceCode"] = null,
        });
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var dashboard = scope.ServiceProvider.GetRequiredService<ParallelRunDashboardService>();
        await scope.ServiceProvider.GetRequiredService<ParallelRun>().StartAsync(id, new(30, Today.AddDays(-3)), Actor, default);
        var drift = scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>();
        await drift.ScanAsync(id, default);
        simulator.EquipmentCards[0]["CustomerCode"] = "C-CONTOSO";
        await drift.ScanAsync(id, default);

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var now = fixture.Clock.UtcNow;
        Inbound(db, tenant, id, "within", now.AddSeconds(-60), now);
        Inbound(db, tenant, id, "late", now.AddSeconds(-600), now);
        var clean = Run(db, tenant, id, now.AddDays(-2), ReconciliationRunStatus.Clean);
        var mismatch = Run(db, tenant, id, now.AddDays(-1), ReconciliationRunStatus.Mismatch);
        Run(db, tenant, id, now.AddHours(-2), ReconciliationRunStatus.Mismatch);
        Run(db, tenant, id, now.AddDays(-10), ReconciliationRunStatus.Mismatch); // before the run: not counted
        await db.SaveChangesAsync();

        var reconciliation = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
        Assert.Equal("reconciliation_run_not_mismatch", (await Assert.ThrowsAsync<ReconciliationRuleException>(() =>
            reconciliation.ExplainAsync(clean, "Nothing to explain", Actor, default))).Rule);
        Assert.Equal("reconciliation_explanation_required", (await Assert.ThrowsAsync<ReconciliationRuleException>(() =>
            reconciliation.ExplainAsync(mismatch, new string('x', 501), Actor, default))).Rule);
        var explained = await reconciliation.ExplainAsync(mismatch, "Delivery posted after the check window", Actor, default);
        Assert.Equal("Delivery posted after the check window", explained.Run.ExplanationNote);
        Assert.NotNull(explained.Run.ExplainedByName);

        var view = await dashboard.ViewAsync(id, default);
        Assert.Equal(2, view.SyncLag.MessagesToday);
        Assert.Equal(0.5m, view.SyncLag.ShareWithinTargetToday);
        Assert.Equal(180, view.SyncLag.TargetSeconds);
        Assert.Equal(1, view.Conflicts.Open);
        Assert.Equal(0, view.Conflicts.AutoResolved);
        Assert.Equal(new ReconciliationSummaryView(3, 1, 1, 1, 0, DateOnly.FromDateTime(now.AddDays(-1))), view.Reconciliation);
        Assert.Equal(0, view.FailedMessages.Open);

        var conflict = Assert.Single((await scope.ServiceProvider.GetRequiredService<CoexistenceConflicts>()
            .ListAsync(id, null, null, 5, default)).Items);
        Assert.Equal("Service desk", conflict.DeciderRoleLabel);
        Assert.Equal("C-NORTHWIND kept in the module", conflict.ModuleValueLabel);
    }

    [DatabaseFact]
    public async Task Go_live_record_is_issued_only_when_all_seven_items_are_done_and_is_frozen()
    {
        await using var simulator = new Cx01Simulator();
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var dashboard = scope.ServiceProvider.GetRequiredService<ParallelRunDashboardService>();
        await scope.ServiceProvider.GetRequiredService<ParallelRun>().StartAsync(id, new(30, Today.AddDays(-29)), Actor, default);

        var open = await dashboard.ViewAsync(id, default);
        Assert.Equal(7, open.GoLive.Total);
        Assert.Equal(0, open.GoLive.Done);
        Assert.Equal(["validation_signed_off", "delta_load", "rollback_rehearsed", "users_trained", "returns_handover",
            "month_end_close", "customer_signoff"], open.GoLive.Items.Select(x => x.Code));
        Assert.Contains(open.GoLive.IssueRefusals, x => x.Code == "go_live_items_open");
        Assert.Equal("go_live_items_open", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.IssueRecordAsync(id, Actor, default))).Rule);
        Assert.Equal("go_live_item_not_confirmable", (await Assert.ThrowsAsync<CoexistenceRuleException>(() =>
            dashboard.ConfirmGoLiveItemAsync(id, GoLiveItemCodes.DeltaLoad, new("Fast"), Actor, default))).Rule);

        SeedCutover(scope.ServiceProvider.GetRequiredService<IntegrationDbContext>(), tenant, id,
            delta: TimeSpan.FromMinutes(82), rollback: TimeSpan.FromMinutes(170));
        await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().SaveChangesAsync();
        var contracts = scope.ServiceProvider.GetRequiredService<WarrantyContractMaintenanceSettings>();
        await contracts.UpdateAsync(id, new(WarrantyContractsMaintainedIn.Module, (await contracts.GetAsync(default)).Version),
            Actor, default);
        await dashboard.ConfirmHandoverAsync(id, HandoverKinds.InstalledBaseAndCover, new("Card fields read-only"), Actor, default);
        await dashboard.ConfirmHandoverAsync(id, HandoverKinds.Returns, new("Returns read-only"), Actor, default);
        await dashboard.ConfirmGoLiveItemAsync(id, GoLiveItemCodes.UsersTrained, new("Service desk 14 of 14, warehouse 9 of 9"), Actor, default);
        await dashboard.ConfirmGoLiveItemAsync(id, GoLiveItemCodes.MonthEndClose, new("October close passed in both"), Actor, default);
        var ready = await dashboard.ConfirmGoLiveItemAsync(id, GoLiveItemCodes.CustomerSignoff,
            new("Signed by the customer's service director"), Actor, default);

        Assert.True(ready.GoLive.CanIssue, string.Join("; ", ready.GoLive.Items.Where(x => x.Status != "done")
            .Select(x => $"{x.Code}: {x.Detail}")));
        Assert.Equal("D1 · 1 h 22 m (target under 2 h)", ready.GoLive.Items.Single(x => x.Code == GoLiveItemCodes.DeltaLoad).Detail);
        Assert.Contains("2 h 50 m", ready.GoLive.Items.Single(x => x.Code == GoLiveItemCodes.RollbackRehearsed).Detail);
        Assert.NotNull(ready.CutoverAt);

        var record = await dashboard.IssueRecordAsync(id, Actor, default);
        Assert.Equal(7, record.Items.Count);
        Assert.Equal(4, record.Handover.Count);
        var csv = Encoding.UTF8.GetString(await dashboard.RecordCsvAsync(record.RecordId, default));
        Assert.Contains("Customer sign-off", csv);
        Assert.Contains("Handover,Returns (RMA),module_owns", csv);

        // Frozen: later changes do not alter the issued record.
        await dashboard.ConfirmGoLiveItemAsync(id, GoLiveItemCodes.UsersTrained, new("Changed later"), Actor, default);
        var again = await dashboard.RecordAsync(record.RecordId, default);
        Assert.Equal("Service desk 14 of 14, warehouse 9 of 9",
            again.Items.Single(x => x.Code == GoLiveItemCodes.UsersTrained).Evidence);
        Assert.Equal(record.RecordId, (await dashboard.ViewAsync(id, default)).GoLive.RecordId);
    }

    [DatabaseFact]
    public async Task Go_live_marks_a_slow_delta_or_rollback_as_failed()
    {
        await using var simulator = new Cx01Simulator();
        var (tenant, id) = await RegisteredAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        SeedCutover(db, tenant, id, delta: TimeSpan.FromMinutes(150), rollback: TimeSpan.FromHours(5));
        await db.SaveChangesAsync();
        var view = await scope.ServiceProvider.GetRequiredService<ParallelRunDashboardService>().ViewAsync(id, default);
        Assert.Equal("fail", view.GoLive.Items.Single(x => x.Code == GoLiveItemCodes.DeltaLoad).Status);
        Assert.Equal("fail", view.GoLive.Items.Single(x => x.Code == GoLiveItemCodes.RollbackRehearsed).Status);
        Assert.Equal("done", view.GoLive.Items.Single(x => x.Code == GoLiveItemCodes.ValidationSignedOff).Status);
    }

    private DateOnly Today => DateOnly.FromDateTime(fixture.Clock.UtcNow);

    // A source of this connection with a signed-off final load, a delta after it and a rehearsal rolled back: the
    // rows the console writes, seeded directly so the go-live record can be read without running a pipeline.
    private void SeedCutover(IntegrationDbContext db, TenantId tenant, Guid connectionId, TimeSpan delta, TimeSpan rollback)
    {
        var now = fixture.Clock.UtcNow;
        var batchId = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();
        var canonicalId = Guid.CreateVersion7();
        var reconstructionId = Guid.CreateVersion7();
        var extracted = now.AddDays(-20);
        db.MigrationSources.Add(new MigrationSource
        {
            TenantId = tenant, ConnectionId = connectionId, DisplayLabel = "Fictional migration source",
            SourceSystem = "fixture-source", Runtime = MigrationSourceRuntime.SqlServer,
            Route = MigrationSourceRoute.Separate, DatabaseName = "fixture", SchemaName = "dbo",
            CommandTimeoutSeconds = 30, LicencePosition = MigrationLicencePosition.Confirmed,
            CreatedAt = extracted, UpdatedAt = extracted,
        });
        db.MigrationBatches.Add(new MigrationBatch
        {
            TenantId = tenant, BatchId = batchId, DisplayNumber = 1, SourceSystem = "fixture-source",
            ConnectionId = connectionId, Kind = "full", RequestedBy = Actor, RequestedAt = extracted,
            StartedAt = extracted, FinishedAt = extracted, Status = MigrationBatchStatus.Succeeded,
        });
        db.MigrationProfileRuns.Add(new() { TenantId = tenant, RunId = profileId, BatchId = batchId,
            Status = MigrationProfileRunStatus.Completed, StartedAt = extracted, FinishedAt = extracted });
        db.MigrationCanonRuns.Add(new() { TenantId = tenant, RunId = canonicalId, BatchId = batchId,
            ProfileRunId = profileId, RequestedAt = extracted, StartedAt = extracted, FinishedAt = extracted,
            Status = CanonicalisationRunStatus.Completed });
        db.MigrationReconstructionRuns.Add(new() { TenantId = tenant, RunId = reconstructionId,
            CanonicalisationRunId = canonicalId, BatchId = batchId, RequestedAt = extracted,
            StartedAt = extracted, FinishedAt = extracted, Status = ReconstructionRunStatus.Completed });
        var mapRunId = Guid.CreateVersion7();
        db.MigrationMapRuns.Add(new MigrationMapRun
        {
            TenantId = tenant, RunId = mapRunId, ReconstructionRunId = reconstructionId, BatchId = batchId,
            CutoverDate = Today, Status = "completed",
        });
        var validationId = Guid.CreateVersion7();
        db.MigrationValidationRuns.Add(new MigrationValidationRun
        {
            TenantId = tenant, RunId = validationId, BatchId = batchId, MapRunId = mapRunId,
            TargetKind = ValidationTargetKind.Plan, Rehearsal = false, RequestedAt = now.AddDays(-15),
            FinishedAt = now.AddDays(-15), Status = ValidationRunStatus.Completed,
        });
        db.MigrationSignoffRecords.Add(new MigrationSignoffRecord
        {
            TenantId = tenant, RecordId = Guid.CreateVersion7(), RunId = validationId, Rehearsal = false,
            CreatedAt = now.AddDays(-15), CreatedBy = Actor,
        });
        db.Set<MigrationApplyRun>().Add(Rolled(Load(tenant, batchId, mapRunId, rehearsal: true, ApplyRunStatus.RolledBack,
            now.AddDays(-18), TimeSpan.FromMinutes(30)), now.AddDays(-17), rollback));
        var final = Load(tenant, batchId, mapRunId, rehearsal: false, ApplyRunStatus.Completed, now.AddDays(-12),
            TimeSpan.FromMinutes(95));
        db.Set<MigrationApplyRun>().Add(final);
        var deltaRun = Load(tenant, batchId, mapRunId, rehearsal: false, ApplyRunStatus.Completed, now.AddDays(-11), delta);
        deltaRun.Kind = ApplyRunKind.Delta;
        deltaRun.BaseLoadId = final.RunId;
        db.Set<MigrationApplyRun>().Add(deltaRun);
    }

    private static MigrationApplyRun Load(TenantId tenant, Guid batchId, Guid mapRunId, bool rehearsal, string status,
        DateTime startedAt, TimeSpan took) => new()
    {
        TenantId = tenant, RunId = Guid.CreateVersion7(startedAt), MapRunId = mapRunId, BatchId = batchId,
        Rehearsal = rehearsal, Status = status, RequestedBy = Actor, RequestedAt = startedAt, StartedAt = startedAt,
        FinishedAt = startedAt + took,
    };

    private static MigrationApplyRun Rolled(MigrationApplyRun run, DateTime startedAt, TimeSpan took)
    {
        run.RollbackStartedAt = startedAt;
        run.RolledBackAt = startedAt + took;
        run.RolledBackBy = Actor;
        return run;
    }

    private static void Inbound(IntegrationDbContext db, TenantId tenant, Guid connectionId, string key,
        DateTime occurredAt, DateTime acceptedAt) =>
        db.InboundMessages.Add(new()
        {
            TenantId = tenant, InboundMessageId = Guid.CreateVersion7(), MessageId = Guid.CreateVersion7(),
            ConnectionId = connectionId, ConnectorCode = "CX01", ErpEntity = "delivery", ErpExternalId = "lag-" + key,
            ErpChangeMarker = "1", MarkerKind = "version", ContractVersion = "1", MessageType = "DeliveryPosted",
            RawPayload = "{}", CanonicalMessage = "{}", OccurredAt = occurredAt, ObservedAt = acceptedAt,
            AcceptedAt = acceptedAt, IsLatest = true, TranslationStatus = "completed",
        });

    private static Guid Run(IntegrationDbContext db, TenantId tenant, Guid connectionId, DateTime at, string status)
    {
        var id = Guid.CreateVersion7(at);
        db.ReconciliationRuns.Add(new()
        {
            TenantId = tenant, ReconciliationRunId = id, ConnectionId = connectionId, RangeStart = DateOnly.FromDateTime(at),
            RangeEnd = DateOnly.FromDateTime(at), Trigger = "scheduled", Status = status, StartedAt = at, CompletedAt = at,
            MismatchCount = status == ReconciliationRunStatus.Mismatch ? 2 : 0,
        });
        return id;
    }

    private async Task<(TenantId Tenant, Guid ConnectionId)> RegisteredAsync(Cx01Simulator simulator)
    {
        var tenant = await fixture.NewTenantAsync("golive");
        using (var scope = fixture.ScopeFor(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>().SeedGenericEntriesAsync(default);
            var catalogue = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ConnectorCatalogue;
            await catalogue.ExecuteUpdateAsync(update => update.SetProperty(x => x.Enabled, true));
        }
        using var registration = fixture.ScopeFor(tenant);
        var result = await registration.ServiceProvider.GetRequiredService<ConnectionCommands>()
            .RegisterAsync(simulator.RegistrationRequest(), Actor, default);
        return (tenant, result.ConnectionId);
    }
}
