using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests.Workbench;

/// <summary>P3 exit audit: ticket facts that the route matrix alone did not reproduce.</summary>
[Collection("serialisation host")]
public sealed class P3ExitTicketTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly WorkbenchActor Technician = new("exit-technician", [YojanaRole.Technician]);
    private static readonly WorkbenchActor SerialAdministrator = new("exit-serial-administrator", [YojanaRole.SerialAdministrator]);
    private static readonly WorkbenchActor ProductionSupervisor = new("exit-production-supervisor", [YojanaRole.ProductionSupervisor]);
    private static readonly WorkbenchActor ShippingSupervisor = new("exit-shipping-supervisor", [YojanaRole.ShippingSupervisor, YojanaRole.ServiceSupervisor]);

    private CorrectionScenarioHelper Scenario => new(fixture, Entity, At);
    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();

    [DatabaseFact]
    public async Task Ticket_T1_swaps_crossed_serials_and_requests_both_asset_tags_through_the_workbench()
    {
        var tenant = await fixture.NewTenantAsync("exit-t1");
        var item = Guid.NewGuid();
        var first = await Scenario.CommissionAsync(tenant, Marker("T1A"), item: item);
        var second = await Scenario.CommissionAsync(tenant, Marker("T1B"), item: item);
        await Scenario.InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), first.UnitId,
            new(IdentifierType.CustomerAssetTag, "TAG-" + Marker("T1A")), At.AddMinutes(1), "line", Entity));
        await Scenario.InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), second.UnitId,
            new(IdentifierType.CustomerAssetTag, "TAG-" + Marker("T1B")), At.AddMinutes(1), "line", Entity));

        var correctionId = Guid.NewGuid();
        var command = new SwapIdentifiers(Guid.NewGuid(), first.UnitId, second.UnitId, IdentifierType.InternalSerial,
            SwapIdentifiersReason.CrossRecordedAtBuild, At.AddDays(1), SerialAdministrator.UserRef, Entity);
        var request = Request(correctionId, CorrectionWorkbenchChoice.MistypedOrCrossed,
            CorrectionCommand.SwapIdentifiers, new(WorkbenchTargetKind.Unit, UnitId: first.UnitId), command.Reason,
            command, "crossed");

        var (result, preview, committed) = await Scenario.CommitWorkbenchAsync<SwapIdentifiersResult>(tenant, request,
            Technician, SerialAdministrator, SerialAdministrator);

        Assert.Equal(2, preview.PrintJobs.Count);
        Assert.Equal(2, result.AssetTagPrintJobsRequested);
        Assert.Equal(CorrectionInitiatedVia.Workbench, await InitiatedViaAsync(tenant, correctionId));
        Assert.Equal(WorkbenchRequestStatus.Committed, committed.Status);
    }

    [DatabaseFact]
    public async Task Ticket_T13_adds_the_missing_component_to_both_units_through_one_workbench_command()
    {
        var tenant = await fixture.NewTenantAsync("exit-t13");
        var parentOne = await Scenario.CommissionAsync(tenant, Marker("T13P1"));
        var parentTwo = await Scenario.CommissionAsync(tenant, Marker("T13P2"));
        var childOne = await Scenario.CommissionAsync(tenant, Marker("T13C1"));
        var childTwo = await Scenario.CommissionAsync(tenant, Marker("T13C2"));
        var correctionId = Guid.NewGuid();
        var command = new CorrectAsBuilt(Guid.NewGuid(),
        [
            new(new(UnitId: parentOne.UnitId), At.AddHours(2),
                [new(AsBuiltCorrectionChange.Add, Installed: new(Unit: new(UnitId: childOne.UnitId), Slot: "drive-1"))]),
            new(new(UnitId: parentTwo.UnitId), At.AddHours(2),
                [new(AsBuiltCorrectionChange.Add, Installed: new(Unit: new(UnitId: childTwo.UnitId), Slot: "drive-1"))]),
        ], CorrectAsBuiltReason.ComponentNotCaptured, ProductionSupervisor.UserRef, Entity);
        var request = Request(correctionId, CorrectionWorkbenchChoice.WrongComponents,
            CorrectionCommand.CorrectAsBuilt, new(WorkbenchTargetKind.Unit, UnitId: parentOne.UnitId), command.Reason,
            command, "as_built");

        var (result, preview, _) = await Scenario.CommitWorkbenchAsync<CorrectAsBuiltResult>(tenant, request,
            Technician, ProductionSupervisor, ProductionSupervisor);

        Assert.Equal((2, 2), (preview.Change, result.ParentCount));
        using var scope = fixture.ScopeFor(tenant);
        var links = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().AssemblyLinks
            .Where(link => (link.ParentUnitId == parentOne.UnitId || link.ParentUnitId == parentTwo.UnitId)
                && link.RemovedAt == null).ToListAsync();
        Assert.Equal(2, links.Count);
        Assert.Equal(CorrectionInitiatedVia.Workbench, await InitiatedViaAsync(tenant, correctionId));
    }

    [DatabaseFact]
    public async Task Ticket_T15_routes_a_reported_missing_required_serial_to_the_as_built_command()
    {
        var tenant = await fixture.NewTenantAsync("exit-t15");
        var parentItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().RegisterBom(parentItem,
            [new ExpectedComponent("drive-1", componentItem, "DRIVE", 1)]);
        var parent = await Scenario.CommissionAsync(tenant, Marker("T15P"), item: parentItem);
        var child = await Scenario.CommissionAsync(tenant, Marker("T15C"), item: componentItem);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var before = await scope.ServiceProvider.GetRequiredService<AsBuiltCompleteness>()
                .CheckAsync(parent.UnitId, parentItem, DateOnly.FromDateTime(At), CancellationToken.None);
            Assert.Equal(1, before.MissingRequiredComponentSerials);
        }

        var correctionId = Guid.NewGuid();
        var command = new CorrectAsBuilt(Guid.NewGuid(),
            [new(new(UnitId: parent.UnitId), At.AddHours(2),
                [new(AsBuiltCorrectionChange.Add, Installed: new(Unit: new(UnitId: child.UnitId), Slot: "drive-1"))])],
            CorrectAsBuiltReason.ComponentNotCaptured, ProductionSupervisor.UserRef, Entity);
        var request = Request(correctionId, CorrectionWorkbenchChoice.WrongComponents,
            CorrectionCommand.CorrectAsBuilt, new(WorkbenchTargetKind.Unit, UnitId: parent.UnitId), command.Reason,
            command, "as_built");
        await Scenario.CommitWorkbenchAsync<CorrectAsBuiltResult>(tenant, request,
            Technician, ProductionSupervisor, ProductionSupervisor);

        using var check = fixture.ScopeFor(tenant);
        var after = await check.ServiceProvider.GetRequiredService<AsBuiltCompleteness>()
            .CheckAsync(parent.UnitId, parentItem, DateOnly.FromDateTime(At.AddDays(1)), CancellationToken.None);
        Assert.True(after.IsComplete);
        Assert.Equal(CorrectionInitiatedVia.Workbench, await InitiatedViaAsync(tenant, correctionId));
    }

    [DatabaseFact]
    public async Task Ticket_T31_relabels_the_serial_with_the_R_prefix_through_the_workbench()
    {
        var tenant = await fixture.NewTenantAsync("exit-t31");
        var unit = await Scenario.CommissionAsync(tenant, Marker("T31"));
        var correctionId = Guid.NewGuid();
        var replacement = "R" + unit.Serial;
        var command = new CorrectIdentifier(Guid.NewGuid(), unit.UnitId, IdentifierType.InternalSerial, replacement,
            IdentifierCorrectionReason.Relabel, At.AddDays(1), SerialAdministrator.UserRef, Entity);
        var request = Request(correctionId, CorrectionWorkbenchChoice.MistypedOrCrossed,
            CorrectionCommand.CorrectIdentifier, new(WorkbenchTargetKind.Unit, UnitId: unit.UnitId), command.Reason,
            command, "mistyped");

        await Scenario.CommitWorkbenchAsync<CorrectIdentifierResult>(tenant, request,
            Technician, SerialAdministrator, SerialAdministrator);

        using var scope = fixture.ScopeFor(tenant);
        var current = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Identifiers
            .SingleAsync(row => row.UnitId == unit.UnitId && row.Type == IdentifierType.InternalSerial && row.ValidTo == null);
        Assert.Equal(replacement, current.RawValue);
        Assert.Equal(IdentifierCorrectionReason.Relabel, (await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .Corrections.SingleAsync(row => row.CorrectionId == correctionId)).ReasonCode);
        Assert.Equal(CorrectionInitiatedVia.Workbench, await InitiatedViaAsync(tenant, correctionId));
    }

    [DatabaseFact]
    public async Task Ticket_T45_voids_a_cancelled_order_unit_without_deleting_it()
    {
        var tenant = await fixture.NewTenantAsync("exit-t45");
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var unit = await Scenario.CommissionAsync(tenant, Marker("T45"), document: order);
        var correctionId = Guid.NewGuid();
        var command = new VoidUnit(Guid.NewGuid(), unit.UnitId, VoidUnitReason.WorkOrderCancelled, At.AddDays(1),
            ProductionSupervisor.UserRef, Entity, order);
        var request = Request(correctionId, CorrectionWorkbenchChoice.WrongAllocation,
            CorrectionCommand.VoidUnit, new(WorkbenchTargetKind.Unit, UnitId: unit.UnitId), command.Reason,
            command, "void");

        var (result, _, _) = await Scenario.CommitWorkbenchAsync<VoidUnitResult>(tenant, request,
            Technician, ProductionSupervisor, ProductionSupervisor);

        Assert.Equal(LifecycleStatus.Void, result.LifecycleStatus);
        using var scope = fixture.ScopeFor(tenant);
        var stored = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Units
            .SingleAsync(row => row.UnitId == unit.UnitId);
        Assert.Equal(LifecycleStatus.Void, stored.LifecycleStatus);
        Assert.Equal(CorrectionInitiatedVia.Workbench, await InitiatedViaAsync(tenant, correctionId));
    }

    [DatabaseFact]
    public async Task Ticket_T66a_replaces_only_the_copied_serial_and_keeps_the_other_document_unit()
    {
        var tenant = await fixture.NewTenantAsync("exit-t66a");
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var wrong = await Scenario.CommissionAsync(tenant, Marker("T66W"), UnitOrigin.Purchased, location: warehouse, item: item);
        var unchanged = await Scenario.CommissionAsync(tenant, Marker("T66U"), UnitOrigin.Purchased, location: warehouse, item: item);
        var physical = await Scenario.CommissionAsync(tenant, Marker("T66P"), UnitOrigin.Purchased, location: warehouse, item: item);
        var transfer = new TransferDocument(new("transfer", Guid.NewGuid()), At.AddDays(1), warehouse,
            LocationKind.Warehouse, destination, LocationKind.Warehouse,
            [new DocumentLine(1, item, 2, [new(wrong.Serial), new(unchanged.Serial)])]);
        Documents.Add(transfer);
        await Scenario.InvokeAsync<ConsumedEventResult>(tenant,
            new InventoryTransferPosted(Envelope(transfer.PostedAt), transfer.Document));
        var recorded = await Scenario.SelectAsync(tenant, [wrong.Serial]);
        var actual = await Scenario.SelectAsync(tenant, [physical.Serial]);
        var correctionId = Guid.NewGuid();
        var command = new CorrectDocumentSerials(Guid.NewGuid(), transfer.Document, recorded, actual,
            CorrectDocumentSerialsReason.CopiedSerialsWithoutScan, At.AddDays(2), ShippingSupervisor.UserRef, Entity);
        var request = Request(correctionId, CorrectionWorkbenchChoice.WrongDocumentSerials,
            CorrectionCommand.CorrectDocumentSerials, new(WorkbenchTargetKind.DocumentLine, Document: transfer.Document),
            command.Reason, command);

        var (result, _, _) = await Scenario.CommitWorkbenchAsync<CorrectDocumentSerialsResult>(tenant, request,
            Technician, ShippingSupervisor, ShippingSupervisor);

        Assert.Equal((1, 1, false), (result.Reversed, result.Shipped, result.WholeListReplacement));
        using var scope = fixture.ScopeFor(tenant);
        var current = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Current
            .Where(row => row.UnitId == wrong.UnitId || row.UnitId == unchanged.UnitId || row.UnitId == physical.UnitId)
            .ToDictionaryAsync(row => row.UnitId);
        Assert.Equal(warehouse, current[wrong.UnitId].LocationId);
        Assert.Equal(destination, current[unchanged.UnitId].LocationId);
        Assert.Equal(destination, current[physical.UnitId].LocationId);
        Assert.Equal(CorrectionInitiatedVia.Workbench, await InitiatedViaAsync(tenant, correctionId));
    }

    [DatabaseFact]
    public async Task Ticket_T67_reverses_the_earlier_swap_through_a_later_workbench_correction()
    {
        var tenant = await fixture.NewTenantAsync("exit-t67");
        var item = Guid.NewGuid();
        var first = await Scenario.CommissionAsync(tenant, Marker("T67A"), item: item);
        var second = await Scenario.CommissionAsync(tenant, Marker("T67B"), item: item);
        var earlierId = Guid.NewGuid();
        await Scenario.InvokeAsync<SwapIdentifiersResult>(tenant, new SwapIdentifiers(Guid.NewGuid(), first.UnitId,
            second.UnitId, IdentifierType.InternalSerial, SwapIdentifiersReason.CrossRecordedAtBuild, At.AddDays(1),
            SerialAdministrator.UserRef, Entity,
            Correction: new(earlierId, SwapIdentifiersReason.CrossRecordedAtBuild,
                InitiatingRole: CorrectionInitiatingRole.SerialAdministrator),
            CommittedBy: new(SerialAdministrator.UserRef, SerialAdministrator.Roles)));

        var reversalId = Guid.NewGuid();
        var reversal = new SwapIdentifiers(Guid.NewGuid(), first.UnitId, second.UnitId,
            IdentifierType.InternalSerial, SwapIdentifiersReason.LabelAppliedToWrongUnit, At.AddDays(2),
            SerialAdministrator.UserRef, Entity,
            Correction: new(reversalId, SwapIdentifiersReason.LabelAppliedToWrongUnit,
                CorrectsCorrectionId: earlierId, InitiatingRole: CorrectionInitiatingRole.SerialAdministrator));
        var request = Request(reversalId, CorrectionWorkbenchChoice.MistypedOrCrossed,
            CorrectionCommand.SwapIdentifiers, new(WorkbenchTargetKind.Unit, UnitId: first.UnitId), reversal.Reason,
            reversal, "crossed");

        await Scenario.CommitWorkbenchAsync<SwapIdentifiersResult>(tenant, request,
            Technician, SerialAdministrator, SerialAdministrator);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(first.Serial.Replace("-", "", StringComparison.Ordinal),
            (await db.Identifiers.SingleAsync(row => row.UnitId == first.UnitId
                && row.Type == IdentifierType.InternalSerial && row.ValidTo == null)).NormalisedValue);
        var recorded = await db.Corrections.SingleAsync(row => row.CorrectionId == reversalId);
        Assert.Equal(earlierId, recorded.CorrectsCorrectionId);
        Assert.Equal(CorrectionInitiatedVia.Workbench, recorded.InitiatedVia);
    }

    [DatabaseFact]
    public async Task Ticket_T68_reassigns_three_defective_allocations_to_three_replacements()
    {
        var tenant = await fixture.NewTenantAsync("exit-t68");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item, "T68");
        var originalOrder = new DocumentRef("production_order", Guid.NewGuid());
        var replacementOrder = new DocumentRef("production_order", Guid.NewGuid());
        Documents.Add(new ProductionOrderAllocation(originalOrder, item, scheme.Code, 3));
        Documents.Add(new ProductionOrderAllocation(replacementOrder, item, scheme.Code, 3));
        await Scenario.InvokeAsync<AllocateSerialRangeResult>(tenant,
            new AllocateSerialRange(Guid.NewGuid(), scheme.Code, 3, At, "planner", Start: scheme.Format(0, 1),
                ItemId: item, AllocatedTo: originalOrder));
        await Scenario.InvokeAsync<AllocateSerialRangeResult>(tenant,
            new AllocateSerialRange(Guid.NewGuid(), scheme.Code, 3, At, "planner", Start: scheme.Format(0, 11),
                ItemId: item, AllocatedTo: replacementOrder));
        // A sequential range returns its bounds, not its serials, so the three pairs are listed from the scheme.
        string[] originalSerials = [scheme.Format(0, 1), scheme.Format(0, 2), scheme.Format(0, 3)];
        string[] replacementSerials = [scheme.Format(0, 11), scheme.Format(0, 12), scheme.Format(0, 13)];
        var correctionId = Guid.NewGuid();
        var command = new ReassignAllocation(correctionId, originalOrder, replacementOrder,
            AllocationReassignmentMode.PairwiseExchange, SelectionSource.ByPastedList(originalSerials),
            SelectionSource.ByPastedList(replacementSerials), ReassignAllocationReason.AllocationError,
            At.AddDays(1), ProductionSupervisor.UserRef, Entity);
        var request = Request(correctionId, CorrectionWorkbenchChoice.WrongAllocation,
            CorrectionCommand.ReassignAllocation, new(WorkbenchTargetKind.DocumentLine, Document: originalOrder),
            command.Reason, command, "move");

        await Scenario.CommitWorkbenchAsync<ReassignAllocationResult>(tenant, request,
            Technician, ProductionSupervisor, ProductionSupervisor);

        using var scope = fixture.ScopeFor(tenant);
        var overrides = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().AllocationOverrides
            .Where(row => originalSerials.Contains(row.SerialValue) || replacementSerials.Contains(row.SerialValue))
            .ToListAsync();
        Assert.Equal(6, overrides.Count);
        Assert.All(overrides.Where(row => originalSerials.Contains(row.SerialValue)),
            row => Assert.Equal(replacementOrder.Id, row.ProductionOrderId));
        Assert.All(overrides.Where(row => replacementSerials.Contains(row.SerialValue)),
            row => Assert.Equal(originalOrder.Id, row.ProductionOrderId));
        Assert.Equal(CorrectionInitiatedVia.Workbench, await InitiatedViaAsync(tenant, correctionId));
    }

    [DatabaseFact]
    public async Task Ticket_T74_corrects_the_erroneous_document_then_reships_the_same_unit_without_deleting_it()
    {
        var tenant = await fixture.NewTenantAsync("exit-t74");
        var warehouse = Guid.NewGuid();
        var firstSite = Guid.NewGuid();
        var secondSite = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var unit = await Scenario.CommissionAsync(tenant, Marker("T74"), UnitOrigin.Purchased, location: warehouse);
        var erroneous = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, At.AddDays(1),
            [new DocumentLine(1, unit.ItemId, 1, [new(unit.Serial)])], firstSite);
        Documents.Add(erroneous);
        await Scenario.InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(Envelope(erroneous.PostedAt), erroneous.Document));
        var recorded = await Scenario.SelectAsync(tenant, [unit.Serial]);
        var none = await Scenario.SelectAsync(tenant, []);
        var correctionId = Guid.NewGuid();
        var command = new CorrectDocumentSerials(Guid.NewGuid(), erroneous.Document, recorded, none,
            CorrectDocumentSerialsReason.ReturnedNeverDelivered, At.AddDays(2), ShippingSupervisor.UserRef, Entity);
        var request = Request(correctionId, CorrectionWorkbenchChoice.WrongDocumentSerials,
            CorrectionCommand.CorrectDocumentSerials, new(WorkbenchTargetKind.DocumentLine, Document: erroneous.Document),
            command.Reason, command);

        await Scenario.CommitWorkbenchAsync<CorrectDocumentSerialsResult>(tenant, request,
            Technician, ShippingSupervisor, ShippingSupervisor);

        var reshipment = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, At.AddDays(3),
            [new DocumentLine(1, unit.ItemId, 1, [new(unit.Serial)])], secondSite);
        Documents.Add(reshipment);
        var reshipped = await Scenario.InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(Envelope(reshipment.PostedAt), reshipment.Document));
        Assert.Equal((1, 0), (reshipped.UnitsAffected, reshipped.Exceptions.Count));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Single(await db.Units.Where(row => row.UnitId == unit.UnitId).ToListAsync());
        Assert.Equal(2, await db.Events.CountAsync(row => row.UnitId == unit.UnitId && row.EventType == UnitEventType.Shipped));
        Assert.Single(await db.Events.Where(row => row.UnitId == unit.UnitId && row.EventType == UnitEventType.ShipmentReversed).ToListAsync());
        Assert.Equal(CorrectionInitiatedVia.Workbench, await InitiatedViaAsync(tenant, correctionId));
    }

    private async Task<string> InitiatedViaAsync(TenantId tenant, Guid correctionId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Corrections
            .Where(row => row.CorrectionId == correctionId).Select(row => row.InitiatedVia).SingleAsync();
    }

    private SerialSchemeDefinition RegisterScheme(Guid itemId, string prefix)
    {
        var code = prefix + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var scheme = new SerialSchemeDefinition(code, IdentifierType.InternalSerial, code, null, "-", 4);
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(scheme);
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(
            InMemoryItemPorts.DefaultProfile(itemId) with { SerialScheme = code });
        return scheme;
    }

    private static WorkbenchPreviewRequest Request<T>(Guid correctionId, string choice, string command,
        WorkbenchTarget target, string reason, T arguments, string route = "route") where T : class =>
        CorrectionScenarioHelper.WorkbenchRequest(correctionId, choice, command, target, reason, arguments, route);

    private static ThinEnvelope Envelope(DateTime at) => new(Guid.NewGuid(), Entity, at, "p3-exit");

    private static string Marker(string ticket) =>
        ticket + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
}
