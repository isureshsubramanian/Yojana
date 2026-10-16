using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T11: as-built trees as recorded facts; tree integrity; dormant and reassembled; rework swap; as-built in the release record.</summary>
[Collection("serialisation host")]
public sealed class AssemblyTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 12, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<Guid> UnitAsync(TenantId tenant, Guid item, string serial, string origin = UnitOrigin.Manufactured)
    {
        var r = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, origin, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At, "t", Entity));
        return r.Lines[0].UnitId!.Value;
    }

    private async Task<string> StatusAsync(TenantId tenant, Guid unit)
    {
        using var scope = fixture.ScopeFor(tenant);
        return (await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Units.AsNoTracking().SingleAsync(u => u.UnitId == unit)).LifecycleStatus;
    }

    [DatabaseFact]
    public async Task Assembly_records_serialised_lot_and_phantom_children_and_the_release_record_freezes_them()
    {
        var tenant = await fixture.NewTenantAsync("as");
        var p = "AS" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var parent = await UnitAsync(tenant, parentItem, $"{p}-P1");
        var pcb = await UnitAsync(tenant, Guid.NewGuid(), $"{p}-PCBA");
        var pump = await UnitAsync(tenant, Guid.NewGuid(), $"{p}-PUA");
        var gasketItem = Guid.NewGuid();
        var kitSn = Guid.NewGuid();

        var result = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent,
        [
            new ChildInput(pcb, Slot: "A1"),
            new ChildInput(pump, Slot: "SB", ThroughPhantomItemId: kitSn),
            new ChildInput(null, gasketItem, Guid.NewGuid(), 2, Slot: null),
        ], At.AddHours(1), "line2", Entity, Document: new DocumentRef("production_order", Guid.NewGuid())));
        Assert.Equal(3, result.LinkIds.Count);
        Assert.Equal(LifecycleStatus.PreRelease, result.ParentLifecycleStatus);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var links = await db.AssemblyLinks.Where(l => l.ParentUnitId == parent).ToListAsync();
            Assert.Equal(2, links.Count(l => l.ChildUnitId != null));
            var lot = links.Single(l => l.ChildUnitId == null);
            Assert.Equal(gasketItem, lot.ChildItemId);
            Assert.Equal(2m, lot.Quantity);
            Assert.Equal(kitSn, links.Single(l => l.ChildUnitId == pump).ThroughPhantomItemId);
            Assert.Equal([UnitEventType.Commissioned, UnitEventType.Installed], await db.Events.Where(e => e.UnitId == pcb).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync());
            Assert.Equal(UnitEventType.Assembled, (await db.Events.Where(e => e.UnitId == parent).OrderByDescending(e => e.Seq).FirstAsync()).EventType);
            Assert.Contains("parent_unit_id", (await db.Current.SingleAsync(c => c.UnitId == pcb)).Attributes);
        }

        var released = await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), parent, At.AddHours(2), "q.mahler", Entity, "sig"));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var record = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().BuildRecords.SingleAsync(b => b.BuildRecordId == released.BuildRecordId);
            Assert.Contains($"{p}PCBA", record.Payload);
            Assert.Contains("\"slot\":\"SB\"", record.Payload);
            Assert.Contains(gasketItem.ToString(), record.Payload);
        }
    }

    [DatabaseFact]
    public async Task A_child_has_one_open_parent_and_a_unit_cannot_contain_itself_or_an_ancestor()
    {
        var tenant = await fixture.NewTenantAsync("at");
        var p = "AT" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var a = await UnitAsync(tenant, item, $"{p}-A");
        var b = await UnitAsync(tenant, item, $"{p}-B");
        var c = await UnitAsync(tenant, item, $"{p}-C");
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), a, [new ChildInput(b)], At.AddHours(1), "t", Entity));
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), b, [new ChildInput(c)], At.AddHours(2), "t", Entity));

        var twice = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), c, [new ChildInput(b)], At.AddHours(3), "t", Entity)));
        Assert.Equal("assembly_cycle", twice.Rule);
        var self = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), a, [new ChildInput(a)], At.AddHours(3), "t", Entity)));
        Assert.Equal("assembly_cycle", self.Rule);
        var d = await UnitAsync(tenant, item, $"{p}-D");
        var elsewhere = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), d, [new ChildInput(c)], At.AddHours(3), "t", Entity)));
        Assert.Equal("child_already_installed", elsewhere.Rule);
    }

    [DatabaseFact]
    public async Task Full_disassembly_parks_a_released_parent_as_dormant_and_reassembly_restores_it()
    {
        var tenant = await fixture.NewTenantAsync("ad");
        var p = "AD" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var parent = await UnitAsync(tenant, item, $"{p}-P");
        var x = await UnitAsync(tenant, item, $"{p}-X");
        var y = await UnitAsync(tenant, item, $"{p}-Y");
        var links = (await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent, [new ChildInput(x, Slot: "1"), new ChildInput(y, Slot: "2")], At.AddHours(1), "t", Entity))).LinkIds;
        await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), parent, At.AddHours(2), "q", Entity, "sig"));

        var partial = await InvokeAsync<RecordDisassemblyResult>(tenant, new RecordDisassembly(Guid.NewGuid(), parent, [links[0]], At.AddDays(1), "svc", Entity, "repair"));
        Assert.Equal(LifecycleStatus.Released, partial.ParentLifecycleStatus);
        var full = await InvokeAsync<RecordDisassemblyResult>(tenant, new RecordDisassembly(Guid.NewGuid(), parent, null, At.AddDays(2), "svc", Entity, "teardown"));
        Assert.Single(full.ClosedLinkIds);
        Assert.Equal(LifecycleStatus.Dormant, full.ParentLifecycleStatus);
        Assert.Equal(LifecycleStatus.Dormant, await StatusAsync(tenant, parent));

        var again = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent, [new ChildInput(x, Slot: "1")], At.AddDays(3), "svc", Entity));
        Assert.Equal(LifecycleStatus.Released, again.ParentLifecycleStatus);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(UnitEventType.Reassembled, (await db.Events.Where(e => e.UnitId == parent).OrderByDescending(e => e.Seq).FirstAsync()).EventType);
        Assert.Equal([UnitEventType.Commissioned, UnitEventType.Installed, UnitEventType.Removed, UnitEventType.Installed], await db.Events.Where(e => e.UnitId == x).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync());
        Assert.Equal(3, await db.AssemblyLinks.CountAsync(l => l.ParentUnitId == parent));
        Assert.Equal(1, await db.AssemblyLinks.CountAsync(l => l.ParentUnitId == parent && l.RemovedAt == null));
        // the reassembly froze BuildRecord v2 with the new as-built (one child now)
        var records = await db.BuildRecords.Where(b => b.UnitId == parent).OrderBy(b => b.Version).ToListAsync();
        Assert.Equal([1, 2], records.Select(r => r.Version));
        Assert.Contains($"{p}X", records[1].Payload);
        Assert.DoesNotContain($"{p}Y", records[1].Payload);
    }

    [DatabaseFact]
    public async Task Rework_swaps_a_component_in_one_command_and_a_replay_is_harmless()
    {
        var tenant = await fixture.NewTenantAsync("ar");
        var p = "AR" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var item = Guid.NewGuid();
        var parent = await UnitAsync(tenant, item, $"{p}-P");
        var old = await UnitAsync(tenant, item, $"{p}-OLD");
        var fresh = await UnitAsync(tenant, item, $"{p}-NEW");
        var first = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent, [new ChildInput(old, Slot: "SB")], At.AddHours(1), "t", Entity));
        var swap = new RecordAssembly(Guid.NewGuid(), parent, [new ChildInput(fresh, Slot: "SB")], At.AddHours(5), "rework", Entity, Removals: [first.LinkIds[0]], RemovalReason: "failed test");
        var swapped = await InvokeAsync<RecordAssemblyResult>(tenant, swap);
        Assert.Single(swapped.ClosedLinkIds);
        Assert.Single(swapped.LinkIds);
        var replay = await InvokeAsync<RecordAssemblyResult>(tenant, swap);
        Assert.Equal(swapped.LinkIds, replay.LinkIds);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(UnitDisposition.Available, (await db.Current.SingleAsync(c => c.UnitId == old)).Disposition);
        Assert.Equal(UnitDisposition.InProgress, (await db.Current.SingleAsync(c => c.UnitId == fresh)).Disposition);
        Assert.Equal(2, await db.AssemblyLinks.CountAsync(l => l.ParentUnitId == parent));
    }
}
