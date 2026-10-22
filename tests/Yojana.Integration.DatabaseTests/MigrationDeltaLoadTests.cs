using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Apply;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Integration.DatabaseTests;

/// <summary>
/// P9-T10b (OD-59 to OD-62): the final load hands the connector the extraction time, every load records which unit each
/// source key became, and a delta load on a newer extraction carries new serials, replaced cover, owner differences and
/// serials gone from the source, and is undone on its own.
/// </summary>
[Collection("integration host")]
public sealed class MigrationDeltaLoadTests(IntegrationHostFixture fixture)
{
    private const string Unsynced = "NETWORK-MASTER";
    private const string Admin = "user:admin";

    [DatabaseFact]
    public async Task A_final_load_needs_a_stopped_connection_hands_over_the_extraction_time_and_registers_its_units()
    {
        var tenant = await fixture.NewTenantAsync("dhand");
        var setup = await SetUpAsync(tenant, new DateTime(2026, 9, 1, 8, 55, 0));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var apply = scope.ServiceProvider.GetRequiredService<ApplyService>();

        var connection = await db.Connections.SingleAsync(row => row.ConnectionId == setup.ConnectionId);
        connection.Status = ConnectionStatus.Active;
        await db.SaveChangesAsync();
        var running = await Assert.ThrowsAsync<ApplyRuleException>(() =>
            apply.EnqueueAsync(setup.BatchId, new(false), Admin, default));
        Assert.Equal("migration_apply_connection_running", running.Rule);
        // A rehearsal does not touch the connection, so it may run beside a live sync.
        connection.Status = ConnectionStatus.Paused;
        await db.SaveChangesAsync();

        var queued = await apply.EnqueueAsync(setup.BatchId, new(false), Admin, default);
        Assert.Equal(1, await apply.RunPendingAsync(default));
        var view = (await apply.GetAsync(setup.BatchId, default))!;
        Assert.Equal(ApplyRunStatus.Completed, view.Status);
        Assert.Equal(new DateTime(2026, 9, 1, 8, 55, 0), view.WatermarkHandedOverFrom);
        Assert.Null(view.WatermarkNote);

        var watermark = await db.ConnectionPollWatermarks.AsNoTracking().SingleAsync(row =>
            row.ConnectionId == setup.ConnectionId);
        Assert.StartsWith("2026-09-01T08:55:00", watermark.Watermark);
        Assert.Equal(fixture.Clock.UtcNow, watermark.NextPollAt);
        // The connection stays stopped: resuming it is a person's step.
        Assert.Equal(ConnectionStatus.Paused,
            (await db.Connections.AsNoTracking().SingleAsync(row => row.ConnectionId == setup.ConnectionId)).Status);

        var registered = await db.Set<MigrationLoadedUnit>().AsNoTracking()
            .Where(row => row.ConnectionId == setup.ConnectionId).ToListAsync();
        Assert.Equal(view.Applied, registered.Count);
        Assert.All(registered, row =>
        {
            Assert.Equal(queued.LoadId, row.LoadId);
            Assert.Equal(queued.LoadId, row.CoverLoadId);
            Assert.Equal(MigrationSourceKey.Hash(row.SourceKey), row.SourceKeyHash);
            Assert.Equal(64, row.CoverHash.Length);
        });
    }

