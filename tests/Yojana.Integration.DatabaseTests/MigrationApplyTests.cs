using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.LegacyMigration.Apply;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Integration.DatabaseTests;

/// <summary>P9-T10 (OD-52, OD-53): a completed load plan applied into the module as a rehearsal, refusals listed, rolled back.</summary>
[Collection("integration host")]
public sealed class MigrationApplyTests(IntegrationHostFixture fixture)
{
    private const string Unsynced = "NETWORK-MASTER";

    [DatabaseFact]
    public async Task A_rehearsal_load_writes_the_plan_into_the_module_lists_refusals_and_rolls_back()
    {
        var tenant = await fixture.NewTenantAsync("apply");
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

        // The source connection: its legal entity, and every plan item synced as a serial-tracked item but one.
        var batch = await db.MigrationBatches.SingleAsync(row => row.BatchId == ids.BatchId);
        db.Connections.Add(new Connection { TenantId = tenant, ConnectionId = batch.ConnectionId, ConnectorCode = "CX01",
            DisplayLabel = "Fictional ERP", Status = ConnectionStatus.Active, CreatedAt = fixture.Clock.UtcNow });
        db.ConnectionGatewaySettings.Add(new ConnectionGatewaySetting { TenantId = tenant, ConnectionId = batch.ConnectionId,
            LegalEntityId = Guid.NewGuid(), OutboundMode = "reference_only", NativeWarrantyObjectMode = "frozen", OrderingWaitMinutes = 15 });
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
        await db.SaveChangesAsync();

        var apply = scope.ServiceProvider.GetRequiredService<ApplyService>();
        var final = await Assert.ThrowsAsync<ApplyRuleException>(() => apply.EnqueueAsync(ids.BatchId, new(false), "user:admin", default));
        Assert.Equal("migration_apply_signoff_required", final.Rule);

        var queued = await apply.EnqueueAsync(ids.BatchId, new(true), "user:admin", default);
        Assert.Equal(ApplyRunStatus.Queued, queued.Status);
        Assert.Equal(map.RunId, queued.MapRunId);
        Assert.Equal(1, await apply.RunPendingAsync(default));

        var view = (await apply.GetAsync(ids.BatchId, default))!;
        Assert.Equal(ApplyRunStatus.Completed, view.Status);
        var planned = map.UnitsByOrigin.Single(row => row.Key == UnitOrigin.Migrated).Count;
        Assert.Equal(planned, view.UnitsInPlan);
        Assert.Equal(planned - 1, view.Applied);
        Assert.Equal(1, view.Refused);
        Assert.True(view.EventsWritten >= view.Applied);
        var refusal = Assert.Single((await apply.RefusalsAsync(view.LoadId, null, 50, default)).Items);
        Assert.Equal(Unsynced, refusal.ItemCode);
        Assert.Equal(["item_not_synced"], refusal.Reasons);

        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var loaded = await serialisation.MigrationLoadUnits.Where(row => row.LoadId == view.LoadId).Select(row => row.UnitId).ToListAsync();
        Assert.Equal(planned - 1, loaded.Count);
        Assert.All(await serialisation.Units.Where(row => loaded.Contains(row.UnitId)).ToListAsync(),
            unit => Assert.Equal(UnitOrigin.Migrated, unit.Origin));
        // The in-stock server sits in custody at its warehouse; shipped units have an owner and no custody row.
        var stockPlan = await db.MigrationPlanUnits.Where(plan => plan.MapRunId == map.RunId && plan.ItemCode == "SERVER-CONTRACT")
            .Select(plan => plan.PlanUnitId).SingleAsync();
        var stockUnit = await serialisation.MigrationLoadUnits.Where(row => row.LoadId == view.LoadId && row.PlanUnitId == stockPlan)
            .Select(row => row.UnitId).SingleAsync();
        var stock = await serialisation.Current.SingleAsync(row => row.UnitId == stockUnit);
        Assert.Equal(LocationKind.Warehouse, stock.LocationKind);
        Assert.Null(stock.OwnerPartyRef);
        var owned = await serialisation.Episodes.Where(row => loaded.Contains(row.UnitId) && row.Kind == EpisodeKind.Ownership &&
            row.ValidTo == null).ToListAsync();
        // Every synced shipped plan unit with a current owner in the plan gets exactly one open ownership row.
        var expectedOwners = await db.MigrationPlanUnits.CountAsync(plan => plan.MapRunId == map.RunId &&
            plan.ItemCode != Unsynced && plan.LocationState == PlanLocationState.Shipped &&
            db.MigrationPlanOwnership.Any(owner => owner.PlanUnitId == plan.PlanUnitId && owner.IsCurrent));
        Assert.True(expectedOwners > 0);
        Assert.Equal(expectedOwners, owned.Count);
        Assert.All(owned, row => Assert.Equal(EpisodeSource.Migration, row.Source));

        var again = await Assert.ThrowsAsync<ApplyRuleException>(() => apply.EnqueueAsync(ids.BatchId, new(true), "user:admin", default));
        Assert.Equal("migration_apply_load_open", again.Rule);

        var rolledBack = await apply.RollBackAsync(view.LoadId, "user:admin", default);
        Assert.Equal(RollBackMigrationLoadResult.RolledBack, rolledBack.Outcome);
        Assert.Equal(planned - 1, rolledBack.UnitsRemoved);
        Assert.Equal(0, await serialisation.Units.CountAsync(row => loaded.Contains(row.UnitId)));
        Assert.Equal(ApplyRunStatus.RolledBack, (await apply.GetAsync(ids.BatchId, default))!.Status);

        // After a rollback the next rehearsal loads the same plan afresh.
        var second = await apply.EnqueueAsync(ids.BatchId, new(true), "user:admin", default);
        Assert.NotEqual(view.LoadId, second.LoadId);
        Assert.Equal(1, await apply.RunPendingAsync(default));
        Assert.Equal(planned - 1, (await apply.GetAsync(ids.BatchId, default))!.Applied);
    }

