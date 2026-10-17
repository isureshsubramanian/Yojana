using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Workbench;

namespace Yojana.Serialisation.DatabaseTests;

/*
P3-T20 coverage map (one end-to-end database scenario per owned case):
  32  -> Case_32_a_sale_recorded_for_a_unit_that_never_shipped_is_reversed_with_its_cover
  117 -> Case_117_crossed_identifiers_are_swapped_atomically_and_a_later_swap_restores_them
  118 -> Case_118_a_wrong_item_is_corrected_by_assignment_and_a_later_correction_restores_it
  119 -> Case_119_a_kitted_surplus_unit_is_reverse_issued_then_voided_without_deletion
  122 -> Case_122_late_serialisation_covers_open_stock_and_records_the_explicit_exemption
  131 -> Case_131_duplicate_unit_records_merge_to_a_readable_alias_and_can_be_split_again
  133 -> Case_133_pre_commission_allocations_move_between_orders_and_can_be_moved_back
  135 -> Case_135_an_incomplete_vendor_list_flags_the_exact_remainder_and_a_supplement_completes_it
  137 -> Case_137_quantity_reduction_waits_for_component_return_then_voids_without_reissue
  141 -> Case_141_wrong_document_serials_are_compensated_without_stock_quantity_and_can_be_reversed
  143 -> Case_143_a_wrong_opening_order_is_repointed_with_cover_and_labels_then_reversed
*/