    [DatabaseFact]
    public async Task A_delta_load_adds_new_serials_lists_owner_differences_and_gone_serials_and_is_undone_alone()
    {
        var tenant = await fixture.NewTenantAsync("dload");
        var setup = await SetUpAsync(tenant, new DateTime(2026, 12, 31, 9, 0, 0));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var apply = scope.ServiceProvider.GetRequiredService<ApplyService>();
        var final = await FinalLoadAsync(apply, setup.BatchId);

        var sameBatch = await Assert.ThrowsAsync<ApplyRuleException>(() =>
            apply.EnqueueDeltaAsync(setup.BatchId, Admin, default));
        Assert.Equal("migration_delta_same_batch", sameBatch.Rule);

        var plan = await db.MigrationPlanUnits.AsNoTracking().Where(row => row.MapRunId == setup.MapRunId).ToListAsync();
        var gone = plan.Single(row => row.ItemCode == "STORAGE-DRAFT");
        var owned = await db.MigrationPlanOwnership.AsNoTracking().Where(row => row.MapRunId == setup.MapRunId &&
                row.IsCurrent && row.PartyCode != null && row.PlanUnitId != gone.PlanUnitId &&
                db.MigrationPlanUnits.Any(unit => unit.PlanUnitId == row.PlanUnitId && unit.ItemCode != Unsynced))
            .Select(row => row.PlanUnitId).FirstAsync();
        var template = plan.Single(row => row.ItemCode == "SERVER-CONTRACT");
        var delta = await CloneAsync(db, tenant, setup, plan.Where(row => row.PlanUnitId != gone.PlanUnitId).ToList(),
            ownerChange: (owned, "NEW-OWNER"),
            extra: [(template, "delta|new-serial", "SER-NEW-1", "MFG-NEW-1"),
                    (template, "delta|same-serial-new-key", null, null)]);

        var queued = await apply.EnqueueDeltaAsync(delta.BatchId, Admin, default);
        Assert.Equal(final.RunId, (await db.Set<MigrationApplyRun>().AsNoTracking()
            .SingleAsync(row => row.RunId == queued.LoadId)).BaseLoadId);
        Assert.Equal(1, await apply.RunPendingAsync(default));
        var view = (await apply.GetAsync(delta.BatchId, default))!;
        Assert.Equal((ApplyRunStatus.Completed, ApplyRunKind.Delta), (view.Status, view.Kind));
        Assert.Equal(1, view.Applied);
        Assert.Equal(1, view.AlreadyInModule);
        Assert.Equal(1, view.OwnerDiffers);
        Assert.Equal(1, view.GoneFromSource);
        Assert.Equal(0, view.CoverReplaced);
        Assert.Null(view.WatermarkHandedOverFrom);

        var findings = await apply.DeltaItemsAsync(view.LoadId, null, null, null, null, 50, default);
        Assert.Equal(4, findings.Total);
        Assert.Equal([DeltaItemKind.NewUnit, DeltaItemKind.AlreadyInModule, DeltaItemKind.OwnerDiffers,
            DeltaItemKind.GoneFromSource], findings.Counts.Select(row => row.Kind));
        var newUnit = (await apply.DeltaItemsAsync(view.LoadId, DeltaItemKind.NewUnit, null, null, null, 50, default)).Items.Single();
        Assert.Equal(("SERVER-CONTRACT", "SER-NEW-1"), (newUnit.ItemCode, newUnit.Serial));
        var ownerItem = (await apply.DeltaItemsAsync(view.LoadId, DeltaItemKind.OwnerDiffers, null, null, null, 50, default)).Items.Single();
        Assert.Contains("NEW-OWNER", ownerItem.Detail);
        var conflict = await db.CoexistenceConflicts.AsNoTracking().SingleAsync(row =>
            row.ConnectionId == setup.ConnectionId && row.Kind == CoexistenceConflictKind.MigrationOwnerDiffers);
        Assert.Equal(("NEW-OWNER", CoexistenceConflictStatus.Open), (conflict.RemoteValue, conflict.Status));
        // Nothing about the owner was written: the module unit keeps the loaded owner.
        Assert.NotEqual("NEW-OWNER", (await db.Set<MigrationLoadedUnit>().AsNoTracking().SingleAsync(row =>
            row.UnitId == ownerItem.UnitId)).OwnerPartyCode);
        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var goneItem = (await apply.DeltaItemsAsync(view.LoadId, DeltaItemKind.GoneFromSource, null, null, null, 50, default)).Items.Single();
        Assert.True(await serialisation.Units.AnyAsync(row => row.UnitId == goneItem.UnitId));

        // Undone on its own (OD-62): its new unit leaves the module and the register; the final load stays.
        var undone = await apply.RollBackAsync(view.LoadId, Admin, default);
        Assert.Equal(RollBackMigrationLoadResult.RolledBack, undone.Outcome);
        Assert.Equal(1, undone.UnitsRemoved);
        Assert.False(await serialisation.Units.AnyAsync(row => row.UnitId == newUnit.UnitId));
        Assert.False(await db.Set<MigrationLoadedUnit>().AnyAsync(row => row.LoadId == view.LoadId));
        Assert.Equal(final.Applied, await db.Set<MigrationLoadedUnit>().CountAsync(row => row.LoadId == final.RunId));
        Assert.Equal(ApplyRunStatus.Completed, (await db.Set<MigrationApplyRun>().AsNoTracking()
            .SingleAsync(row => row.RunId == final.RunId)).Status);
    }

