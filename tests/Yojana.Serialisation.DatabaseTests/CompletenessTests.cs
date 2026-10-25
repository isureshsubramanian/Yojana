using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T12: expected serialised components from the BOM port, the completeness query, and the block-or-warn release guard.</summary>
[Collection("serialisation host")]
public sealed class CompletenessTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<Guid> UnitAsync(TenantId tenant, Guid item, string serial) =>
        (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At, "t", Entity))).Lines[0].UnitId!.Value;

    [DatabaseFact]
    public async Task Missing_and_unexpected_slots_are_reported_and_block_release_under_the_block_policy()
    {
        var tenant = await fixture.NewTenantAsync("cp");
        var ports = fixture.Services.GetRequiredService<InMemoryItemPorts>();
        var p = "CP" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        var pcbItem = Guid.NewGuid();
        var pumpItem = Guid.NewGuid();
        var wrongPumpItem = Guid.NewGuid();
        ports.RegisterBom(parentItem, [new ExpectedComponent("A1", pcbItem, "PCB-07", 1), new ExpectedComponent("SB", pumpItem, "PU-3", 1)]);
        await fixture.EnableErpBomCompletenessAsync(tenant);

        var complete = await UnitAsync(tenant, parentItem, $"{p}-01");
        var missing = await UnitAsync(tenant, parentItem, $"{p}-02");
        var wrong = await UnitAsync(tenant, parentItem, $"{p}-03");
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), complete, [new ChildInput(await UnitAsync(tenant, pcbItem, $"{p}-PCBA"), Slot: "A1"), new ChildInput(await UnitAsync(tenant, pumpItem, $"{p}-PUA"), Slot: "SB")], At.AddHours(1), "t", Entity));
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), missing, [new ChildInput(await UnitAsync(tenant, pcbItem, $"{p}-PCBB"), Slot: "A1")], At.AddHours(1), "t", Entity));
        await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), wrong, [new ChildInput(await UnitAsync(tenant, pcbItem, $"{p}-PCBC"), Slot: "A1"), new ChildInput(await UnitAsync(tenant, wrongPumpItem, $"{p}-PUBX"), Slot: "SB")], At.AddHours(1), "t", Entity));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var query = scope.ServiceProvider.GetRequiredService<AsBuiltCompleteness>();
            var report = await query.QueryAsync([(complete, parentItem), (missing, parentItem), (wrong, parentItem)], DateOnly.FromDateTime(At), CancellationToken.None);
            Assert.True(report[0].IsComplete);
            Assert.Equal(1, report[1].MissingRequiredComponentSerials);
            Assert.Equal(SlotStatus.Missing, report[1].Slots.Single(s => s.Slot == "SB").Outcome);
            Assert.Equal($"{p}PCBB", report[1].Slots.Single(s => s.Slot == "A1").Captured[0].Serial);
            Assert.Equal(SlotStatus.UnexpectedItem, report[2].Slots.Single(s => s.Slot == "SB").Outcome);
            Assert.Equal(ItemSerialisationProfile.Completeness.Block, report[2].Policy);
        }

        var ok = await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), complete, At.AddHours(2), "q", Entity, "sig"));
        Assert.Equal(LifecycleStatus.Released, ok.LifecycleStatus);
        var blocked = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), missing, At.AddHours(2), "q", Entity, "sig")));
        Assert.Equal("incomplete_asbuilt", blocked.Rule);
        Assert.Contains("SB PU-3", blocked.Message);
        var blockedWrong = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), wrong, At.AddHours(2), "q", Entity, "sig")));
        Assert.Equal("incomplete_asbuilt", blockedWrong.Rule);
    }

    [DatabaseFact]
    public async Task Under_the_warn_policy_an_incomplete_unit_still_releases()
    {
        var tenant = await fixture.NewTenantAsync("cw");
        var ports = fixture.Services.GetRequiredService<InMemoryItemPorts>();
        var p = "CW" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var parentItem = Guid.NewGuid();
        ports.Register(InMemoryItemPorts.DefaultProfile(parentItem) with { AsBuiltCompleteness = ItemSerialisationProfile.Completeness.Warn });
        ports.RegisterBom(parentItem, [new ExpectedComponent("A1", Guid.NewGuid(), "PCB-07", 1)]);
        await fixture.EnableErpBomCompletenessAsync(tenant);
        var unit = await UnitAsync(tenant, parentItem, $"{p}-01");
        var released = await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), unit, At.AddHours(2), "q", Entity, "sig"));
        Assert.Equal(LifecycleStatus.Released, released.LifecycleStatus);
        using var scope = fixture.ScopeFor(tenant);
        var report = await scope.ServiceProvider.GetRequiredService<AsBuiltCompleteness>().CheckAsync(unit, parentItem, DateOnly.FromDateTime(At), CancellationToken.None);
        Assert.Equal(1, report.MissingRequiredComponentSerials);
        Assert.Equal(ItemSerialisationProfile.Completeness.Warn, report.Policy);
    }

    [DatabaseFact]
    public async Task An_item_without_a_bom_has_nothing_expected_and_releases()
    {
        var tenant = await fixture.NewTenantAsync("cn");
        var p = "CN" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var unit = await UnitAsync(tenant, Guid.NewGuid(), $"{p}-01");
        var released = await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), unit, At.AddHours(2), "q", Entity, "sig"));
        Assert.Equal(LifecycleStatus.Released, released.LifecycleStatus);
    }

    [DatabaseFact]
    public async Task Synced_BOM_does_not_block_when_the_tenant_setting_is_off()
    {
        var tenant = await fixture.NewTenantAsync("coff");
        var parentItem = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().RegisterBom(parentItem,
            [new ExpectedComponent("A1", Guid.NewGuid(), "FIC-NVME", 1)]);
        var unit = await UnitAsync(tenant, parentItem, "COFF-" + Guid.NewGuid().ToString("N")[..8]);

        var released = await InvokeAsync<ReleaseUnitResult>(tenant,
            new ReleaseUnit(Guid.NewGuid(), unit, At.AddHours(2), "q", Entity, "sig"));

        Assert.Equal(LifecycleStatus.Released, released.LifecycleStatus);
        using var scope = fixture.ScopeFor(tenant);
        var report = await scope.ServiceProvider.GetRequiredService<AsBuiltCompleteness>()
            .CheckAsync(unit, parentItem, DateOnly.FromDateTime(At), default);
        Assert.True(report.IsComplete);
        Assert.Empty(report.Slots);
    }
}
