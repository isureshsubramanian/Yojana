using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using Wolverine;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.LegacyMigration.Apply;
using Yojana.Integration.LegacyMigration.LinkOverrides;
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

        var linkSourceKey = await db.MigrationPlanReplacementLinks.Where(row => row.MapRunId == map.RunId)
            .Select(row => row.SourceKey).SingleAsync();
        db.MigrationLinkOverrides.Add(new MigrationLinkOverride
        {
            TenantId = tenant, SourceKey = linkSourceKey, Direction = ReplacementDirection.VendorFacing,
            ReplacementReason = ReplacementReason.Goodwill, State = ReplacementLinkState.ClosedBilled,
            ReplacementConditionAllowed = ReplacementConditionAllowed.New,
            PricePolicy = ReplacementPricePolicy.Difference, DifferenceAmount = 25m, Currency = "EUR",
            UploadedAt = fixture.Clock.UtcNow, UploadedBy = "user:migration-lead",
            UploadFileName = "replacement-links.csv",
        });
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
        Assert.Equal("Unknown user", view.RequestedByName);
        var planned = map.UnitsByOrigin.Single(row => row.Key == UnitOrigin.Migrated).Count;
        var refusalItems = (await apply.RefusalsAsync(view.LoadId, null, 50, default)).Items;
        var refusalSummary = string.Join("; ", refusalItems.Select(row =>
            $"{row.ItemCode}:{string.Join(',', row.Reasons)}"));
        Assert.Equal(planned, view.UnitsInPlan);
        Assert.True(view.Applied == planned - 1, refusalSummary);
        Assert.Equal(1, view.Refused);
        var cardPolicyGap = Assert.Single(view.GapsByKind!, row =>
            row.Kind == PlanGapKind.CardCoverPolicyNotSet);
        Assert.Equal((PlanGapKind.CardCoverPolicyNotSet, 1), (cardPolicyGap.Kind, cardPolicyGap.Count));
        Assert.DoesNotContain(view.GapsByKind!, row => row.Kind == PlanGapKind.SerialMasterCoverPolicyNotSet);
        Assert.True(view.EventsWritten >= view.Applied);
        // Plan cover with no policy named (equipment-card or serial-master dates) is not loaded; backfill closes it.
        var expectedEntitlements = await db.MigrationPlanEntitlements.LongCountAsync(row =>
            row.MapRunId == map.RunId && row.PolicyId != null && db.MigrationPlanUnits.Any(unit =>
                unit.PlanUnitId == row.PlanUnitId && unit.ItemCode != Unsynced));
        Assert.Equal(expectedEntitlements, view.EntitlementsWritten);
        Assert.Equal(await db.MigrationPlanReplacementLinks.LongCountAsync(row => row.MapRunId == map.RunId),
            view.ReplacementLinksWritten);
        Assert.Equal(1, view.ReplacementLinksUsingOverride);
        Assert.Equal(view.Applied, view.CertificatesRecorded);
        var refusal = Assert.Single(refusalItems);
        Assert.Equal(Unsynced, refusal.ItemCode);
        Assert.Equal(["item_not_synced"], refusal.Reasons);
        Assert.Equal(1, (await apply.RefusalsAsync(view.LoadId, "item_not_synced", null, null, null, 50,
            default)).Total);

        var extraPlan = await db.MigrationPlanUnits.Where(row => row.MapRunId == map.RunId &&
                row.ItemCode != Unsynced && db.MigrationPlanIdentifiers.Any(identifier =>
                    identifier.PlanUnitId == row.PlanUnitId && identifier.IdentifierType == IdentifierType.InternalSerial))
            .OrderBy(row => row.PlanUnitId).Select(row => new
            {
                row.PlanUnitId,
                row.ItemCode,
                Serial = db.MigrationPlanIdentifiers.Where(identifier => identifier.PlanUnitId == row.PlanUnitId &&
                        identifier.IdentifierType == IdentifierType.InternalSerial)
                    .Select(identifier => identifier.RawValue).First(),
            }).FirstAsync();
        db.Set<MigrationApplyRefusal>().Add(new MigrationApplyRefusal
        {
            TenantId = tenant, RefusalId = Guid.CreateVersion7(fixture.Clock.UtcNow.AddMilliseconds(1)),
            RunId = view.LoadId, PlanUnitId = extraPlan.PlanUnitId,
            Reasons = "duplicate_in_batch condition_grade_unknown",
        });
        await db.SaveChangesAsync();
        var refusalPage = await apply.RefusalsAsync(view.LoadId, null, null, null, null, 1, default);
        Assert.Equal(2, refusalPage.Total);
        Assert.Equal(2, refusalPage.LoadTotal);
        Assert.Single(refusalPage.Items);
        Assert.NotNull(refusalPage.NextCursor);
        Assert.Contains(refusalPage.CountsByReason, row => row.Reason == "duplicate_in_batch" &&
            row.Label == "Identifier is duplicated in this load" && row.Count == 1);
        var byItem = await apply.RefusalsAsync(view.LoadId, null, extraPlan.ItemCode, null, null, 50, default);
        Assert.Equal(extraPlan.PlanUnitId, Assert.Single(byItem.Items).PlanUnitId);
        var bySerial = await apply.RefusalsAsync(view.LoadId, null, null,
            extraPlan.Serial[^Math.Min(5, extraPlan.Serial.Length)..], null, 50, default);
        Assert.Equal(extraPlan.Serial, Assert.Single(bySerial.Items).Serial);
        var byReason = await apply.RefusalsAsync(view.LoadId, "condition_grade_unknown", null, null, null, 50,
            default);
        Assert.Equal(extraPlan.PlanUnitId, Assert.Single(byReason.Items).PlanUnitId);
        var csv = Encoding.UTF8.GetString(await apply.ExportRefusalsAsync(view.LoadId, "duplicate_in_batch",
            null, null, default));
        Assert.Contains("Item code,Serial,Why it was refused", csv);
        Assert.Contains("Identifier is duplicated in this load", csv);
        Assert.Contains("Condition grade is not known", csv);
        Assert.DoesNotContain("duplicate_in_batch", csv);

        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var overriddenLink = await serialisation.ReplacementLinks.SingleAsync(row => row.MigrationLoadId == view.LoadId);
        Assert.Equal((ReplacementDirection.VendorFacing, ReplacementReason.Goodwill,
                ReplacementLinkState.ClosedBilled, ReplacementConditionAllowed.New,
                ReplacementPricePolicy.Difference, 25m, "EUR"),
            (overriddenLink.Direction, overriddenLink.ReplacementReason, overriddenLink.State,
                overriddenLink.ReplacementConditionAllowed, overriddenLink.PricePolicy,
                overriddenLink.DifferenceAmount, overriddenLink.Currency));
        var loaded = await serialisation.MigrationLoadUnits.Where(row => row.LoadId == view.LoadId).Select(row => row.UnitId).ToListAsync();
        Assert.Equal(planned - 1, loaded.Count);
        Assert.All(await serialisation.Units.Where(row => loaded.Contains(row.UnitId)).ToListAsync(),
            unit => Assert.Equal(UnitOrigin.Migrated, unit.Origin));
        var publishedPlan = await db.MigrationPlanUnits.SingleAsync(row => row.MapRunId == map.RunId
            && row.ItemCode == "SERVER-CONTRACT");
        var publishedUnit = await serialisation.MigrationLoadUnits.Where(row => row.LoadId == view.LoadId
            && row.PlanUnitId == publishedPlan.PlanUnitId).Select(row => row.UnitId).SingleAsync();
        var appliedEntitlement = await serialisation.Entitlements.SingleAsync(row =>
            row.UnitId == publishedUnit && row.Provenance == EntitlementProvenance.Migrated);
        Assert.Equal(policyId, appliedEntitlement.PolicyVersionId);
        Assert.Equal(new DateOnly(2026, 10, 15), appliedEntitlement.EndDate);
        var draftPlan = await db.MigrationPlanUnits.SingleAsync(row => row.MapRunId == map.RunId
            && row.ItemCode == "STORAGE-DRAFT");
        var draftUnit = await serialisation.MigrationLoadUnits.Where(row => row.LoadId == view.LoadId
            && row.PlanUnitId == draftPlan.PlanUnitId).Select(row => row.UnitId).SingleAsync();
        Assert.False(await serialisation.Entitlements.AnyAsync(row => row.UnitId == draftUnit));
        Assert.True(await db.MigrationPlanGaps.AnyAsync(row => row.MapRunId == map.RunId
            && row.PlanUnitId == draftPlan.PlanUnitId && row.Kind == PlanGapKind.PolicyNotPublished));
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
        // Last step of the load (P9-T09): the gap report over migrated owners, recorded on the apply run.
        var ownedUnits = owned.Select(row => row.UnitId).ToList();
        var coveredOwners = await serialisation.Entitlements.Where(row => ownedUnits.Contains(row.UnitId) &&
            row.CoverageFamily == CoverageFamilyCode.Base).Select(row => row.UnitId).Distinct().CountAsync();
        Assert.NotNull(view.GapReportRunId);
        Assert.Equal(owned.Count - coveredOwners, view.CoverGaps);
        var gapRun = await serialisation.EntitlementGapRuns.SingleAsync(row => row.RunId == view.GapReportRunId);
        Assert.Equal(EntitlementGapTrigger.MigrationLoad, gapRun.RunTrigger);

        var moduleValidationId = Guid.CreateVersion7(fixture.Clock.UtcNow.AddMilliseconds(1));
        db.MigrationValidationRuns.Add(new MigrationValidationRun
        {
            TenantId = tenant, RunId = moduleValidationId, BatchId = ids.BatchId, MapRunId = map.RunId,
            TargetKind = ValidationTargetKind.Module, Rehearsal = true, RequestedAt = fixture.Clock.UtcNow,
            StartedAt = fixture.Clock.UtcNow, FinishedAt = fixture.Clock.UtcNow,
            Status = ValidationRunStatus.Completed,
        });
        await db.SaveChangesAsync();
        var moduleValidation = await scope.ServiceProvider.GetRequiredService<ValidationService>()
            .GetAsync(ids.BatchId, default);
        Assert.Equal(view.FinishedAt, moduleValidation!.LoadDate);
        Assert.Equal(1, moduleValidation.RehearsalNumber);

        var again = await Assert.ThrowsAsync<ApplyRuleException>(() => apply.EnqueueAsync(ids.BatchId, new(true), "user:admin", default));
        Assert.Equal("migration_apply_load_open", again.Rule);

        var rolledBack = await apply.RollBackAsync(view.LoadId, "user:admin", default);
        Assert.Equal(RollBackMigrationLoadResult.RolledBack, rolledBack.Outcome);
        Assert.Equal(planned - 1, rolledBack.UnitsRemoved);
        Assert.Equal(view.EntitlementsWritten, rolledBack.EntitlementsRemoved);
        Assert.Equal(view.ReplacementLinksWritten, rolledBack.ReplacementLinksRemoved);
        Assert.Equal(view.CertificatesRecorded, rolledBack.CertificatesRemoved);
        Assert.Equal(0, await serialisation.Units.CountAsync(row => loaded.Contains(row.UnitId)));
        Assert.Equal(ApplyRunStatus.RolledBack, (await apply.GetAsync(ids.BatchId, default))!.Status);

        // After a rollback the next rehearsal loads the same plan afresh.
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<LinkOverrideService>()
            .DeleteAllAsync(new(true), default)).Removed);
        var second = await apply.EnqueueAsync(ids.BatchId, new(true), "user:admin", default);
        Assert.NotEqual(view.LoadId, second.LoadId);
        Assert.Equal(1, await apply.RunPendingAsync(default));
        var secondRun = await db.Set<MigrationApplyRun>().SingleAsync(row => row.RunId == second.LoadId);
        Assert.Equal(planned - 1, secondRun.Applied);
        Assert.Equal(0, secondRun.ReplacementLinksUsingOverride);
        var defaultLink = await serialisation.ReplacementLinks.AsNoTracking()
            .SingleAsync(row => row.MigrationLoadId == second.LoadId);
        Assert.Equal((ReplacementDirection.CustomerFacing, ReplacementReason.WarrantyDefect,
                ReplacementLinkState.ClosedReturned, ReplacementConditionAllowed.Any,
                ReplacementPricePolicy.Zero, 0m, "XXX"),
            (defaultLink.Direction, defaultLink.ReplacementReason, defaultLink.State,
                defaultLink.ReplacementConditionAllowed, defaultLink.PricePolicy,
                defaultLink.DifferenceAmount, defaultLink.Currency));

        var touchedLoadUnit = await serialisation.MigrationLoadUnits.AsNoTracking()
            .Where(row => row.LoadId == second.LoadId).OrderBy(row => row.PlanUnitId)
            .Select(row => new { row.PlanUnitId, row.UnitId }).FirstAsync();
        var touchedSerial = await db.MigrationPlanIdentifiers.AsNoTracking().Where(row =>
                row.PlanUnitId == touchedLoadUnit.PlanUnitId && row.IdentifierType == IdentifierType.InternalSerial)
            .Select(row => row.RawValue).FirstAsync();
        var legalEntity = await db.ConnectionGatewaySettings.AsNoTracking().Where(row =>
                row.ConnectionId == batch.ConnectionId).Select(row => row.LegalEntityId).SingleAsync();
        await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<UnitFlagResult>(new SetUnitFlag(
            Guid.NewGuid(), touchedLoadUnit.UnitId, OverlayFlag.Lost,
            new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), "user:stores", legalEntity,
            "Missing at cycle count"), new DeliveryOptions { TenantId = tenant.ToString() }, default);
        var refusedRollback = await apply.RollBackAsync(second.LoadId, "user:admin", default);
        Assert.Equal(RollBackMigrationLoadResult.RefusedTouched, refusedRollback.Outcome);
        Assert.Equal(1, refusedRollback.TouchedUnitCount);
        Assert.Equal(touchedSerial, Assert.Single(refusedRollback.TouchedUnits).Serial);
        Assert.Null(refusedRollback.TouchedNextCursor);
        var touchedPage = await apply.TouchedUnitsAsync(second.LoadId, null, 50, default);
        Assert.Equal(1, touchedPage.Total);
        Assert.Equal(touchedSerial, Assert.Single(touchedPage.Items).Serial);
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
        Assert.True((await scope.ServiceProvider.GetRequiredService<ValidationService>()
            .GetAsync(ids.BatchId, default))!.HasFinalSignoff);
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