    [DatabaseFact]
    public async Task A_delta_load_replaces_changed_migrated_cover_and_undoing_it_restores_the_register()
    {
        var tenant = await fixture.NewTenantAsync("dcover");
        var setup = await SetUpAsync(tenant, new DateTime(2026, 12, 31, 9, 0, 0));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var apply = scope.ServiceProvider.GetRequiredService<ApplyService>();
        var final = await FinalLoadAsync(apply, setup.BatchId);
        var plan = await db.MigrationPlanUnits.AsNoTracking().Where(row => row.MapRunId == setup.MapRunId).ToListAsync();
        var covered = plan.Single(row => row.ItemCode == "SERVER-CONTRACT");
        var before = await db.Set<MigrationLoadedUnit>().AsNoTracking().SingleAsync(row =>
            row.SourceKeyHash == covered.SourceKeyHash);

        var delta = await CloneAsync(db, tenant, setup, plan, coverStartChange: (covered.PlanUnitId, new DateOnly(2026, 1, 15)));
        var queued = await apply.EnqueueDeltaAsync(delta.BatchId, Admin, default);
        Assert.Equal(1, await apply.RunPendingAsync(default));
        var view = (await apply.GetAsync(delta.BatchId, default))!;
        var coverFindings = await apply.DeltaItemsAsync(view.LoadId, null, null, null, null, 50, default);
        Assert.True(view.CoverReplaced == 1, string.Join("; ", coverFindings.Items.Select(row =>
            $"{row.Kind}:{row.ItemCode}:{row.Detail}")) + $" refused={view.Refused}");
        Assert.Equal(0, view.Applied);
        var after = await db.Set<MigrationLoadedUnit>().AsNoTracking().SingleAsync(row =>
            row.SourceKeyHash == covered.SourceKeyHash);
        Assert.NotEqual(before.CoverHash, after.CoverHash);
        Assert.Equal(queued.LoadId, after.CoverLoadId);
        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.True(await serialisation.Entitlements.AnyAsync(row => row.UnitId == before.UnitId &&
            row.MigrationLoadId == queued.LoadId && row.StartDate == new DateOnly(2026, 1, 15)));

        var undone = await apply.RollBackAsync(view.LoadId, Admin, default);
        Assert.Equal(RollBackMigrationLoadResult.RolledBack, undone.Outcome);
        Assert.Equal(1, undone.CoverReplacementsReversed);
        var restored = await db.Set<MigrationLoadedUnit>().AsNoTracking().SingleAsync(row =>
            row.SourceKeyHash == covered.SourceKeyHash);
        Assert.Equal((before.CoverHash, final.RunId), (restored.CoverHash, restored.CoverLoadId));
        Assert.False(await serialisation.Entitlements.AnyAsync(row => row.MigrationLoadId == queued.LoadId));
    }

