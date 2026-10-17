using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T08: ADR-0001 cases 119 and 137, including transport replay.</summary>
[Collection("serialisation host")]
public sealed class ManufacturingContractTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<Guid> CommissionAsync(TenantId tenant, string serial, DateTime at, DocumentRef? order = null, Guid? location = null)
    {
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(),
            UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            at, "manufacturing", Entity, Document: order, LocationId: location, LocationKind: location is null ? null : LocationKind.Warehouse));
        return commissioned.Lines[0].UnitId!.Value;
    }

    [DatabaseFact]
    public async Task Component_return_closes_links_returns_custody_and_replay_is_a_no_op()
    {
        var tenant = await fixture.NewTenantAsync("mcr");
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var oldWarehouse = Guid.NewGuid();
        var returnWarehouse = Guid.NewGuid();
        var parent = await CommissionAsync(tenant, "MCR-P-" + Guid.NewGuid().ToString("N")[..8], At, order, oldWarehouse);
        var child = await CommissionAsync(tenant, "MCR-C-" + Guid.NewGuid().ToString("N")[..8], At.AddMinutes(1), location: oldWarehouse);
        var link = Assert.Single((await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent,
            [new ChildInput(child, Slot: "drive-1")], At.AddMinutes(5), "manufacturing", Entity, Document: order))).LinkIds);
        var messageId = Guid.NewGuid();
        var message = new ManufacturingComponentReturned(new ThinEnvelope(messageId, Entity, At.AddHours(1), "manufacturing"),
            order, returnWarehouse, [new(parent, link)]);

        var first = await InvokeAsync<ManufacturingComponentReturnedResult>(tenant, message);
        var replay = await InvokeAsync<ManufacturingComponentReturnedResult>(tenant, message);

        Assert.Equal([link], first.ClosedAssemblyLinkIds);
        Assert.Equal([child], first.ReturnedUnitIds);
        Assert.Empty(first.Refusals);
        Assert.False(first.Duplicate);
        Assert.True(replay.Duplicate);
        Assert.Equal(first.ClosedAssemblyLinkIds, replay.ClosedAssemblyLinkIds);
        Assert.Equal(first.ReturnedUnitIds, replay.ReturnedUnitIds);
        Assert.Equal(first.ProductionOrder, replay.ProductionOrder);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.NotNull((await db.AssemblyLinks.SingleAsync(row => row.LinkId == link)).RemovedAt);
            var custody = await db.Episodes.SingleAsync(row => row.UnitId == child && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
            Assert.Equal((LocationKind.Warehouse, returnWarehouse), (custody.LocationKind, custody.LocationId));
            Assert.Single(await db.Events.Where(row => row.UnitId == parent && row.EventType == UnitEventType.Disassembled).ToListAsync());
            Assert.Single(await db.ManufacturingMessageReceipts.Where(row => row.MessageId == messageId).ToListAsync());
        }

        var cancel = new ManufacturingProductionOrderQuantityChangeRequested(
            new ThinEnvelope(Guid.NewGuid(), Entity, At.AddHours(2), "manufacturing"), order, 1, 0, [parent]);
        var cancelled = await InvokeAsync<ProductionOrderQuantityChangeResult>(tenant, cancel);
        Assert.True(cancelled.CanApplyQuantityChange);
        Assert.Equal([parent], cancelled.VoidedUnitIds);
        var reissue = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<RecordAssemblyResult>(tenant,
            new RecordAssembly(Guid.NewGuid(), parent, [new ChildInput(child)], At.AddHours(3), "manufacturing", Entity, Document: order)));
        Assert.Equal("component_reissue_to_voided_unit", reissue.Rule);
    }

    [DatabaseFact]
    public async Task Quantity_reduction_selects_newest_units_reports_components_and_replay_does_not_publish_again()
    {
        var tenant = await fixture.NewTenantAsync("mqr");
        var order = new DocumentRef("production_order", Guid.NewGuid());
        var oldest = await CommissionAsync(tenant, "MQR-1-" + Guid.NewGuid().ToString("N")[..8], At, order);
        var middle = await CommissionAsync(tenant, "MQR-2-" + Guid.NewGuid().ToString("N")[..8], At.AddMinutes(1), order);
        var newest = await CommissionAsync(tenant, "MQR-3-" + Guid.NewGuid().ToString("N")[..8], At.AddMinutes(2), order);
        var component = await CommissionAsync(tenant, "MQR-C-" + Guid.NewGuid().ToString("N")[..8], At.AddMinutes(3));
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), newest,
            [new ChildInput(component)], At.AddMinutes(4), "manufacturing", Entity, Document: order));
        var messageId = Guid.NewGuid();
        var message = new ManufacturingProductionOrderQuantityChangeRequested(
            new ThinEnvelope(messageId, Entity, At.AddHours(1), "manufacturing"), order, 3, 1);

        var first = await InvokeAsync<ProductionOrderQuantityChangeResult>(tenant, message);
        Assert.Equal([newest, middle], first.SelectedUnitIds);
        Assert.Equal([middle], first.VoidedUnitIds);
        var refusal = Assert.Single(first.Refusals);
        Assert.Equal((newest, "components_still_issued"), (refusal.UnitId, refusal.Rule));
        Assert.False(first.CanApplyQuantityChange);
        var summary = Assert.Single(await PublishedEventSink.WaitForSummaryAsync<UnitProductionOrderReconciled>(
            outcome => outcome.RequestMessageId == messageId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(first.SelectedUnitIds, summary.SelectedUnitIds);
        Assert.Equal(first.VoidedUnitIds, summary.VoidedUnitIds);
        Assert.False(summary.CanApplyQuantityChange);
        var voided = Assert.Single(await PublishedEventSink.WaitForAsync<UnitVoided>(
            outcome => outcome.Header.UnitId == middle && outcome.Correction.InitiatedVia == CorrectionInitiatedVia.Integration,
            1, TimeSpan.FromSeconds(20)));
        Assert.Equal(VoidUnitReason.WorkOrderQuantityReduced, voided.Correction.ReasonCode);
        Assert.DoesNotContain(PublishedEventSink.Received.OfType<UnitVoided>(), outcome => outcome.Header.UnitId == newest);

        var summariesBeforeReplay = PublishedEventSink.Summaries.OfType<UnitProductionOrderReconciled>().Count(outcome => outcome.RequestMessageId == messageId);
        var replay = await InvokeAsync<ProductionOrderQuantityChangeResult>(tenant, message);
        Assert.True(replay.Duplicate);
        Assert.Equal(first.SelectedUnitIds, replay.SelectedUnitIds);
        Assert.Equal(first.VoidedUnitIds, replay.VoidedUnitIds);
        Assert.Equal(first.Refusals.Select(item => item.Rule), replay.Refusals.Select(item => item.Rule));
        await Task.Delay(500);
        Assert.Equal(summariesBeforeReplay, PublishedEventSink.Summaries.OfType<UnitProductionOrderReconciled>().Count(outcome => outcome.RequestMessageId == messageId));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(LifecycleStatus.PreRelease, (await db.Units.SingleAsync(unit => unit.UnitId == oldest)).LifecycleStatus);
        Assert.Equal(LifecycleStatus.Void, (await db.Units.SingleAsync(unit => unit.UnitId == middle)).LifecycleStatus);
        Assert.Equal(LifecycleStatus.PreRelease, (await db.Units.SingleAsync(unit => unit.UnitId == newest)).LifecycleStatus);
        Assert.Single(await db.Events.Where(row => row.UnitId == middle && row.EventType == UnitEventType.Voided).ToListAsync());
        Assert.Single(await db.ManufacturingMessageReceipts.Where(row => row.MessageId == messageId).ToListAsync());
    }
}