    [DatabaseFact]
    public async Task A_final_load_needs_the_final_signoff_record_of_exactly_this_load_plan()
    {
        var tenant = await fixture.NewTenantAsync("apply-final");
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
        var older = (await mapping.GetAsync(ids.BatchId, default))!;
        // A final sign-off of an older load plan of the same batch does not carry over to the plan mapped after it.
        AddSignoff(db, tenant, ids.BatchId, older.RunId, rehearsal: false);
        await db.SaveChangesAsync();
        // The second plan must be later on the clock: run ids alone do not order by time in SQL Server.
        var startedAt = fixture.Clock.UtcNow;
        fixture.Clock.UtcNow = startedAt.AddMinutes(5);
        try
        {
            await mapping.EnqueueAsync(ids.BatchId, default);
            Assert.Equal(1, await mapping.RunPendingAsync(default));
        }
        finally
        {
            fixture.Clock.UtcNow = startedAt;
        }
        var map = (await mapping.GetAsync(ids.BatchId, default))!;
        Assert.NotEqual(older.RunId, map.RunId);
        var apply = scope.ServiceProvider.GetRequiredService<ApplyService>();

        // Nor does a rehearsal sign-off of this plan.
        AddSignoff(db, tenant, ids.BatchId, map.RunId, rehearsal: true);
        await db.SaveChangesAsync();
        var refused = await Assert.ThrowsAsync<ApplyRuleException>(() => apply.EnqueueAsync(ids.BatchId, new(false), "user:admin", default));
        Assert.Equal("migration_apply_signoff_required", refused.Rule);

        AddSignoff(db, tenant, ids.BatchId, map.RunId, rehearsal: false);
        await db.SaveChangesAsync();
        var queued = await apply.EnqueueAsync(ids.BatchId, new(false), "user:admin", default);
        Assert.False(queued.Rehearsal);
        Assert.Equal(ApplyRunStatus.Queued, queued.Status);
    }

    private void AddSignoff(IntegrationDbContext db, TenantId tenant, Guid batchId, Guid mapRunId,
        bool rehearsal)
    {
        var runId = Guid.CreateVersion7();
        db.Set<MigrationValidationRun>().Add(new MigrationValidationRun
        {
            TenantId = tenant, RunId = runId, BatchId = batchId, MapRunId = mapRunId, TargetKind = ValidationTargetKind.Plan, Rehearsal = rehearsal,
            RequestedAt = fixture.Clock.UtcNow, FinishedAt = fixture.Clock.UtcNow, Status = ValidationRunStatus.Completed,
        });
        db.Set<MigrationSignoffRecord>().Add(new MigrationSignoffRecord
        {
            TenantId = tenant, RecordId = Guid.CreateVersion7(), RunId = runId, Rehearsal = rehearsal,
            CreatedAt = fixture.Clock.UtcNow, CreatedBy = "user:admin",
        });
    }
}