    [DatabaseFact]
    public async Task The_console_reads_load_actions_labels_and_undo_order_and_the_final_load_is_undone_after_its_deltas()
    {
        var tenant = await fixture.NewTenantAsync("dcons");
        var setup = await SetUpAsync(tenant, new DateTime(2026, 12, 31, 9, 0, 0));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var apply = scope.ServiceProvider.GetRequiredService<ApplyService>();
        var watermarkBefore = await db.ConnectionPollWatermarks.AsNoTracking().SingleAsync(row =>
            row.ConnectionId == setup.ConnectionId);

        // Before any load: rehearsal and final may start; a delta has nothing to build on.
        var ready = await apply.ReadinessAsync(setup.BatchId, default);
        Assert.True(ready.Rehearsal.Allowed);
        Assert.True(ready.Final.Allowed);
        Assert.Equal(["migration_delta_base_missing"], ready.Delta.Reasons.Select(row => row.Code));
        var connection = await db.Connections.SingleAsync(row => row.ConnectionId == setup.ConnectionId);
        connection.Status = ConnectionStatus.Active;
        await db.SaveChangesAsync();
        var running = await apply.ReadinessAsync(setup.BatchId, default);
        Assert.True(running.Rehearsal.Allowed);
        Assert.Contains("Stop the sync on Fictional ERP first", running.Final.Reasons.Single().Message);
        connection.Status = ConnectionStatus.Paused;
        await db.SaveChangesAsync();

        var final = await FinalLoadAsync(apply, setup.BatchId);
        var afterFinal = await apply.ReadinessAsync(setup.BatchId, default);
        Assert.Equal("migration_apply_rehearsal_closed", afterFinal.Rehearsal.Reasons.Single().Code);
        Assert.Equal("migration_apply_final_done", afterFinal.Final.Reasons.Single().Code);
        Assert.StartsWith("F1 is in the module", afterFinal.Final.Reasons.Single().Message);
        Assert.Equal("migration_delta_same_batch", afterFinal.Delta.Reasons.Single().Code);
        // Connection health carries the hand-over while the connection is stopped (OD-60).
        var health = scope.ServiceProvider.GetRequiredService<Yojana.Integration.Connections.ConnectionQueries>();
        Assert.Equal(new DateTime(2026, 12, 31, 9, 0, 0), (await health.HealthAsync(setup.ConnectionId, default)).HandOver!.From);
        // Enqueue refuses with exactly the reason the console shows.
        var refused = await Assert.ThrowsAsync<ApplyRuleException>(() =>
            apply.EnqueueAsync(setup.BatchId, new(true), Admin, default));
        Assert.Equal(afterFinal.Rehearsal.Reasons.Single().Code, refused.Rule);

        var plan = await db.MigrationPlanUnits.AsNoTracking().Where(row => row.MapRunId == setup.MapRunId).ToListAsync();
        var owned = await db.MigrationPlanOwnership.AsNoTracking().Where(row => row.MapRunId == setup.MapRunId &&
                row.IsCurrent && row.PartyCode != null &&
                db.MigrationPlanUnits.Any(unit => unit.PlanUnitId == row.PlanUnitId && unit.ItemCode != Unsynced))
            .Select(row => row.PlanUnitId).FirstAsync();
        var template = plan.Single(row => row.ItemCode == "SERVER-CONTRACT");
        var delta = await CloneAsync(db, tenant, setup, plan, ownerChange: (owned, "NEW-OWNER"),
            extra: [(template, "delta|new-a", "SER-NEW-A", "MFG-NEW-A"), (template, "delta|new-b", "SER-NEW-B", "MFG-NEW-B")]);
        var deltaReady = await apply.ReadinessAsync(delta.BatchId, default);
        Assert.True(deltaReady.Delta.Allowed);
        Assert.Equal((final.RunId, "F1"), (deltaReady.Delta.BaseLoadId!.Value, deltaReady.Delta.BaseLabel));
        // Loads are ordered and labelled by request time; the shared test clock stands still, so it moves on here.
        var originalNow = fixture.Clock.UtcNow;
        Guid deltaId;
        try
        {
            fixture.Clock.UtcNow = originalNow.AddMinutes(5);
            deltaId = (await apply.EnqueueDeltaAsync(delta.BatchId, Admin, default)).LoadId;
            Assert.Equal(1, await apply.RunPendingAsync(default));
        }
        finally { fixture.Clock.UtcNow = originalNow; }
        var queued = new { LoadId = deltaId };

        // Loads newest first, labelled by kind; the final load waits for its delta, the delta can be undone.
        var loads = await apply.LoadsAsync(setup.SourceId, null, 50, default);
        Assert.Equal([("D1", "delta", "F1"), ("F1", "final", (string?)null)],
            loads.Items.Select(row => (row.Label, row.Kind, row.BaseLabel)));
        Assert.True(loads.Items[0].Undo.Allowed);
        Assert.Equal("A later delta load (D1) stands. Undo it first.", loads.Items[1].Undo.Reason);
        Assert.Equal(("Fictional ERP", "stopped", (DateTime?)new DateTime(2026, 12, 31, 9, 0, 0)),
            (loads.Items[1].HandOver!.ConnectionName, loads.Items[1].HandOver!.SyncState, loads.Items[1].HandOver!.From));
        Assert.Null(loads.Items[0].HandOver);
        var page = await apply.LoadsAsync(setup.SourceId, null, 1, default);
        Assert.Equal("F1", (await apply.LoadsAsync(setup.SourceId, page.NextCursor, 1, default)).Items.Single().Label);
        Assert.Equal("D1", (await apply.LoadAsync(queued.LoadId, default))!.Label);
        var blocked = await Assert.ThrowsAsync<ApplyRuleException>(() => apply.RollBackAsync(final.RunId, Admin, default));
        Assert.Equal("migration_final_later_delta", blocked.Rule);

        // Findings: exact item code, serial contains, the whole-load total, and the owner difference's conflict.
        var all = await apply.DeltaItemsAsync(queued.LoadId, null, null, null, null, 50, default);
        var serialMatch = await apply.DeltaItemsAsync(queued.LoadId, null, null, "NEW-A", null, 50, default);
        Assert.Equal(("SER-NEW-A", 1L, all.Total), (serialMatch.Items.Single().Serial, serialMatch.Total, serialMatch.LoadTotal));
        Assert.Equal(0, (await apply.DeltaItemsAsync(queued.LoadId, null, "SERVER", null, null, 50, default)).Total);
        var ownerItem = (await apply.DeltaItemsAsync(queued.LoadId, DeltaItemKind.OwnerDiffers, null, null, null, 50,
            default)).Items.Single();
        Assert.Equal(ownerItem.ConflictId, await db.CoexistenceConflicts.AsNoTracking().Where(row =>
            row.Kind == CoexistenceConflictKind.MigrationOwnerDiffers).Select(row => (Guid?)row.ConflictId).SingleAsync());
        var export = apply.ExportDeltaItems(queued.LoadId, DeltaItemKind.NewUnit, null, null);
        var rows = new List<IReadOnlyList<string?>>();
        await foreach (var row in export.Rows) rows.Add(row);
        Assert.Equal(["SER-NEW-A", "SER-NEW-B"], rows.Select(row => row[0]).Order());
        Assert.All(rows, row => Assert.Equal("New in the source", row[2]));

        // Latest first: the delta, then the final load, which puts the connector's watermark back (OD-63).
        Assert.Equal(RollBackMigrationLoadResult.RolledBack, (await apply.RollBackAsync(queued.LoadId, Admin, default)).Outcome);
        // Loads clear the shared change tracker, so the connection is read again before it is changed.
        connection = await db.Connections.SingleAsync(row => row.ConnectionId == setup.ConnectionId);
        connection.Status = ConnectionStatus.Active;
        await db.SaveChangesAsync();
        Assert.Equal("migration_apply_connection_running",
            (await Assert.ThrowsAsync<ApplyRuleException>(() => apply.RollBackAsync(final.RunId, Admin, default))).Rule);
        connection = await db.Connections.SingleAsync(row => row.ConnectionId == setup.ConnectionId);
        connection.Status = ConnectionStatus.Paused;
        await db.SaveChangesAsync();
        var undone = await apply.RollBackAsync(final.RunId, Admin, default);
        Assert.Equal(RollBackMigrationLoadResult.RolledBack, undone.Outcome);
        var watermark = await db.ConnectionPollWatermarks.AsNoTracking().SingleAsync(row =>
            row.ConnectionId == setup.ConnectionId);
        Assert.Equal((watermarkBefore.Watermark, watermarkBefore.NextPollAt), (watermark.Watermark, watermark.NextPollAt));
        Assert.False(await db.Set<MigrationLoadedUnit>().AnyAsync(row => row.ConnectionId == setup.ConnectionId));
        Assert.Null((await health.HealthAsync(setup.ConnectionId, default)).HandOver);
        var again = await apply.ReadinessAsync(setup.BatchId, default);
        Assert.True(again.Rehearsal.Allowed);
        Assert.True(again.Final.Allowed);
        Assert.Equal(["Already undone.", "Already undone."],
            (await apply.LoadsAsync(setup.SourceId, null, 50, default)).Items.Select(row => row.Undo.Reason));
    }

