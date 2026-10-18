using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Selection;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T18: selection sets resolve from every source, the preview is the command's own, execution is one transaction with one document and one approval, find-within works server-side.</summary>
[Collection("serialisation host")]
public sealed class SelectionSetTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private string NewScheme(string prefix)
    {
        var code = prefix + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(new SerialSchemeDefinition(code, IdentifierType.InternalSerial, code, "yyMM", "-", 6));
        return code;
    }

    [DatabaseFact]
    public async Task A_serial_range_expands_within_its_block_reports_unknown_serials_and_rejects_a_boundary_crossing()
    {
        var tenant = await fixture.NewTenantAsync("sl");
        var scheme = NewScheme("SL");
        var item = Guid.NewGuid();
        var lines = Enumerable.Range(1, 5).Select(n => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{scheme}2610-{n:D6}")])).ToList();
        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured, lines, At, "t", Entity, Document: new DocumentRef("production_order", Guid.NewGuid())));

        var resolved = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.BySerialRange(scheme, $"{scheme}2610-000001", $"{scheme}2610-000008"), "t"));
        Assert.Equal(5, resolved.Resolved);
        Assert.Equal(3, resolved.Unknown.Count);
        var crossing = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.BySerialRange(scheme, $"{scheme}2610-000001", $"{scheme}2611-000010"), "t")));
        Assert.Equal("range_crosses_block", crossing.Rule);
    }

    [DatabaseFact]
    public async Task Document_customer_item_date_range_and_pasted_list_sources_resolve()
    {
        var tenant = await fixture.NewTenantAsync("ss");
        var p = "SS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var customer = Guid.NewGuid();
        var units = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-1")]), new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-2")]), new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-3")])], At, "t", Entity, Document: order))).Lines.Select(l => l.UnitId!.Value).ToList();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), units[0], customer, PartyType.Customer, EpisodeReason.Sale, At.AddDays(1), "s", Entity));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), units[1], customer, PartyType.Customer, EpisodeReason.Sale, At.AddDays(1), "s", Entity));

        Assert.Equal(3, (await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByDocument(order), "t"))).Resolved);
        Assert.Equal(2, (await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByCustomer(customer), "t"))).Resolved);
        Assert.Equal(3, (await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByItem(item), "t"))).Resolved);
        Assert.Equal(2, (await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByDateRange(At.AddDays(1), At.AddDays(2), UnitEventType.OwnershipOpened), "t"))).Resolved);
        var pasted = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByPastedList([$"{p}-1", $"{p}-3", $"{p}-9", $"{p}-1"]), "t"));
        Assert.Equal(2, pasted.Resolved);
        Assert.Equal([$"{p}-9"], pasted.Unknown);
    }

    [DatabaseFact]
    public async Task A_shipment_base_order_resolves_through_the_indexed_document_projection()
    {
        var tenant = await fixture.NewTenantAsync("sbase");
        var ports = fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
        var prefix = "SB" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), At,
            [new DocumentLine(1, item, 2, [new DocumentSerial($"{prefix}-1"), new DocumentSerial($"{prefix}-2")],
                Guid.NewGuid(), LocationKind.Warehouse)]);
        ports.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new PurchasingReceiptPosted(new ThinEnvelope(Guid.NewGuid(), Entity, At, "selection-test"), receipt.Document));

        var order = new DocumentRef("sales_order", Guid.NewGuid(), 7);
        var shipment = new ShipmentDocument(new DocumentRef("delivery", Guid.NewGuid()), Guid.NewGuid(), At.AddDays(1),
            [new DocumentLine(1, item, 2, [new DocumentSerial($"{prefix}-1"), new DocumentSerial($"{prefix}-2")])],
            BaseOrder: order);
        ports.Add(shipment);
        await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(new ThinEnvelope(Guid.NewGuid(), Entity, At.AddDays(1), "selection-test"), shipment.Document));

        var byOrder = await InvokeAsync<ResolveSelectionSetResult>(tenant,
            new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByDocument(order), "selection-test"));
        Assert.Equal(2, byOrder.Resolved);
        var otherLine = await InvokeAsync<ResolveSelectionSetResult>(tenant,
            new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByDocument(order with { Line = 8 }), "selection-test"));
        Assert.Equal(0, otherLine.Resolved);
    }

    [DatabaseFact]
    public async Task Preview_is_the_commands_judgement_execution_is_one_transaction_and_a_replay_returns_the_same_report()
    {
        var tenant = await fixture.NewTenantAsync("sx");
        var ports = fixture.Services.GetRequiredService<InMemoryItemPorts>();
        var p = "SX" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var pcbItem = Guid.NewGuid();
        ports.RegisterBom(item, [new ExpectedComponent("A1", pcbItem, "PCB-07", 1)]);
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var units = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured, Enumerable.Range(1, 4).Select(n => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-{n:D3}")])).ToList(), At, "t", Entity, Document: order))).Lines.Select(l => l.UnitId!.Value).ToList();
        // unit 1 and 2 complete; unit 3 incomplete (blocked); unit 4 already released (skipped)
        foreach (var u in units.Take(2))
        {
            var pcb = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), pcbItem, UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-PCB-{u:N}"[..24])])], At, "t", Entity))).Lines[0].UnitId!.Value;
            await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), u, [new ChildInput(pcb, Slot: "A1")], At.AddHours(1), "t", Entity));
        }

        var pcb4 = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), pcbItem, UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-PCB4")])], At, "t", Entity))).Lines[0].UnitId!.Value;
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), units[3], [new ChildInput(pcb4, Slot: "A1")], At.AddHours(1), "t", Entity));
        await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), units[3], At.AddHours(2), "q", Entity, "sig"));

        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByDocument(order), "r.okafor"));
        var arguments = new BulkOperationArguments(At.AddDays(1), "q.mahler", Entity, order, "final_test_passed", Extra: new Dictionary<string, string> { ["release_signature_ref"] = "sig:q.mahler" });
        var preview = await InvokeAsync<BulkPreviewResult>(tenant, new PreviewBulkOperation(selection.SelectionId, ReleaseUnitsOperation.OperationName, arguments));
        Assert.Equal(4, preview.Total);
        Assert.Equal(2, preview.Change);
        Assert.Equal(1, preview.Skip);
        Assert.Equal(1, preview.Block);
        Assert.Equal(UnitOutcome.Outcomes.Block, preview.Page[0].Outcome); // blocked first
        Assert.Equal("incomplete_asbuilt", preview.Page[0].Reason);
        var onlyBlocked = await InvokeAsync<BulkPreviewResult>(tenant, new PreviewBulkOperation(selection.SelectionId, ReleaseUnitsOperation.OperationName, arguments, OutcomeFilter: UnitOutcome.Outcomes.Block));
        Assert.Single(onlyBlocked.Page);
        var found = await InvokeAsync<BulkPreviewResult>(tenant, new PreviewBulkOperation(selection.SelectionId, ReleaseUnitsOperation.OperationName, arguments, Find: $"{p}-002"));
        Assert.Single(found.Page);

        var execute = new ExecuteBulkOperation(Guid.NewGuid(), selection.SelectionId, ReleaseUnitsOperation.OperationName, arguments, new BulkApproval("q.mahler"));
        var result = await InvokeAsync<BulkExecutionResult>(tenant, execute);
        Assert.Equal(2, result.Done);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(1, result.Blocked);
        Assert.False(result.Replayed);
        var replay = await InvokeAsync<BulkExecutionResult>(tenant, execute);
        Assert.True(replay.Replayed);
        Assert.Equal(2, replay.Done);
        var again = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<BulkExecutionResult>(tenant, execute with { CommandId = Guid.NewGuid() }));
        Assert.Equal("selection_already_executed", again.Rule);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(3, await db.Units.CountAsync(u => units.Contains(u.UnitId) && u.LifecycleStatus == LifecycleStatus.Released));
        Assert.Equal(LifecycleStatus.PreRelease, (await db.Units.SingleAsync(u => u.UnitId == units[2])).LifecycleStatus);
        var report = await db.SelectionSetMembers.Where(m => m.SelectionId == selection.SelectionId).ToListAsync();
        Assert.Equal(2, report.Count(m => m.Outcome == UnitOutcome.Outcomes.Done));
        Assert.Single(report, m => m.Outcome == UnitOutcome.Outcomes.Block && m.Reason == "incomplete_asbuilt");
        var stored = await db.SelectionSets.SingleAsync(s => s.SelectionId == selection.SelectionId);
        Assert.Equal("q.mahler", stored.ApproverRef);
        Assert.Equal(order.Id, stored.SourceDocumentId);
        var find = await InvokeAsync<BulkPreviewResult>(tenant, new FindInSelection(selection.SelectionId, $"{p}-00"));
        Assert.Equal(4, find.Total);
    }
}
