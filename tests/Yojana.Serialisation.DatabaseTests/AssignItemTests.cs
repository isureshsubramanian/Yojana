using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T13: effective-dated item and model version with reasons (case 42).</summary>
[Collection("serialisation host")]
public sealed class AssignItemTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Upgrade_closes_the_current_assignment_opens_the_next_and_updates_current_state()
    {
        var tenant = await fixture.NewTenantAsync("ai");
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var modelV3 = Guid.NewGuid();
        var p = "AI" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var unit = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), itemA, UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-01")])], At, "t", Entity))).Lines[0].UnitId!.Value;

        var command = new AssignItem(Guid.NewGuid(), unit, itemB, ItemAssignmentReason.Upgrade, At.AddDays(30), "svc", Entity, modelV3, Note: "field upgrade kit K-7");
        var result = await InvokeAsync<AssignItemResult>(tenant, command);
        Assert.Equal(itemA, result.PreviousItemId);
        Assert.Equal(itemB, result.NewItemId);
        var replay = await InvokeAsync<AssignItemResult>(tenant, command);
        Assert.Equal(result.NewAssignmentId, replay.NewAssignmentId);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var rows = await db.ItemAssignments.Where(a => a.UnitId == unit).OrderBy(a => a.ValidFrom).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(At.AddDays(30), rows[0].ValidTo);
        Assert.Equal(ItemAssignmentReason.Initial, rows[0].Reason);
        Assert.Equal(ItemAssignmentReason.Upgrade, rows[1].Reason);
        Assert.Equal(modelV3, rows[1].ModelVersionId);
        Assert.Null(rows[1].ValidTo);
        var current = await db.Current.SingleAsync(c => c.UnitId == unit);
        Assert.Equal(itemB, current.ItemId);
        Assert.Equal(modelV3, current.ModelVersionId);

        var same = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AssignItemResult>(tenant, new AssignItem(Guid.NewGuid(), unit, itemB, ItemAssignmentReason.Relabel, At.AddDays(31), "svc", Entity, modelV3)));
        Assert.Equal("no_change", same.Rule);
        var early = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AssignItemResult>(tenant, new AssignItem(Guid.NewGuid(), unit, itemA, ItemAssignmentReason.Conversion, At.AddDays(1), "svc", Entity)));
        Assert.Equal("effective_before_current", early.Rule);
        var initial = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AssignItemResult>(tenant, new AssignItem(Guid.NewGuid(), unit, itemA, ItemAssignmentReason.Initial, At.AddDays(40), "svc", Entity)));
        Assert.Equal("reason_unknown", initial.Rule);
    }
}