    private sealed record Setup(Guid BatchId, Guid MapRunId, Guid ConnectionId, Guid SourceId);

    private async Task<Setup> SetUpAsync(TenantId tenant, DateTime sourceClock)
    {
        var policyId = await new MigrationMappingTests(fixture).PublishedPolicyAsync(tenant);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var ids = MigrationMappingTests.SeedPipeline(db, tenant, policyId);
        await db.SaveChangesAsync();
        var mappingSettings = scope.ServiceProvider.GetRequiredService<MappingSettings>();
        var defaults = await mappingSettings.GetAsync(default);
        await mappingSettings.SetAsync(new(true, new DateOnly(2026, 9, 1), defaults.ConditionMap), default);
        var mapping = scope.ServiceProvider.GetRequiredService<MappingService>();
        await mapping.EnqueueAsync(ids.BatchId, default);
        Assert.Equal(1, await mapping.RunPendingAsync(default));
        var map = (await mapping.GetAsync(ids.BatchId, default))!;

        var batch = await db.MigrationBatches.SingleAsync(row => row.BatchId == ids.BatchId);
        batch.SourceClockAtStart = sourceClock;
        db.Connections.Add(new Connection { TenantId = tenant, ConnectionId = batch.ConnectionId, ConnectorCode = "CX01",
            DisplayLabel = "Fictional ERP", Status = ConnectionStatus.Paused, CreatedAt = fixture.Clock.UtcNow,
            RegistrationCompleted = true });
        db.ConnectionGatewaySettings.Add(new ConnectionGatewaySetting { TenantId = tenant, ConnectionId = batch.ConnectionId,
            LegalEntityId = Guid.NewGuid(), OutboundMode = "reference_only", NativeWarrantyObjectMode = "frozen", OrderingWaitMinutes = 15 });
        db.ConnectionPollWatermarks.Add(new ConnectionPollWatermark { TenantId = tenant, ConnectionId = batch.ConnectionId,
            EntityType = "deliveries", OverlapSeconds = 300, IntervalSeconds = 120,
            NextPollAt = fixture.Clock.UtcNow.AddDays(1) });
        var itemCodes = await db.MigrationPlanUnits.Where(row => row.MapRunId == map.RunId).Select(row => row.ItemCode)
            .Distinct().ToListAsync();
        foreach (var code in itemCodes.Where(code => code != Unsynced))
        {
            var moduleId = Guid.CreateVersion7();
            db.ExternalIdMaps.Add(new ExternalIdMap
            {
                TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(), ConnectionId = batch.ConnectionId, EntityType = "item",
                ExternalId = code, ModuleId = moduleId, ValidFrom = fixture.Clock.UtcNow, NaturalKeyHash = code,
                MappingSource = ExternalIdMappingSource.Auto, MappedBy = "fixture", MappedAt = fixture.Clock.UtcNow,
            });
            db.ReferenceProjections.Add(new ReferenceProjection
            {
                TenantId = tenant, EntityType = "item", ModuleId = moduleId, Code = code, DisplayName = code,
                TrackingMode = "serial", Uom = "EA", AttributesJson = "{}", UpdatedAt = fixture.Clock.UtcNow,
            });
        }
        AddFinalSignoff(db, tenant, ids.BatchId, map.RunId);
        await db.SaveChangesAsync();
        return new(ids.BatchId, map.RunId, batch.ConnectionId, batch.ConnectionId);
    }

