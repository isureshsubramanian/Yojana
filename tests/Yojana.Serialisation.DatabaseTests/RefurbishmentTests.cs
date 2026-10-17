using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Refurbishments;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T10: refurbishment identity, work, cost, RMA and whole-unit consistency.</summary>
[Collection("serialisation host")]
public sealed class RefurbishmentTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("70000000-0000-0000-0000-000000000010");
    private static readonly Guid Warehouse = Guid.Parse("70000000-0000-0000-0000-000000000011");
    private static readonly Guid Repair = Guid.Parse("70000000-0000-0000-0000-000000000012");
    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
    private InMemoryWarrantyPorts Warranties => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Same_unit_same_serial_releases_with_owner_custody_identifiers_entitlements_and_ledger_consistent()
    {
        var setup = await SetUpUnitAsync("refurb-a", "class-a");
        await SetPolicyAsync(setup.Tenant, RefurbishmentIdentity.SameUnitSameSerial, 3);
        var entitlementId = await CreateEntitlementAsync(setup, "A");

        var released = await OpenTestAndReleaseAsync(setup, At.AddHours(1), "SERIAL-A-UNCHANGED");

        Assert.Equal(setup.UnitId, released.ReleasedUnitId);
        Assert.Equal(RefurbishmentIdentity.SameUnitSameSerial, released.IdentityPolicy);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertWholeUnitAsync(db, setup.UnitId, Entity, LocationKind.Warehouse, Warehouse, entitlementId, 1);
        var serial = Assert.Single(await db.Identifiers.Where(i => i.UnitId == setup.UnitId && i.Type == IdentifierType.InternalSerial).ToListAsync());
        Assert.Equal(setup.Serial.Replace("-", "", StringComparison.Ordinal), serial.NormalisedValue);
        Assert.Null(serial.ValidTo);
        Assert.Equal(RefurbishmentStatus.Released, (await db.RefurbishmentWorkOrders.SingleAsync()).Status);
        Assert.Contains(await db.Events.Where(e => e.UnitId == setup.UnitId).Select(e => e.EventType).ToListAsync(), e => e == RefurbishmentEventType.Released);
    }

    [DatabaseFact]
    public async Task Same_unit_new_serial_twice_preserves_unit_ownership_and_entitlements_with_two_link_lineage()
    {
        var setup = await SetUpUnitAsync("refurb-b", "class-b");
        var entitlementId = await CreateEntitlementAsync(setup, "B");

        var first = await OpenTestAndReleaseAsync(setup, At.AddHours(1), null);
        var second = await OpenTestAndReleaseAsync(setup, At.AddHours(4), "SERIAL-B-3");

        Assert.Equal((setup.UnitId, setup.UnitId), (first.ReleasedUnitId, second.ReleasedUnitId));
        Assert.Equal(2, second.CycleNumber);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertWholeUnitAsync(db, setup.UnitId, Entity, LocationKind.Warehouse, Warehouse, entitlementId, 3);
        var identifiers = await db.Identifiers.Where(i => i.UnitId == setup.UnitId && i.Type == IdentifierType.InternalSerial)
            .OrderBy(i => i.ValidFrom).ToListAsync();
        Assert.Equal(3, identifiers.Count);
        Assert.Equal(identifiers[0].IdentifierId, identifiers[1].SupersedesIdentifierId);
        Assert.Equal(identifiers[1].IdentifierId, identifiers[2].SupersedesIdentifierId);
        Assert.Equal((first.EventId, second.EventId), (identifiers[1].EventId, identifiers[2].EventId));
        Assert.NotNull(identifiers[0].ValidTo);
        Assert.NotNull(identifiers[1].ValidTo);
        Assert.Null(identifiers[2].ValidTo);
        Assert.Equal("SERIALB3", identifiers[2].NormalisedValue);
        Assert.Equal(2, await db.Events.CountAsync(e => e.UnitId == setup.UnitId && e.EventType == RefurbishmentEventType.Released));
    }

    [DatabaseFact]
    public async Task New_unit_policy_creates_lineage_retires_source_and_transfers_entitlement_under_named_policy()
    {
        var setup = await SetUpUnitAsync("refurb-c", "class-c");
        await SetPolicyAsync(setup.Tenant, RefurbishmentIdentity.NewUnit, 3, "CARRY");
        await InvokeAsync<ReplacementPolicyRow>(setup.Tenant,
            new DefineReplacementPolicy("CARRY", ReplacementRule.CarryRemaining, null, null, "Refurbishment carry-over"));
        var entitlementId = await CreateEntitlementAsync(setup, "C");

        var released = await OpenTestAndReleaseAsync(setup, At.AddHours(1), "SERIAL-C-NEW", RefurbishmentKind.Remanufacture);

        Assert.NotEqual(setup.UnitId, released.ReleasedUnitId);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var source = await db.Units.SingleAsync(u => u.UnitId == setup.UnitId);
        var derived = await db.Units.SingleAsync(u => u.UnitId == released.ReleasedUnitId);
        Assert.Equal(LifecycleStatus.Decommissioned, source.LifecycleStatus);
        Assert.Equal(setup.UnitId, derived.DerivedFromUnitId);
        await AssertOwnerAndHolderAsync(db, setup.UnitId, Entity, LocationKind.Warehouse, Warehouse);
        await AssertOwnerAndHolderAsync(db, derived.UnitId, Entity, LocationKind.Warehouse, Warehouse);
        var original = await db.Entitlements.SingleAsync(e => e.EntitlementId == entitlementId);
        var transferred = Assert.Single(await db.Entitlements.Where(e => e.ParentEntitlementId == entitlementId).ToListAsync());
        Assert.Equal(EntitlementStatus.Closed, original.Status);
        Assert.Equal(derived.UnitId, transferred.UnitId);
        Assert.Single(await db.Identifiers.Where(i => i.UnitId == setup.UnitId && i.Type == IdentifierType.InternalSerial).ToListAsync());
        Assert.Single(await db.Identifiers.Where(i => i.UnitId == derived.UnitId && i.Type == IdentifierType.InternalSerial && i.ValidTo == null).ToListAsync());
        Assert.Contains(await db.Events.Where(e => e.UnitId == setup.UnitId).Select(e => e.EventType).ToListAsync(), e => e == UnitEventType.Decommissioned);
        Assert.Contains(await db.Events.Where(e => e.UnitId == derived.UnitId).Select(e => e.EventType).ToListAsync(), e => e == RefurbishmentEventType.Released);
    }

    [DatabaseFact]
    public async Task Cycle_limit_and_missing_sanitisation_or_test_are_typed_refusals()
    {
        var setup = await SetUpUnitAsync("refurb-gates", "data-bearing", requiresSanitisation: true);
        await SetPolicyAsync(setup.Tenant, RefurbishmentIdentity.SameUnitSameSerial, 3, itemId: setup.ItemId, itemMaxCycles: 1);
        await RecordReturnedOwnershipAsync(setup);
        var opened = await InvokeAsync<OpenRefurbishmentResult>(setup.Tenant,
            new OpenRefurbishment(Guid.NewGuid(), setup.UnitId, RefurbishmentKind.Refurbish, At.AddHours(3), "quality", Entity, Repair));
        var release = Release(opened.WorkOrderId, At.AddHours(4));

        var sanitisation = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant, release));
        Assert.Equal("sanitisation_required", sanitisation.Rule);
        await InvokeAsync<RecordSanitisationResult>(setup.Tenant, Sanitisation(setup.UnitId, At.AddHours(3.25)));
        await InvokeAsync<RecordTestResult>(setup.Tenant,
            new RecordTest(Guid.NewGuid(), setup.UnitId, "visual-inspection", UnitTestResult.Pass, "{}",
                "fixture-fictional-visual", At.AddHours(3.4), "quality", Entity));
        var test = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant, release));
        Assert.Equal("passing_key_function_test_required", test.Rule);
        await RecordPassingTestAsync(setup.Tenant, setup.UnitId, At.AddHours(3.5));
        await InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant, release);

        var cycle = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<OpenRefurbishmentResult>(setup.Tenant,
            new OpenRefurbishment(Guid.NewGuid(), setup.UnitId, RefurbishmentKind.Refurbish, At.AddHours(5), "quality", Entity, Repair)));
        Assert.Equal("max_refurb_cycles", cycle.Rule);
        using var scope = fixture.ScopeFor(setup.Tenant);
        await AssertWholeUnitAsync(scope.ServiceProvider.GetRequiredService<SerialisationDbContext>(), setup.UnitId,
            Entity, LocationKind.Warehouse, Warehouse, null, 1);
    }

    [DatabaseFact]
    public async Task Removed_serialised_part_gets_its_own_spares_custody_fact_and_keeps_ownership_and_identity()
    {
        var parent = await SetUpUnitAsync("refurb-part-parent", "assembly");
        var child = await AddUnitAsync(parent.Tenant, "PART-CHILD", Guid.NewGuid(), "component");
        var assembly = await InvokeAsync<RecordAssemblyResult>(parent.Tenant,
            new RecordAssembly(Guid.NewGuid(), parent.UnitId, [new ChildInput(child.UnitId, Slot: "drive-1")], At.AddHours(1), "production", Entity));
        var opened = await InvokeAsync<OpenRefurbishmentResult>(parent.Tenant,
            new OpenRefurbishment(Guid.NewGuid(), parent.UnitId, RefurbishmentKind.Refurbish, At.AddHours(2), "quality", Entity, Repair));
        var spares = Guid.NewGuid();

        await InvokeAsync<RefurbishmentPartResult>(parent.Tenant,
            new RemoveRefurbishmentPart(Guid.NewGuid(), opened.WorkOrderId, Assert.Single(assembly.LinkIds),
                RemovedPartDestination.Spares, At.AddHours(3), "technician", Entity, spares));

        using var scope = fixture.ScopeFor(parent.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertOwnerAndHolderAsync(db, child.UnitId, Entity, LocationKind.Warehouse, spares);
        await AssertNoEntitlementsAndSingleOpenSerialAsync(db, child.UnitId);
        await AssertOwnerAndHolderAsync(db, parent.UnitId, Entity, LocationKind.InRepair, Repair);
        await AssertNoEntitlementsAndSingleOpenSerialAsync(db, parent.UnitId);
        Assert.NotNull((await db.AssemblyLinks.SingleAsync(l => l.LinkId == assembly.LinkIds[0])).RemovedAt);
        Assert.Contains(await db.Events.Where(e => e.UnitId == child.UnitId).Select(e => e.EventType).ToListAsync(), e => e == UnitEventType.CustodyChanged);
        Assert.Contains(await db.Events.Where(e => e.UnitId == parent.UnitId).Select(e => e.EventType).ToListAsync(), e => e == RefurbishmentEventType.PartRemoved);
    }

    [DatabaseFact]
    public async Task Costs_roll_into_release_and_every_posting_is_published_once_with_replay()
    {
        var setup = await SetUpUnitAsync("refurb-cost", "costed");
        await SetPolicyAsync(setup.Tenant, RefurbishmentIdentity.SameUnitSameSerial, 3);
        var openedCommand = new OpenRefurbishment(Guid.NewGuid(), setup.UnitId, RefurbishmentKind.Refurbish, At.AddHours(1), "quality", Entity, Repair);
        var opened = await InvokeAsync<OpenRefurbishmentResult>(setup.Tenant, openedCommand);
        Assert.True((await InvokeAsync<OpenRefurbishmentResult>(setup.Tenant, openedCommand)).Replayed);
        var operation = await InvokeAsync<RefurbishmentOperationResult>(setup.Tenant,
            new AddRefurbishmentOperation(Guid.NewGuid(), opened.WorkOrderId, 10, "Inspect and rebuild", "technician", 2m, At.AddHours(1.1), "planner", Entity));
        await InvokeAsync<RefurbishmentOperationResult>(setup.Tenant,
            new CompleteRefurbishmentOperation(Guid.NewGuid(), opened.WorkOrderId, operation.OperationId, 2m, At.AddHours(1.5), "technician", Entity));
        await InvokeAsync<RefurbishmentCostResult>(setup.Tenant,
            new RecordRefurbishmentCost(Guid.NewGuid(), opened.WorkOrderId, RefurbishmentCostKind.GradedReceipt, 720m, "EUR", "Graded receipt value", At.AddHours(1.6), "finance", Entity));
        await InvokeAsync<RefurbishmentLabourResult>(setup.Tenant,
            new RecordRefurbishmentLabour(Guid.NewGuid(), opened.WorkOrderId, operation.OperationId, 2m, 50m, "EUR", At.AddHours(1.7), "technician", Entity));
        await InvokeAsync<RefurbishmentPartResult>(setup.Tenant,
            new InstallRefurbishmentPart(Guid.NewGuid(), opened.WorkOrderId,
                new ChildInput(null, Guid.NewGuid(), Quantity: 1, Slot: "filter"), 80m, "EUR", At.AddHours(1.8), "technician", Entity, Repair));
        await RecordPassingTestAsync(setup.Tenant, setup.UnitId, At.AddHours(2));
        var releaseCommand = Release(opened.WorkOrderId, At.AddHours(3));

        var released = await InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant, releaseCommand);
        var replay = await InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant, releaseCommand);

        Assert.Equal((900m, "EUR"), (released.TotalCost, released.Currency));
        Assert.True(replay.Replayed);
        Assert.Equal(released.EventId, replay.EventId);
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<RefurbishmentReleased>(e => e.WorkOrderId == opened.WorkOrderId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((opened.WorkOrderId, opened.WorkOrderNumber, setup.UnitId, setup.UnitId, RefurbishmentKind.Refurbish,
                ConditionGrades.RefurbishedA, 1, RefurbishmentIdentity.SameUnitSameSerial, 900m, "EUR"),
            (published.WorkOrderId, published.WorkOrderNumber, published.SourceUnitId, published.ReleasedUnitId,
                published.Kind, published.Grade, published.CycleNumber, published.IdentityPolicy, published.TotalCost, published.Currency));
        Assert.Equal((RefurbishmentReleased.Schema, RefurbishmentReleased.Version, setup.UnitId),
            (published.Header.Schema, published.Header.Version, published.Header.UnitId));
        Assert.Equal(3, (await PublishedEventSink.WaitForAsync<RefurbishmentCostPosted>(e => e.WorkOrderId == opened.WorkOrderId, 3, TimeSpan.FromSeconds(20))).Count);
        using var releaseSchema = System.Text.Json.JsonDocument.Parse(PublishedEventSchemas.Read(RefurbishmentReleased.Schema, RefurbishmentReleased.Version));
        using var costSchema = System.Text.Json.JsonDocument.Parse(PublishedEventSchemas.Read(RefurbishmentCostPosted.Schema, RefurbishmentCostPosted.Version));
        Assert.Equal(RefurbishmentReleased.Schema, releaseSchema.RootElement.GetProperty("title").GetString());
        Assert.Equal(RefurbishmentCostPosted.Schema, costSchema.RootElement.GetProperty("title").GetString());
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(3, await db.RefurbishmentCosts.CountAsync(c => c.WorkOrderId == opened.WorkOrderId));
        await AssertWholeUnitAsync(db, setup.UnitId, Entity, LocationKind.Warehouse, Warehouse, null, 1);
    }

    [DatabaseFact]
    public async Task Rework_keeps_identity_never_voids_and_requests_release_record_reissue_only_after_as_built_change()
    {
        var setup = await SetUpUnitAsync("refurb-rework", "production");
        var opened = Assert.Single((await InvokeAsync<OpenReworkResult>(setup.Tenant,
            new OpenRework(Guid.NewGuid(), [setup.UnitId], ReworkSelectionKind.ProductionOrder, "PO-FICTIONAL-104",
                At.AddHours(1), "production", Entity, Repair))).WorkOrders);
        await InvokeAsync<RefurbishmentPartResult>(setup.Tenant,
            new InstallRefurbishmentPart(Guid.NewGuid(), opened.WorkOrderId,
                new ChildInput(null, Guid.NewGuid(), Quantity: 1, Slot: "seal"), 0m, "EUR", At.AddHours(1.5), "technician", Entity, Repair));
        await RecordPassingTestAsync(setup.Tenant, setup.UnitId, At.AddHours(2));
        var released = await InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant, Release(opened.WorkOrderId, At.AddHours(3)));

        Assert.Equal(setup.UnitId, released.ReleasedUnitId);
        Assert.Equal(RefurbishmentIdentity.SameUnitSameSerial, released.IdentityPolicy);
        var request = Assert.Single(await PublishedEventSink.WaitForAsync<ReleaseRecordReissueRequested>(e => e.WorkOrderId == opened.WorkOrderId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal("as_built_changed", request.Reason);
        using var schema = System.Text.Json.JsonDocument.Parse(PublishedEventSchemas.Read(ReleaseRecordReissueRequested.Schema, ReleaseRecordReissueRequested.Version));
        Assert.Equal(ReleaseRecordReissueRequested.Schema, schema.RootElement.GetProperty("title").GetString());
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.NotEqual(LifecycleStatus.Void, (await db.Units.SingleAsync(u => u.UnitId == setup.UnitId)).LifecycleStatus);
        await AssertWholeUnitAsync(db, setup.UnitId, Entity, LocationKind.Warehouse, Warehouse, null, 1);
    }

    [DatabaseFact]
    public async Task Rma_refurbish_disposition_opens_work_order_through_port_and_keeps_return_facts_consistent()
    {
        var setup = await SetUpRmaUnitAsync("refurb-rma");
        var requested = await InvokeAsync<RequestRmaResult>(setup.Tenant,
            new RequestRma(Guid.NewGuid(), DateOnly.FromDateTime(At), setup.Customer, setup.Customer, Entity, Entity, "DE", "EUR",
                [new RmaRequestLineInput(Identifier: new DocumentSerial(setup.Serial), RequestedAmount: 100m)], At, "service.desk"));
        await InvokeAsync<AuthoriseRmaResult>(setup.Tenant,
            new AuthoriseRma(Guid.NewGuid(), requested.RmaId, At.AddHours(1), "service.desk"));
        var receiving = Guid.NewGuid();
        await InvokeAsync<ReceiveReturnResult>(setup.Tenant,
            new ReceiveReturn(Guid.NewGuid(), requested.RmaId, receiving,
                [new ReturnReceiptLineInput(requested.LineIds[0], new DocumentSerial(setup.Serial), ConditionGrades.Defective, "graded_value")],
                At.AddHours(2), At.AddHours(3), At.AddHours(3), "returns"));
        var disposition = new DispositionRma(Guid.NewGuid(), requested.RmaId,
            [new RmaDispositionLineInput(requested.LineIds[0], RmaDisposition.Refurbish)],
            new DateOnly(2026, 9, 1), At.AddHours(5), "returns");

        var result = await InvokeAsync<DispositionRmaResult>(setup.Tenant, disposition);
        Assert.True((await InvokeAsync<DispositionRmaResult>(setup.Tenant, disposition)).Replayed);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var workOrder = Assert.Single(await db.RefurbishmentWorkOrders.Where(w => w.SourceRmaLineId == requested.LineIds[0]).ToListAsync());
        Assert.Equal((setup.UnitId, RefurbishmentStatus.Open, RefurbishmentKind.Refurbish),
            (workOrder.TargetUnitId, workOrder.Status, workOrder.Kind));
        Assert.Equal(RmaStage.Dispositioned, result.Stage);
        await AssertOwnerAndHolderAsync(db, setup.UnitId, Entity, LocationKind.InRepair, receiving);
        var entitlement = await db.Entitlements.SingleAsync(e => e.EntitlementId == setup.EntitlementId);
        Assert.NotEqual(EntitlementStatus.Closed, entitlement.Status);
        Assert.Single(await db.Identifiers.Where(i => i.UnitId == setup.UnitId && i.Type == IdentifierType.InternalSerial && i.ValidTo == null).ToListAsync());
        Assert.Contains(await db.Events.Where(e => e.UnitId == setup.UnitId).Select(e => e.EventType).ToListAsync(), e => e == RefurbishmentEventType.Opened);
        Assert.Contains(await db.Events.Where(e => e.UnitId == setup.UnitId).Select(e => e.EventType).ToListAsync(), e => e == UnitEventType.OwnershipOpened);
        Assert.Contains(await db.Events.Where(e => e.UnitId == setup.UnitId).Select(e => e.EventType).ToListAsync(), e => e == UnitEventType.CustodyChanged);
    }

    [DatabaseFact]
    public async Task Cancellation_closes_life_episode_returns_custody_and_replays_without_identity_or_entitlement_changes()
    {
        var setup = await SetUpUnitAsync("refurb-cancel", "cancelled");
        var entitlementId = await CreateEntitlementAsync(setup, "X");
        var opened = await InvokeAsync<OpenRefurbishmentResult>(setup.Tenant,
            new OpenRefurbishment(Guid.NewGuid(), setup.UnitId, RefurbishmentKind.Refurbish, At.AddHours(1), "quality", Entity, Repair));
        var command = new CancelRefurbishment(Guid.NewGuid(), opened.WorkOrderId, "Inspection found an uneconomic repair",
            At.AddHours(2), "quality", Entity, Warehouse);

        var first = await InvokeAsync<CancelRefurbishmentResult>(setup.Tenant, command);
        var replay = await InvokeAsync<CancelRefurbishmentResult>(setup.Tenant, command);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.EventId, replay.EventId);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertOwnerAndHolderAsync(db, setup.UnitId, Entity, LocationKind.Warehouse, Warehouse);
        var entitlement = await db.Entitlements.SingleAsync(e => e.UnitId == setup.UnitId);
        Assert.Equal(entitlementId, entitlement.EntitlementId);
        Assert.NotEqual(EntitlementStatus.Closed, entitlement.Status);
        Assert.Single(await db.Identifiers.Where(i => i.UnitId == setup.UnitId && i.Type == IdentifierType.InternalSerial && i.ValidTo == null).ToListAsync());
        Assert.Equal(At.AddHours(2), (await db.LifeEpisodes.SingleAsync(e => e.LifeEpisodeId == opened.LifeEpisodeId)).EndedAt);
        Assert.Contains(await db.Events.Where(e => e.UnitId == setup.UnitId).Select(e => e.EventType).ToListAsync(), e => e == RefurbishmentEventType.Cancelled);
    }

    private async Task<UnitSetup> SetUpUnitAsync(string tenantName, string itemClass, bool requiresSanitisation = false)
    {
        var tenant = await fixture.NewTenantAsync(tenantName);
        return await AddUnitAsync(tenant, tenantName.ToUpperInvariant() + "-SERIAL", Guid.NewGuid(), itemClass, requiresSanitisation);
    }

    private async Task<UnitSetup> AddUnitAsync(TenantId tenant, string serial, Guid itemId, string itemClass, bool requiresSanitisation = false)
    {
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(itemId) with
        {
            ItemClass = itemClass,
            RequiresSanitisation = requiresSanitisation,
        });
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant,
            new CommissionUnits(Guid.NewGuid(), itemId, UnitOrigin.Purchased,
                [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At, "receiving", Entity,
                LocationId: Warehouse, LocationKind: LocationKind.Warehouse));
        var unitId = Assert.Single(commissioned.Lines).UnitId!.Value;
        await InvokeAsync<OpenOwnershipResult>(tenant,
            new OpenOwnership(Guid.NewGuid(), unitId, Entity, PartyType.Us, "company_stock", At.AddMinutes(1), "receiving", Entity));
        return new(tenant, itemId, unitId, serial, itemClass);
    }

    private async Task SetPolicyAsync(TenantId tenant, string identity, int maxCycles, string? replacementPolicy = null,
        Guid? itemId = null, int? itemMaxCycles = null)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<RefurbishmentPolicySettings>()
            .SetAsync(new RefurbishmentPolicy(identity, maxCycles, [], replacementPolicy,
                itemId is { } id && itemMaxCycles is { } cycles ? [new RefurbishmentItemCyclePolicy(id, cycles)] : null), CancellationToken.None);
    }

    private async Task<ReleaseRefurbishmentResult> OpenTestAndReleaseAsync(UnitSetup setup, DateTime openedAt,
        string? newSerial, string kind = RefurbishmentKind.Refurbish)
    {
        var opened = await InvokeAsync<OpenRefurbishmentResult>(setup.Tenant,
            new OpenRefurbishment(Guid.NewGuid(), setup.UnitId, kind, openedAt, "quality", Entity, Repair));
        await RecordPassingTestAsync(setup.Tenant, setup.UnitId, openedAt.AddMinutes(20));
        return await InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant,
            Release(opened.WorkOrderId, openedAt.AddHours(1), newSerial));
    }

    private static ReleaseRefurbishment Release(Guid workOrderId, DateTime at, string? newSerial = null) =>
        new(Guid.NewGuid(), workOrderId, ConditionGrades.RefurbishedA, "graded_receipt_plus_postings", at,
            "quality", Entity, Warehouse, newSerial, "F2", "C2");

    private async Task RecordPassingTestAsync(TenantId tenant, Guid unitId, DateTime at) =>
        await InvokeAsync<RecordTestResult>(tenant,
            new RecordTest(Guid.NewGuid(), unitId, RefurbishmentTestKind.KeyFunctions, UnitTestResult.Pass, "{}", "fixture-fictional-12", at, "quality", Entity));

    private static RecordSanitisation Sanitisation(Guid unitId, DateTime at) =>
        new(Guid.NewGuid(), unitId, "Fictional Devices", "Storage One", "MEDIA-1", "solid_state",
            SanitisationMethod.Purge, "Fictional Wipe", "full verification", "CERT-1", at, "quality", Entity);

    private async Task RecordReturnedOwnershipAsync(UnitSetup setup)
    {
        var customer = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant,
            new OpenOwnership(Guid.NewGuid(), setup.UnitId, customer, PartyType.Customer, EpisodeReason.Sale, At.AddHours(1), "sales", Entity));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant,
            new OpenOwnership(Guid.NewGuid(), setup.UnitId, Entity, PartyType.Us, EpisodeReason.Return, At.AddHours(2), "returns", Entity));
    }

    private async Task<Guid> CreateEntitlementAsync(UnitSetup setup, string marker)
    {
        var family = "refurb_" + marker.ToLowerInvariant();
        await InvokeAsync<CoverageFamilyRow>(setup.Tenant,
            new DefineCoverageFamily(family, "Refurbishment " + marker, false, AssuranceOrService.Assurance));
        var code = "REF-" + marker + "-" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var draft = await InvokeAsync<PolicyDraftResult>(setup.Tenant,
            new CreatePolicyDraft(Guid.NewGuid(), code, code, family, EntitlementType.Manufacturer,
                new DateOnly(2026, 1, 1), PolicyValidatorTestsTerms.Standard24(), "test"));
        await InvokeAsync<PolicyDraftResult>(setup.Tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var entitlement = await InvokeAsync<CreateEntitlementResult>(setup.Tenant,
            new CreateEntitlement(Guid.NewGuid(), setup.UnitId, family, new DateOnly(2026, 8, 1), Entity, At.AddMinutes(2), "desk",
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = At.AddMinutes(2) }, PolicyVersionId: draft.PolicyVersionId,
                OwnerPartyRef: Entity, OwnerPartyType: PartyType.Us, Country: "DE", SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        return entitlement.EntitlementId;
    }

    private async Task<RmaSetup> SetUpRmaUnitAsync(string tenantName)
    {
        var tenant = await fixture.NewTenantAsync(tenantName);
        await InvokeAsync<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant,
            new DefineReplacementPolicy("RMA-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var policyCode = "RMA-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var draft = await InvokeAsync<PolicyDraftResult>(tenant,
            new CreatePolicyDraft(Guid.NewGuid(), policyCode, policyCode, CoverageFamilyCode.Base,
                EntitlementType.Manufacturer, new DateOnly(2025, 1, 1), PolicyValidatorTestsTerms.Standard24(), "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with { ItemClass = "rma-return" });
        Warranties.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, policyCode, true));
        var serial = "RMA-REFURB-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant,
            new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
                [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At.AddDays(-40), "receiving", Entity,
                LocationId: Warehouse, LocationKind: LocationKind.Warehouse));
        var unitId = Assert.Single(commissioned.Lines).UnitId!.Value;
        var customer = Guid.NewGuid();
        var entitlement = await InvokeAsync<CreateEntitlementResult>(tenant,
            new CreateEntitlement(Guid.NewGuid(), unitId, CoverageFamilyCode.Base, new DateOnly(2026, 7, 1), Entity,
                At.AddDays(-35), "service.desk", new Dictionary<string, DateTime> { [StartTrigger.Invoice] = At.AddDays(-35) },
                PolicyVersionId: draft.PolicyVersionId, OwnerPartyRef: customer, OwnerPartyType: PartyType.Customer,
                Country: "DE", SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        var shipment = new DocumentRef("delivery", Guid.NewGuid(), 1);
        Documents.Add(new ShipmentDocument(shipment, customer, At.AddDays(-30),
            [new DocumentLine(1, item, 1, [new DocumentSerial(serial)])], Guid.NewGuid()));
        var shipped = await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(new ThinEnvelope(Guid.NewGuid(), Entity, At.AddDays(-30), "sales"), shipment));
        Assert.Empty(shipped.Exceptions);
        var delivery = new DocumentRef("delivery_confirmation", Guid.NewGuid());
        Documents.Add(new DeliveryConfirmation(delivery, shipment, At.AddDays(-29), Guid.NewGuid()));
        var delivered = await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesDeliveryConfirmed(new ThinEnvelope(Guid.NewGuid(), Entity, At.AddDays(-29), "carrier"), delivery));
        Assert.Empty(delivered.Exceptions);
        return new(tenant, unitId, item, serial, customer, entitlement.EntitlementId);
    }

    private static async Task AssertWholeUnitAsync(SerialisationDbContext db, Guid unitId, Guid owner,
        string locationKind, Guid locationId, Guid? unchangedEntitlementId, int expectedInternalSerials)
    {
        await AssertOwnerAndHolderAsync(db, unitId, owner, locationKind, locationId);
        if (unchangedEntitlementId is { } entitlementId)
        {
            var entitlements = await db.Entitlements.Where(e => e.UnitId == unitId).ToListAsync();
            Assert.Single(entitlements);
            Assert.Equal(entitlementId, entitlements[0].EntitlementId);
            Assert.NotEqual(EntitlementStatus.Closed, entitlements[0].Status);
        }
        else
        {
            Assert.Empty(await db.Entitlements.Where(e => e.UnitId == unitId).ToListAsync());
        }
        Assert.Equal(expectedInternalSerials,
            await db.Identifiers.CountAsync(i => i.UnitId == unitId && i.Type == IdentifierType.InternalSerial));
        Assert.Single(await db.Identifiers.Where(i => i.UnitId == unitId && i.Type == IdentifierType.InternalSerial && i.ValidTo == null).ToListAsync());
        Assert.Contains(await db.Events.Where(e => e.UnitId == unitId).Select(e => e.EventType).ToListAsync(), e => e == RefurbishmentEventType.Released);
    }

    private static async Task AssertNoEntitlementsAndSingleOpenSerialAsync(SerialisationDbContext db, Guid unitId)
    {
        Assert.Empty(await db.Entitlements.Where(e => e.UnitId == unitId).ToListAsync());
        Assert.Single(await db.Identifiers.Where(i => i.UnitId == unitId
            && i.Type == IdentifierType.InternalSerial && i.ValidTo == null).ToListAsync());
    }

    private static async Task AssertOwnerAndHolderAsync(SerialisationDbContext db, Guid unitId, Guid owner,
        string locationKind, Guid locationId)
    {
        var ownership = await db.Episodes.SingleAsync(e => e.UnitId == unitId && e.Kind == EpisodeKind.Ownership && e.ValidTo == null);
        var custody = await db.Episodes.SingleAsync(e => e.UnitId == unitId && e.Kind == EpisodeKind.Custody && e.ValidTo == null);
        Assert.Equal((owner, PartyType.Us), (ownership.PartyRef, ownership.PartyType));
        Assert.Equal((locationKind, locationId), (custody.LocationKind, custody.LocationId));
    }

    private sealed record UnitSetup(TenantId Tenant, Guid ItemId, Guid UnitId, string Serial, string ItemClass);
    private sealed record RmaSetup(TenantId Tenant, Guid UnitId, Guid ItemId, string Serial, Guid Customer, Guid EntitlementId);
}
