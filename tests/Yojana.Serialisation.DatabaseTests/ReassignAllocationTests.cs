using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T11: selected pre-commission reservations, pairwise exchange, move capacity and reversal.</summary>
[Collection("serialisation host")]
public sealed class ReassignAllocationTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly CorrectionCommitter Supervisor = new("production-supervisor", [YojanaRole.ProductionSupervisor]);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private string NewScheme(string prefix)
    {
        var code = prefix + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(new SerialSchemeDefinition(code, IdentifierType.InternalSerial, code, "yyMM", "-", 6));
        return code;
    }

    private static ReassignAllocation Command(Guid commandId, DocumentRef fromOrder, DocumentRef toOrder,
        SelectionSource from, SelectionSource? exchange, string mode = AllocationReassignmentMode.PairwiseExchange,
        string reason = ReassignAllocationReason.AllocationError, CorrectionMetadata? correction = null, bool preview = false,
        CorrectionCommitter? committer = null) =>
        new(commandId, fromOrder, toOrder, mode, from, exchange, reason, At.AddHours(1),
            (committer ?? Supervisor).UserRef, Entity, "order allocation corrected", correction, committer ?? Supervisor, preview);

    private void AddOrder(DocumentRef order, Guid item, string scheme, int quantity) =>
        fixture.Services.GetRequiredService<InMemoryDocumentPorts>().Add(new ProductionOrderAllocation(order, item, scheme, quantity));

    [DatabaseFact]
    public async Task Partial_pairwise_exchange_moves_only_the_selected_serials_and_records_per_serial_facts()
    {
        var tenant = await fixture.NewTenantAsync("ra");
        var scheme = NewScheme("RA");
        var item = Guid.NewGuid();
        var orderA = new DocumentRef("production_order", Guid.NewGuid());
        var orderB = new DocumentRef("production_order", Guid.NewGuid());
        AddOrder(orderA, item, scheme, 40);
        AddOrder(orderB, item, scheme, 40);
        var a = await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 40, At, "planner", Start: $"{scheme}2610-000000", ItemId: item, AllocatedTo: orderA));
        var b = await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 40, At, "planner", Start: $"{scheme}2610-000100", ItemId: item, AllocatedTo: orderB));
        var commandId = Guid.NewGuid();
        var from = SelectionSource.BySerialRange(scheme, $"{scheme}2610-000000", $"{scheme}2610-000027");
        var exchange = SelectionSource.BySerialRange(scheme, $"{scheme}2610-000100", $"{scheme}2610-000127");

        var preview = await InvokeAsync<ReassignAllocationResult>(tenant, Command(commandId, orderA, orderB, from, exchange, preview: true));

        Assert.Null(preview.Preview.Refusal);
        Assert.Equal(28, preview.Preview.Change);
        Assert.Equal(28, preview.Preview.AllocationReassignments!.Count);
        Assert.Equal($"{scheme}2610-000000", preview.Preview.AllocationReassignments[0].FromSerial);
        Assert.Equal($"{scheme}2610-000100", preview.Preview.AllocationReassignments[0].ExchangeSerial);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Empty(await db.Corrections.ToListAsync());
            Assert.Empty(await db.AllocationOverrides.ToListAsync());
            Assert.Empty(await db.AllocationReassignments.ToListAsync());
        }

        var result = await InvokeAsync<ReassignAllocationResult>(tenant, Command(commandId, orderA, orderB, from, exchange));
        var replay = await InvokeAsync<ReassignAllocationResult>(tenant, Command(commandId, orderA, orderB, from, exchange));

        Assert.False(result.Replayed);
        Assert.True(replay.Replayed);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(56, await db.AllocationOverrides.CountAsync());
            var facts = await db.AllocationReassignments.OrderBy(r => r.Pair).ThenBy(r => r.SerialOrdinal).ToListAsync();
            Assert.Equal(56, facts.Count);
            Assert.All(facts, fact =>
            {
                using var attributes = JsonDocument.Parse(fact.Attributes);
                Assert.Equal(commandId, attributes.RootElement.GetProperty("correction_id").GetGuid());
                Assert.Equal(ReassignAllocationReason.AllocationError, attributes.RootElement.GetProperty("reason_code").GetString());
                Assert.Equal(CorrectionInitiatedVia.Api, attributes.RootElement.GetProperty("initiated_via").GetString());
            });
            Assert.Equal(0, (await db.Corrections.SingleAsync()).UnitCount);
            Assert.Empty(await db.Units.ToListAsync());
            Assert.Equal(orderA.Id, (await db.SerialRanges.SingleAsync(r => r.RangeId == a.RangeId)).AllocatedToDocumentId);
            Assert.Equal(orderB.Id, (await db.SerialRanges.SingleAsync(r => r.RangeId == b.RangeId)).AllocatedToDocumentId);
        }
        var published = await PublishedEventSink.WaitForSummaryAsync<UnitAllocationReassigned>(e => e.Correction.CorrectionId == commandId, 1, TimeSpan.FromSeconds(10));
        Assert.Single(published);
        Assert.Equal(56, published[0].Reassignments.Count);
        Assert.All(published[0].Reassignments, row => Assert.False(string.IsNullOrWhiteSpace(row.Serial)));
    }

    [DatabaseFact]
    public async Task Commissioned_pair_is_first_in_preview_and_blocks_the_whole_commit()
    {
        var tenant = await fixture.NewTenantAsync("rb");
        var scheme = NewScheme("RB");
        var item = Guid.NewGuid();
        var orderA = new DocumentRef("production_order", Guid.NewGuid());
        var orderB = new DocumentRef("production_order", Guid.NewGuid());
        AddOrder(orderA, item, scheme, 3);
        AddOrder(orderB, item, scheme, 3);
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 3, At, "planner", Start: $"{scheme}2610-000000", ItemId: item, AllocatedTo: orderA));
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 3, At, "planner", Start: $"{scheme}2610-000100", ItemId: item, AllocatedTo: orderB));
        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{scheme}2610-000001")])], At.AddMinutes(10), "line", Entity, Document: orderA));
        var from = SelectionSource.ByPastedList([$"{scheme}2610-000000", $"{scheme}2610-000001", $"{scheme}2610-000002"]);
        var exchange = SelectionSource.ByPastedList([$"{scheme}2610-000100", $"{scheme}2610-000101", $"{scheme}2610-000102"]);
        var command = Command(Guid.NewGuid(), orderA, orderB, from, exchange);

        var preview = await InvokeAsync<ReassignAllocationResult>(tenant, command with { PreviewOnly = true });

        Assert.Equal(1, preview.Preview.Block);
        Assert.Equal(ReassignAllocationRules.AlreadyCommissioned, preview.Preview.AllocationReassignments![0].Rule);
        Assert.Equal(2, preview.Preview.AllocationReassignments[0].Pair);
        Assert.Equal(ReassignAllocationRules.CommissionedUnblockingStep, preview.Preview.AllocationReassignments[0].UnblockingStep);
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReassignAllocationResult>(tenant, command));
        Assert.Equal(ReassignAllocationRules.AlreadyCommissioned, refusal.Rule);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Empty(await db.AllocationOverrides.ToListAsync());
        Assert.Empty(await db.AllocationReassignments.ToListAsync());
        Assert.Empty(await db.Corrections.ToListAsync());
    }

    [DatabaseFact]
    public async Task Commissioning_a_moved_serial_honours_the_override_order()
    {
        var tenant = await fixture.NewTenantAsync("rc");
        var scheme = NewScheme("RC");
        var item = Guid.NewGuid();
        var orderA = new DocumentRef("production_order", Guid.NewGuid());
        var orderB = new DocumentRef("production_order", Guid.NewGuid());
        AddOrder(orderA, item, scheme, 2);
        AddOrder(orderB, item, scheme, 2);
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 2, At, "planner", Start: $"{scheme}2610-000000", ItemId: item, AllocatedTo: orderA));
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 2, At, "planner", Start: $"{scheme}2610-000100", ItemId: item, AllocatedTo: orderB));
        await InvokeAsync<ReassignAllocationResult>(tenant, Command(Guid.NewGuid(), orderA, orderB,
            SelectionSource.ByPastedList([$"{scheme}2610-000000"]), SelectionSource.ByPastedList([$"{scheme}2610-000100"])));

        var wrong = new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{scheme}2610-000000")])], At.AddHours(2), "line", Entity, Document: orderA);
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CommissionUnitsResult>(tenant, wrong));
        Assert.Equal("serial_reserved_for_other_order", refusal.Rule);

        var right = await InvokeAsync<CommissionUnitsResult>(tenant, wrong with { CommandId = Guid.NewGuid(), Document = orderB });
        Assert.Equal(1, right.Commissioned);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var current = await db.AllocationOverrides
            .SingleAsync(r => r.SerialValue == $"{scheme}2610-000000");
        Assert.Equal(right.Lines[0].UnitId, current.CommissionedUnitId);
        var commissioned = await db.Events.SingleAsync(e => e.UnitId == right.Lines[0].UnitId && e.EventType == UnitEventType.Commissioned);
        Assert.Equal(orderB.Id, commissioned.DocumentId);
    }

    [DatabaseFact]
    public async Task Reversal_restores_every_serial_and_is_blocked_per_pair_if_one_was_commissioned_meanwhile()
    {
        var tenant = await fixture.NewTenantAsync("rr");
        var scheme = NewScheme("RR");
        var item = Guid.NewGuid();
        var orderA = new DocumentRef("production_order", Guid.NewGuid());
        var orderB = new DocumentRef("production_order", Guid.NewGuid());
        AddOrder(orderA, item, scheme, 2);
        AddOrder(orderB, item, scheme, 2);
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 2, At, "planner", Start: $"{scheme}2610-000000", ItemId: item, AllocatedTo: orderA));
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 2, At, "planner", Start: $"{scheme}2610-000100", ItemId: item, AllocatedTo: orderB));
        var firstId = Guid.NewGuid();
        await InvokeAsync<ReassignAllocationResult>(tenant, Command(firstId, orderA, orderB,
            SelectionSource.ByPastedList([$"{scheme}2610-000000", $"{scheme}2610-000001"]),
            SelectionSource.ByPastedList([$"{scheme}2610-000100", $"{scheme}2610-000101"])));
        var reversalId = Guid.NewGuid();
        var metadata = new CorrectionMetadata(reversalId, ReassignAllocationReason.WorkOrderChanged,
            CorrectsCorrectionId: firstId, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor);
        var reversal = Command(reversalId, orderB, orderA,
            SelectionSource.ByPastedList([$"{scheme}2610-000000", $"{scheme}2610-000001"]),
            SelectionSource.ByPastedList([$"{scheme}2610-000100", $"{scheme}2610-000101"]),
            reason: ReassignAllocationReason.WorkOrderChanged, correction: metadata);

        await InvokeAsync<ReassignAllocationResult>(tenant, reversal);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var rows = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().AllocationOverrides.ToListAsync();
            Assert.Equal(orderA.Id, rows.Single(r => r.SerialValue.EndsWith("000000", StringComparison.Ordinal)).ProductionOrderId);
            Assert.Equal(orderB.Id, rows.Single(r => r.SerialValue.EndsWith("000100", StringComparison.Ordinal)).ProductionOrderId);
        }

        var secondId = Guid.NewGuid();
        await InvokeAsync<ReassignAllocationResult>(tenant, Command(secondId, orderA, orderB,
            SelectionSource.ByPastedList([$"{scheme}2610-000000"]), SelectionSource.ByPastedList([$"{scheme}2610-000100"])));
        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{scheme}2610-000000")])], At.AddHours(2), "line", Entity, Document: orderB));
        var blockedMetadata = new CorrectionMetadata(Guid.NewGuid(), ReassignAllocationReason.WorkOrderChanged,
            CorrectsCorrectionId: secondId, InitiatingRole: CorrectionInitiatingRole.ProductionSupervisor);
        var blocked = Command(blockedMetadata.CorrectionId, orderB, orderA,
            SelectionSource.ByPastedList([$"{scheme}2610-000000"]), SelectionSource.ByPastedList([$"{scheme}2610-000100"]),
            reason: ReassignAllocationReason.WorkOrderChanged, correction: blockedMetadata, preview: true);
        var preview = await InvokeAsync<ReassignAllocationResult>(tenant, blocked);
        Assert.Equal(ReassignAllocationRules.AlreadyCommissioned, preview.Preview.AllocationReassignments!.Single().Rule);
    }

    [DatabaseFact]
    public async Task Move_refuses_when_the_target_would_exceed_its_quantity()
    {
        var tenant = await fixture.NewTenantAsync("rm");
        var scheme = NewScheme("RM");
        var item = Guid.NewGuid();
        var orderA = new DocumentRef("production_order", Guid.NewGuid());
        var orderB = new DocumentRef("production_order", Guid.NewGuid());
        AddOrder(orderA, item, scheme, 2);
        AddOrder(orderB, item, scheme, 2);
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 2, At, "planner", Start: $"{scheme}2610-000000", ItemId: item, AllocatedTo: orderA));
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 2, At, "planner", Start: $"{scheme}2610-000100", ItemId: item, AllocatedTo: orderB));

        var preview = await InvokeAsync<ReassignAllocationResult>(tenant, Command(Guid.NewGuid(), orderA, orderB,
            SelectionSource.ByPastedList([$"{scheme}2610-000000"]), null, AllocationReassignmentMode.Move, preview: true));

        Assert.Equal("target_order_quantity_exceeded", preview.Preview.Refusal!.Rule);
        Assert.Equal(ReassignAllocationRules.QuantityUnblockingStep, preview.Preview.Refusal.UnblockingStep);
    }

    [DatabaseFact]
    public async Task Five_hundred_pair_preview_uses_a_bounded_number_of_statements()
    {
        const int count = 500;
        var tenant = await fixture.NewTenantAsync("rs");
        var scheme = NewScheme("RS");
        var item = Guid.NewGuid();
        var orderA = new DocumentRef("production_order", Guid.NewGuid());
        var orderB = new DocumentRef("production_order", Guid.NewGuid());
        AddOrder(orderA, item, scheme, count);
        AddOrder(orderB, item, scheme, count);
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, count, At, "planner", Start: $"{scheme}2610-000000", ItemId: item, AllocatedTo: orderA));
        await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, count, At, "planner", Start: $"{scheme}2610-001000", ItemId: item, AllocatedTo: orderB));
        var command = Command(Guid.NewGuid(), orderA, orderB,
            SelectionSource.BySerialRange(scheme, $"{scheme}2610-000000", $"{scheme}2610-000499"),
            SelectionSource.BySerialRange(scheme, $"{scheme}2610-001000", $"{scheme}2610-001499"), preview: true);

        var (preview, statements) = await StatementCounter.CountAsync(() => InvokeAsync<ReassignAllocationResult>(tenant, command));

        Assert.Equal(count, preview.Preview.Change);
        Assert.True(statements < 30, $"500-pair preview took {statements} statements");
    }
}
