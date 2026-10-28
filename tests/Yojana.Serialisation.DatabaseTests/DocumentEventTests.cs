using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T15: thin consumed events become unit facts through the publisher's query ports; replays are duplicates; 3PL serials arrive later.</summary>
[Collection("serialisation host")]
public sealed class DocumentEventTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime T0 = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private InMemoryDocumentPorts Ports => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();

    private static ThinEnvelope Env(DateTime at) => new(Guid.NewGuid(), Entity, at, "cx01-test");

    private static DocumentLine Line(int no, Guid item, Guid? location, params string[] serials) => new(no, item, serials.Length, serials.Select(s => new DocumentSerial(s)).ToList(), location, location is null ? null : LocationKind.Warehouse);

    [DatabaseFact]
    public async Task A_receipt_commissions_units_and_a_replay_of_the_same_document_changes_nothing()
    {
        var tenant = await fixture.NewTenantAsync("dr");
        var p = "DR" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var leeds = Guid.NewGuid();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0, [Line(3, item, leeds, $"{p}-001", $"{p}-002", $"{p}-003")]);
        Ports.Add(receipt);
        var first = await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), receipt.Document));
        Assert.Equal(3, first.UnitsAffected);
        Assert.Empty(first.Exceptions);
        var again = await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), receipt.Document));
        Assert.Equal(0, again.UnitsAffected);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var units = await db.Units.Where(u => u.Origin == UnitOrigin.Purchased && u.CreatedEventId != Guid.Empty).Join(db.Identifiers.Where(i => i.NormalisedValue.StartsWith(p)), u => u.UnitId, i => i.UnitId, (u, i) => u).ToListAsync();
        Assert.Equal(3, units.Count);
        Assert.All(units, u => Assert.Equal(LifecycleStatus.Released, u.LifecycleStatus));
        Assert.Equal(3, await db.Current.CountAsync(c => units.Select(u => u.UnitId).Contains(c.UnitId) && c.LocationId == leeds && c.LocationKind == LocationKind.Warehouse));
    }

    [DatabaseFact]
    public async Task Cancelling_a_receipt_reverses_untouched_units_and_the_serial_commissions_a_fresh_unit()
    {
        var tenant = await fixture.NewTenantAsync("drcancel");
        var serial = "SRV-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var original = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0,
            [Line(1, item, warehouse, serial)]);
        Ports.Add(original);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), original.Document));

        var first = await InvokeAsync<ConsumedEventResult>(tenant,
            new DocumentCancellationPosted(Env(T0.AddDays(1)), original.Document));
        var replay = await InvokeAsync<ConsumedEventResult>(tenant,
            new DocumentCancellationPosted(Env(T0.AddDays(1)), original.Document));

        Assert.Equal(1, first.UnitsAffected);
        Assert.Empty(first.Exceptions);
        Assert.True(replay.Duplicate);
        using (var check = fixture.ScopeFor(tenant))
        {
            var db = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var reversed = await db.Identifiers.Where(x => x.RawValue == serial).Select(x => x.UnitId).SingleAsync();
            Assert.Equal(LifecycleStatus.Reversed,
                (await db.Units.SingleAsync(x => x.UnitId == reversed)).LifecycleStatus);
            var current = await db.Current.SingleAsync(x => x.UnitId == reversed);
            Assert.Equal((LifecycleStatus.Reversed, UnitDisposition.Inactive, null, null),
                (current.LifecycleStatus, current.Disposition, current.LocationId, current.OpenCustodyEpisodeId));
            var identifier = await db.Identifiers.SingleAsync(x => x.UnitId == reversed);
            Assert.Equal(T0.AddDays(1), identifier.ValidTo);
            Assert.Equal(EpisodeReason.DocumentCorrection, identifier.CorrectionReason);
            var episode = await db.Episodes.SingleAsync(x => x.UnitId == reversed && x.Kind == EpisodeKind.Custody);
            Assert.Equal(T0.AddDays(1), episode.ValidTo);
            var reversal = await db.Events.SingleAsync(x => x.UnitId == reversed
                && x.EventType == UnitEventType.ReceiptReversed);
            Assert.NotNull(reversal.ReversesEventId);
        }

        var replacement = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0.AddDays(2),
            [Line(1, item, warehouse, serial)]);
        Ports.Add(replacement);
        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant,
            new PurchasingReceiptPosted(Env(T0.AddDays(2)), replacement.Document))).UnitsAffected);
        using var final = fixture.ScopeFor(tenant);
        var finalDb = final.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unitIds = await finalDb.Identifiers.Where(x => x.RawValue == serial).OrderBy(x => x.ValidFrom)
            .Select(x => x.UnitId).ToListAsync();
        Assert.Equal(2, unitIds.Count);
        Assert.NotEqual(unitIds[0], unitIds[1]);
        Assert.Equal(unitIds[0], (await finalDb.Units.SingleAsync(x => x.UnitId == unitIds[1])).DerivedFromUnitId);
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<UnitReceiptReversed>(
            message => message.Header.UnitId == unitIds[0], 1, TimeSpan.FromSeconds(20)));
        Assert.Single(published.ClosedIdentifierIds);
        Assert.Single(published.ClosedEpisodeIds);
    }

    [DatabaseFact]
    public async Task Cancelling_a_receipt_with_a_later_shipment_leaves_the_unit_unchanged_and_reports_the_fact()
    {
        var tenant = await fixture.NewTenantAsync("drtouch");
        var serial = "NET-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var original = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0,
            [Line(1, item, warehouse, serial)]);
        Ports.Add(original);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), original.Document));
        var shipment = new ShipmentDocument(new DocumentRef("delivery", Guid.NewGuid()), Guid.NewGuid(),
            T0.AddHours(2), [Line(1, item, null, serial)], Guid.NewGuid(), Guid.NewGuid());
        Ports.Add(shipment);
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Env(T0.AddHours(2)), shipment.Document));

        var result = await InvokeAsync<ConsumedEventResult>(tenant,
            new DocumentCancellationPosted(Env(T0.AddDays(1)), original.Document));

        var exception = Assert.Single(result.Exceptions);
        Assert.Contains(UnitEventType.Shipped, exception, StringComparison.Ordinal);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = await db.Identifiers.Where(x => x.RawValue == serial).Select(x => x.UnitId).SingleAsync();
        Assert.Equal(LifecycleStatus.Released, (await db.Units.SingleAsync(x => x.UnitId == unit)).LifecycleStatus);
        Assert.False(await db.Events.AnyAsync(x => x.UnitId == unit && x.EventType == UnitEventType.ReceiptReversed));
        Assert.Null((await db.Identifiers.SingleAsync(x => x.UnitId == unit)).ValidTo);
    }

    [DatabaseFact]
    public async Task Shipment_then_delivery_moves_custody_and_opens_ownership_on_delivery_with_the_shipment_facts_recorded()
    {
        var tenant = await fixture.NewTenantAsync("ds");
        var p = "DS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var leeds = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var carrier = Guid.NewGuid();
        var lab2 = Guid.NewGuid();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0, [Line(1, item, leeds, $"{p}-001", $"{p}-002")]);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), receipt.Document));

        var shipment = new ShipmentDocument(new DocumentRef("delivery", Guid.NewGuid()), customer, T0.AddDays(1), [Line(4, item, null, $"{p}-001", $"{p}-002", $"{p}-999")], lab2, carrier, CustomerPoRef: "BWA-4471");
        Ports.Add(shipment);
        var shipped = await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Env(T0.AddDays(1)), shipment.Document));
        Assert.Equal(2, shipped.UnitsAffected);
        Assert.Single(shipped.Exceptions);
        Assert.Contains("unknown serial", shipped.Exceptions[0]);

        var confirmation = new DeliveryConfirmation(new DocumentRef("pod", Guid.NewGuid()), shipment.Document, T0.AddDays(3), lab2);
        Ports.Add(confirmation);
        var delivered = await InvokeAsync<ConsumedEventResult>(tenant, new SalesDeliveryConfirmed(Env(T0.AddDays(3)), confirmation.Document));
        Assert.Equal(2, delivered.UnitsAffected);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = await db.Identifiers.Where(i => i.NormalisedValue == $"{p}001").Select(i => i.UnitId).SingleAsync();
        var types = await db.Events.Where(e => e.UnitId == unit).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync();
        var shipmentTypes = new[] { UnitEventType.Commissioned, UnitEventType.Shipped, UnitEventType.Delivered,
            UnitEventType.OwnershipOpened };
        Assert.Equal(shipmentTypes, types.Where(shipmentTypes.Contains));
        var shippedEvent = await db.Events.SingleAsync(e => e.UnitId == unit && e.EventType == UnitEventType.Shipped);
        Assert.Contains("\"serial_confirmation_mode\":\"scan_required\"", shippedEvent.Attributes);
        Assert.Contains("\"serials_copied_without_scan\":false", shippedEvent.Attributes);
        Assert.Equal(4, shippedEvent.DocumentLine);
        Assert.Equal("2026-09", shippedEvent.PostingPeriod);
        var current = await db.Current.SingleAsync(c => c.UnitId == unit);
        Assert.Equal(LocationKind.CustomerSite, current.LocationKind);
        Assert.Equal(customer, current.OwnerPartyRef);
        Assert.Equal(lab2, current.LocationId);
        var ownership = await db.Episodes.SingleAsync(e => e.UnitId == unit && e.Kind == EpisodeKind.Ownership);
        Assert.Equal("BWA-4471", ownership.CustomerPoRef);
        Assert.Equal(3, await db.Episodes.CountAsync(e => e.UnitId == unit && e.Kind == EpisodeKind.Custody));
    }

    [DatabaseFact]
    public async Task A_3pl_line_without_serials_waits_and_the_confirmation_ships_with_the_physical_time()
    {
        var tenant = await fixture.NewTenantAsync("dp");
        var p = "DP" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0, [Line(1, item, Guid.NewGuid(), $"{p}-001", $"{p}-002")]);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), receipt.Document));
        var shipment = new ShipmentDocument(new DocumentRef("delivery", Guid.NewGuid()), Guid.NewGuid(), T0.AddDays(1), [new DocumentLine(1, item, 2, [])]);
        Ports.Add(shipment);
        var posted = await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Env(T0.AddDays(1)) with { AttributionCompleteness = ThinEnvelope.Completeness.Partial }, shipment.Document));
        Assert.Equal(1, posted.LinesPending);
        Assert.Equal(0, posted.UnitsAffected);

        var physical = T0.AddDays(2).AddHours(5);
        var confirmed = await InvokeAsync<ConfirmShipmentSerialsResult>(tenant, new ConfirmShipmentSerials(Guid.NewGuid(), shipment.Document, 1, [new DocumentSerial($"{p}-001"), new DocumentSerial($"{p}-002")], physical, "three_pl", Entity));
        Assert.Equal(2, confirmed.Shipped);
        Assert.Equal(0, confirmed.Remaining);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var pending = await db.PendingShipmentLines.SingleAsync(x => x.DocumentId == shipment.Document.Id);
        Assert.NotNull(pending.ResolvedAt);
        var unit = await db.Identifiers.Where(i => i.NormalisedValue == $"{p}002").Select(i => i.UnitId).SingleAsync();
        var shipped = await db.Events.SingleAsync(e => e.UnitId == unit && e.EventType == UnitEventType.Shipped);
        Assert.Equal(physical, shipped.EventTime); // business time is the physical ship time, not the posting
    }

    [DatabaseFact]
    public async Task Production_receipt_commissions_the_parent_and_links_the_issued_component_serials()
    {
        var tenant = await fixture.NewTenantAsync("dm");
        var p = "DM" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var pcbItem = Guid.NewGuid();
        var facility = Guid.NewGuid();
        var pcbReceipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0, [Line(1, pcbItem, facility, $"{p}-PCBA", $"{p}-PCBB")]);
        Ports.Add(pcbReceipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), pcbReceipt.Document));

        var order = new DocumentRef("production_order", Guid.NewGuid());
        var production = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order, T0.AddDays(1), [Line(1, parentItem, facility, $"{p}-P1", $"{p}-P2")],
            [new ComponentIssue(10, pcbItem, 1, [new DocumentSerial($"{p}-PCBA")], ParentSerial: $"{p}-P1", Slot: "A1"), new ComponentIssue(11, pcbItem, 1, [new DocumentSerial($"{p}-PCBB")], ParentSerial: $"{p}-P2", Slot: "A1"), new ComponentIssue(12, Guid.NewGuid(), 2, [], ParentSerial: $"{p}-P1")], facility);
        Ports.Add(production);
        var result = await InvokeAsync<ConsumedEventResult>(tenant, new ManufacturingProductionReceiptPosted(Env(T0.AddDays(1)), production.Document));
        Assert.Equal(2, result.UnitsAffected);
        Assert.Empty(result.Exceptions);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var p1 = await db.Identifiers.Where(i => i.NormalisedValue == $"{p}P1").Select(i => i.UnitId).SingleAsync();
        Assert.Equal(LifecycleStatus.PreRelease, (await db.Units.SingleAsync(u => u.UnitId == p1)).LifecycleStatus);
        var links = await db.AssemblyLinks.Where(l => l.ParentUnitId == p1).ToListAsync();
        Assert.Equal(2, links.Count); // one serialised PCB, one non-serialised line
        Assert.Contains(links, l => l.ChildUnitId != null && l.Slot == "A1");
        Assert.Contains(links, l => l.ChildUnitId == null && l.Quantity == 2);
    }

    [DatabaseFact]
    public async Task Twenty_five_manually_issued_components_are_held_then_linked_to_the_single_received_parent()
    {
        var tenant = await fixture.NewTenantAsync("dm25");
        var prefix = "SRV" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var serials = Enumerable.Range(1, 25).Select(number => $"{prefix}-NIC-{number:000}").ToArray();
        var componentReceipt = new ReceiptDocument(new DocumentRef("goods_receipt", Guid.NewGuid()),
            Guid.NewGuid(), T0, [Line(1, componentItem, warehouse, serials)]);
        Ports.Add(componentReceipt);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new PurchasingReceiptPosted(Env(T0), componentReceipt.Document));
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var issue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), order,
            T0.AddHours(1), [new ComponentIssue(1, componentItem, 25,
                serials.Select(serial => new DocumentSerial(serial)).ToList())], parentItem, 1m);
        Ports.Add(issue);

        var issued = await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingComponentIssued(Env(T0.AddHours(1)), issue.Document));

        Assert.Equal(25, issued.UnitsAffected);
        using (var heldScope = fixture.ScopeFor(tenant))
        {
            var heldDb = heldScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(25, await heldDb.ProductionComponentHolds.CountAsync(row => row.LinkedAt == null));
            Assert.Equal(25, await heldDb.Current.CountAsync(row => row.LocationKind == LocationKind.InProduction));
            Assert.False(await heldDb.Units.AnyAsync(row => row.LifecycleStatus == LifecycleStatus.Scrapped));
        }

        fixture.Services.GetRequiredService<InMemoryItemPorts>().RegisterBom(parentItem,
            [new ExpectedComponent("NIC", componentItem, "FIC-NIC-25G", 25)]);
        await fixture.EnableErpBomCompletenessAsync(tenant);
        var parentSerial = $"{prefix}-PARENT-001";
        var production = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order,
            T0.AddHours(2), [Line(1, parentItem, warehouse, parentSerial)], [], warehouse);
        Ports.Add(production);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(2)), production.Document));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var parent = await db.Identifiers.Where(row => row.RawValue == parentSerial)
            .Select(row => row.UnitId).SingleAsync();
        Assert.Equal(25, await db.AssemblyLinks.CountAsync(row => row.ParentUnitId == parent
            && row.RemovedAt == null));
        Assert.Equal(25, await db.ProductionComponentHolds.CountAsync(row => row.ParentUnitId == parent
            && row.LinkedAt != null));
        Assert.All(await db.ProductionComponentHolds.Where(row => row.ParentUnitId == parent).ToListAsync(),
            hold =>
            {
                Assert.Equal(issue.Document.Id, hold.IssueDocumentId);
                Assert.Equal(production.Document.Id, hold.ReceiptDocumentId);
            });
        await AssertComponentsTakeParentLocationAsync(db, parent);
        var unitQueries = scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Queries.UnitQueries>();
        var parentDetail = await unitQueries.DetailAsync(parent, CancellationToken.None);
        Assert.NotNull(parentDetail);
        Assert.All(parentDetail.AsBuilt, row =>
        {
            Assert.Equal(order, row.ProductionOrder);
            Assert.Equal(issue.Document.Id, row.Issue?.Id);
            Assert.Equal(production.Document.Id, row.Receipt?.Id);
        });
        var released = await InvokeAsync<ReleaseUnitResult>(tenant,
            new ReleaseUnit(Guid.NewGuid(), parent, T0.AddHours(3), "quality.user", Entity, "release-check"));
        Assert.Equal(LifecycleStatus.Released, released.LifecycleStatus);
        Assert.False(await db.Units.AnyAsync(row => row.LifecycleStatus == LifecycleStatus.Scrapped));
    }

    [DatabaseFact]
    public async Task A_late_issue_links_immediately_but_multiple_received_parents_wait_for_confirmed_assignment()
    {
        var tenant = await fixture.NewTenantAsync("dmlate");
        var prefix = "NET" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().RegisterBom(parentItem,
            [new ExpectedComponent("PORT", componentItem, "FIC-NIC-25G", 1)]);
        await fixture.EnableErpBomCompletenessAsync(tenant);
        var componentReceipt = new ReceiptDocument(new DocumentRef("goods_receipt", Guid.NewGuid()),
            Guid.NewGuid(), T0, [Line(1, componentItem, warehouse, $"{prefix}-C1", $"{prefix}-C2")]);
        Ports.Add(componentReceipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), componentReceipt.Document));

        var singleOrder = new DocumentRef("production_order", Guid.NewGuid());
        var singleReceipt = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), singleOrder,
            T0.AddHours(1), [Line(1, parentItem, warehouse, $"{prefix}-P1")], [], warehouse,
            ProducedItemId: parentItem, PlannedQuantity: 1m);
        Ports.Add(singleReceipt);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(1)), singleReceipt.Document));
        var lateIssue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), singleOrder,
            T0.AddHours(2), [new ComponentIssue(1, componentItem, 1, [new DocumentSerial($"{prefix}-C1")])]);
        Ports.Add(lateIssue);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingComponentIssued(Env(T0.AddHours(2)), lateIssue.Document));

        var multiOrder = new DocumentRef("production_order", Guid.NewGuid());
        var multiIssue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), multiOrder,
            T0.AddHours(3), [new ComponentIssue(1, componentItem, 1, [new DocumentSerial($"{prefix}-C2")])],
            parentItem, 2m);
        Ports.Add(multiIssue);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingComponentIssued(Env(T0.AddHours(3)), multiIssue.Document));
        var multiReceipt = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), multiOrder,
            T0.AddHours(4), [Line(1, parentItem, warehouse, $"{prefix}-P2")], [], warehouse,
            ProducedItemId: parentItem, PlannedQuantity: 2m);
        Ports.Add(multiReceipt);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(4)), multiReceipt.Document));

        Guid selectedParent;
        using (var waitingScope = fixture.ScopeFor(tenant))
        {
            var waitingDb = waitingScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            selectedParent = await waitingDb.Identifiers.Where(row => row.RawValue == $"{prefix}-P2")
                .Select(row => row.UnitId).SingleAsync();
            Assert.True(await waitingDb.ProductionComponentHolds.AnyAsync(row =>
                row.ProductionOrderId == multiOrder.Id && row.LinkedAt == null));
            Assert.True(await waitingDb.ProductionHoldTasks.AnyAsync(row =>
                row.ProductionOrderId == multiOrder.Id && row.Kind == "assignment" && row.Status == "open"));
            var queries = waitingScope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Manufacturing.ProductionHoldQueries>();
            var page = await queries.PageAsync(null, 25, CancellationToken.None);
            Assert.Contains(page.Items, row => row.ProductionOrder == multiOrder
                && row.Status == "waiting_for_assignment" && row.ParentsReceived == 1
                && row.PlannedQuantity == 2m && row.ProducedItemId == parentItem);
            var detail = await queries.DetailAsync(multiOrder, CancellationToken.None);
            Assert.NotNull(detail);
            Assert.Equal("waiting_for_assignment", detail.Status);
            Assert.Single(detail.Components);
            Assert.Single(detail.Parents);
            Assert.Equal(2m, detail.PlannedQuantity);
        }
        var blocked = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReleaseUnitResult>(tenant,
            new ReleaseUnit(Guid.NewGuid(), selectedParent, T0.AddHours(5), "quality.user", Entity, "release-check")));
        Assert.Equal("incomplete_asbuilt", blocked.Rule);
        var assigned = await InvokeAsync<AssignProductionComponentsResult>(tenant,
            new AssignProductionComponents(Guid.NewGuid(), multiOrder, $"{prefix}-P2",
                [$"{prefix}-C2"], Entity, "production.operator"));
        Assert.Single(assigned.AssemblyLinkIds);
        var released = await InvokeAsync<ReleaseUnitResult>(tenant,
            new ReleaseUnit(Guid.NewGuid(), selectedParent, T0.AddHours(6), "quality.user", Entity, "release-check"));
        Assert.Equal(LifecycleStatus.Released, released.LifecycleStatus);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.True(await db.ProductionComponentHolds.AnyAsync(row => row.ProductionOrderId == singleOrder.Id
            && row.LinkedAt != null && row.IssueDocumentId == lateIssue.Document.Id));
        Assert.True(await db.ProductionHoldTasks.AnyAsync(row => row.ProductionOrderId == multiOrder.Id
            && row.Status == "completed" && row.CompletedBy == "production.operator"));
        var lateParent = await db.Identifiers.Where(row => row.RawValue == $"{prefix}-P1")
            .Select(row => row.UnitId).SingleAsync();
        await AssertComponentsTakeParentLocationAsync(db, lateParent);
        await AssertComponentsTakeParentLocationAsync(db, selectedParent);
    }

    [DatabaseFact]
    public async Task A_late_issue_dated_after_this_clock_still_moves_the_component_to_its_parent()
    {
        // The ERP dates documents by the company's calendar day (00:00 UTC), which can be ahead of this clock.
        var tenant = await fixture.NewTenantAsync("dmday");
        var prefix = "DAY" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var day = DateTime.SpecifyKind(DateTime.UtcNow.Date.AddDays(2), DateTimeKind.Utc);
        var parentItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().RegisterBom(parentItem,
            [new ExpectedComponent("PORT", componentItem, "FIC-NIC-25G", 1)]);
        await fixture.EnableErpBomCompletenessAsync(tenant);
        var componentReceipt = new ReceiptDocument(new DocumentRef("goods_receipt", Guid.NewGuid()),
            Guid.NewGuid(), day, [Line(1, componentItem, warehouse, $"{prefix}-C1")]);
        Ports.Add(componentReceipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(day), componentReceipt.Document));
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var receipt = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order, day,
            [Line(1, parentItem, warehouse, $"{prefix}-P1")], [], warehouse, ProducedItemId: parentItem,
            PlannedQuantity: 1m);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new ManufacturingProductionReceiptPosted(Env(day), receipt.Document));
        var issue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), order, day,
            [new ComponentIssue(1, componentItem, 1, [new DocumentSerial($"{prefix}-C1")])], parentItem, 1m);
        Ports.Add(issue);

        var issued = await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingComponentIssued(Env(day), issue.Document));

        Assert.Empty(issued.Exceptions);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var parent = await db.Identifiers.Where(row => row.RawValue == $"{prefix}-P1").Select(row => row.UnitId).SingleAsync();
        Assert.True(await db.ProductionComponentHolds.AllAsync(row => row.ProductionOrderId != order.Id
            || (row.LinkedAt != null && row.ParentUnitId == parent)));
        await AssertComponentsTakeParentLocationAsync(db, parent);
    }

    [DatabaseFact]
    public async Task A_component_already_linked_to_the_parent_keeps_its_link_when_its_hold_is_completed()
    {
        var tenant = await fixture.NewTenantAsync("dmkeep");
        var prefix = "KEEP" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().RegisterBom(parentItem,
            [new ExpectedComponent("PORT", componentItem, "FIC-NIC-25G", 1)]);
        var componentReceipt = new ReceiptDocument(new DocumentRef("goods_receipt", Guid.NewGuid()),
            Guid.NewGuid(), T0, [Line(1, componentItem, warehouse, $"{prefix}-C1")]);
        Ports.Add(componentReceipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), componentReceipt.Document));
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var receipt = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order,
            T0.AddHours(1), [Line(1, parentItem, warehouse, $"{prefix}-P1")], [], warehouse,
            ProducedItemId: parentItem, PlannedQuantity: 1m);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(1)), receipt.Document));
        Guid parent, component;
        using (var setupScope = fixture.ScopeFor(tenant))
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            parent = await setupDb.Identifiers.Where(row => row.RawValue == $"{prefix}-P1").Select(row => row.UnitId).SingleAsync();
            component = await setupDb.Identifiers.Where(row => row.RawValue == $"{prefix}-C1").Select(row => row.UnitId).SingleAsync();
        }
        var linked = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent,
            [new ChildInput(component, Slot: "PORT")], T0.AddHours(2), "production.operator", Entity));
        var issue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), order,
            T0.AddHours(3), [new ComponentIssue(1, componentItem, 1, [new DocumentSerial($"{prefix}-C1")])],
            parentItem, 1m);
        Ports.Add(issue);

        var issued = await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingComponentIssued(Env(T0.AddHours(3)), issue.Document));

        Assert.Empty(issued.Exceptions);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var links = await db.AssemblyLinks.Where(row => row.ChildUnitId == component && row.RemovedAt == null).ToListAsync();
        Assert.Equal(Assert.Single(linked.LinkIds), Assert.Single(links).LinkId);
        var hold = await db.ProductionComponentHolds.SingleAsync(row => row.UnitId == component);
        Assert.Equal(links[0].LinkId, hold.AssemblyLinkId);
        Assert.NotNull(hold.LinkedAt);
        await AssertComponentsTakeParentLocationAsync(db, parent);
    }

    [DatabaseFact]
    public async Task A_refused_link_part_way_leaves_no_link_and_every_hold_open()
    {
        var tenant = await fixture.NewTenantAsync("dmatom");
        var prefix = "ATOM" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var otherItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().RegisterBom(parentItem,
            [new ExpectedComponent("PORT", componentItem, "FIC-NIC-25G", 2)]);
        var stock = new ReceiptDocument(new DocumentRef("goods_receipt", Guid.NewGuid()), Guid.NewGuid(), T0,
            [Line(1, componentItem, warehouse, $"{prefix}-C1", $"{prefix}-C2"), Line(2, otherItem, warehouse, $"{prefix}-O1")]);
        Ports.Add(stock);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), stock.Document));
        Guid other, second;
        using (var setupScope = fixture.ScopeFor(tenant))
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            other = await setupDb.Identifiers.Where(row => row.RawValue == $"{prefix}-O1").Select(row => row.UnitId).SingleAsync();
            second = await setupDb.Identifiers.Where(row => row.RawValue == $"{prefix}-C2").Select(row => row.UnitId).SingleAsync();
        }
        // C2 is already inside another unit, so linking it to the new parent is refused after C1 was taken.
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), other,
            [new ChildInput(second)], T0.AddMinutes(30), "production.operator", Entity));
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var receipt = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order,
            T0.AddHours(1), [Line(1, parentItem, warehouse, $"{prefix}-P1")], [], warehouse,
            ProducedItemId: parentItem, PlannedQuantity: 1m);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(1)), receipt.Document));
        var issue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), order,
            T0.AddHours(2), [new ComponentIssue(1, componentItem, 1, [new DocumentSerial($"{prefix}-C1")]),
                new ComponentIssue(2, componentItem, 1, [new DocumentSerial($"{prefix}-C2")])], parentItem, 1m);
        Ports.Add(issue);

        var issued = await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingComponentIssued(Env(T0.AddHours(2)), issue.Document));

        Assert.Contains(issued.Exceptions, text => text.Contains("child_already_installed", StringComparison.Ordinal));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var parent = await db.Identifiers.Where(row => row.RawValue == $"{prefix}-P1").Select(row => row.UnitId).SingleAsync();
        Assert.False(await db.AssemblyLinks.AnyAsync(row => row.ParentUnitId == parent && row.RemovedAt == null));
        var holds = await db.ProductionComponentHolds.Where(row => row.ProductionOrderId == order.Id).ToListAsync();
        Assert.Equal(2, holds.Count);
        Assert.All(holds, hold => Assert.Null(hold.LinkedAt));
        Assert.Equal(2, await db.Current.CountAsync(row => (row.UnitId == second || holds.Select(h => h.UnitId).Contains(row.UnitId))
            && row.LocationKind == LocationKind.InProduction));
    }

    [DatabaseFact]
    public async Task Assignment_uses_the_open_hold_when_the_component_was_returned_and_issued_again()
    {
        var tenant = await fixture.NewTenantAsync("dmagain");
        var prefix = "AGN" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().RegisterBom(parentItem,
            [new ExpectedComponent("PORT", componentItem, "FIC-NIC-25G", 1)]);
        var stock = new ReceiptDocument(new DocumentRef("goods_receipt", Guid.NewGuid()), Guid.NewGuid(), T0,
            [Line(1, componentItem, warehouse, $"{prefix}-C1")]);
        Ports.Add(stock);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), stock.Document));
        var order = new DocumentRef("production_order", Guid.NewGuid());
        async Task IssueAsync(DateTime at)
        {
            var issue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), order, at,
                [new ComponentIssue(1, componentItem, 1, [new DocumentSerial($"{prefix}-C1")])], parentItem, 2m);
            Ports.Add(issue);
            await InvokeAsync<ConsumedEventResult>(tenant, new ManufacturingComponentIssued(Env(at), issue.Document));
        }
        await IssueAsync(T0.AddHours(1));
        var returned = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order,
            T0.AddHours(2), [], [], warehouse, [Line(1, componentItem, warehouse, $"{prefix}-C1")], parentItem, 2m);
        Ports.Add(returned);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(2)), returned.Document));
        await IssueAsync(T0.AddHours(3));
        var receipt = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order,
            T0.AddHours(4), [Line(1, parentItem, warehouse, $"{prefix}-P1")], [], warehouse,
            ProducedItemId: parentItem, PlannedQuantity: 2m);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(4)), receipt.Document));

        var assigned = await InvokeAsync<AssignProductionComponentsResult>(tenant,
            new AssignProductionComponents(Guid.NewGuid(), order, $"{prefix}-P1", [$"{prefix}-C1"], Entity,
                "production.operator"));

        Assert.Single(assigned.AssemblyLinkIds);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var holds = await db.ProductionComponentHolds.Where(row => row.ProductionOrderId == order.Id).ToListAsync();
        Assert.Equal(2, holds.Count);
        Assert.Single(holds, hold => hold.ReturnedAt != null && hold.LinkedAt == null);
        Assert.Single(holds, hold => hold.LinkedAt != null && hold.ReturnedAt == null);
        await AssertComponentsTakeParentLocationAsync(db, assigned.ParentUnitId);
    }

    [DatabaseFact]
    public async Task Returned_components_of_different_items_may_share_a_serial()
    {
        // The ERP numbered a CPU and a DIMM alike in live receipt 142; serial plus item identifies the unit.
        var tenant = await fixture.NewTenantAsync("dmsame");
        var serial = "SAME" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var cpuItem = Guid.NewGuid();
        var dimmItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var stock = new ReceiptDocument(new DocumentRef("goods_receipt", Guid.NewGuid()), Guid.NewGuid(), T0,
            [Line(1, cpuItem, warehouse, serial), Line(2, dimmItem, warehouse, serial)]);
        Ports.Add(stock);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), stock.Document));
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var issue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), order,
            T0.AddHours(1), [new ComponentIssue(1, cpuItem, 1, [new DocumentSerial(serial)]),
                new ComponentIssue(2, dimmItem, 1, [new DocumentSerial(serial)])], parentItem, 2m);
        Ports.Add(issue);
        await InvokeAsync<ConsumedEventResult>(tenant, new ManufacturingComponentIssued(Env(T0.AddHours(1)), issue.Document));
        var returned = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order,
            T0.AddHours(2), [], [], warehouse,
            [Line(1, cpuItem, warehouse, serial), Line(2, dimmItem, warehouse, serial)], parentItem, 2m);
        Ports.Add(returned);

        var result = await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(2)), returned.Document));

        Assert.DoesNotContain(result.Exceptions, text => text.Contains("component_repeated", StringComparison.Ordinal));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var holds = await db.ProductionComponentHolds.Where(row => row.ProductionOrderId == order.Id).ToListAsync();
        Assert.Equal(2, holds.Count);
        Assert.All(holds, hold => Assert.NotNull(hold.ReturnedAt));
    }

    // A component linked out of a production hold leaves in_production and sits where its parent sits.
    private static async Task AssertComponentsTakeParentLocationAsync(SerialisationDbContext db, Guid parent)
    {
        var parentState = await db.Current.SingleAsync(row => row.UnitId == parent);
        var children = await db.AssemblyLinks.Where(row => row.ParentUnitId == parent && row.ChildUnitId != null
            && row.RemovedAt == null).Select(row => row.ChildUnitId!.Value).ToListAsync();
        Assert.NotEmpty(children);
        var states = await db.Current.Where(row => children.Contains(row.UnitId)).ToListAsync();
        Assert.Equal(children.Count, states.Count);
        Assert.All(states, state =>
        {
            Assert.NotEqual(LocationKind.InProduction, state.LocationKind);
            Assert.Equal(parentState.LocationKind, state.LocationKind);
            Assert.Equal(parentState.LocationId, state.LocationId);
        });
    }

    [DatabaseFact]
    public async Task Unknown_planned_quantity_never_guesses_from_one_received_parent()
    {
        var tenant = await fixture.NewTenantAsync("dmunknown");
        var prefix = "UNK" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var componentReceipt = new ReceiptDocument(new DocumentRef("goods_receipt", Guid.NewGuid()),
            Guid.NewGuid(), T0, [Line(1, componentItem, warehouse, $"{prefix}-C1")]);
        Ports.Add(componentReceipt);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new PurchasingReceiptPosted(Env(T0), componentReceipt.Document));
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var issue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), order,
            T0.AddHours(1), [new ComponentIssue(1, componentItem, 1, [new DocumentSerial($"{prefix}-C1")])],
            parentItem);
        Ports.Add(issue);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingComponentIssued(Env(T0.AddHours(1)), issue.Document));
        var production = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order,
            T0.AddHours(2), [Line(1, parentItem, warehouse, $"{prefix}-P1")], [], warehouse,
            ProducedItemId: parentItem);
        Ports.Add(production);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(2)), production.Document));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.True(await db.ProductionComponentHolds.AnyAsync(row => row.ProductionOrderId == order.Id
            && row.LinkedAt == null));
        Assert.True(await db.ProductionHoldTasks.AnyAsync(row => row.ProductionOrderId == order.Id
            && row.Kind == "assignment" && row.Status == "open"));
    }

    [DatabaseFact]
    public async Task A_component_return_closes_the_hold_and_moves_the_existing_unit_back_to_the_receiving_warehouse()
    {
        var tenant = await fixture.NewTenantAsync("dmreturn");
        var serial = "SSD-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var componentItem = Guid.NewGuid();
        var parentItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var receipt = new ReceiptDocument(new DocumentRef("goods_receipt", Guid.NewGuid()), Guid.NewGuid(), T0,
            [Line(1, componentItem, warehouse, serial)]);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), receipt.Document));
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var issue = new ComponentIssueDocument(new DocumentRef("component_issue", Guid.NewGuid()), order,
            T0.AddHours(1), [new ComponentIssue(1, componentItem, 1, [new DocumentSerial(serial)])]);
        Ports.Add(issue);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingComponentIssued(Env(T0.AddHours(1)), issue.Document));
        var production = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order,
            T0.AddHours(2), [], [], warehouse, [Line(2, componentItem, warehouse, serial)], parentItem, 1m);
        Ports.Add(production);

        await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(2)), production.Document));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var component = await db.Identifiers.Where(row => row.RawValue == serial).Select(row => row.UnitId).SingleAsync();
        var current = await db.Current.SingleAsync(row => row.UnitId == component);
        Assert.Equal((warehouse, LocationKind.Warehouse), (current.LocationId, current.LocationKind));
        Assert.NotNull((await db.ProductionComponentHolds.SingleAsync(row => row.UnitId == component)).ReturnedAt);
        Assert.Single(await db.Units.ToListAsync());
    }

    [DatabaseFact]
    public async Task A_return_line_with_an_unknown_serial_is_refused_without_commissioning_a_unit()
    {
        var tenant = await fixture.NewTenantAsync("dmreturnunknown");
        var componentItem = Guid.NewGuid();
        var parentItem = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var production = new ProductionReceipt(new DocumentRef("production_receipt", Guid.NewGuid()), order,
            T0.AddHours(2), [], [], warehouse,
            [Line(2, componentItem, warehouse, "FIC-UNKNOWN-COMPONENT-01")], parentItem, 1m);
        Ports.Add(production);

        var result = await InvokeAsync<ConsumedEventResult>(tenant,
            new ManufacturingProductionReceiptPosted(Env(T0.AddHours(2)), production.Document));

        Assert.Contains(result.Exceptions, value => value.Contains("component_unknown", StringComparison.Ordinal));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Empty(await db.Units.ToListAsync());
    }

    [DatabaseFact]
    public async Task Transfer_return_loaner_adjustment_and_period_close_each_record_their_fact()
    {
        var tenant = await fixture.NewTenantAsync("dx");
        var p = "DX" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var leeds = Guid.NewGuid();
        var bristol = Guid.NewGuid();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), T0, [Line(1, item, leeds, $"{p}-001", $"{p}-002", $"{p}-003")]);
        Ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(T0), receipt.Document));

        var transfer = new TransferDocument(new DocumentRef("transfer", Guid.NewGuid()), T0.AddDays(1), leeds, LocationKind.Warehouse, bristol, LocationKind.Warehouse, [Line(1, item, null, $"{p}-001")]);
        Ports.Add(transfer);
        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant, new InventoryTransferPosted(Env(T0.AddDays(1)), transfer.Document))).UnitsAffected);

        var loaner = new LoanerIssue(new DocumentRef("service_order", Guid.NewGuid()), Guid.NewGuid(), T0.AddDays(2), Guid.NewGuid(), new DateOnly(2026, 10, 15), [Line(1, item, null, $"{p}-002")]);
        Ports.Add(loaner);
        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant, new ServiceLoanerIssued(Env(T0.AddDays(2)), loaner.Document))).UnitsAffected);

        var returned = new ReturnDocument(new DocumentRef("return", Guid.NewGuid()), Guid.NewGuid(), T0.AddDays(10), leeds, [Line(1, item, null, $"{p}-002")], ReturnReasonCode: "no_vacuum");
        Ports.Add(returned);
        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant, new SalesReturnPosted(Env(T0.AddDays(10)), returned.Document))).UnitsAffected);

        var scrap = new AdjustmentDocument(new DocumentRef("adjustment", Guid.NewGuid()), T0.AddDays(11), AdjustmentDocument.Kinds.Out, "scrap", [Line(1, item, leeds, $"{p}-003")]);
        Ports.Add(scrap);
        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant, new InventoryAdjustmentPosted(Env(T0.AddDays(11)), scrap.Document))).UnitsAffected);

        await InvokeAsync<ConsumedEventResult>(tenant, new FinancePeriodClosed(Env(T0.AddDays(40)), "2026-09"));
        var late = new TransferDocument(new DocumentRef("transfer", Guid.NewGuid()), T0.AddDays(20), bristol, LocationKind.Warehouse, leeds, LocationKind.Warehouse, [Line(1, item, null, $"{p}-001")]);
        Ports.Add(late);
        await InvokeAsync<ConsumedEventResult>(tenant, new InventoryTransferPosted(Env(T0.AddDays(41)), late.Document));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var u1 = await db.Identifiers.Where(i => i.NormalisedValue == $"{p}001").Select(i => i.UnitId).SingleAsync();
        var u2 = await db.Identifiers.Where(i => i.NormalisedValue == $"{p}002").Select(i => i.UnitId).SingleAsync();
        var u3 = await db.Identifiers.Where(i => i.NormalisedValue == $"{p}003").Select(i => i.UnitId).SingleAsync();
        var one = await db.Current.SingleAsync(c => c.UnitId == u1);
        Assert.Equal(leeds, one.LocationId);
        var two = await db.Current.SingleAsync(c => c.UnitId == u2);
        Assert.Equal(LocationKind.Quarantine, two.LocationKind);
        Assert.Equal(UnitDisposition.Returned, two.Disposition);
        Assert.Equal(new DateOnly(2026, 10, 15), (await db.Episodes.SingleAsync(e => e.UnitId == u2 && e.Reason == "loan")).DueBack);
        var returnedDisposition = Assert.Single(await PublishedEventSink.WaitForAsync<UnitDispositionChanged>(
            message => message.Header.UnitId == u2 && message.Header.Document?.Id == returned.Document.Id,
            1, TimeSpan.FromSeconds(20)));
        Assert.Equal((UnitDisposition.Active, UnitDisposition.Returned, UnitEventType.CustodyChanged),
            (returnedDisposition.PreviousDisposition, returnedDisposition.Disposition,
                returnedDisposition.Cause));
        var three = await db.Units.SingleAsync(u => u.UnitId == u3);
        Assert.Equal(LifecycleStatus.Scrapped, three.LifecycleStatus);
        Assert.Equal(UnitDisposition.Destroyed, (await db.Current.SingleAsync(c => c.UnitId == three.UnitId)).Disposition);
        Assert.Single(await db.FinancePeriods.Where(f => f.Period == "2026-09").ToListAsync());
        Assert.Equal("2026-10", (await scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Commands.PostingPeriods>().ResolveAsync(T0.AddDays(20), CancellationToken.None)));
    }
}