/// <summary>P3-T20: correction edge cases through public commands, document messages and the published outbox.</summary>
[Collection("serialisation host")]
public sealed class CorrectionEdgeCaseScenarioTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("a0000000-0000-0000-0000-000000000020");
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly CorrectionCommitter Production = new("production-supervisor", [YojanaRole.ProductionSupervisor]);
    private static readonly CorrectionCommitter SerialAdmin = new("serial-admin", [YojanaRole.SerialAdministrator]);
    private static readonly CorrectionCommitter Shipping = new("shipping-supervisor", [YojanaRole.ShippingSupervisor, YojanaRole.ServiceSupervisor]);
    private static readonly WorkbenchActor Technician = new("technician", [YojanaRole.Technician]);

    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
    private CorrectionScenarioHelper Scenario => new(fixture, Entity, At);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
        => await Scenario.InvokeAsync<T>(tenant, message);

    private async Task<(Guid UnitId, Guid ItemId, string Serial)> CommissionAsync(TenantId tenant, string prefix,
        string origin = UnitOrigin.Manufactured, DocumentRef? document = null, Guid? location = null, Guid? item = null)
        => await Scenario.CommissionAsync(tenant, prefix, origin, document, location, item);

    private async Task<Guid> SelectAsync(TenantId tenant, IReadOnlyList<string> serials)
        => await Scenario.SelectAsync(tenant, serials);

    private static ThinEnvelope Envelope(DateTime at) => new(Guid.NewGuid(), Entity, at, "scenario");

    private async Task AssertKpiAsync(TenantId tenant, Guid correctionId, string command, int unitCount,
        string initiatedVia = CorrectionInitiatedVia.Workbench)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var row = await db.Corrections.AsNoTracking().SingleAsync(c => c.CorrectionId == correctionId);
        Assert.Equal((command, unitCount), (row.Command, row.UnitCount));
        Assert.Equal(initiatedVia, row.InitiatedVia);
    }

    private async Task<(T Result, CorrectionPreview Preview)> CommitWorkbenchAsync<T>(TenantId tenant,
        WorkbenchPreviewRequest request, WorkbenchActor committer, params WorkbenchActor[] approvers) where T : class
    {
        var (result, preview, _) = await Scenario.CommitWorkbenchAsync<T>(tenant, request, Technician, committer, approvers);
        return (result, preview);
    }

    private static WorkbenchPreviewRequest WorkbenchRequest<T>(Guid correctionId, string choice, string command,
        WorkbenchTarget target, string reason, T arguments) where T : class =>
        CorrectionScenarioHelper.WorkbenchRequest(correctionId, choice, command, target, reason, arguments);

    [DatabaseFact]
    public async Task Case_32_a_sale_recorded_for_a_unit_that_never_shipped_is_reversed_with_its_cover()
    {
        var tenant = await fixture.NewTenantAsync("ec32");
        var warehouse = Guid.NewGuid();
        var site = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var unit = await CommissionAsync(tenant, "EC32", UnitOrigin.Purchased, location: warehouse);
        await InvokeAsync<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        var policy = await PolicyAsync(tenant, "EC32");
        var shipment = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, At.AddDays(1),
            [new DocumentLine(1, unit.ItemId, 1, [new(unit.Serial)])], site);
        Documents.Add(shipment);
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Envelope(shipment.PostedAt), shipment.Document));
        var entitlement = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit.UnitId,
            CoverageFamilyCode.Base, DateOnly.FromDateTime(shipment.PostedAt), Entity, shipment.PostedAt, "scenario",
            new Dictionary<string, DateTime> { [StartTrigger.Deliver] = shipment.PostedAt }, policy, customer,
            PartyType.Customer, SourceDocument: shipment.Document));
        var recorded = await SelectAsync(tenant, [unit.Serial]);
        var none = await SelectAsync(tenant, []);
        var correctionId = Guid.NewGuid();
        var command = new CorrectDocumentSerials(Guid.NewGuid(), shipment.Document, recorded, none,
            CorrectDocumentSerialsReason.ReturnedNeverDelivered, At.AddDays(2), Shipping.UserRef, Entity,
            new(correctionId, CorrectDocumentSerialsReason.ReturnedNeverDelivered,
                EvidenceRefs: ["warehouse:stocktake"], InitiatingRole: CorrectionInitiatingRole.WarehouseLead), Shipping);

        var (result, preview) = await CommitWorkbenchAsync<CorrectDocumentSerialsResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.WrongDocumentSerials,
                CorrectionCommand.CorrectDocumentSerials, new(WorkbenchTargetKind.DocumentLine, Document: shipment.Document),
                command.Reason, command), new WorkbenchActor(Shipping.UserRef, Shipping.Roles),
            new WorkbenchActor(Shipping.UserRef, Shipping.Roles));
        Assert.Equal((1, 1, 0), (preview.UnitCount, preview.Change, preview.Block));
        Assert.Equal((1, 0, 1, 0), (result.Reversed, result.Shipped, result.EntitlementsVoided, result.EntitlementsCreated));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var shipped = await db.Events.SingleAsync(e => e.UnitId == unit.UnitId && e.EventType == UnitEventType.Shipped);
            var reversed = await db.Events.SingleAsync(e => e.UnitId == unit.UnitId && e.EventType == UnitEventType.ShipmentReversed);
            Assert.Equal(shipped.EventId, reversed.ReversesEventId);
            Assert.Equal(warehouse, (await db.Current.SingleAsync(c => c.UnitId == unit.UnitId)).LocationId);
            var cover = await db.Entitlements.SingleAsync(e => e.EntitlementId == entitlement.EntitlementId);
            Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.VoidedDocumentCorrection), (cover.Status, cover.ClosedReason));
        }
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.CorrectDocumentSerials, 1);
        var published = Assert.Single(await PublishedEventSink.WaitForSummaryAsync<DocumentSerialsCorrected>(
            e => e.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal([unit.UnitId], published.ReversedUnitIds);
        Assert.Empty(published.ShippedUnitIds);
    }

    [DatabaseFact]
    public async Task Case_117_crossed_identifiers_are_swapped_atomically_and_a_later_swap_restores_them()
    {
        var tenant = await fixture.NewTenantAsync("ec117");
        var item = Guid.NewGuid();
        var first = await CommissionAsync(tenant, "EC117A", item: item);
        var second = await CommissionAsync(tenant, "EC117B", item: item);
        var correctionId = Guid.NewGuid();
        var swapId = Guid.NewGuid();
        var command = new SwapIdentifiers(Guid.NewGuid(), first.UnitId, second.UnitId,
            IdentifierType.InternalSerial, SwapIdentifiersReason.CrossRecordedAtBuild, At.AddDays(1), SerialAdmin.UserRef, Entity,
            Correction: new(correctionId, SwapIdentifiersReason.CrossRecordedAtBuild, EvidenceRefs: ["photo:pair"],
                InitiatingRole: CorrectionInitiatingRole.SerialAdministrator), CommittedBy: SerialAdmin, SwapId: swapId);
        await CommitWorkbenchAsync<SwapIdentifiersResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.MistypedOrCrossed, CorrectionCommand.SwapIdentifiers,
                new(WorkbenchTargetKind.Unit, UnitId: first.UnitId), command.Reason, command),
            new WorkbenchActor(SerialAdmin.UserRef, SerialAdmin.Roles),
            new WorkbenchActor(SerialAdmin.UserRef, SerialAdmin.Roles));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(second.Serial.Replace("-", "", StringComparison.Ordinal),
                (await db.Identifiers.SingleAsync(i => i.UnitId == first.UnitId && i.Type == IdentifierType.InternalSerial && i.ValidTo == null)).NormalisedValue);
            Assert.Equal(2, await db.Events.CountAsync(e => e.EventType == UnitEventType.IdentifiersSwapped
                && (e.UnitId == first.UnitId || e.UnitId == second.UnitId)));
        }
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.SwapIdentifiers, 2);
        Assert.Equal(2, (await PublishedEventSink.WaitForAsync<UnitIdentifiersSwapped>(
            e => e.SwapId == swapId, 2, TimeSpan.FromSeconds(20))).Count);

        var reversalId = Guid.NewGuid();
        await InvokeAsync<SwapIdentifiersResult>(tenant, new SwapIdentifiers(Guid.NewGuid(), first.UnitId, second.UnitId,
            IdentifierType.InternalSerial, SwapIdentifiersReason.LabelAppliedToWrongUnit, At.AddDays(2), SerialAdmin.UserRef, Entity,
            Correction: new(reversalId, SwapIdentifiersReason.LabelAppliedToWrongUnit, CorrectsCorrectionId: correctionId,
                InitiatingRole: CorrectionInitiatingRole.SerialAdministrator), CommittedBy: SerialAdmin));
        using var check = fixture.ScopeFor(tenant);
        var checkDb = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(first.Serial.Replace("-", "", StringComparison.Ordinal),
            (await checkDb.Identifiers.SingleAsync(i => i.UnitId == first.UnitId && i.Type == IdentifierType.InternalSerial && i.ValidTo == null)).NormalisedValue);
        Assert.Equal(correctionId, (await checkDb.Corrections.SingleAsync(c => c.CorrectionId == reversalId)).CorrectsCorrectionId);
    }

    [DatabaseFact]
    public async Task Case_118_a_wrong_item_is_corrected_by_assignment_and_a_later_correction_restores_it()
    {
        var tenant = await fixture.NewTenantAsync("ec118");
        var warehouse = Guid.NewGuid();
        var original = await CommissionAsync(tenant, "EC118", UnitOrigin.Purchased, location: warehouse);
        var correctedItem = Guid.NewGuid();
        var correctionId = Guid.NewGuid();
        CorrectItem Command(Guid item, Guid id, Guid? corrects = null) => new(Guid.NewGuid(), original.UnitId, item,
            CorrectItemReason.WrongItemAtReceipt, At.AddDays(corrects is null ? 1 : 2), "inventory-controller", Entity,
            new(id, CorrectItemReason.WrongItemAtReceipt, EvidenceRefs: ["receipt:118"], CorrectsCorrectionId: corrects,
                InitiatingRole: CorrectionInitiatingRole.WarehouseLead),
            new("inventory-controller", [YojanaRole.InventoryController]));

        var inventory = new WorkbenchActor("inventory-controller", [YojanaRole.InventoryController]);
        var firstCommand = Command(correctedItem, correctionId);
        var (first, _) = await CommitWorkbenchAsync<CorrectItemResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.WrongItem, CorrectionCommand.CorrectItem,
                new(WorkbenchTargetKind.Unit, UnitId: original.UnitId), firstCommand.Reason, firstCommand),
            inventory, inventory);
        Assert.NotNull(first.InventoryReclassificationRef);
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.CorrectItem, 1);
        Assert.Single(await PublishedEventSink.WaitForAsync<UnitItemCorrected>(
            e => e.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));

        var reversalId = Guid.NewGuid();
        await InvokeAsync<CorrectItemResult>(tenant, Command(original.ItemId, reversalId, correctionId));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(original.ItemId, (await db.Current.SingleAsync(c => c.UnitId == original.UnitId)).ItemId);
        Assert.Equal([original.ItemId, correctedItem, original.ItemId],
            await db.ItemAssignments.Where(a => a.UnitId == original.UnitId).OrderBy(a => a.ValidFrom).Select(a => a.ItemId).ToListAsync());
        Assert.Equal(2, await db.Events.CountAsync(e => e.UnitId == original.UnitId && e.EventType == UnitEventType.ItemCorrected));
    }

    [DatabaseFact]
    public async Task Case_119_a_kitted_surplus_unit_is_reverse_issued_then_voided_without_deletion()
    {
        var tenant = await fixture.NewTenantAsync("ec119");
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var warehouse = Guid.NewGuid();
        var parent = await CommissionAsync(tenant, "EC119P", document: order, location: warehouse);
        var child = await CommissionAsync(tenant, "EC119C", location: warehouse);
        var assembly = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent.UnitId,
            [new ChildInput(child.UnitId)], At.AddHours(1), "manufacturing", Entity, Document: order));
        await InvokeAsync<ManufacturingComponentReturnedResult>(tenant, new ManufacturingComponentReturned(
            Envelope(At.AddHours(2)), order, warehouse, [new(parent.UnitId, Assert.Single(assembly.LinkIds))]));
        var correctionId = Guid.NewGuid();
        var command = new VoidUnit(Guid.NewGuid(), parent.UnitId, VoidUnitReason.WorkOrderQuantityReduced, At.AddDays(1),
            Production.UserRef, Entity, order, Correction: new(correctionId, VoidUnitReason.WorkOrderQuantityReduced,
                InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor), CommittedBy: Production);
        var production = new WorkbenchActor(Production.UserRef, Production.Roles);
        var (voided, _) = await CommitWorkbenchAsync<VoidUnitResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.WrongAllocation, CorrectionCommand.VoidUnit,
                new(WorkbenchTargetKind.Unit, UnitId: parent.UnitId), command.Reason, command), production, production);
        Assert.Equal((LifecycleStatus.Void, SerialReuseOutcome.NeverReuse), (voided.LifecycleStatus, voided.SerialReuseOutcome));
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.VoidUnit, 1);
        Assert.Single(await PublishedEventSink.WaitForAsync<UnitVoided>(
            e => e.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));

        var reversalId = Guid.NewGuid();
        var restored = await InvokeAsync<VoidUnitResult>(tenant, command with
        {
            CommandId = Guid.NewGuid(), EventTime = At.AddDays(2),
            Correction = new(reversalId, VoidUnitReason.WorkOrderQuantityReduced, CorrectsCorrectionId: correctionId,
                InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor),
        });
        Assert.True(restored.Reversed);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(2, await db.Units.CountAsync(u => u.UnitId == parent.UnitId || u.UnitId == child.UnitId));
        Assert.NotNull((await db.AssemblyLinks.SingleAsync(a => a.LinkId == assembly.LinkIds[0])).RemovedAt);
        Assert.Single(await db.Events.Where(e => e.UnitId == parent.UnitId && e.EventType == UnitEventType.Voided).ToListAsync());
        Assert.Single(await db.Events.Where(e => e.UnitId == parent.UnitId && e.EventType == UnitEventType.VoidReversed).ToListAsync());
    }

    [DatabaseFact]
    public async Task Case_122_late_serialisation_covers_open_stock_and_records_the_explicit_exemption()
    {
        var tenant = await fixture.NewTenantAsync("ec122");
        var item = Guid.NewGuid();
        var location = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(
            InMemoryItemPorts.DefaultProfile(item) with { TrackingMode = ItemSerialisationProfile.TrackingModes.None });
        fixture.Services.GetRequiredService<InMemoryStockQuantityPort>().Set(item, [new(location, LocationKind.Warehouse, 2)]);
        var correctionId = Guid.NewGuid();
        var source = new DocumentRef("stocktake", Guid.NewGuid());
        var command = new LateSerialization(Guid.NewGuid(), item, At.AddDays(1), LateSerializationReason.CodingErrorAtItemSetup,
            "item-owner", Entity, source,
            [new(Guid.NewGuid(), location, LocationKind.Warehouse, source,
                [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "EC122-0001")])], 1, "label unreadable")], [],
            new(correctionId, LateSerializationReason.CodingErrorAtItemSetup, EvidenceRefs: ["stocktake:122"],
                InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor,
                Approvers: [new("inventory-controller", [YojanaRole.InventoryController]), new("quality", [YojanaRole.Quality])]),
            new("item-owner", [YojanaRole.ItemMasterOwner]));

        // Separation of duties: the item-master owner signs and commits; the inventory controller and the quality approver
        // for the exemption are other people.
        var itemOwner = new WorkbenchActor("item-owner", [YojanaRole.ItemMasterOwner]);
        var (result, _) = await CommitWorkbenchAsync<LateSerializationResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.WrongComponents, CorrectionCommand.LateSerialization,
                new(WorkbenchTargetKind.DocumentLine, Document: source), command.Reason, command), itemOwner,
            new WorkbenchActor("inventory-controller", [YojanaRole.InventoryController]), new WorkbenchActor("quality", [YojanaRole.Quality]), itemOwner);
        Assert.Equal((1, 1, false), (result.Commissioned, result.Exempted, result.Reversed));
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.LateSerialization, 1);
        var published = Assert.Single(await PublishedEventSink.WaitForSummaryAsync<ItemTrackingModeChanged>(
            e => e.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((1, 1), (published.StockCoverage.Single().CommissionedQuantity, published.StockCoverage.Single().ExemptQuantity));
        using var scope = fixture.ScopeFor(tenant);
        var fact = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events
            .SingleAsync(e => e.EventType == UnitEventType.Commissioned && e.DocumentId == source.Id);
        Assert.True(JsonDocument.Parse(fact.Attributes!).RootElement.GetProperty("commissioned_late").GetBoolean());
    }

    [DatabaseFact]
    public async Task Case_131_duplicate_unit_records_merge_to_a_readable_alias_and_can_be_split_again()
    {
        var tenant = await fixture.NewTenantAsync("ec131");
        var item = Guid.NewGuid();
        var survivor = await CommissionAsync(tenant, "EC131S", item: item);
        var absorbed = await CommissionAsync(tenant, "EC131A", item: item);
        var correctionId = Guid.NewGuid();
        var approval = new CorrectionApprover("second-serial-admin", [YojanaRole.SerialAdministrator]);
        var command = new MergeUnits(Guid.NewGuid(), survivor.UnitId, absorbed.UnitId, MergeUnitsReason.DuplicateRecord,
            At.AddDays(1), SerialAdmin.UserRef, Entity,
            Correction: new(correctionId, MergeUnitsReason.DuplicateRecord, EvidenceRefs: ["photo:131"], Approvers: [approval]),
            CommittedBy: SerialAdmin);
        var firstAdmin = new WorkbenchActor("serial-approver-a", [YojanaRole.SerialAdministrator]);
        var secondAdmin = new WorkbenchActor("serial-approver-b", [YojanaRole.SerialAdministrator]);
        var (merged, _) = await CommitWorkbenchAsync<MergeUnitsResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.DuplicateUnit, CorrectionCommand.MergeUnits,
                new(WorkbenchTargetKind.Unit, UnitId: survivor.UnitId), command.Reason, command),
            firstAdmin, firstAdmin, secondAdmin);
        Assert.False(merged.Reversed);
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.MergeUnits, 2);
        Assert.Equal(2, (await PublishedEventSink.WaitForAsync<UnitMerged>(
            e => e.Correction.CorrectionId == correctionId, 2, TimeSpan.FromSeconds(20))).Count);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var alias = await db.Units.SingleAsync(u => u.UnitId == absorbed.UnitId);
            Assert.Equal((LifecycleStatus.Merged, survivor.UnitId), (alias.LifecycleStatus, alias.MergedIntoUnitId));
            Assert.All(await db.Events.Where(e => e.MergedFromUnitId == absorbed.UnitId).ToListAsync(),
                e => Assert.Equal(survivor.UnitId, e.UnitId));
            Assert.Equal(2, await db.Events.CountAsync(e => e.EventType == UnitEventType.Merged
                && (e.UnitId == survivor.UnitId || e.UnitId == absorbed.UnitId)));
        }

        var reversalId = Guid.NewGuid();
        var split = await InvokeAsync<MergeUnitsResult>(tenant, command with
        {
            CommandId = Guid.NewGuid(), EventTime = At.AddDays(2),
            Correction = new(reversalId, MergeUnitsReason.DuplicateRecord, CorrectsCorrectionId: correctionId,
                Approvers: [approval]),
        });
        Assert.True(split.Reversed);
        using var check = fixture.ScopeFor(tenant);
        var restored = await check.ServiceProvider.GetRequiredService<SerialisationDbContext>().Units
            .SingleAsync(u => u.UnitId == absorbed.UnitId);
        Assert.NotEqual(LifecycleStatus.Merged, restored.LifecycleStatus);
        Assert.Null(restored.MergedIntoUnitId);
    }

    [DatabaseFact]
    public async Task Case_133_pre_commission_allocations_move_between_orders_and_can_be_moved_back()
    {
        var tenant = await fixture.NewTenantAsync("ec133");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item, "EC133");
        var firstOrder = new DocumentRef("production_order", Guid.NewGuid());
        var secondOrder = new DocumentRef("production_order", Guid.NewGuid());
        Documents.Add(new ProductionOrderAllocation(firstOrder, item, scheme.Code, 1));
        Documents.Add(new ProductionOrderAllocation(secondOrder, item, scheme.Code, 1));
        var firstSerial = scheme.Format(0, 1);
        var secondSerial = scheme.Format(0, 2);
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme.Code, 1, At,
            "planner", Start: firstSerial, ItemId: item, AllocatedTo: firstOrder));
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme.Code, 1, At,
            "planner", Start: secondSerial, ItemId: item, AllocatedTo: secondOrder));
        var correctionId = Guid.NewGuid();
        ReassignAllocation Command(Guid id, DocumentRef from, DocumentRef to, Guid? corrects = null) => new(id, from, to,
            AllocationReassignmentMode.PairwiseExchange, SelectionSource.ByPastedList([firstSerial]),
            SelectionSource.ByPastedList([secondSerial]), ReassignAllocationReason.AllocationError, At.AddDays(corrects is null ? 1 : 2),
            Production.UserRef, Entity, Correction: new(id, ReassignAllocationReason.AllocationError,
                CorrectsCorrectionId: corrects, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor), CommittedBy: Production);

        var production = new WorkbenchActor(Production.UserRef, Production.Roles);
        var firstMove = Command(correctionId, firstOrder, secondOrder);
        await CommitWorkbenchAsync<ReassignAllocationResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.WrongAllocation, CorrectionCommand.ReassignAllocation,
                new(WorkbenchTargetKind.DocumentLine, Document: firstOrder), firstMove.Reason, firstMove), production, production);
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.ReassignAllocation, 0);
        Assert.Single(await PublishedEventSink.WaitForSummaryAsync<UnitAllocationReassigned>(
            e => e.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var rows = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().AllocationOverrides.ToListAsync();
            Assert.Equal(secondOrder.Id, rows.Single(r => r.SerialValue == firstSerial).ProductionOrderId);
            Assert.Equal(firstOrder.Id, rows.Single(r => r.SerialValue == secondSerial).ProductionOrderId);
        }

        var reversalId = Guid.NewGuid();
        await InvokeAsync<ReassignAllocationResult>(tenant, Command(reversalId, secondOrder, firstOrder, correctionId));
        using var check = fixture.ScopeFor(tenant);
        var restored = await check.ServiceProvider.GetRequiredService<SerialisationDbContext>().AllocationOverrides.ToListAsync();
        Assert.Equal(firstOrder.Id, restored.Single(r => r.SerialValue == firstSerial).ProductionOrderId);
        Assert.Equal(secondOrder.Id, restored.Single(r => r.SerialValue == secondSerial).ProductionOrderId);
    }

    [DatabaseFact]
    public async Task Case_135_an_incomplete_vendor_list_flags_the_exact_remainder_and_a_supplement_completes_it()
    {
        var tenant = await fixture.NewTenantAsync("ec135");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item, "EC135", IdentifierType.VendorSerial);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var provisional = Enumerable.Range(1, 3)
            .Select(i => new CommissionLine([new IdentifierInput(IdentifierType.Provisional, $"EC135-P-{i:000}")])).ToList();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Manufactured, provisional, At, "manufacturing", Entity, Document: order));
        var units = commissioned.Lines.Select(l => l.UnitId!.Value).ToList();
        var correctionId = Guid.NewGuid();
        var first = new AssignIdentifiers(correctionId, order, IdentifierAssignmentMode.AllocationOrder,
            [new(scheme.Format(0, 1))], AssignIdentifiersReason.VendorList, At.AddDays(1), Production.UserRef, Entity,
            Correction: new(correctionId, AssignIdentifiersReason.VendorList,
                InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor),
            CommittedBy: new(Production.UserRef, Production.Roles));
        var production = new WorkbenchActor(Production.UserRef, Production.Roles);
        var (partial, _) = await CommitWorkbenchAsync<AssignIdentifiersResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.WrongComponents, CorrectionCommand.AssignIdentifiers,
                new(WorkbenchTargetKind.DocumentLine, Document: order), first.Reason, first), production, production);
        Assert.Equal((1, 2), (partial.Assigned, partial.RemainingCount));
        Assert.Equal(units.Skip(1).Order(), partial.AwaitingVendorSerialUnitIds.Order());
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.AssignIdentifiers, 3);
        var published = Assert.Single(await PublishedEventSink.WaitForSummaryAsync<UnitIdentifiersAssigned>(
            e => e.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(2, published.RemainingCount);

        var supplementId = Guid.NewGuid();
        var supplement = first with
        {
            CommandId = supplementId, Mode = IdentifierAssignmentMode.ExplicitPairs,
            Lines = [new(scheme.Format(0, 2), units[1]), new(scheme.Format(0, 3), units[2])],
            Reason = AssignIdentifiersReason.VendorListSupplement, EventTime = At.AddDays(2),
            Correction = new(supplementId, AssignIdentifiersReason.VendorListSupplement,
                InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor),
        };
        var completed = await InvokeAsync<AssignIdentifiersResult>(tenant, supplement);
        Assert.Equal((2, 0), (completed.Assigned, completed.RemainingCount));
        using var scope = fixture.ScopeFor(tenant);
        var rows = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Units
            .Where(u => units.Contains(u.UnitId)).ToListAsync();
        Assert.All(rows, row => Assert.DoesNotContain(OverlayFlag.AwaitingVendorSerial,
            JsonSerializer.Deserialize<List<string>>(row.OverlayFlags)!));
        Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events
            .CountAsync(e => units.Contains(e.UnitId) && e.EventType == UnitEventType.IdentifierAssigned));
    }

    [DatabaseFact]
    public async Task Case_137_quantity_reduction_waits_for_component_return_then_voids_without_reissue()
    {
        var tenant = await fixture.NewTenantAsync("ec137");
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var warehouse = Guid.NewGuid();
        var parent = await CommissionAsync(tenant, "EC137P", document: order, location: warehouse);
        var child = await CommissionAsync(tenant, "EC137C", location: warehouse);
        var assembly = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent.UnitId,
            [new ChildInput(child.UnitId)], At.AddHours(1), "manufacturing", Entity, Document: order));
        var blockedMessageId = Guid.NewGuid();
        var blocked = await InvokeAsync<ProductionOrderQuantityChangeResult>(tenant,
            new ManufacturingProductionOrderQuantityChangeRequested(new(blockedMessageId, Entity, At.AddHours(2), "manufacturing"),
                order, 1, 0, [parent.UnitId]));
        Assert.False(blocked.CanApplyQuantityChange);
        Assert.Equal("components_still_issued", Assert.Single(blocked.Refusals).Rule);

        await InvokeAsync<ManufacturingComponentReturnedResult>(tenant, new ManufacturingComponentReturned(
            Envelope(At.AddHours(3)), order, warehouse, [new(parent.UnitId, assembly.LinkIds[0])]));
        var appliedMessageId = Guid.NewGuid();
        var applied = await InvokeAsync<ProductionOrderQuantityChangeResult>(tenant,
            new ManufacturingProductionOrderQuantityChangeRequested(new(appliedMessageId, Entity, At.AddHours(4), "manufacturing"),
                order, 1, 0, [parent.UnitId]));
        Assert.True(applied.CanApplyQuantityChange);
        Assert.Equal([parent.UnitId], applied.VoidedUnitIds);
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<UnitVoided>(
            e => e.Header.UnitId == parent.UnitId && e.Correction.InitiatedVia == CorrectionInitiatedVia.Integration,
            1, TimeSpan.FromSeconds(20)));
        await AssertKpiAsync(tenant, published.Correction.CorrectionId, CorrectionCommand.VoidUnit, 1,
            CorrectionInitiatedVia.Integration);
        Assert.Single(await PublishedEventSink.WaitForSummaryAsync<UnitProductionOrderReconciled>(
            e => e.RequestMessageId == appliedMessageId, 1, TimeSpan.FromSeconds(20)));
        using (var scope = fixture.ScopeFor(tenant))
        {
            Assert.Single(await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events
                .Where(e => e.UnitId == parent.UnitId && e.EventType == UnitEventType.Voided).ToListAsync());
        }
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<RecordAssemblyResult>(tenant,
            new RecordAssembly(Guid.NewGuid(), parent.UnitId, [new ChildInput(child.UnitId)], At.AddDays(1),
                "manufacturing", Entity, Document: order)));
        Assert.Equal("component_reissue_to_voided_unit", refusal.Rule);
    }

    [DatabaseFact]
    public async Task Case_141_wrong_document_serials_are_compensated_without_stock_quantity_and_can_be_reversed()
    {
        var tenant = await fixture.NewTenantAsync("ec141");
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var destination = Guid.NewGuid();
        await InvokeAsync<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        var policy = await PolicyAsync(tenant, "EC141");
        var recordedUnit = await CommissionAsync(tenant, "EC141R", UnitOrigin.Purchased, location: warehouse, item: item);
        var physicalUnit = await CommissionAsync(tenant, "EC141P", UnitOrigin.Purchased, location: warehouse, item: item);
        var transfer = new TransferDocument(new("transfer", Guid.NewGuid()), At.AddDays(1), warehouse,
            LocationKind.Warehouse, destination, LocationKind.Warehouse,
            [new DocumentLine(1, item, 1, [new(recordedUnit.Serial)])]);
        Documents.Add(transfer);
        await InvokeAsync<ConsumedEventResult>(tenant, new InventoryTransferPosted(Envelope(transfer.PostedAt), transfer.Document));
        await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), recordedUnit.UnitId,
            CoverageFamilyCode.Base, DateOnly.FromDateTime(transfer.PostedAt), Entity, transfer.PostedAt, "scenario",
            new Dictionary<string, DateTime> { [StartTrigger.Deliver] = transfer.PostedAt }, policy,
            SourceDocument: transfer.Document));
        var recorded = await SelectAsync(tenant, [recordedUnit.Serial]);
        var physical = await SelectAsync(tenant, [physicalUnit.Serial]);
        var correctionId = Guid.NewGuid();
        CorrectDocumentSerials Command(Guid id, Guid left, Guid right, Guid? corrects = null) => new(Guid.NewGuid(), transfer.Document,
            left, right, CorrectDocumentSerialsReason.ShippedWrongUnits, At.AddDays(corrects is null ? 2 : 3), Shipping.UserRef, Entity,
            new(id, CorrectDocumentSerialsReason.ShippedWrongUnits, CorrectsCorrectionId: corrects,
                InitiatingRole: CorrectionInitiatingRole.WarehouseLead), Shipping);
        var shipping = new WorkbenchActor(Shipping.UserRef, Shipping.Roles);
        var firstCorrection = Command(correctionId, recorded, physical);
        var (corrected, _) = await CommitWorkbenchAsync<CorrectDocumentSerialsResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.WrongDocumentSerials,
                CorrectionCommand.CorrectDocumentSerials, new(WorkbenchTargetKind.DocumentLine, Document: transfer.Document),
                firstCorrection.Reason, firstCorrection), shipping, shipping);
        Assert.Equal((1, 1, 1, 1), (corrected.Reversed, corrected.Shipped,
            corrected.EntitlementsVoided, corrected.EntitlementsCreated));
        Assert.Contains(fixture.Services.GetRequiredService<InMemoryStockUnitEventPort>().Corrections,
            c => c.CorrectionId == correctionId && c.QuantityDelta == 0);
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.CorrectDocumentSerials, 2);
        Assert.Single(await PublishedEventSink.WaitForSummaryAsync<DocumentSerialsCorrected>(
            e => e.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(warehouse, (await db.Current.SingleAsync(c => c.UnitId == recordedUnit.UnitId)).LocationId);
            Assert.Equal(destination, (await db.Current.SingleAsync(c => c.UnitId == physicalUnit.UnitId)).LocationId);
            var reversal = await db.Events.SingleAsync(e => e.UnitId == recordedUnit.UnitId && e.EventType == UnitEventType.ShipmentReversed);
            Assert.NotNull(reversal.ReversesEventId);
            Assert.Contains(await db.Entitlements.Where(e => e.UnitId == recordedUnit.UnitId).ToListAsync(),
                e => e.ClosedReason == EntitlementClosedReason.VoidedDocumentCorrection);
            Assert.Contains(await db.Entitlements.Where(e => e.UnitId == physicalUnit.UnitId).ToListAsync(),
                e => e.Status != EntitlementStatus.Closed && e.SourceDocumentId == transfer.Document.Id);
        }

        var reversalId = Guid.NewGuid();
        await InvokeAsync<CorrectDocumentSerialsResult>(tenant, Command(reversalId, physical, recorded, correctionId));
        using var check = fixture.ScopeFor(tenant);
        var current = await check.ServiceProvider.GetRequiredService<SerialisationDbContext>().Current
            .Where(c => c.UnitId == recordedUnit.UnitId || c.UnitId == physicalUnit.UnitId).ToDictionaryAsync(c => c.UnitId);
        Assert.Equal(destination, current[recordedUnit.UnitId].LocationId);
        Assert.Equal(warehouse, current[physicalUnit.UnitId].LocationId);
    }

    [DatabaseFact]
    public async Task Case_143_a_wrong_opening_order_is_repointed_with_cover_and_labels_then_reversed()
    {
        var tenant = await fixture.NewTenantAsync("ec143");
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var site = Guid.NewGuid();
        var customer = Guid.NewGuid();
        await InvokeAsync<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        var oldPolicy = await PolicyAsync(tenant, "EC143O");
        var correctedPolicy = await PolicyAsync(tenant, "EC143N");
        var unit = await CommissionAsync(tenant, "EC143", UnitOrigin.Purchased, location: warehouse, item: item);
        var wrongOrder = new DocumentRef("sales_order", Guid.NewGuid());
        var correctedOrder = new DocumentRef("sales_order", Guid.NewGuid());
        var wrongDelivery = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, At.AddDays(1),
            [new DocumentLine(1, item, 1, [new(unit.Serial)])], site, BaseOrder: wrongOrder, CustomerPoRef: "PO-WRONG");
        Documents.Add(wrongDelivery);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(Envelope(wrongDelivery.PostedAt), wrongDelivery.Document));
        var pod = new DeliveryConfirmation(new("pod", Guid.NewGuid()), wrongDelivery.Document,
            wrongDelivery.PostedAt.AddHours(2), site);
        Documents.Add(pod);
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesDeliveryConfirmed(Envelope(pod.DeliveredAt), pod.Document));
        var wrongInvoice = new InvoiceDocument(new("invoice", Guid.NewGuid()), wrongOrder, customer, Entity,
            At.AddDays(2), [new InvoiceLine(1, Guid.NewGuid(), 1, 10m, "GBP", "warranty_service",
                PolicyVersionId: oldPolicy, Target: new(WarrantyTargetRule.SameOrder))], "PO-WRONG", "GB");
        var correctedInvoice = new InvoiceDocument(new("invoice", Guid.NewGuid()), correctedOrder, customer, Entity,
            At.AddDays(3), [new InvoiceLine(1, Guid.NewGuid(), 1, 10m, "GBP", "warranty_service",
                PolicyVersionId: correctedPolicy, Target: new(WarrantyTargetRule.SameOrder))], "PO-CORRECT", "GB");
        Documents.Add(wrongInvoice);
        Documents.Add(correctedInvoice);
        await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit.UnitId,
            CoverageFamilyCode.Base, DateOnly.FromDateTime(wrongInvoice.PostedAt), Entity, wrongInvoice.PostedAt,
            "scenario", new Dictionary<string, DateTime> { [StartTrigger.Invoice] = wrongInvoice.PostedAt }, oldPolicy,
            customer, PartyType.Customer, SourceDocument: wrongInvoice.Document with { Line = 1 }, CustomerPoRef: "PO-WRONG"));
        var selection = await SelectAsync(tenant, [unit.Serial]);
        var orderDesk = new CorrectionCommitter("order-desk", [YojanaRole.OrderDeskSupervisor]);
        var correctionId = Guid.NewGuid();
        CorrectSourceDocument Command(Guid id, DocumentRef previous, DocumentRef corrected, Guid? corrects = null) => new(
            Guid.NewGuid(), selection, previous, corrected, CorrectSourceDocumentReason.ReleasedUnderWrongOrder,
            At.AddDays(corrects is null ? 4 : 5), orderDesk.UserRef, Entity,
            Correction: new(id, CorrectSourceDocumentReason.ReleasedUnderWrongOrder, EvidenceRefs: ["order:143"],
                CorrectsCorrectionId: corrects, InitiatingRole: CorrectionInitiatingRole.CustomerService), CommittedBy: orderDesk);

        var orderDeskActor = new WorkbenchActor(orderDesk.UserRef, orderDesk.Roles);
        var firstCorrection = Command(correctionId, wrongDelivery.Document, correctedOrder);
        var (result, _) = await CommitWorkbenchAsync<CorrectSourceDocumentResult>(tenant,
            WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.WrongOrder, CorrectionCommand.CorrectSourceDocument,
                new(WorkbenchTargetKind.SelectionSet, SelectionId: selection), firstCorrection.Reason, firstCorrection),
            orderDeskActor, orderDeskActor);
        Assert.Equal((1, 1, 1, 1, false), (result.UnitsCorrected, result.EntitlementsSuperseded,
            result.EntitlementsCreated, result.LabelReprintsRequested, result.OwnerChanged));
        await AssertKpiAsync(tenant, correctionId, CorrectionCommand.CorrectSourceDocument, 1);
        var summary = Assert.Single(await PublishedEventSink.WaitForSummaryAsync<OwnershipSourceDocumentCorrected>(
            e => e.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(correctedOrder, summary.CorrectedOpeningDocument);
        Assert.Single(await PublishedEventSink.WaitForAsync<LabelReprintJobRequested>(
            e => e.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));
        Assert.Single(await PublishedEventSink.WaitForAsync<EntitlementSuperseded>(
            e => e.Header.UnitId == unit.UnitId && e.NewPolicyVersionId == correctedPolicy, 1, TimeSpan.FromSeconds(20)));
        Assert.Single(await PublishedEventSink.WaitForAsync<EntitlementCreated>(
            e => e.Header.UnitId == unit.UnitId && e.PolicyVersionId == correctedPolicy, 1, TimeSpan.FromSeconds(20)));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var episode = await db.Episodes.SingleAsync(e => e.UnitId == unit.UnitId && e.Kind == EpisodeKind.Ownership && e.ValidTo == null);
            Assert.Equal(correctedOrder.Id, episode.OpeningDocumentId);
            var cover = await db.Entitlements.Where(e => e.UnitId == unit.UnitId).ToListAsync();
            Assert.Contains(cover, e => e.ClosedReason == EntitlementClosedReason.SourceDocumentCorrected && e.PolicyVersionId == oldPolicy);
            Assert.Contains(cover, e => e.Status != EntitlementStatus.Closed && e.PolicyVersionId == correctedPolicy);
            Assert.Single(await db.Events.Where(e => e.UnitId == unit.UnitId
                && e.EventType == UnitEventType.OwnershipSourceDocumentCorrected).ToListAsync());
        }

        var reversalId = Guid.NewGuid();
        await InvokeAsync<CorrectSourceDocumentResult>(tenant,
            Command(reversalId, correctedOrder, wrongDelivery.Document, correctionId));
        using var check = fixture.ScopeFor(tenant);
        var checkDb = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(wrongDelivery.Document.Id, (await checkDb.Episodes.SingleAsync(e => e.UnitId == unit.UnitId
            && e.Kind == EpisodeKind.Ownership && e.ValidTo == null)).OpeningDocumentId);
        Assert.Equal(correctionId, (await checkDb.Corrections.SingleAsync(c => c.CorrectionId == reversalId)).CorrectsCorrectionId);
        Assert.Contains(await checkDb.Entitlements.Where(e => e.UnitId == unit.UnitId).ToListAsync(),
            e => e.Status != EntitlementStatus.Closed && e.PolicyVersionId == oldPolicy);
    }

    private SerialSchemeDefinition RegisterScheme(Guid item, string prefix, string identifierType = IdentifierType.InternalSerial)
    {
        var code = prefix + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var scheme = new SerialSchemeDefinition(code, identifierType, code, null, "-", 4);
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(scheme);
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(
            InMemoryItemPorts.DefaultProfile(item) with { SerialScheme = code });
        return scheme;
    }

    private async Task<Guid> PolicyAsync(TenantId tenant, string prefix)
    {
        var code = prefix + "-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2026, 1, 1),
            PolicyValidatorTestsTerms.Standard24(), "scenario"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "scenario"));
        return draft.PolicyVersionId;
    }
}
