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
        Assert.Equal([UnitEventType.Commissioned, UnitEventType.Shipped, UnitEventType.Delivered, UnitEventType.OwnershipOpened], types);
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
        var three = await db.Units.SingleAsync(u => u.UnitId == u3);
        Assert.Equal(LifecycleStatus.Scrapped, three.LifecycleStatus);
        Assert.Equal(UnitDisposition.Destroyed, (await db.Current.SingleAsync(c => c.UnitId == three.UnitId)).Disposition);
        Assert.Single(await db.FinancePeriods.Where(f => f.Period == "2026-09").ToListAsync());
        Assert.Equal("2026-10", (await scope.ServiceProvider.GetRequiredService<Yojana.Serialisation.Commands.PostingPeriods>().ResolveAsync(T0.AddDays(20), CancellationToken.None)));
    }
}
