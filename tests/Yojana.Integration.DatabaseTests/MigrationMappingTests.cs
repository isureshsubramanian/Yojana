using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class MigrationMappingTests(IntegrationHostFixture fixture)
{
    private static readonly DateTime Extracted = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Mapping_builds_an_inspectable_plan_and_honours_precedence_reviews_and_gaps()
    {
        var tenant = await fixture.NewTenantAsync("mapplan");
        var policyId = await PublishedPolicyAsync(tenant);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var ids = SeedPipeline(db, tenant, policyId);
        await db.SaveChangesAsync();
        var mapping = scope.ServiceProvider.GetRequiredService<MappingService>();

        var refusal = await Assert.ThrowsAsync<MappingRuleException>(() =>
            mapping.EnqueueAsync(ids.BatchId, default));
        Assert.Equal("migration_mapping_waiting_reviews", refusal.Rule);
        Assert.Contains(refusal.WaitingReviews, row => row.Kind == MigrationReviewKind.OrphanEquipmentCard &&
            row.Count == 1);

        var mappingSettings = scope.ServiceProvider.GetRequiredService<MappingSettings>();
        var defaults = await mappingSettings.GetAsync(default);
        Assert.False(defaults.AllowWaitingReviews);
        Assert.Null(defaults.CutoverDate);
        Assert.Equal(ConditionGrades.New, defaults.ConditionMap[^1].Grade);
        await mappingSettings.SetAsync(new(true, new DateOnly(2026, 9, 1), defaults.ConditionMap), default);

        var queued = await mapping.EnqueueAsync(ids.BatchId, default);
        Assert.Equal(MappingRunStatus.Queued, queued.Status);
        var stopwatch = Stopwatch.StartNew();
        Assert.Equal(1, await mapping.RunPendingAsync(default));
        stopwatch.Stop();
        var status = await mapping.GetAsync(ids.BatchId, default);
        Assert.NotNull(status);
        Assert.Equal(MappingRunStatus.Completed, status.Status);
        Assert.Equal(6, status.UnitsByOrigin.Single(row => row.Key == UnitOrigin.Migrated).Count);
        Assert.Equal(1, status.ExcludedCandidates);
        Assert.Equal(1, status.WaitingCandidatesLeftOut);
        Assert.Equal(1, status.UnitsByLocationState.Single(row => row.Key == PlanLocationState.InStock).Count);
        Assert.Equal(5, status.UnitsByLocationState.Single(row => row.Key == PlanLocationState.Shipped).Count);
        Assert.Contains(status.Ownership, row => row.Key == "two_tier" && row.Count == 1);
        Assert.Contains(status.Ownership, row => row.Key == "pseudonymised" && row.Count == 1);
        Assert.Contains(status.GapsByKind, row => row.Key == PlanGapKind.ReplacementUnitMissing);
        Assert.Contains(status.GapsByKind, row => row.Key == PlanGapKind.PolicyNotPublished);
        Assert.Contains(status.GapsByKind, row => row.Key == PlanGapKind.TemplateNotApproved);
        Assert.Contains(status.GapsByKind, row => row.Key == PlanGapKind.NoTemplate);
        Assert.Contains(status.GapsByKind, row => row.Key == PlanGapKind.AttachmentPathMissing);
        Assert.Equal(1, status.ReplacementLinks);
        Assert.Equal(1, status.ExpiredEntitlements);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Tiny mapping took {stopwatch.Elapsed}.");

        var contractUnitId = await db.MigrationPlanUnits.Where(row => row.MapRunId == status.RunId &&
            row.ItemCode == "SERVER-CONTRACT").Select(row => row.PlanUnitId).SingleAsync();
        var contractUnit = await mapping.UnitAsync(contractUnitId, default);
        Assert.NotNull(contractUnit);
        Assert.Equal(UnitOrigin.Migrated, contractUnit.Origin);
        Assert.Equal("WH-A", contractUnit.Warehouse);
        Assert.Equal(LocationKind.Warehouse, contractUnit.CustodyLocationKind);
        Assert.Equal(2, contractUnit.Identifiers.Count);
        Assert.Contains(contractUnit.Identifiers, row => row.Type == IdentifierType.InternalSerial &&
            row.Issuer == PlanIssuerKind.Tenant && row.SourceDataValue == "I");
        Assert.Contains(contractUnit.Identifiers, row => row.Type == IdentifierType.ManufacturerSerial &&
            row.Issuer == PlanIssuerKind.Manufacturer);
        Assert.DoesNotContain(contractUnit.Events, row => row.MovementKind == HistoryMovementKind.IssuedOut);
        Assert.Contains(contractUnit.Events, row => row.MovementKind == HistoryMovementKind.Received);
        Assert.Contains(contractUnit.Log, row => row.Kind == PlanLogKind.EventExcluded);
        Assert.Equal(2, contractUnit.Ownership.Count);
        Assert.All(contractUnit.Ownership, row =>
        {
            Assert.True(row.Pseudonymised);
            Assert.Null(row.Party);
        });
        var entitlement = Assert.Single(contractUnit.Entitlements);
        Assert.Equal("base", entitlement.CoverageFamily);
        Assert.Equal(policyId, entitlement.PolicyId);
        Assert.Equal(new DateOnly(2026, 10, 15), entitlement.EndDate);
        Assert.False(entitlement.Expired);
        Assert.Equal(PlanWarrantyEvidenceSource.ContractLine, entitlement.DateSource);
        Assert.Equal(3, entitlement.Evidence.Count);
        Assert.Single(entitlement.Evidence, row => row.SelectedForEntitlement &&
            row.SourceKind == PlanWarrantyEvidenceSource.ContractLine);
        Assert.Equal((1, EntitlementProvenance.Migrated),
            (contractUnit.Certificate.Version, contractUnit.Certificate.Provenance));

        var placeholderId = await db.MigrationPlanUnits.Where(row => row.MapRunId == status.RunId &&
            row.ItemCode == "PC-PLACEHOLDER").Select(row => row.PlanUnitId).SingleAsync();
        var placeholder = await mapping.UnitAsync(placeholderId, default);
        Assert.Equal("202", Assert.Single(placeholder!.Identifiers).RawValue);
        Assert.Contains("placeholder", placeholder.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PlanWarrantyEvidenceSource.EquipmentCard,
            Assert.Single(placeholder.Entitlements).DateSource);
        Assert.Contains(placeholder.Events, row => row.Provenance == HistoryProvenance.Reconstructed);
        Assert.Single(placeholder.ReplacementLinks);

        var draftGapId = await db.MigrationPlanUnits.Where(row => row.MapRunId == status.RunId &&
            row.ItemCode == "STORAGE-DRAFT").Select(row => row.PlanUnitId).SingleAsync();
        var draft = await mapping.UnitAsync(draftGapId, default);
        Assert.Empty(draft!.Entitlements);
        Assert.Contains(draft.Gaps, row => row.Kind == PlanGapKind.PolicyNotPublished);
        Assert.Equal(ConditionGrades.Defective, draft.ConditionGrade);
        Assert.Equal("inferred", draft.ConditionProvenance);

        var differingId = await db.MigrationPlanUnits.Where(row => row.MapRunId == status.RunId &&
            row.ItemCode == "PC-NOT-APPROVED").Select(row => row.PlanUnitId).SingleAsync();
        var differing = await mapping.UnitAsync(differingId, default);
        Assert.Equal(2, differing!.Ownership.Count);
        Assert.Contains(differing.Ownership, row => row.Tier == PlanOwnershipTier.CurrentCustomer &&
            row.PartyReference == "CARD-NEWER" && row.IsCurrent);
        Assert.Contains(differing.Ownership, row => row.Tier == PlanOwnershipTier.EvidenceCustomer &&
            row.PartyReference == "SHIP-OLDER" && !row.IsCurrent);

        var masterOnlyId = await db.MigrationPlanUnits.Where(row => row.MapRunId == status.RunId &&
            row.ItemCode == "NETWORK-MASTER").Select(row => row.PlanUnitId).SingleAsync();
        var masterOnly = await mapping.UnitAsync(masterOnlyId, default);
        var masterEntitlement = Assert.Single(masterOnly!.Entitlements);
        Assert.Equal(PlanWarrantyEvidenceSource.SerialMaster, masterEntitlement.DateSource);
        Assert.Equal(EntitlementDirection.Inbound, masterEntitlement.Direction);
        Assert.True(masterEntitlement.Expired);

        var firstPage = await mapping.GapsAsync(ids.BatchId, null, null, 1, default);
        Assert.Single(firstPage.Items);
        Assert.NotNull(firstPage.NextCursor);
        Assert.Single((await mapping.GapsAsync(ids.BatchId, null, firstPage.NextCursor, 1, default)).Items);

        var otherTenant = await fixture.NewTenantAsync("mapother");
        using var otherScope = fixture.ScopeFor(otherTenant);
        var otherDb = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(0, await otherDb.MigrationPlanUnits.CountAsync());
        Assert.Equal(0, await otherDb.MigrationPlanEvents.CountAsync());
        Assert.Equal(0, await otherDb.MigrationPlanEntitlements.CountAsync());
    }

    [DatabaseFact]
    public async Task Mapping_refuses_missing_incomplete_and_profile_blocked_reconstruction()
    {
        var tenant = await fixture.NewTenantAsync("maprefuse");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var sourceId = Guid.CreateVersion7();
        var batchId = Guid.CreateVersion7();
        db.MigrationSources.Add(Source(tenant, sourceId));
        db.MigrationBatches.Add(Batch(tenant, sourceId, batchId));
        await db.SaveChangesAsync();
        var mapping = scope.ServiceProvider.GetRequiredService<MappingService>();
        var missing = await Assert.ThrowsAsync<MappingRuleException>(() => mapping.EnqueueAsync(batchId, default));
        Assert.Equal("migration_mapping_reconstruction_missing", missing.Rule);
        var validation = scope.ServiceProvider.GetRequiredService<ValidationService>();
        var validationMissing = await Assert.ThrowsAsync<ValidationRuleException>(() =>
            validation.EnqueueAsync(batchId, true, default));
        Assert.Equal("migration_validation_map_missing", validationMissing.Rule);

        var profileId = Guid.CreateVersion7();
        var canonicalId = Guid.CreateVersion7();
        var reconstructionId = Guid.CreateVersion7();
        db.MigrationProfileRuns.Add(new() { TenantId = tenant, RunId = profileId, BatchId = batchId,
            Status = MigrationProfileRunStatus.Completed, StartedAt = Extracted, FinishedAt = Extracted });
        db.MigrationCanonRuns.Add(new() { TenantId = tenant, RunId = canonicalId, BatchId = batchId,
            ProfileRunId = profileId, RequestedAt = Extracted, Status = CanonicalisationRunStatus.Completed });
        db.MigrationReconstructionRuns.Add(new() { TenantId = tenant, RunId = reconstructionId,
            CanonicalisationRunId = canonicalId, BatchId = batchId, RequestedAt = Extracted,
            Status = ReconstructionRunStatus.Running });
        await db.SaveChangesAsync();
        var incomplete = await Assert.ThrowsAsync<MappingRuleException>(() => mapping.EnqueueAsync(batchId, default));
        Assert.Equal("migration_mapping_reconstruction_not_completed", incomplete.Rule);
        (await db.MigrationReconstructionRuns.SingleAsync()).Status = ReconstructionRunStatus.Completed;
        (await db.MigrationProfileRuns.SingleAsync()).Status = MigrationProfileRunStatus.Blocked;
        await db.SaveChangesAsync();
        var blocked = await Assert.ThrowsAsync<MappingRuleException>(() => mapping.EnqueueAsync(batchId, default));
        Assert.Equal("migration_mapping_profile_blocked", blocked.Rule);
        Assert.Equal(0, await mapping.EnqueueEligibleAsync(default));
        Assert.Equal(0, await db.MigrationMapRuns.CountAsync());

        var completedMap = new MigrationMapRun
        {
            TenantId = tenant, RunId = Guid.CreateVersion7(), ReconstructionRunId = reconstructionId,
            BatchId = batchId, CutoverDate = new(2026, 9, 1), RequestedAt = Extracted,
            FinishedAt = Extracted, Status = MappingRunStatus.Completed,
        };
        var newerMap = new MigrationMapRun
        {
            TenantId = tenant, RunId = Guid.CreateVersion7(), ReconstructionRunId = reconstructionId,
            BatchId = batchId, CutoverDate = new(2026, 9, 1), RequestedAt = Extracted.AddMinutes(1),
            Status = MappingRunStatus.Running,
        };
        db.MigrationMapRuns.AddRange(completedMap, newerMap);
        await db.SaveChangesAsync();
        var superseded = await Assert.ThrowsAsync<ValidationRuleException>(() =>
            validation.EnqueueAsync(batchId, true, default));
        Assert.Equal("migration_validation_map_superseded", superseded.Rule);
        newerMap.Status = MappingRunStatus.Failed;
        await db.SaveChangesAsync();
        var failed = await Assert.ThrowsAsync<ValidationRuleException>(() =>
            validation.EnqueueAsync(batchId, true, default));
        Assert.Equal("migration_validation_map_failed", failed.Rule);
    }

    [DatabaseFact]
    public async Task Validation_records_checks_signatures_re_runs_samples_and_rehearsal_signoff()
    {
        var tenant = await fixture.NewTenantAsync("validation");
        var policyId = await PublishedPolicyAsync(tenant);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var ids = SeedPipeline(db, tenant, policyId);
        await db.SaveChangesAsync();
        var mappingSettings = scope.ServiceProvider.GetRequiredService<MappingSettings>();
        var defaults = await mappingSettings.GetAsync(default);
        await mappingSettings.SetAsync(new(true, new DateOnly(2026, 9, 1), defaults.ConditionMap), default);
        var mapping = scope.ServiceProvider.GetRequiredService<MappingService>();
        await mapping.EnqueueAsync(ids.BatchId, default);
        Assert.Equal(1, await mapping.RunPendingAsync(default));

        var profileRunId = await db.MigrationCanonRuns.Where(x => x.BatchId == ids.BatchId)
            .Select(x => x.ProfileRunId).SingleAsync();
        db.MigrationProfileResults.Add(new()
        {
            TenantId = tenant, RunId = profileRunId,
            ClassCode = MigrationProfileClasses.NegativeQuantity.Code,
            Outcome = MigrationProfileOutcome.StopLoad, RowCount = 1, SamplesJson = "[]",
        });
        await db.SaveChangesAsync();

        var validation = scope.ServiceProvider.GetRequiredService<ValidationService>();
        var validationSettings = scope.ServiceProvider.GetRequiredService<ValidationSettings>();
        var validationDefaults = await validationSettings.GetAsync(default);
        Assert.Equal(0.1m, validationDefaults.HistoricalTolerancePercent);
        Assert.Equal(200, validationDefaults.SampleSize);
        Assert.Equal([1, 3, 5], validationDefaults.AgeBandYears);
        await validationSettings.SetAsync(new(0.25m, 100, [1, 2, 5], validationDefaults.Version), default);
        var queued = await validation.EnqueueAsync(ids.BatchId, true, default);
        var duplicate = await Assert.ThrowsAsync<ValidationRuleException>(() =>
            validation.EnqueueAsync(ids.BatchId, true, default));
        Assert.Equal("migration_validation_already_running", duplicate.Rule);
        var stopwatch = Stopwatch.StartNew();
        Assert.Equal(1, await validation.RunPendingAsync(default));
        stopwatch.Stop();
        var view = await validation.GetAsync(ids.BatchId, default);
        Assert.NotNull(view);
        Assert.Equal(14, view.ChecksTotal);
        Assert.Equal(ValidationTargetKind.Plan, view.TargetKind);
        foreach (var code in new[]
                 {
                     "unit_master", "active_cover", "open_service_calls", "stock_by_item_warehouse",
                     "serial_in_multiple_warehouses", "historical_events", "status_histogram",
                     "event_per_unit", "one_current_row", "signed_quantity",
                 })
            Assert.Equal(ValidationResult.Pass, view.Checks.Single(x => x.Code == code).Result);
        Assert.Contains(view.Checks, x => x.Code == "negative_quantity" &&
            x.Result == ValidationResult.HardFail);
        Assert.Contains(view.Checks, x => x.Code == "cover_gap_report" && x.Result == ValidationResult.Report);
        Assert.Contains(view.Checks, x => x.Code == "attachments_by_hash" && x.Result == ValidationResult.Report);
        Assert.Contains(view.Checks, x => x.Code == "historical_events" && x.LimitKind == "< 0.25%");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), $"Tiny validation took {stopwatch.Elapsed}.");

        var hard = view.Checks.Single(x => x.Code == "negative_quantity");
        var hardExplain = await Assert.ThrowsAsync<ValidationRuleException>(() =>
            validation.ExplainAsync(hard.CheckId, "Cannot waive this.", "preparer", default));
        Assert.Equal("migration_validation_hard_fail_not_explainable", hardExplain.Rule);

        var calls = view.Checks.Single(x => x.Code == "open_service_calls");
        await validation.SignAsync(calls.CheckId, ValidationSignatureRole.Preparer, "person-a",
            [YojanaRole.MigrationLead], default);
        var same = await Assert.ThrowsAsync<ValidationRuleException>(() => validation.SignAsync(calls.CheckId,
            ValidationSignatureRole.Reviewer, "person-a", [YojanaRole.TenantAdministrator], default));
        Assert.Equal("migration_validation_distinct_signers_required", same.Rule);
        await validation.SignAsync(calls.CheckId, ValidationSignatureRole.Reviewer, "person-b",
            [YojanaRole.TenantAdministrator], default);

        var unchanged = view.Checks.Single(x => x.Code == "attachments_by_hash");
        await validation.SignAsync(unchanged.CheckId, ValidationSignatureRole.Preparer, "person-a",
            [YojanaRole.MigrationLead], default);
        await validation.SignAsync(unchanged.CheckId, ValidationSignatureRole.Reviewer, "person-b",
            [YojanaRole.TenantAdministrator], default);

        var firstSample = await validation.SampleAsync(queued.RunId, null, 200, default);
        foreach (var sample in firstSample.Items)
            await validation.TraceAsync(sample.SampleId, "traced", "Trail checked.", "person-a", default);
        Assert.Equal(ValidationResult.Pass,
            (await validation.CheckAsync(view.Checks.Single(x => x.Code == "stratified_sample").CheckId,
                default))!.Check.Result);
        db.Set<ServiceCallStagingRow>().Add(Stage(new ServiceCallStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = "call:1", RowHash = [1],
            CallId = 1, ItemCode = "SERVER-CONTRACT", InternalSerial = "SER-101", Status = 1,
            OpenedAt = Extracted,
        }, tenant, ids.BatchId, "call:1"));
        var quantity = await db.Set<SerialQuantityStagingRow>().SingleAsync(x =>
            x.BatchId == ids.BatchId && x.SystemNumber == 101);
        quantity.Quantity = 2;
        var entitlement = await db.MigrationPlanEntitlements.FirstAsync(x => x.MapRunId == view.MapRunId &&
            x.EndDate >= new DateOnly(2026, 9, 1));
        db.MigrationPlanEntitlements.Remove(entitlement);
        var sourceEvent = await db.MigrationPlanEvents.FirstAsync(x => x.MapRunId == view.MapRunId &&
            x.Provenance == HistoryProvenance.Source);
        db.MigrationPlanEvents.Remove(sourceEvent);
        db.MigrationPlanUnits.Add(new()
        {
            TenantId = tenant, PlanUnitId = Guid.CreateVersion7(), MapRunId = view.MapRunId,
            CandidateId = Guid.CreateVersion7(), ItemCode = "SERVER-EXTRA", Origin = UnitOrigin.Migrated,
            HistoryDepth = Yojana.Integration.LegacyMigration.Reconstruction.HistoryDepth.Full,
            LocationState = "", ConditionGrade = ConditionGrades.Defective,
            ConditionProvenance = "inferred",
        });
        db.MigrationProfileResults.Add(new()
        {
            TenantId = tenant, RunId = profileRunId,
            ClassCode = MigrationProfileClasses.SerialInMultipleWarehouses.Code,
            Outcome = MigrationProfileOutcome.StopLoad, RowCount = 1, SamplesJson = "[]",
        });
        await db.SaveChangesAsync();
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        var second = await validation.EnqueueAsync(ids.BatchId, true, default);
        Assert.Equal(1, await validation.RunPendingAsync(default));
        var secondSample = await validation.SampleAsync(second.RunId, null, 200, default);
        Assert.Equal(firstSample.Items.Select(x => x.Serial).Order(), secondSample.Items.Select(x => x.Serial).Order());
        var secondView = await validation.GetAsync(ids.BatchId, default);
        Assert.NotNull(secondView);
        var changedCalls = secondView.Checks.Single(x => x.Code == "open_service_calls");
        Assert.Equal(ValidationResult.Explained, changedCalls.Result);
        Assert.Empty(changedCalls.Signatures);
        Assert.Equal(2, secondView.Checks.Single(x => x.Code == "attachments_by_hash").Signatures.Count);
        foreach (var code in new[]
                 {
                     "unit_master", "active_cover", "stock_by_item_warehouse", "historical_events", "status_histogram",
                     "event_per_unit", "one_current_row", "signed_quantity",
                 })
            Assert.Equal(ValidationResult.Fail, secondView.Checks.Single(x => x.Code == code).Result);
        foreach (var code in new[]
                 {
                     "serial_in_multiple_warehouses", "negative_quantity",
                 })
            Assert.Equal(ValidationResult.HardFail, secondView.Checks.Single(x => x.Code == code).Result);
        Assert.Equal(ValidationResult.Fail,
            secondView.Checks.Single(x => x.Code == "stratified_sample").Result);
        var cover = secondView.Checks.Single(x => x.Code == "active_cover");
        Assert.Equal(ValidationResult.Explained, (await validation.ExplainAsync(cover.CheckId,
            "The unresolved template approvals are tracked for this rehearsal.", "person-a", default)).Result);
        var callHistory = await validation.CheckAsync(calls.CheckId, default);
        Assert.NotNull(callHistory);
        Assert.All(callHistory.SignatureHistory, x => Assert.True(x.Superseded));

        var rehearsal = await validation.CreateSignoffAsync(second.RunId, "person-b", default);
        Assert.True(rehearsal.Rehearsal);
        Assert.Equal(14, rehearsal.Lines.Count);
        Assert.Contains("REHEARSAL", System.Text.Encoding.UTF8.GetString(
            await validation.ExportCsvAsync(rehearsal.RecordId, default)));

        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        var finalRun = await validation.EnqueueAsync(ids.BatchId, false, default);
        Assert.Equal(1, await validation.RunPendingAsync(default));
        var blocked = await Assert.ThrowsAsync<ValidationRuleException>(() =>
            validation.CreateSignoffAsync(finalRun.RunId, "person-b", default));
        Assert.Equal("migration_signoff_hard_fail", blocked.Rule);

        var otherTenant = await fixture.NewTenantAsync("valother");
        using var otherScope = fixture.ScopeFor(otherTenant);
        var otherDb = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Empty(await otherDb.MigrationValidationRuns.ToListAsync());
        Assert.Empty(await otherDb.MigrationValidationChecks.ToListAsync());
    }

    internal static (Guid BatchId, Guid ReconstructionId) SeedPipeline(IntegrationDbContext db, TenantId tenant,
        Guid policyId)
    {
        var sourceId = Guid.CreateVersion7();
        var batchId = Guid.CreateVersion7();
        var profileId = Guid.CreateVersion7();
        var canonicalId = Guid.CreateVersion7();
        var reconstructionId = Guid.CreateVersion7();
        db.MigrationSources.Add(Source(tenant, sourceId));
        db.MigrationBatches.Add(Batch(tenant, sourceId, batchId));
        db.MigrationProfileRuns.Add(new() { TenantId = tenant, RunId = profileId, BatchId = batchId,
            Status = MigrationProfileRunStatus.Completed, StartedAt = Extracted, FinishedAt = Extracted });
        db.MigrationCanonRuns.Add(new() { TenantId = tenant, RunId = canonicalId, BatchId = batchId,
            ProfileRunId = profileId, RequestedAt = Extracted, StartedAt = Extracted, FinishedAt = Extracted,
            Status = CanonicalisationRunStatus.Completed });
        db.MigrationReconstructionRuns.Add(new() { TenantId = tenant, RunId = reconstructionId,
            CanonicalisationRunId = canonicalId, BatchId = batchId, RequestedAt = Extracted,
            StartedAt = Extracted, FinishedAt = Extracted, Status = ReconstructionRunStatus.Completed });

        var contract = Candidate(db, tenant, batchId, canonicalId, "SERVER-CONTRACT", "SER-101", 101,
            "MFG-101", warrantyStart: new(2026, 1, 1), warrantyEnd: new(2027, 1, 1), dataSource: "I");
        var placeholder = Candidate(db, tenant, batchId, canonicalId, "PC-PLACEHOLDER", null, 202,
            null, warrantyStart: new(2026, 2, 1), warrantyEnd: new(2026, 12, 1));
        var draft = Candidate(db, tenant, batchId, canonicalId, "STORAGE-DRAFT", "SER-303", 303,
            null, warrantyStart: new(2026, 3, 1), warrantyEnd: new(2027, 3, 1));
        var excluded = Candidate(db, tenant, batchId, canonicalId, "NETWORK-EXCLUDED", "SER-404", 404,
            null, disposition: CandidateDisposition.Excluded);
        var waiting = Candidate(db, tenant, batchId, canonicalId, "SERVER-WAITING", "SER-505", 505, null);
        var notApproved = Candidate(db, tenant, batchId, canonicalId, "PC-NOT-APPROVED", "SER-606", 606, null);
        var noTemplate = Candidate(db, tenant, batchId, canonicalId, "STORAGE-NO-TEMPLATE", "SER-707", 707, null);
        var masterOnly = Candidate(db, tenant, batchId, canonicalId, "NETWORK-MASTER", "SER-808", 808,
            "MFG-808", warrantyStart: new(2025, 8, 1), warrantyEnd: new(2026, 8, 1));

        db.Set<SerialQuantityStagingRow>().Add(Stage(new SerialQuantityStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = "quantity:101", RowHash = [1],
            ItemCode = contract.ItemCode, SystemNumber = 101, WarehouseCode = "WH-A", Quantity = 1,
        }, tenant, batchId, "quantity:101"));
        var cardContract = Card(db, tenant, batchId, canonicalId, contract, 1001, "END-CUSTOMER",
            "DIRECT-CUSTOMER", personalDataRemoved: true, new(2026, 2, 1), new(2026, 11, 1),
            replacesCardId: 9999);
        _ = Card(db, tenant, batchId, canonicalId, placeholder, 1002, "CARD-CUSTOMER", null,
            personalDataRemoved: false, new(2026, 2, 1), new(2026, 12, 1));
        _ = Card(db, tenant, batchId, canonicalId, draft, 1003, "DRAFT-CUSTOMER", null,
            personalDataRemoved: false, new(2026, 3, 1), new(2027, 3, 1), replacesCardId: 1002,
            status: "defective");
        _ = Card(db, tenant, batchId, canonicalId, notApproved, 1004, "CARD-NEWER", null,
            personalDataRemoved: false, null, null);
        db.Set<CardHistoryStagingRow>().Add(Stage(new CardHistoryStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = "card-history:1001", RowHash = [1],
            CardId = 1001, HistorySequence = 1, CustomerCode = "END-CUSTOMER",
            ChangedAt = new(2026, 8, 1),
        }, tenant, batchId, "card-history:1001"));
        db.Set<CardHistoryStagingRow>().Add(Stage(new CardHistoryStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = "card-history:1004", RowHash = [1],
            CardId = 1004, HistorySequence = 1, CustomerCode = "CARD-NEWER",
            ChangedAt = new(2026, 8, 15),
        }, tenant, batchId, "card-history:1004"));

        var received = History(db, tenant, reconstructionId, contract, 1, HistoryMovementKind.Received,
            new(2026, 1, 1), "event:received", "WH-A");
        var shipped = History(db, tenant, reconstructionId, contract, 2, HistoryMovementKind.Shipped,
            new(2026, 7, 1), "event:shipped", party: "SHIP-CUSTOMER");
        var issued = History(db, tenant, reconstructionId, contract, 3, HistoryMovementKind.IssuedOut,
            new(2026, 7, 2), "event:issued");
        _ = History(db, tenant, reconstructionId, placeholder, 1, HistoryMovementKind.OpeningBalance,
            new(2026, 1, 1), "opening:202", provenance: HistoryProvenance.Reconstructed);
        var differingShipment = History(db, tenant, reconstructionId, notApproved, 1,
            HistoryMovementKind.Shipped, new(2026, 7, 1), "event:not-approved-shipped",
            party: "SHIP-OLDER");
        _ = History(db, tenant, reconstructionId, draft, 1, HistoryMovementKind.OpeningBalance,
            new(2026, 3, 1), "opening:303", provenance: HistoryProvenance.Reconstructed);
        _ = History(db, tenant, reconstructionId, noTemplate, 1, HistoryMovementKind.OpeningBalance,
            new(2026, 3, 1), "opening:707", provenance: HistoryProvenance.Reconstructed);
        _ = History(db, tenant, reconstructionId, masterOnly, 1, HistoryMovementKind.OpeningBalance,
            new(2025, 8, 1), "opening:808", provenance: HistoryProvenance.Reconstructed);
        db.Set<TransactionLogHeaderStagingRow>().Add(Stage(new TransactionLogHeaderStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = "transaction-header:1",
            RowHash = [1], LogEntry = 1, ItemCode = contract.ItemCode, StockEffect = 1,
            DocumentDate = new(2026, 1, 1),
        }, tenant, batchId, "transaction-header:1"));
        db.Set<TransactionLogLineStagingRow>().Add(Stage(new TransactionLogLineStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = "transaction-line:1",
            RowHash = [1], LogEntry = 1, ItemCode = contract.ItemCode, SystemNumber = 101, Quantity = 1,
        }, tenant, batchId, "transaction-line:1"));
        db.MigrationOwnershipEpisodes.Add(new()
        {
            TenantId = tenant, OwnershipEpisodeId = Guid.CreateVersion7(),
            ReconstructionRunId = reconstructionId, CandidateId = contract.CandidateId,
            PartyCode = "SHIP-CUSTOMER", FromHistoryEventId = shipped.HistoryEventId, OpenReason = "shipment",
        });
        db.MigrationOwnershipEpisodes.Add(new()
        {
            TenantId = tenant, OwnershipEpisodeId = Guid.CreateVersion7(),
            ReconstructionRunId = reconstructionId, CandidateId = notApproved.CandidateId,
            PartyCode = "SHIP-OLDER", FromHistoryEventId = differingShipment.HistoryEventId,
            OpenReason = "shipment",
        });
        db.MigrationReconstructionLogs.Add(new()
        {
            TenantId = tenant, LogId = Guid.CreateVersion7(), ReconstructionRunId = reconstructionId,
            CandidateId = contract.CandidateId, SourceKey = "event:issued", ReasonCode = ReconstructionLogReason.IssuedOut,
            Note = "Flagged issue.",
        });
        var exclusionReview = Review(db, tenant, batchId, canonicalId, contract,
            MigrationReviewKind.HistoryNeedsReview, MigrationReviewStatus.Decided);
        var exclusionDecision = Guid.CreateVersion7();
        exclusionReview.AppliedDecisionId = exclusionDecision;
        db.MigrationReviewDecisions.Add(new()
        {
            TenantId = tenant, DecisionId = exclusionDecision, ReviewItemId = exclusionReview.ReviewItemId,
            Fingerprint = exclusionReview.Fingerprint, CorrelationKey = exclusionReview.CorrelationKey,
            Decision = MigrationReviewDecisionKind.ExcludeFromHistory, Reason = "Exclude only the flagged event.",
            DecidedBy = "fictional-migration-lead", DecidedAt = Extracted,
        });
        _ = Review(db, tenant, batchId, canonicalId, waiting, MigrationReviewKind.OrphanEquipmentCard,
            MigrationReviewStatus.Waiting);

        Contract(db, tenant, batchId, canonicalId, contract, 501, "Approved coverage",
            new(2026, 1, 1), new(2027, 1, 1), new(2026, 10, 15));
        Contract(db, tenant, batchId, canonicalId, draft, 502, "Draft policy coverage",
            new(2026, 3, 1), new(2027, 3, 1), null);
        Contract(db, tenant, batchId, canonicalId, notApproved, 503, "Awaiting approval",
            new(2026, 3, 1), new(2027, 3, 1), null, status: "I");
        Contract(db, tenant, batchId, canonicalId, noTemplate, 504, null,
            new(2026, 3, 1), new(2027, 3, 1), null, status: "I");
        Contract(db, tenant, batchId, canonicalId, masterOnly, 505, "Approved coverage",
            new(2026, 3, 1), new(2027, 3, 1), null, serviceType: "S", status: "I");
        var approvedMapping = Mapping(db, tenant, batchId, policyId, "Approved coverage", "base", approved: true);
        _ = approvedMapping;
        _ = Mapping(db, tenant, batchId, Guid.CreateVersion7(), "Draft policy coverage", "base", approved: true);
        _ = Mapping(db, tenant, batchId, policyId, "Awaiting approval", "base", approved: false);

        db.Set<AttachmentStagingRow>().Add(Stage(new AttachmentStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = "attachment:1|0", RowHash = [1],
            AttachmentEntry = 1, LineNumber = 0, FileName = "service-evidence", FileExtension = "pdf",
            FileSize = 2048,
        }, tenant, batchId, "attachment:1|0"));
        _ = masterOnly;
        return (batchId, reconstructionId);
    }

    private static MigrationUnitCandidate Candidate(IntegrationDbContext db, TenantId tenant, Guid batchId,
        Guid canonicalId, string item, string? serial, int systemNumber, string? manufacturerSerial,
        DateTime? warrantyStart = null, DateTime? warrantyEnd = null, string? dataSource = null,
        string disposition = CandidateDisposition.Active)
    {
        var key = $"serial:{item}:{systemNumber}";
        var candidate = new MigrationUnitCandidate
        {
            TenantId = tenant, CandidateId = Guid.CreateVersion7(), RunId = canonicalId,
            PrimarySourceKey = key, ItemCode = item, ItemName = item.Replace('-', ' '), Family = item.Split('-')[0],
            CanonicalSerial = serial ?? $"SYSTEM-{systemNumber}", StoredSerial = serial,
            TrimmedSerial = serial ?? $"SYSTEM-{systemNumber}",
            FamilyNormalisedSerial = serial ?? systemNumber.ToString(), MatchRule = CanonicalMatchRule.Exact,
            Provenance = CanonicalProvenance.Source, Disposition = disposition,
            HistoryDepth = Yojana.Integration.LegacyMigration.Reconstruction.HistoryDepth.Full,
        };
        db.MigrationUnitCandidates.Add(candidate);
        db.Set<SerialMasterStagingRow>().Add(Stage(new SerialMasterStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = key, RowHash = [1],
            ItemCode = item, SystemNumber = systemNumber, SerialEntry = systemNumber,
            SerialNumber = serial, ManufacturerSerial = manufacturerSerial, WarrantyStart = warrantyStart,
            WarrantyEnd = warrantyEnd, DataSource = dataSource,
        }, tenant, batchId, key));
        return candidate;
    }

    private static EquipmentCardStagingRow Card(IntegrationDbContext db, TenantId tenant, Guid batchId,
        Guid canonicalId, MigrationUnitCandidate candidate, int cardId, string customer, string? direct,
        bool personalDataRemoved, DateTime? start, DateTime? end, int? replacesCardId = null,
        string? status = null)
    {
        var key = $"card:{cardId}";
        var card = Stage(new EquipmentCardStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = key, RowHash = [1],
            CardId = cardId, ItemCode = candidate.ItemCode, InternalSerial = candidate.StoredSerial,
            CustomerCode = customer, DirectCustomerCode = direct,
            PersonalDataStatus = personalDataRemoved ? "D" : null, WarrantyStart = start, WarrantyEnd = end,
            ReplacesCardId = replacesCardId, Status = status,
        }, tenant, batchId, key);
        db.Set<EquipmentCardStagingRow>().Add(card);
        db.MigrationUnitCandidateSources.Add(new()
        {
            TenantId = tenant, CandidateSourceId = Guid.CreateVersion7(), RunId = canonicalId,
            CandidateId = candidate.CandidateId, MatchedSerialSourceKey = candidate.PrimarySourceKey,
            SourceKind = CandidateSourceKind.EquipmentCard, SourceKey = key, Role = CandidateSourceRole.Kept,
        });
        db.MigrationEntitlementCandidates.Add(new()
        {
            TenantId = tenant, EntitlementCandidateId = Guid.CreateVersion7(), RunId = canonicalId,
            CandidateId = candidate.CandidateId, SerialMasterSourceKey = candidate.PrimarySourceKey,
            SourceKind = CandidateSourceKind.EquipmentCard, SourceKey = key, StartDate = start, EndDate = end,
            MatchRule = CanonicalMatchRule.Exact, Provenance = CanonicalProvenance.Source,
        });
        return card;
    }

    private static void Contract(IntegrationDbContext db, TenantId tenant, Guid batchId, Guid canonicalId,
        MigrationUnitCandidate candidate, int contractId, string? template, DateTime start,
        DateTime end, DateTime? termination, string serviceType = "W", string status = "A")
    {
        var contractKey = $"contract:{contractId}";
        var lineKey = $"contract-line:{contractId}:0";
        db.Set<ServiceContractStagingRow>().Add(Stage(new ServiceContractStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = contractKey, RowHash = [1],
            ContractId = contractId, CustomerCode = "FIC-CUSTOMER", StartDate = start, EndDate = end,
            TerminationDate = termination, Status = status, TemplateName = template, ServiceType = serviceType,
        }, tenant, batchId, contractKey));
        db.Set<ContractLineStagingRow>().Add(Stage(new ContractLineStagingRow
        {
            SourceSystem = "fixture-source", SourceTable = "fixture", SourcePk = lineKey, RowHash = [1],
            ContractId = contractId, LineNumber = 0, ItemCode = candidate.ItemCode,
            InternalSerial = candidate.StoredSerial, StartDate = start, EndDate = end,
            TerminationDate = termination,
        }, tenant, batchId, lineKey));
        foreach (var (kind, key) in new[]
                 {
                     (CandidateSourceKind.Contract, contractKey),
                     (CandidateSourceKind.ContractLine, lineKey),
                 })
            db.MigrationUnitCandidateSources.Add(new()
            {
                TenantId = tenant, CandidateSourceId = Guid.CreateVersion7(), RunId = canonicalId,
                CandidateId = candidate.CandidateId, MatchedSerialSourceKey = candidate.PrimarySourceKey,
                SourceKind = kind, SourceKey = key, Role = CandidateSourceRole.Kept,
            });
        db.MigrationEntitlementCandidates.Add(new()
        {
            TenantId = tenant, EntitlementCandidateId = Guid.CreateVersion7(), RunId = canonicalId,
            CandidateId = candidate.CandidateId, SerialMasterSourceKey = candidate.PrimarySourceKey,
            SourceKind = CandidateSourceKind.ContractLine, SourceKey = lineKey, StartDate = start, EndDate = end,
            MatchRule = CanonicalMatchRule.Exact, Provenance = CanonicalProvenance.Source,
        });
    }

    private static TemplateMapping Mapping(IntegrationDbContext db, TenantId tenant, Guid batchId,
        Guid policyId, string template, string family, bool approved)
    {
        var mappingId = Guid.CreateVersion7();
        var mapping = new TemplateMapping
        {
            TenantId = tenant, TemplateMappingId = mappingId, TemplateKey = template.ToLowerInvariant(),
            SourceSystem = "fixture-source", TemplateSourceKey = $"template:{template}", TemplateName = template,
            ExtractionBatchId = batchId, ExtractedAt = Extracted, SourceFingerprint = new('a', 64),
            SourceTermsJson = "{}", WordingToReviewJson = "[]", PatternKey = new('b', 64),
            PatternLabel = template, DecompositionFingerprint = new('c', 64),
            State = approved ? TemplateMappingState.Approved : TemplateMappingState.Draft,
            ProposedAt = Extracted, ApprovedAt = approved ? Extracted : null,
            ApprovedBy = approved ? "fictional-migration-lead" : null,
        };
        db.TemplateMappings.Add(mapping);
        db.TemplateMappingBlocks.Add(new()
        {
            TenantId = tenant, TemplateMappingBlockId = Guid.CreateVersion7(), TemplateMappingId = mappingId,
            Position = 0, FamilyCode = family, FamilyName = family, Stacking = false,
            Resolution = TemplateBlockResolution.ExistingPolicy, PolicyId = policyId, PolicyCode = "BASE-12",
            PolicyVersion = 1, PolicyName = "Fictional hardware base cover", ProposedTermsJson = "{}",
        });
        return mapping;
    }

    private static MigrationHistoryEvent History(IntegrationDbContext db, TenantId tenant,
        Guid reconstructionId, MigrationUnitCandidate candidate, int sequence, string kind, DateTime date,
        string sourceKey, string? warehouse = null, string? party = null,
        string provenance = HistoryProvenance.Source)
    {
        Guid? eventCandidateId = null;
        if (provenance == HistoryProvenance.Source)
        {
            eventCandidateId = Guid.CreateVersion7();
            db.MigrationEventCandidates.Add(new()
            {
                TenantId = tenant, EventCandidateId = eventCandidateId.Value, RunId = candidate.RunId,
                CandidateId = candidate.CandidateId, SerialMasterSourceKey = candidate.PrimarySourceKey,
                TransactionSourceKey = sourceKey, MatchRule = CanonicalMatchRule.Exact,
                Provenance = CanonicalProvenance.Source,
            });
        }
        var row = new MigrationHistoryEvent
        {
            TenantId = tenant, HistoryEventId = Guid.CreateVersion7(), ReconstructionRunId = reconstructionId,
            CandidateId = candidate.CandidateId, Sequence = sequence, MovementKind = kind,
            EffectiveDate = date, SourceDocumentTypeLabel = kind.Replace('_', ' '), Warehouse = warehouse,
            PartyCode = party, Quantity = kind == HistoryMovementKind.Shipped ? -1 : 1,
            Provenance = provenance, EventCandidateId = eventCandidateId,
        };
        db.MigrationHistoryEvents.Add(row);
        return row;
    }

    private static MigrationReviewItem Review(IntegrationDbContext db, TenantId tenant, Guid batchId,
        Guid canonicalId, MigrationUnitCandidate candidate, string kind, string status)
    {
        var id = Guid.CreateVersion7();
        var row = new MigrationReviewItem
        {
            TenantId = tenant, ReviewItemId = id, RunId = canonicalId, BatchId = batchId,
            SourceSystem = "fixture-source", Kind = kind, Status = status,
            Fingerprint = new string('d', 63) + id.ToString("N")[0],
            CorrelationKey = new string('e', 63) + id.ToString("N")[0], CanonicalKey = candidate.CanonicalSerial,
            ItemCode = candidate.ItemCode, StoredSerial = candidate.StoredSerial, RuleTried = "fixture",
            SourceKeysJson = "[]", CandidateSourceKeysJson = JsonSerializer.Serialize(new[]
                { candidate.PrimarySourceKey }), CandidateSummary = "Fixture review", CreatedAt = Extracted,
        };
        db.MigrationReviewItems.Add(row);
        return row;
    }

    private static MigrationSource Source(TenantId tenant, Guid sourceId) => new()
    {
        TenantId = tenant, ConnectionId = sourceId, DisplayLabel = "Fictional migration source",
        SourceSystem = "fixture-source", Runtime = MigrationSourceRuntime.SqlServer,
        Route = MigrationSourceRoute.Separate, DatabaseName = "fixture", SchemaName = "dbo",
        CommandTimeoutSeconds = 30, LicencePosition = MigrationLicencePosition.Confirmed,
        CreatedAt = Extracted, UpdatedAt = Extracted,
    };

    private static MigrationBatch Batch(TenantId tenant, Guid sourceId, Guid batchId) => new()
    {
        TenantId = tenant, BatchId = batchId, ConnectionId = sourceId, SourceSystem = "fixture-source",
        Kind = "full", RequestedBy = "fictional-migration-lead", RequestedAt = Extracted,
        StartedAt = Extracted, FinishedAt = Extracted, Status = MigrationBatchStatus.Succeeded,
    };

    private static T Stage<T>(T row, TenantId tenant, Guid batchId, string key) where T : StagingRow
    {
        row.TenantId = tenant;
        row.BatchId = batchId;
        row.FirstBatchId = batchId;
        row.SourceSystem = "fixture-source";
        row.SourceTable = "fixture";
        row.SourcePk = key;
        row.ExtractedAt = Extracted;
        row.RowHash = [1, 2, 3];
        return row;
    }

    internal async Task<Guid> PublishedPolicyAsync(TenantId tenant)
    {
        var id = Guid.CreateVersion7();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.CoverageFamilies.Add(new CoverageFamily
        {
            TenantId = tenant, FamilyCode = "base", Name = "Base warranty", Stacking = false,
            AccountingTag = AssuranceOrService.Assurance, CreatedAt = Extracted,
            LastModifiedAt = Extracted, LastModifiedBy = "test",
        });
        db.PolicyVersions.Add(new WarrantyPolicyVersion
        {
            TenantId = tenant, PolicyVersionId = id, PolicyCode = "BASE-12", Version = 1,
            Name = "Fictional hardware base cover", CoverageFamily = "base",
            EntitlementType = EntitlementType.Manufacturer, AppliesToContractsFrom = new(2026, 1, 1),
            Status = PolicyVersionStatus.Published, RuleVersion = "v1.d1", DurationValue = 12,
            DurationUnit = DurationRule.Months, EndOfMonthRule = DurationRule.Clamp,
            DurationBasis = DurationBasis.Time, StartTrigger = StartTrigger.Deliver,
            Coverage = JsonSerializer.Serialize(new CoverageTerms(100, 100, 100, "next_business_day", 24),
                LedgerJson.Options), Transferable = Transferability.Yes, SettlementFormula = "full",
            AssuranceOrService = AssuranceOrService.Assurance,
            ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc, CreatedAt = Extracted, CreatedBy = "test",
            LastModifiedAt = Extracted, LastModifiedBy = "test", PublishedAt = Extracted,
            PublishedBy = "test",
        });
        await db.SaveChangesAsync();
        return id;
    }
}


