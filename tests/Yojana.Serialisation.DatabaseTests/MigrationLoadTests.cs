using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P9-T10 (OD-52, OD-53): ApplyMigratedUnits writes a load plan chunk set-based; RollBackMigrationLoad undoes an untouched load.</summary>
[Collection("serialisation host")]
public sealed class MigrationLoadTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime Received = new(2024, 3, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Shipped = new(2024, 5, 20, 14, 30, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        return await bus.InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static string Prefix() => "MG" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    /// <summary>A rack server shipped to a customer, with a manufacturer serial beside the incumbent's serial.</summary>
    private static MigratedUnit ShippedServer(Guid item, string serial, Guid customer) => new(
        Guid.CreateVersion7(), item, HistoryDepth.Full, Received,
        [new IdentifierInput(IdentifierType.InternalSerial, serial), new IdentifierInput(IdentifierType.ManufacturerSerial, "MF-" + serial)],
        [
            new MigratedHistoryEvent(Received, "received", "source", "Goods receipt", 4101, "V-ARVEN", "WH-01", 1),
            new MigratedHistoryEvent(Shipped, "shipped", "source", "Delivery", 7702, "C-NANDIRA", "WH-01", -1),
        ],
        null, new MigratedOwnership(customer, PartyType.Customer, Shipped, false), ConditionGrades.New, "inferred");

    /// <summary>A storage shelf still in the warehouse at cutover, with only an opening balance.</summary>
    private static MigratedUnit StockShelf(Guid item, string serial, Guid warehouse) => new(
        Guid.CreateVersion7(), item, HistoryDepth.OpeningOnly, Received,
        [new IdentifierInput(IdentifierType.InternalSerial, serial)],
        [new MigratedHistoryEvent(Received, "opening_balance", "reconstructed", null, null, null, "WH-02", 1)],
        new MigratedCustody(warehouse, LocationKind.Warehouse, Received), null, ConditionGrades.New, "inferred");

    private static ApplyMigratedUnits Chunk(Guid load, params MigratedUnit[] units) =>
        new(load, Entity, "user:migration-lead", "migration:test", units);

    [DatabaseFact]
    public async Task Apply_writes_migrated_units_with_history_custody_ownership_and_current_state_and_a_resend_writes_nothing()
    {
        var tenant = await fixture.NewTenantAsync("mg1");
        var item = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var prefix = Prefix();
        var server = ShippedServer(item, prefix + "-001", customer);
        var shelf = StockShelf(item, prefix + "-002", warehouse);
        var load = Guid.NewGuid();

        var result = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(load, server, shelf));
        Assert.Equal(2, result.Applied);
        Assert.Equal(0, result.Refused);
        // server: created + 2 history + ownership; shelf: created + 1 history + custody
        Assert.Equal(7, result.EventsWritten);

        var serverUnit = result.Units.Single(u => u.PlanUnitId == server.PlanUnitId).UnitId!.Value;
        var shelfUnit = result.Units.Single(u => u.PlanUnitId == shelf.PlanUnitId).UnitId!.Value;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var units = await db.Units.Where(u => u.UnitId == serverUnit || u.UnitId == shelfUnit).ToListAsync();
            Assert.All(units, u => Assert.Equal(UnitOrigin.Migrated, u.Origin));
            Assert.Equal(HistoryDepth.OpeningOnly, units.Single(u => u.UnitId == shelfUnit).HistoryDepth);
            Assert.Equal(Received, units.Single(u => u.UnitId == serverUnit).CreatedEventTime);
            Assert.Equal(3, await db.Identifiers.CountAsync(i => i.UnitId == serverUnit || i.UnitId == shelfUnit));

            var events = await db.Events.Where(e => e.UnitId == serverUnit).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync();
            Assert.Equal([UnitEventType.Commissioned, UnitEventType.MigratedHistory, UnitEventType.MigratedHistory, UnitEventType.OwnershipOpened], events);

            var owner = await db.Episodes.SingleAsync(e => e.UnitId == serverUnit && e.ValidTo == null);
            Assert.Equal(EpisodeKind.Ownership, owner.Kind);
            Assert.Equal(customer, owner.PartyRef);
            Assert.Equal(Shipped, owner.ValidFrom);
            Assert.Equal(EpisodeSource.Migration, owner.Source);
            var custody = await db.Episodes.SingleAsync(e => e.UnitId == shelfUnit && e.ValidTo == null);
            Assert.Equal(EpisodeKind.Custody, custody.Kind);
            Assert.Equal(warehouse, custody.LocationId);

            var current = await db.Current.Where(c => c.UnitId == serverUnit || c.UnitId == shelfUnit).ToListAsync();
            Assert.Equal(customer, current.Single(c => c.UnitId == serverUnit).OwnerPartyRef);
            Assert.Equal(warehouse, current.Single(c => c.UnitId == shelfUnit).LocationId);
            Assert.All(current, c => Assert.Equal(ConditionGrades.New, c.ConditionGrade));
            Assert.All(current, c => Assert.Equal(LifecycleStatus.Released, c.LifecycleStatus));

            // OD-52: no release certificate is triggered for migrated owners.
            Assert.Equal(0, await db.CertificateTriggers.CountAsync(t => t.UnitId == serverUnit));
            Assert.Equal(2, await db.MigrationLoadUnits.CountAsync(r => r.LoadId == load));
        }

        var resend = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(load, server, shelf));
        Assert.Equal(2, resend.AlreadyApplied);
        Assert.Equal(0, resend.EventsWritten);
        Assert.Equal(serverUnit, resend.Units.Single(u => u.PlanUnitId == server.PlanUnitId).UnitId);
    }

    [DatabaseFact]
    public async Task Apply_refuses_units_whose_identifier_is_open_elsewhere_repeated_in_the_chunk_or_carrying_an_unknown_grade()
    {
        var tenant = await fixture.NewTenantAsync("mg2");
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var first = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(Guid.NewGuid(), StockShelf(item, prefix + "-010", Guid.NewGuid())));
        Assert.Equal(1, first.Applied);

        var again = StockShelf(item, prefix + "-010", Guid.NewGuid());
        var twinA = StockShelf(item, prefix + "-011", Guid.NewGuid());
        var twinB = StockShelf(item, prefix + "-011", Guid.NewGuid());
        var badGrade = StockShelf(item, prefix + "-012", Guid.NewGuid()) with { ConditionGrade = "like_new_ish" };
        var good = StockShelf(item, prefix + "-013", Guid.NewGuid());
        var result = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(Guid.NewGuid(), again, twinA, twinB, badGrade, good));

        Assert.Equal(1, result.Applied);
        Assert.Equal(4, result.Refused);
        Assert.Contains(result.Units.Single(u => u.PlanUnitId == again.PlanUnitId).Reasons, r => r.EndsWith("already_open_on_another_unit", StringComparison.Ordinal));
        Assert.Contains(result.Units.Single(u => u.PlanUnitId == twinA.PlanUnitId).Reasons, r => r.EndsWith("duplicate_in_batch", StringComparison.Ordinal));
        Assert.Contains("condition_grade_unknown", result.Units.Single(u => u.PlanUnitId == badGrade.PlanUnitId).Reasons);
        Assert.Equal(MigratedUnitOutcome.Applied, result.Units.Single(u => u.PlanUnitId == good.PlanUnitId).Outcome);
    }

    [DatabaseFact]
    public async Task Rollback_removes_an_untouched_load_and_refuses_one_with_a_later_fact()
    {
        var tenant = await fixture.NewTenantAsync("mg3");
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var load = Guid.NewGuid();
        var applied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(load,
            ShippedServer(item, prefix + "-020", Guid.NewGuid()), StockShelf(item, prefix + "-021", Guid.NewGuid())));
        var unitIds = applied.Units.Select(u => u.UnitId!.Value).ToList();

        var rolledBack = await InvokeAsync<RollBackMigrationLoadResult>(tenant, new RollBackMigrationLoad(load, "user:admin"));
        Assert.Equal(RollBackMigrationLoadResult.RolledBack, rolledBack.Outcome);
        Assert.Equal(2, rolledBack.UnitsRemoved);
        Assert.Equal(7, rolledBack.EventsRemoved);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(0, await db.Units.CountAsync(u => unitIds.Contains(u.UnitId)));
            Assert.Equal(0, await db.Identifiers.CountAsync(i => unitIds.Contains(i.UnitId)));
            Assert.Equal(0, await db.Episodes.CountAsync(e => unitIds.Contains(e.UnitId)));
            Assert.Equal(0, await db.Current.CountAsync(c => unitIds.Contains(c.UnitId)));
            Assert.Equal(0, await db.Events.CountAsync(e => unitIds.Contains(e.UnitId)));
            Assert.Equal(0, await db.MigrationLoadUnits.CountAsync(r => r.LoadId == load));
        }

        // The serials are free again: the same plan loads afresh after a rollback.
        var reload = Guid.NewGuid();
        var reapplied = await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(reload, StockShelf(item, prefix + "-021", Guid.NewGuid())));
        Assert.Equal(1, reapplied.Applied);

        var touchedUnit = reapplied.Units.Single().UnitId!.Value;
        await InvokeAsync<UnitFlagResult>(tenant, new SetUnitFlag(Guid.NewGuid(), touchedUnit, OverlayFlag.Lost, new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc), "stores", Entity, "missing at cycle count"));
        var refused = await InvokeAsync<RollBackMigrationLoadResult>(tenant, new RollBackMigrationLoad(reload, "user:admin"));
        Assert.Equal(RollBackMigrationLoadResult.RefusedTouched, refused.Outcome);
        Assert.Equal([touchedUnit], refused.TouchedUnitIds);
        Assert.Equal(0, refused.UnitsRemoved);

        var nothing = await InvokeAsync<RollBackMigrationLoadResult>(tenant, new RollBackMigrationLoad(Guid.NewGuid(), "user:admin"));
        Assert.Equal(RollBackMigrationLoadResult.NothingToRemove, nothing.Outcome);
    }

    [DatabaseFact]
    public async Task Load_units_are_listed_in_plan_order_with_a_keyset_cursor()
    {
        var tenant = await fixture.NewTenantAsync("mg4");
        var item = Guid.NewGuid();
        var prefix = Prefix();
        var load = Guid.NewGuid();
        await InvokeAsync<ApplyMigratedUnitsResult>(tenant, Chunk(load,
            StockShelf(item, prefix + "-030", Guid.NewGuid()), StockShelf(item, prefix + "-031", Guid.NewGuid()), StockShelf(item, prefix + "-032", Guid.NewGuid())));

        var first = await InvokeAsync<MigrationLoadUnitsPage>(tenant, new MigrationLoadUnitsQuery(load, null, 2));
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.NextAfterPlanUnitId);
        var second = await InvokeAsync<MigrationLoadUnitsPage>(tenant, new MigrationLoadUnitsQuery(load, first.NextAfterPlanUnitId, 2));
        Assert.Single(second.Items);
        Assert.Null(second.NextAfterPlanUnitId);
    }
}