    private static async Task<MigrationApplyRun> FinalLoadAsync(ApplyService apply, Guid batchId)
    {
        var queued = await apply.EnqueueAsync(batchId, new(false), Admin, default);
        Assert.Equal(1, await apply.RunPendingAsync(default));
        var view = (await apply.GetAsync(batchId, default))!;
        Assert.Equal(ApplyRunStatus.Completed, view.Status);
        return new MigrationApplyRun { RunId = queued.LoadId, Applied = view.Applied, Status = view.Status, RequestedBy = Admin };
    }

    /// <summary>
    /// A later extraction of the same source, mapped: a new batch and map run holding a copy of the given plan units
    /// (new plan ids, same source keys), with the requested changes and extra units.
    /// </summary>
    private async Task<(Guid BatchId, Guid MapRunId)> CloneAsync(IntegrationDbContext db, TenantId tenant, Setup setup,
        List<MigrationPlanUnit> units, (Guid PlanUnitId, string Owner)? ownerChange = null,
        (Guid PlanUnitId, DateOnly Start)? coverStartChange = null,
        IReadOnlyList<(MigrationPlanUnit Template, string SourceKey, string? Serial, string? MakerSerial)>? extra = null)
    {
        var baseBatch = await db.MigrationBatches.AsNoTracking().SingleAsync(row => row.BatchId == setup.BatchId);
        var baseMap = await db.MigrationMapRuns.AsNoTracking().SingleAsync(row => row.RunId == setup.MapRunId);
        var batchId = Guid.CreateVersion7();
        var later = baseBatch.RequestedAt.AddDays(7);
        db.MigrationBatches.Add(new MigrationBatch
        {
            TenantId = tenant, BatchId = batchId, DisplayNumber = 2, ConnectionId = setup.ConnectionId,
            SourceSystem = baseBatch.SourceSystem, Kind = "full", RequestedBy = "fictional-migration-lead",
            RequestedAt = later, StartedAt = later, FinishedAt = later, Status = MigrationBatchStatus.Succeeded,
            SourceClockAtStart = later,
        });
        var mapRunId = Guid.CreateVersion7();
        var planned = units.Select(unit => (Unit: unit, SourceKey: unit.SourceKey!, Serial: (string?)null,
            MakerSerial: (string?)null, Fresh: false)).Concat((extra ?? []).Select(row => (Unit: row.Template,
            row.SourceKey, row.Serial, row.MakerSerial, Fresh: true))).ToList();
        db.MigrationMapRuns.Add(new MigrationMapRun
        {
            TenantId = tenant, RunId = mapRunId, ReconstructionRunId = baseMap.ReconstructionRunId, BatchId = batchId,
            CutoverDate = baseMap.CutoverDate, AllowedWaitingReviews = true, RequestedAt = later, StartedAt = later,
            FinishedAt = later, Status = MappingRunStatus.Completed, UnitCount = planned.Count,
        });
        await db.SaveChangesAsync();
        foreach (var (unit, sourceKey, serial, makerSerial, fresh) in planned)
        {
            var id = Guid.CreateVersion7();
            db.MigrationPlanUnits.Add(new MigrationPlanUnit
            {
                TenantId = tenant, PlanUnitId = id, MapRunId = mapRunId, CandidateId = fresh ? Guid.NewGuid() : unit.CandidateId,
                ItemCode = unit.ItemCode, ItemName = unit.ItemName, Origin = unit.Origin, HistoryDepth = unit.HistoryDepth,
                LocationState = unit.LocationState, WarehouseCode = unit.WarehouseCode,
                CustodyLocationKind = unit.CustodyLocationKind, ConditionGrade = unit.ConditionGrade,
                ConditionProvenance = unit.ConditionProvenance, SourceKey = sourceKey,
                SourceKeyHash = MigrationSourceKey.Hash(sourceKey),
            });
            foreach (var row in await db.MigrationPlanIdentifiers.AsNoTracking().Where(row => row.PlanUnitId == unit.PlanUnitId).ToListAsync())
            {
                var value = row.IdentifierType == IdentifierType.InternalSerial ? serial ?? row.RawValue
                    : makerSerial ?? row.RawValue;
                db.MigrationPlanIdentifiers.Add(new MigrationPlanIdentifier
                {
                    TenantId = tenant, PlanIdentifierId = Guid.CreateVersion7(), MapRunId = mapRunId, PlanUnitId = id,
                    IdentifierType = row.IdentifierType, RawValue = value, IssuerKind = row.IssuerKind,
                    SourceDataValue = row.SourceDataValue, Provenance = row.Provenance,
                });
            }
            foreach (var row in await db.MigrationPlanEvents.AsNoTracking().Where(row => row.PlanUnitId == unit.PlanUnitId).ToListAsync())
            {
                db.MigrationPlanEvents.Add(new MigrationPlanEvent
                {
                    TenantId = tenant, PlanEventId = Guid.CreateVersion7(), MapRunId = mapRunId, PlanUnitId = id,
                    HistoryEventId = row.HistoryEventId, Sequence = row.Sequence, MovementKind = row.MovementKind,
                    EffectiveDate = row.EffectiveDate, DocumentTypeLabel = row.DocumentTypeLabel,
                    DocumentNumber = row.DocumentNumber, PartyCode = row.PartyCode, WarehouseCode = row.WarehouseCode,
                    Quantity = row.Quantity, Provenance = row.Provenance,
                });
            }
            foreach (var row in await db.MigrationPlanOwnership.AsNoTracking().Where(row => row.PlanUnitId == unit.PlanUnitId).ToListAsync())
            {
                var changed = !fresh && ownerChange is { } change && change.PlanUnitId == unit.PlanUnitId && row.IsCurrent;
                db.MigrationPlanOwnership.Add(new MigrationPlanOwnership
                {
                    TenantId = tenant, PlanOwnershipId = Guid.CreateVersion7(), MapRunId = mapRunId, PlanUnitId = id,
                    Tier = row.Tier, PartyCode = changed ? ownerChange!.Value.Owner : row.PartyCode, PartyName = row.PartyName,
                    PartyReference = changed ? ownerChange!.Value.Owner : row.PartyReference,
                    Pseudonymised = !changed && row.Pseudonymised, IsCurrent = row.IsCurrent,
                    EvidenceSource = row.EvidenceSource, EvidenceDate = row.EvidenceDate,
                });
            }
            var newStart = !fresh && coverStartChange is { } cover && cover.PlanUnitId == unit.PlanUnitId ? cover.Start : (DateOnly?)null;
            var entitlements = await db.MigrationPlanEntitlements.AsNoTracking().Where(row => row.PlanUnitId == unit.PlanUnitId).ToListAsync();
            // The source changed the start of the named cover: that entitlement and the evidence row it was selected from.
            var changedCover = newStart is null ? null : entitlements.FirstOrDefault(row => row.PolicyId != null);
            foreach (var row in entitlements)
            {
                db.MigrationPlanEntitlements.Add(new MigrationPlanEntitlement
                {
                    TenantId = tenant, PlanEntitlementId = Guid.CreateVersion7(), MapRunId = mapRunId, PlanUnitId = id,
                    CoverageFamily = row.CoverageFamily, PolicyId = row.PolicyId, PolicyVersion = row.PolicyVersion,
                    StartDate = row == changedCover ? newStart : row.StartDate, EndDate = row.EndDate, TerminationDate = row.TerminationDate,
                    RemainingDays = row.RemainingDays, Expired = row.Expired, Direction = row.Direction,
                    Provenance = row.Provenance, DateSource = row.DateSource, SourceKey = row.SourceKey,
                });
            }
            foreach (var row in await db.MigrationPlanWarrantyEvidence.AsNoTracking().Where(row => row.PlanUnitId == unit.PlanUnitId).ToListAsync())
            {
                db.MigrationPlanWarrantyEvidence.Add(new MigrationPlanWarrantyEvidence
                {
                    TenantId = tenant, PlanWarrantyEvidenceId = Guid.CreateVersion7(), MapRunId = mapRunId, PlanUnitId = id,
                    SourceKind = row.SourceKind, SourceKey = row.SourceKey,
                    StartDate = changedCover is not null && row.SelectedForEntitlement && row.SourceKind == changedCover.DateSource
                        && row.SourceKey == changedCover.SourceKey ? newStart : row.StartDate,
                    EndDate = row.EndDate,
                    TerminationDate = row.TerminationDate, Direction = row.Direction,
                    SelectedForEntitlement = row.SelectedForEntitlement,
                });
            }
            foreach (var row in await db.MigrationPlanCertificates.AsNoTracking().Where(row => row.PlanUnitId == unit.PlanUnitId).ToListAsync())
            {
                db.MigrationPlanCertificates.Add(new MigrationPlanCertificate
                {
                    TenantId = tenant, PlanCertificateId = Guid.CreateVersion7(), MapRunId = mapRunId, PlanUnitId = id,
                    Version = row.Version, Provenance = row.Provenance,
                });
            }
            await db.SaveChangesAsync();
        }
        return (batchId, mapRunId);
    }

    private void AddFinalSignoff(IntegrationDbContext db, TenantId tenant, Guid batchId, Guid mapRunId)
    {
        var runId = Guid.CreateVersion7();
        db.Set<MigrationValidationRun>().Add(new MigrationValidationRun
        {
            TenantId = tenant, RunId = runId, BatchId = batchId, MapRunId = mapRunId, TargetKind = ValidationTargetKind.Plan,
            Rehearsal = false, RequestedAt = fixture.Clock.UtcNow, FinishedAt = fixture.Clock.UtcNow,
            Status = ValidationRunStatus.Completed,
        });
        db.Set<MigrationSignoffRecord>().Add(new MigrationSignoffRecord
        {
            TenantId = tenant, RecordId = Guid.CreateVersion7(), RunId = runId, Rehearsal = false,
            CreatedAt = fixture.Clock.UtcNow, CreatedBy = Admin,
        });
    }
}
