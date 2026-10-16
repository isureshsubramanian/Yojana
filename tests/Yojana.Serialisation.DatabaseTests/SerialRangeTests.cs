using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T10: allocation with overlap detection, count == quantity, block boundaries, randomised lists, consumption by commissioning.</summary>
[Collection("serialisation host")]
public sealed class SerialRangeTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    /// <summary>Each test gets its own scheme code so ranges never collide across tests or reruns on a persisted database.</summary>
    private string NewScheme(string prefix)
    {
        var code = prefix + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(new SerialSchemeDefinition(code, IdentifierType.InternalSerial, code, "yyMM", "-", 6));
        return code;
    }

    [DatabaseFact]
    public async Task Blocks_are_contiguous_count_equals_quantity_and_overlaps_are_refused_with_the_next_free_start()
    {
        var tenant = await fixture.NewTenantAsync("sr");
        var scheme = NewScheme("SR");
        var first = await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 120, At, "t.varga", AllocatedTo: new DocumentRef("production_order", Guid.NewGuid())));
        Assert.Equal($"{scheme}2610-000000", first.StartValue);
        Assert.Equal($"{scheme}2610-000119", first.EndValue);
        Assert.Equal(120, first.Count);

        var overlap = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 500, At, "r.okafor", Start: $"{scheme}2610-000001")));
        Assert.Equal("range_overlap", overlap.Rule);
        Assert.Contains($"{scheme}2610-000120", overlap.Message);

        var auto = await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 500, At, "r.okafor"));
        Assert.Equal($"{scheme}2610-000120", auto.StartValue);
        Assert.Equal($"{scheme}2610-000619", auto.EndValue);
        Assert.False(auto.StartMoved);

        var explicitFree = await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 10, At, "r.okafor", Start: $"{scheme}2610-000700"));
        Assert.Equal($"{scheme}2610-000709", explicitFree.EndValue);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var rows = await db.SerialRanges.Where(r => r.Scheme == scheme).OrderBy(r => r.StartOrdinal).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(r.Count, r.EndOrdinal - r.StartOrdinal + 1));
        Assert.All(rows, r => Assert.Equal(SerialRangeStatus.Reserved, r.Status));
    }

    [DatabaseFact]
    public async Task A_range_never_crosses_its_block_and_a_replayed_command_returns_the_same_range()
    {
        var tenant = await fixture.NewTenantAsync("sb");
        var scheme = NewScheme("SB");
        var overflow = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 20, At, "t", Start: $"{scheme}2610-999990")));
        Assert.Equal("block_overflow", overflow.Rule);

        var command = new AllocateSerialRange(Guid.NewGuid(), scheme, 5, At, "t", Start: $"{scheme}2611-000100");
        var a = await InvokeAsync<AllocateSerialRangeResult>(tenant, command);
        var b = await InvokeAsync<AllocateSerialRangeResult>(tenant, command);
        Assert.Equal(a.RangeId, b.RangeId);
        Assert.Equal($"{scheme}2611-000104", a.EndValue);
    }

    [DatabaseFact]
    public async Task Randomised_lists_have_members_and_block_later_blocks_on_their_ordinals()
    {
        var tenant = await fixture.NewTenantAsync("sx");
        var scheme = NewScheme("SX");
        var random = await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 250, At, "r.okafor", Randomised: true, AllocatedTo: new DocumentRef("sales_order", Guid.NewGuid())));
        Assert.True(random.Randomised);
        Assert.Equal(250, random.Serials.Count);
        Assert.Equal(250, random.Serials.Distinct().Count());
        Assert.All(random.Serials, s => Assert.StartsWith($"{scheme}2610-", s, StringComparison.Ordinal));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(250, await db.SerialRangeMembers.CountAsync(m => m.RangeId == random.RangeId));
        }

        // A block that covers one of the random serials is refused; the auto path skips taken ordinals.
        var taken = random.Serials.OrderBy(s => s).First();
        var clash = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 3, At, "t", Start: taken)));
        Assert.Equal("range_overlap", clash.Rule);
        var auto = await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 3, At, "t"));
        Assert.DoesNotContain(auto.StartValue, random.Serials);
    }

    [DatabaseFact]
    public async Task Commissioning_serials_from_a_range_moves_it_to_commissioning_and_then_exhausted()
    {
        var tenant = await fixture.NewTenantAsync("sc");
        var scheme = NewScheme("SC");
        var item = Guid.NewGuid();
        var range = await InvokeAsync<AllocateSerialRangeResult>(tenant, new AllocateSerialRange(Guid.NewGuid(), scheme, 3, At, "t", ItemId: item));
        CommissionLine Line(int n) => new([new IdentifierInput(IdentifierType.InternalSerial, $"{scheme}2610-{n:D6}")]);
        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured, [Line(0), Line(1)], At.AddDays(1), "t", Guid.NewGuid()));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var row = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SerialRanges.SingleAsync(r => r.RangeId == range.RangeId);
            Assert.Equal(2, row.CommissionedCount);
            Assert.Equal(SerialRangeStatus.Commissioning, row.Status);
        }

        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured, [Line(2), Line(9)], At.AddDays(2), "t", Guid.NewGuid()));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var row = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SerialRanges.SingleAsync(r => r.RangeId == range.RangeId);
            Assert.Equal(3, row.CommissionedCount); // serial 9 was outside the range: commissioned, not counted
            Assert.Equal(SerialRangeStatus.Exhausted, row.Status);
        }
    }
}
