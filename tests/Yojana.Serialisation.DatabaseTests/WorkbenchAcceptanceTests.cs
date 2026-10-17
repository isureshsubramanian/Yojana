using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Queries;
using Yojana.Serialisation.Workbench;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T17 / ADR-0001 AI 50: every standard workbench route is an executable command path.</summary>
[Collection("serialisation host")]
public sealed class WorkbenchAcceptanceTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("a0000000-0000-0000-0000-000000000017");
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly WorkbenchActor Technician = new("matrix-technician", [YojanaRole.Technician]);

    // Each ADR ticket appears on the route that resolves its request. The five warranty routes without a P3 exit-ticket
    // example are choice rows: the acceptance criterion also requires every standard catalogue option to be executable.
    private static readonly IReadOnlyList<string[]> MatrixRows =
    [
        ["T62/T66a/T71/T74", "correct", CorrectionWorkbenchChoice.WrongDocumentSerials, WorkbenchTargetKind.DocumentLine, CorrectionCommand.CorrectDocumentSerials, UnitEventType.ShipmentReversed],
        ["T31", "mistyped", CorrectionWorkbenchChoice.MistypedOrCrossed, WorkbenchTargetKind.Unit, CorrectionCommand.CorrectIdentifier, UnitEventType.IdentifierCorrected],
        ["T1/T40/T67", "crossed", CorrectionWorkbenchChoice.MistypedOrCrossed, WorkbenchTargetKind.Unit, CorrectionCommand.SwapIdentifiers, UnitEventType.IdentifiersSwapped],
        ["T2/T67", "correct", CorrectionWorkbenchChoice.WrongItem, WorkbenchTargetKind.Unit, CorrectionCommand.CorrectItem, UnitEventType.ItemCorrected],
        ["T70", "correct", CorrectionWorkbenchChoice.WrongOrder, WorkbenchTargetKind.SelectionSet, CorrectionCommand.CorrectSourceDocument, UnitEventType.OwnershipSourceDocumentCorrected],
        ["T40/T68", "move", CorrectionWorkbenchChoice.WrongAllocation, WorkbenchTargetKind.DocumentLine, CorrectionCommand.ReassignAllocation, "unit.allocation_reassigned"],
        ["T3/T45/T53", "void", CorrectionWorkbenchChoice.WrongAllocation, WorkbenchTargetKind.Unit, CorrectionCommand.VoidUnit, UnitEventType.Voided],
        ["T33", "merge", CorrectionWorkbenchChoice.DuplicateUnit, WorkbenchTargetKind.Unit, CorrectionCommand.MergeUnits, UnitEventType.Merged],
        // T51 covers the resulting as-built correction only; its rework lifecycle is deliberately outside P3.
        ["T13/T15/T51", "as_built", CorrectionWorkbenchChoice.WrongComponents, WorkbenchTargetKind.Unit, CorrectionCommand.CorrectAsBuilt, UnitEventType.AsBuiltCorrected],
        ["T16", "late_serialisation", CorrectionWorkbenchChoice.WrongComponents, WorkbenchTargetKind.DocumentLine, CorrectionCommand.LateSerialization, ItemEventType.TrackingModeChanged],
        ["T42/T43", "vendor_list", CorrectionWorkbenchChoice.WrongComponents, WorkbenchTargetKind.DocumentLine, CorrectionCommand.AssignIdentifiers, UnitEventType.IdentifierAssigned],
        ["choice:warranty/amend", "amend", CorrectionWorkbenchChoice.WrongWarranty, WorkbenchTargetKind.SelectionSet, "entitlement.amend", EntitlementEventType.Amended],
        ["T31/warranty", "supersede", CorrectionWorkbenchChoice.WrongWarranty, WorkbenchTargetKind.SelectionSet, "entitlement.supersede", EntitlementEventType.Superseded],
        ["choice:warranty/attach", "attach", CorrectionWorkbenchChoice.WrongWarranty, WorkbenchTargetKind.SelectionSet, "entitlement.attach_extended", EntitlementEventType.ExtendedSold],
        ["choice:warranty/close", "close", CorrectionWorkbenchChoice.WrongWarranty, WorkbenchTargetKind.SelectionSet, "entitlement.close", EntitlementEventType.Closed],
        ["choice:warranty/backfill", "backfill", CorrectionWorkbenchChoice.WrongWarranty, WorkbenchTargetKind.SelectionSet, "entitlement.backfill", EntitlementEventType.Created],
    ];

    public static TheoryData<string, string, string, string, string, string> Matrix
    {
        get
        {
            var data = new TheoryData<string, string, string, string, string, string>();
            foreach (var row in MatrixRows) data.Add(row[0], row[1], row[2], row[3], row[4], row[5]);
            return data;
        }
    }

    private CorrectionScenarioHelper Scenario => new(fixture, Entity, At);
    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
    private InMemoryWarrantyPorts WarrantyPorts => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    [DatabaseTheory]
    [MemberData(nameof(Matrix))]
    public async Task Mapping_table_path_previews_approves_and_commits_the_mapped_command(
        string ticketOrChoice, string narrowingAnswer, string choiceId, string targetKind, string routeCommand, string factType)
    {
        var arranged = await ArrangeAsync(ticketOrChoice, choiceId, narrowingAnswer, routeCommand);
        Assert.Equal(targetKind, arranged.Request.Target.Kind);

        using (var scope = fixture.ScopeFor(arranged.Tenant))
        {
            var catalog = scope.ServiceProvider.GetRequiredService<IWorkbenchChoiceCatalog>();
            var choice = await catalog.GetAsync(choiceId, CancellationToken.None);
            var route = Assert.Single(WorkbenchChoiceRules.Options(choice), option => option.Value == narrowingAnswer);
            Assert.Equal(routeCommand, route.Command);
            Assert.Equal(narrowingAnswer, arranged.Request.Answers.Narrowing!["route"]);
        }

        var (_, preview, committed) = await Scenario.CommitWorkbenchAsync<object>(arranged.Tenant, arranged.Request,
            Technician, arranged.Committer, [.. arranged.Approvers]);
        Assert.True(preview.Change > 0, $"{ticketOrChoice} did not preview a change.");
        Assert.Equal(arranged.Committer.UserRef, committed.CommittedBy);
        Assert.Contains(committed.Approvals, approval => approval.UserRef == arranged.Committer.UserRef);
        Assert.All(committed.Approvals, approval => Assert.NotEqual(committed.InitiatedBy, approval.UserRef));
        Assert.Equal(committed.Approvals.Count, committed.Approvals.Select(approval => approval.UserRef).Distinct().Count());

        await AssertRecordedAsync(arranged.Tenant, arranged.Request.CommandId, routeCommand, factType, choiceId, narrowingAnswer);
    }

    [DatabaseFact]
    public async Task Standard_catalogue_and_every_route_are_in_the_matrix_and_fallback_reaches_refile()
    {
        var tenant = await fixture.NewTenantAsync("wbmeta");
        var unit = await Scenario.CommissionAsync(tenant, "WBMETA");
        using var scope = fixture.ScopeFor(tenant);
        var catalog = scope.ServiceProvider.GetRequiredService<IWorkbenchChoiceCatalog>();
        var shown = await catalog.GetChoicesAsync(CancellationToken.None);
        var matrixRows = MatrixRows.Select(values => new
        {
            Tickets = (string)values[0]!, Route = (string)values[1]!, Choice = (string)values[2]!, Command = (string)values[4]!,
        }).ToList();

        var expectedChoices = shown.Where(choice => !choice.Fallback).Select(choice => choice.Choice).Order(StringComparer.Ordinal);
        Assert.Equal(expectedChoices, matrixRows.Select(row => row.Choice).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var expectedRoutes = shown.Where(choice => !choice.Fallback)
            .SelectMany(choice => WorkbenchChoiceRules.Options(choice).Select(option => (choice.Choice, option.Value, option.Command)))
            .Order().ToList();
        Assert.Equal(expectedRoutes, matrixRows.Select(row => (row.Choice, row.Route, row.Command)).Order().ToList());

        var expectedTickets = new[] { "T1", "T2", "T3", "T13", "T15", "T16", "T31", "T33", "T40", "T42", "T43", "T45", "T51", "T53", "T62", "T66a", "T67", "T68", "T70", "T71", "T74" };
        var coveredTickets = matrixRows.SelectMany(row => row.Tickets.Split('/', StringSplitOptions.RemoveEmptyEntries))
            .Where(value => value.StartsWith('T')).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expectedTickets.Order(StringComparer.Ordinal), coveredTickets);

        Assert.DoesNotContain(shown, choice => choice.Choice == CorrectionWorkbenchChoice.ReplacedOrExchanged);
        Assert.DoesNotContain(shown, choice => choice.Choice == CorrectionWorkbenchChoice.RefurbishedAndReserialised);
        var fallback = Assert.Single(shown, choice => choice.Fallback);
        Assert.Equal("None of these, or not sure", fallback.Label);

        var service = scope.ServiceProvider.GetRequiredService<WorkbenchRequestService>();
        var requestId = Guid.NewGuid();
        var filed = await service.FileUnclassifiedAsync(requestId,
            new(new(WorkbenchTargetKind.Unit, UnitId: unit.UnitId), "The recorded label does not match the physical label",
                [new("photo", "evidence:workbench-meta")]), Technician, CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.Unclassified, filed.Status);

        var correctionId = Guid.NewGuid();
        var corrected = new CorrectIdentifier(Guid.Empty, unit.UnitId, IdentifierType.InternalSerial, unit.Serial + "-R",
            IdentifierCorrectionReason.Typo, At.AddDays(1), "ignored", Entity);
        var refileRequest = CorrectionScenarioHelper.WorkbenchRequest(correctionId, CorrectionWorkbenchChoice.MistypedOrCrossed,
            CorrectionCommand.CorrectIdentifier, new(WorkbenchTargetKind.Unit, UnitId: unit.UnitId),
            IdentifierCorrectionReason.Typo, corrected, "mistyped");
        var administrator = new WorkbenchActor("matrix-refiler", [YojanaRole.SerialAdministrator]);
        var refiled = await service.RefileAsync(requestId, new(refileRequest), administrator, CancellationToken.None);
        Assert.Equal(WorkbenchRequestStatus.Approved, refiled.Status);
        Assert.Equal(WorkbenchRequestStatus.Committed,
            (await service.CommitAsync(requestId, administrator, CancellationToken.None)).Status);
    }

    private Task<ArrangedPath> ArrangeAsync(string ticketOrChoice, string choiceId, string narrowingAnswer, string routeCommand) => routeCommand switch
    {
        CorrectionCommand.CorrectIdentifier => ArrangeCorrectIdentifierAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.SwapIdentifiers => ArrangeSwapAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.CorrectItem => ArrangeCorrectItemAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.CorrectSourceDocument => ArrangeCorrectSourceDocumentAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.ReassignAllocation => ArrangeReassignAllocationAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.VoidUnit => ArrangeVoidAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.MergeUnits => ArrangeMergeAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.CorrectAsBuilt => ArrangeCorrectAsBuiltAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.LateSerialization => ArrangeLateSerializationAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.AssignIdentifiers => ArrangeAssignIdentifiersAsync(ticketOrChoice, choiceId, narrowingAnswer),
        CorrectionCommand.CorrectDocumentSerials => ArrangeCorrectDocumentSerialsAsync(ticketOrChoice, choiceId, narrowingAnswer),
        "entitlement.amend" or "entitlement.supersede" or "entitlement.attach_extended" or "entitlement.close" or "entitlement.backfill"
            => ArrangeEntitlementAsync(ticketOrChoice, choiceId, narrowingAnswer, routeCommand),
        _ => throw new ArgumentOutOfRangeException(nameof(routeCommand), routeCommand, "The matrix route has no scenario."),
    };

    private async Task<ArrangedPath> ArrangeCorrectIdentifierAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17id");
        var unit = await Scenario.CommissionAsync(tenant, Marker(id));
        var correctionId = Guid.NewGuid();
        var command = new CorrectIdentifier(Guid.Empty, unit.UnitId, IdentifierType.InternalSerial, unit.Serial + "-FIX",
            IdentifierCorrectionReason.Typo, At.AddDays(1), "ignored", Entity);
        var committer = Actor("serial", YojanaRole.SerialAdministrator);
        return Path(tenant, correctionId, choice, CorrectionCommand.CorrectIdentifier,
            new(WorkbenchTargetKind.Unit, UnitId: unit.UnitId), command.Reason, command, route, committer, committer);
    }

    private async Task<ArrangedPath> ArrangeSwapAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17swap");
        var item = Guid.NewGuid();
        var first = await Scenario.CommissionAsync(tenant, Marker(id) + "A", item: item);
        var second = await Scenario.CommissionAsync(tenant, Marker(id) + "B", item: item);
        var correctionId = Guid.NewGuid();
        var command = new SwapIdentifiers(Guid.Empty, first.UnitId, second.UnitId, IdentifierType.InternalSerial,
            SwapIdentifiersReason.CrossRecordedAtBuild, At.AddDays(1), "ignored", Entity);
        var committer = Actor("serial", YojanaRole.SerialAdministrator);
        return Path(tenant, correctionId, choice, CorrectionCommand.SwapIdentifiers,
            new(WorkbenchTargetKind.Unit, UnitId: first.UnitId), command.Reason, command, route, committer, committer);
    }

    private async Task<ArrangedPath> ArrangeCorrectItemAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17item");
        var unit = await Scenario.CommissionAsync(tenant, Marker(id), UnitOrigin.Purchased, location: Guid.NewGuid());
        var correctionId = Guid.NewGuid();
        var command = new CorrectItem(Guid.Empty, unit.UnitId, Guid.NewGuid(), CorrectItemReason.WrongItemAtReceipt,
            At.AddDays(1), "ignored", Entity, new(correctionId, CorrectItemReason.WrongItemAtReceipt), new("ignored", []));
        var committer = Actor("inventory", YojanaRole.InventoryController);
        return Path(tenant, correctionId, choice, CorrectionCommand.CorrectItem,
            new(WorkbenchTargetKind.Unit, UnitId: unit.UnitId), command.Reason, command, route, committer, committer);
    }

    private async Task<ArrangedPath> ArrangeVoidAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17void");
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var unit = await Scenario.CommissionAsync(tenant, Marker(id), document: order);
        var correctionId = Guid.NewGuid();
        var command = new VoidUnit(Guid.Empty, unit.UnitId, VoidUnitReason.WorkOrderQuantityReduced, At.AddDays(1),
            "ignored", Entity, order);
        var committer = Actor("production", YojanaRole.ProductionSupervisor);
        return Path(tenant, correctionId, choice, CorrectionCommand.VoidUnit,
            new(WorkbenchTargetKind.Unit, UnitId: unit.UnitId), command.Reason, command, route, committer, committer);
    }

    private async Task<ArrangedPath> ArrangeMergeAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17merge");
        var item = Guid.NewGuid();
        var survivor = await Scenario.CommissionAsync(tenant, Marker(id) + "S", item: item);
        var absorbed = await Scenario.CommissionAsync(tenant, Marker(id) + "A", item: item);
        var correctionId = Guid.NewGuid();
        var command = new MergeUnits(Guid.Empty, survivor.UnitId, absorbed.UnitId, MergeUnitsReason.DuplicateRecord,
            At.AddDays(1), "ignored", Entity);
        var first = Actor("serial-a", YojanaRole.SerialAdministrator);
        var committer = Actor("serial-b", YojanaRole.SerialAdministrator);
        return Path(tenant, correctionId, choice, CorrectionCommand.MergeUnits,
            new(WorkbenchTargetKind.Unit, UnitId: survivor.UnitId), command.Reason, command, route, committer, first, committer);
    }

    private async Task<ArrangedPath> ArrangeCorrectAsBuiltAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17build");
        var parent = await Scenario.CommissionAsync(tenant, Marker(id) + "P");
        var child = await Scenario.CommissionAsync(tenant, Marker(id) + "C");
        var correctionId = Guid.NewGuid();
        var command = new CorrectAsBuilt(Guid.Empty,
            [new(new(UnitId: parent.UnitId), At.AddHours(2),
                [new(AsBuiltCorrectionChange.Add, Installed: new(Unit: new(UnitId: child.UnitId), Slot: "component-1"))])],
            CorrectAsBuiltReason.ComponentNotCaptured, "ignored", Entity);
        var committer = Actor("production", YojanaRole.ProductionSupervisor);
        return Path(tenant, correctionId, choice, CorrectionCommand.CorrectAsBuilt,
            new(WorkbenchTargetKind.Unit, UnitId: parent.UnitId), command.Reason, command, route, committer, committer);
    }

    private async Task<ArrangedPath> ArrangeLateSerializationAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17late");
        var item = Guid.NewGuid();
        var location = Guid.NewGuid();
        var source = new DocumentRef("stocktake", Guid.NewGuid());
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(
            InMemoryItemPorts.DefaultProfile(item) with { TrackingMode = ItemSerialisationProfile.TrackingModes.None });
        fixture.Services.GetRequiredService<InMemoryStockQuantityPort>().Set(item,
            [new(location, LocationKind.Warehouse, 1)]);
        var correctionId = Guid.NewGuid();
        var command = new LateSerialization(Guid.Empty, item, At.AddDays(1), LateSerializationReason.CodingErrorAtItemSetup,
            "ignored", Entity, source,
            [new(Guid.NewGuid(), location, LocationKind.Warehouse, source,
                [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, Marker(id) + "-001")])])], [],
            new(correctionId, LateSerializationReason.CodingErrorAtItemSetup), new("ignored", []));
        var inventory = Actor("inventory", YojanaRole.InventoryController);
        var committer = Actor("item-owner", YojanaRole.ItemMasterOwner);
        return Path(tenant, correctionId, choice, CorrectionCommand.LateSerialization,
            new(WorkbenchTargetKind.DocumentLine, Document: source), command.Reason, command, route, committer, inventory, committer);
    }

    private async Task<ArrangedPath> ArrangeAssignIdentifiersAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17assign");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item, "VSA", IdentifierType.VendorSerial);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        await Scenario.CommissionAsync(tenant, Marker(id), document: order, item: item, identifierType: IdentifierType.Provisional);
        var correctionId = Guid.NewGuid();
        var command = new AssignIdentifiers(Guid.Empty, order, IdentifierAssignmentMode.AllocationOrder,
            [new(scheme.Format(0, 1))], AssignIdentifiersReason.VendorList, At.AddDays(1), "ignored", Entity);
        var committer = Actor("production", YojanaRole.ProductionSupervisor);
        return Path(tenant, correctionId, choice, CorrectionCommand.AssignIdentifiers,
            new(WorkbenchTargetKind.DocumentLine, Document: order), command.Reason, command, route, committer, committer);
    }

    private async Task<ArrangedPath> ArrangeReassignAllocationAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17alloc");
        var item = Guid.NewGuid();
        var scheme = RegisterScheme(item, "RSA", IdentifierType.InternalSerial);
        var fromOrder = new DocumentRef("production_order", Guid.NewGuid());
        var toOrder = new DocumentRef("production_order", Guid.NewGuid());
        Documents.Add(new ProductionOrderAllocation(fromOrder, item, scheme.Code, 1));
        Documents.Add(new ProductionOrderAllocation(toOrder, item, scheme.Code, 1));
        var fromSerial = scheme.Format(0, 1);
        var toSerial = scheme.Format(0, 2);
        await Scenario.InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme.Code, 1,
            At, "planner", Start: fromSerial, ItemId: item, AllocatedTo: fromOrder));
        await Scenario.InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme.Code, 1,
            At, "planner", Start: toSerial, ItemId: item, AllocatedTo: toOrder));
        var correctionId = Guid.NewGuid();
        var command = new ReassignAllocation(Guid.Empty, fromOrder, toOrder, AllocationReassignmentMode.PairwiseExchange,
            SelectionSource.ByPastedList([fromSerial]), SelectionSource.ByPastedList([toSerial]),
            ReassignAllocationReason.AllocationError, At.AddDays(1), "ignored", Entity);
        var committer = Actor("production", YojanaRole.ProductionSupervisor);
        return Path(tenant, correctionId, choice, CorrectionCommand.ReassignAllocation,
            new(WorkbenchTargetKind.DocumentLine, Document: fromOrder), command.Reason, command, route, committer, committer);
    }

    private async Task<ArrangedPath> ArrangeCorrectDocumentSerialsAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17doc");
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var recorded = await Scenario.CommissionAsync(tenant, Marker(id) + "R", UnitOrigin.Purchased, location: warehouse, item: item);
        var physical = await Scenario.CommissionAsync(tenant, Marker(id) + "P", UnitOrigin.Purchased, location: warehouse, item: item);
        var transfer = new TransferDocument(new("transfer", Guid.NewGuid()), At.AddDays(1), warehouse, LocationKind.Warehouse,
            destination, LocationKind.Warehouse, [new DocumentLine(1, item, 1, [new(recorded.Serial)])]);
        Documents.Add(transfer);
        await Scenario.InvokeAsync<ConsumedEventResult>(tenant,
            new InventoryTransferPosted(Envelope(transfer.PostedAt), transfer.Document));
        var recordedSelection = await Scenario.SelectAsync(tenant, [recorded.Serial]);
        var physicalSelection = await Scenario.SelectAsync(tenant, [physical.Serial]);
        var correctionId = Guid.NewGuid();
        var command = new CorrectDocumentSerials(Guid.Empty, transfer.Document, recordedSelection, physicalSelection,
            CorrectDocumentSerialsReason.ShippedWrongUnits, At.AddDays(2), "ignored", Entity);
        var service = Actor("service", YojanaRole.ServiceSupervisor);
        var committer = Actor("shipping", YojanaRole.ShippingSupervisor);
        return Path(tenant, correctionId, choice, CorrectionCommand.CorrectDocumentSerials,
            new(WorkbenchTargetKind.DocumentLine, Document: transfer.Document), command.Reason, command, route, committer, service, committer);
    }

    private async Task<ArrangedPath> ArrangeCorrectSourceDocumentAsync(string id, string choice, string route)
    {
        var tenant = await fixture.NewTenantAsync("wbt17source");
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var site = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var unit = await Scenario.CommissionAsync(tenant, Marker(id), UnitOrigin.Purchased, location: warehouse, item: item);
        var previous = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, At.AddDays(1),
            [new DocumentLine(1, item, 1, [new(unit.Serial)])], site, CustomerPoRef: "PO-WRONG");
        var corrected = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, At.AddDays(2),
            [new DocumentLine(1, item, 1, [new(unit.Serial)])], site, CustomerPoRef: "PO-CORRECT");
        Documents.Add(previous);
        Documents.Add(corrected);
        await Scenario.InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(Envelope(previous.PostedAt), previous.Document));
        var confirmation = new DeliveryConfirmation(new("pod", Guid.NewGuid()), previous.Document,
            previous.PostedAt.AddHours(2), site);
        Documents.Add(confirmation);
        await Scenario.InvokeAsync<ConsumedEventResult>(tenant,
            new SalesDeliveryConfirmed(Envelope(confirmation.DeliveredAt), confirmation.Document));
        var selection = await Scenario.SelectAsync(tenant, [unit.Serial]);
        var correctionId = Guid.NewGuid();
        var command = new CorrectSourceDocument(Guid.Empty, selection, previous.Document, corrected.Document,
            CorrectSourceDocumentReason.ReleasedUnderWrongOrder, At.AddDays(3), "ignored", Entity);
        var committer = Actor("order", YojanaRole.OrderDeskSupervisor);
        return Path(tenant, correctionId, choice, CorrectionCommand.CorrectSourceDocument,
            new(WorkbenchTargetKind.SelectionSet, SelectionId: selection), command.Reason, command, route, committer, committer);
    }

    private async Task<ArrangedPath> ArrangeEntitlementAsync(string id, string choice, string route,
        string routeCommand)
    {
        var tenant = await fixture.NewTenantAsync("wbt17cover");
        var item = Guid.NewGuid();
        var obligor = Guid.NewGuid();
        await Scenario.InvokeAsync<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        var standard = await PublishPolicyAsync(tenant, CoverageFamilyCode.Base, EntitlementType.Manufacturer,
            BaseTerms(24, AssuranceOrService.Assurance));
        var unit = await Scenario.CommissionAsync(tenant, Marker(id), UnitOrigin.Purchased, item: item);
        var selection = await Scenario.SelectAsync(tenant, [unit.Serial]);
        var source = new DocumentRef("invoice", Guid.NewGuid(), 1);

        if (routeCommand == "entitlement.backfill")
        {
            WarrantyPorts.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, standard.PolicyCode, false));
            await Scenario.InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit.UnitId,
                Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale, At.AddDays(1), "sales", Entity,
                EpisodeSource.Delivery, Document: new DocumentRef("delivery", Guid.NewGuid(), 1)));
            var extra = new Dictionary<string, string>
            {
                ["family"] = CoverageFamilyCode.Base,
                ["obligor_entity_id"] = obligor.ToString(),
                ["evidence_refs"] = "delivery:matrix",
                ["finance_review_ref"] = "finance:matrix",
            };
            return EntitlementPath(tenant, choice, route, routeCommand, selection, BackfillReason.HistoricalGap,
                new(At.AddDays(2), "ignored", Entity, source, Extra: extra), YojanaRole.ServiceManager);
        }

        var created = await Scenario.InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(),
            unit.UnitId, CoverageFamilyCode.Base, new DateOnly(2026, 9, 1), obligor, At, "setup",
            new Dictionary<string, DateTime> { [StartTrigger.Deliver] = At }, standard.PolicyVersionId,
            OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, SourceDocument: source));
        var common = new Dictionary<string, string> { ["family"] = CoverageFamilyCode.Base };

        switch (routeCommand)
        {
            case "entitlement.amend":
                common["new_end"] = created.EndDate!.Value.AddDays(10).ToString("yyyy-MM-dd");
                return EntitlementPath(tenant, choice, route, routeCommand, selection, AmendmentReason.Correction,
                    new(At.AddDays(2), "ignored", Entity, source, Extra: common), YojanaRole.ServiceDesk);
            case "entitlement.supersede":
            {
                var upgrade = await PublishPolicyAsync(tenant, CoverageFamilyCode.Base, EntitlementType.Manufacturer,
                    BaseTerms(36, AssuranceOrService.Assurance));
                common["policy_version_id"] = upgrade.PolicyVersionId.ToString();
                common["effective_date"] = "2027-01-01";
                return EntitlementPath(tenant, choice, route, routeCommand, selection, AmendmentReason.Correction,
                    new(At.AddDays(2), "ignored", Entity, source, Extra: common), YojanaRole.ServiceManager);
            }
            case "entitlement.attach_extended":
            {
                const string family = "extended_term";
                await Scenario.InvokeAsync<CoverageFamilyRow>(tenant,
                    new DefineCoverageFamily(family, "Extended term", true, AssuranceOrService.Service));
                var plan = await PublishPolicyAsync(tenant, family, EntitlementType.Extended,
                    BaseTerms(12, AssuranceOrService.Service) with
                    {
                        Start = new(StartTrigger.PlanPurchase, [], null),
                    });
                var extra = new Dictionary<string, string>
                {
                    ["family"] = family,
                    ["policy_version_id"] = plan.PolicyVersionId.ToString(),
                    ["contract_date"] = "2026-09-01",
                    ["obligor_entity_id"] = obligor.ToString(),
                    ["price"] = "120.00",
                    ["currency"] = "GBP",
                };
                return EntitlementPath(tenant, choice, route, routeCommand, selection, AmendmentReason.Purchase,
                    new(At.AddDays(2), "ignored", Entity, source, Extra: extra), YojanaRole.ServiceDesk);
            }
            case "entitlement.close":
                common["effective_date"] = "2026-09-02";
                return EntitlementPath(tenant, choice, route, routeCommand, selection, EntitlementClosedReason.IncludedInError,
                    new(At.AddDays(2), "ignored", Entity, source, Extra: common), YojanaRole.ServiceDesk);
            default:
                throw new ArgumentOutOfRangeException(nameof(routeCommand));
        }
    }

    private ArrangedPath EntitlementPath(TenantId tenant, string choice, string route, string command,
        Guid selectionId, string reason, BulkOperationArguments arguments, string role)
    {
        var correctionId = Guid.NewGuid();
        var committer = Actor(role.Replace('_', '-'), role);
        return Path(tenant, correctionId, choice, command,
            new(WorkbenchTargetKind.SelectionSet, SelectionId: selectionId), reason, arguments, route, committer, committer);
    }

    private async Task<PolicyDraftResult> PublishPolicyAsync(TenantId tenant, string family, string type, PolicyTerms terms)
    {
        var code = "WB-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var draft = await Scenario.InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            family, type, new DateOnly(2026, 1, 1), terms, "matrix"));
        return await Scenario.InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "matrix"));
    }

    private static PolicyTerms BaseTerms(int months, string accountingTag) => new(
        new DurationRule(months, DurationRule.Months), DurationBasis.Time, [],
        new StartRule(StartTrigger.Deliver, [new(StartTrigger.Invoice, 0)], 90),
        new CoverageTerms(100, 100, 0, "return_to_base"), [], ["WORLD"], Transferability.Yes, "full",
        null, [], null, accountingTag, PolicyTerms.TimeZoneUtc);

    private SerialSchemeDefinition RegisterScheme(Guid item, string prefix, string identifierType)
    {
        var code = prefix + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var scheme = new SerialSchemeDefinition(code, identifierType, code, null, "-", 4);
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(scheme);
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(
            InMemoryItemPorts.DefaultProfile(item) with { SerialScheme = code });
        return scheme;
    }

    private static ArrangedPath Path<T>(TenantId tenant, Guid correctionId, string choice, string command,
        WorkbenchTarget target, string reason, T arguments, string route, WorkbenchActor committer,
        params WorkbenchActor[] approvers) where T : class =>
        new(tenant, CorrectionScenarioHelper.WorkbenchRequest(correctionId, choice, command, target, reason, arguments, route),
            committer, approvers);

    private static WorkbenchActor Actor(string name, params string[] roles) =>
        new("matrix-" + name + "-" + Guid.NewGuid().ToString("N")[..6], roles);

    private static ThinEnvelope Envelope(DateTime at) => new(Guid.NewGuid(), Entity, at, "matrix");

    private static string Marker(string id)
    {
        var letters = new string(id.Where(char.IsLetterOrDigit).Take(8).ToArray()).ToUpperInvariant();
        return "WB" + letters + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
    }

    private async Task AssertRecordedAsync(TenantId tenant, Guid correctionId, string routeCommand, string factType,
        string choiceId, string narrowingAnswer)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var correction = await db.Corrections.AsNoTracking().SingleAsync(row => row.CorrectionId == correctionId);
        Assert.Equal(WorkbenchChoiceRules.CorrectionCommandForRoute(routeCommand), correction.Command);
        Assert.Equal(CorrectionInitiatedVia.Workbench, correction.InitiatedVia);
        var route = await scope.ServiceProvider.GetRequiredService<IWorkbenchChoiceCatalog>().RouteAsync(choiceId, routeCommand, CancellationToken.None);
        Assert.Equal(route.RootCause, correction.RootCauseCategory);
        var kpi = await scope.ServiceProvider.GetRequiredService<CorrectionKpiQueries>().QueryAsync(
            new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), CorrectionKpiBreakdown.RootCause, null, CancellationToken.None);
        var rootCause = Assert.Single(kpi.Rows, row => row.Dimension == route.RootCause.ToString());
        Assert.Equal(1, rootCause.Corrections);

        if (factType == ItemEventType.TrackingModeChanged)
        {
            Assert.True(await db.ItemTrackingModeChanges.AsNoTracking().AnyAsync(row => row.CorrectionId == correctionId));
            return;
        }
        if (factType == "unit.allocation_reassigned")
        {
            Assert.True(await db.AllocationReassignments.AsNoTracking().AnyAsync(fact => fact.CorrectionId == correctionId));
            return;
        }

        var events = await db.Events.AsNoTracking().Where(row => row.EventType == factType).ToListAsync();
        Assert.Contains(events, fact => CorrectionId(fact.Attributes) == correctionId);
        Assert.False(string.IsNullOrWhiteSpace(narrowingAnswer));
    }

    private static Guid? CorrectionId(string? attributes)
    {
        if (attributes is null) return null;
        using var document = JsonDocument.Parse(attributes);
        return document.RootElement.TryGetProperty("correction_id", out var value) && value.TryGetGuid(out var id) ? id : null;
    }

    private sealed record ArrangedPath(TenantId Tenant, WorkbenchPreviewRequest Request,
        WorkbenchActor Committer, IReadOnlyList<WorkbenchActor> Approvers);
}
