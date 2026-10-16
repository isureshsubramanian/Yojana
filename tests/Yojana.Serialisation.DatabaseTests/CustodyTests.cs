using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T14: ownership and custody episodes, one open row of each kind, the transition tables, snapshots and owner-scoped asset tags.</summary>
[Collection("serialisation host")]
public sealed class CustodyTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 15, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<Guid> UnitAsync(TenantId tenant, string serial, Guid? location = null)
    {
        var r = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(), UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At, "t", Entity, LocationId: location ?? Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return r.Lines[0].UnitId!.Value;
    }

    [DatabaseFact]
    public async Task Ownership_opens_transfers_in_one_transaction_closes_and_follows_the_transition_table()
    {
        var tenant = await fixture.NewTenantAsync("ow");
        var p = "OW" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var unit = await UnitAsync(tenant, $"{p}-01");
        var us = Guid.NewGuid();
        var brightwater = Guid.NewGuid();
        var caldera = Guid.NewGuid();
        var insurer = Guid.NewGuid();

        var first = await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, us, PartyType.Us, EpisodeReason.Production, At, "t", Entity, Source: EpisodeSource.Receipt));
        Assert.Null(first.ClosedEpisodeId);
        var sale = new OpenOwnership(Guid.NewGuid(), unit, brightwater, PartyType.Customer, EpisodeReason.Sale, At.AddDays(1), "sales", Entity, Document: new DocumentRef("invoice", Guid.NewGuid()), CustomerPoRef: "BWA-4471", CustomerItemRef: "BWA-VX220");
        var sold = await InvokeAsync<OpenOwnershipResult>(tenant, sale);
        Assert.Equal(first.EpisodeId, sold.ClosedEpisodeId);
        var replay = await InvokeAsync<OpenOwnershipResult>(tenant, sale);
        Assert.Equal(sold.EpisodeId, replay.EpisodeId);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var rows = await db.Episodes.Where(e => e.UnitId == unit && e.Kind == EpisodeKind.Ownership).OrderBy(e => e.ValidFrom).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(At.AddDays(1), rows[0].ValidTo);
            Assert.Equal("BWA-4471", rows[1].CustomerPoRef);
            Assert.Equal(1, rows.Count(r => r.ValidTo == null));
            var current = await db.Current.SingleAsync(c => c.UnitId == unit);
            Assert.Equal(brightwater, current.OwnerPartyRef);
            Assert.Equal(rows[1].EpisodeId, current.OpenOwnershipEpisodeId);
            var types = await db.Events.Where(e => e.UnitId == unit && e.EventType.StartsWith("ownership.")).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync();
            Assert.Equal([UnitEventType.OwnershipOpened, UnitEventType.OwnershipClosed, UnitEventType.OwnershipTransferred], types);
        }

        // customer → customer (resale) is allowed; customer → vendor is not.
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, caldera, PartyType.Customer, EpisodeReason.Transfer, At.AddDays(10), "sales", Entity));
        var bad = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, Guid.NewGuid(), PartyType.Vendor, EpisodeReason.Return, At.AddDays(11), "t", Entity)));
        Assert.Equal("ownership_transition", bad.Rule);
        // write-off to the insurer, then close
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, insurer, PartyType.Insurer, EpisodeReason.InsuranceWriteOff, At.AddDays(20), "claims", Entity));
        var closed = await InvokeAsync<CloseOwnershipResult>(tenant, new CloseOwnership(Guid.NewGuid(), unit, "settled", At.AddDays(25), "claims", Entity));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(0, await db.Episodes.CountAsync(e => e.UnitId == unit && e.Kind == EpisodeKind.Ownership && e.ValidTo == null));
            var current = await db.Current.SingleAsync(c => c.UnitId == unit);
            Assert.Null(current.OwnerPartyRef);
            Assert.Null(current.OpenOwnershipEpisodeId);
        }

        var none = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CloseOwnershipResult>(tenant, new CloseOwnership(Guid.NewGuid(), unit, "again", At.AddDays(26), "claims", Entity)));
        Assert.Equal("no_open_ownership", none.Rule);
        Assert.NotEqual(Guid.Empty, closed.ClosedEpisodeId);
    }

    [DatabaseFact]
    public async Task Custody_moves_close_and_open_in_one_event_and_refuse_impossible_moves()
    {
        var tenant = await fixture.NewTenantAsync("cu");
        var p = "CU" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var leeds = Guid.NewGuid();
        var unit = await UnitAsync(tenant, $"{p}-01", leeds);
        var carrier = Guid.NewGuid();
        var site = Guid.NewGuid();
        var site2 = Guid.NewGuid();

        var shipped = await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.InTransit, "ship", At.AddDays(1), "shipping", Entity, PartyRef: carrier, PartyType: PartyType.ThreePl, Document: new DocumentRef("delivery", Guid.NewGuid(), 4)));
        Assert.Equal(LocationKind.Warehouse, shipped.FromLocationKind);
        Assert.NotNull(shipped.ClosedEpisodeId);
        var delivered = await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.CustomerSite, "deliver", At.AddDays(3), "carrier", Entity, LocationId: site, PartyRef: Guid.NewGuid(), PartyType: PartyType.Customer, Source: EpisodeSource.CarrierPod));
        Assert.Equal(shipped.EpisodeId, delivered.ClosedEpisodeId);
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.CustomerSite, "site move", At.AddDays(30), "customer", Entity, LocationId: site2, PartyRef: Guid.NewGuid(), PartyType: PartyType.Customer));

        var impossible = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.Warehouse, "teleport", At.AddDays(31), "t", Entity, LocationId: leeds)));
        Assert.Equal("custody_transition", impossible.Rule);
        var early = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.Returning, "rma", At.AddDays(2), "t", Entity)));
        Assert.Equal("effective_before_current", early.Rule);

        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.Returning, "rma", At.AddDays(40), "customer", Entity));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.Quarantine, "return received", At.AddDays(45), "returns", Entity, LocationId: leeds));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var rows = await db.Episodes.Where(e => e.UnitId == unit && e.Kind == EpisodeKind.Custody).OrderBy(e => e.ValidFrom).ToListAsync();
        Assert.Equal(6, rows.Count); // warehouse (commission), in_transit, site, site2, returning, quarantine
        Assert.Equal(1, rows.Count(r => r.ValidTo == null));
        for (var i = 1; i < rows.Count; i++) Assert.Equal(rows[i].ValidFrom, rows[i - 1].ValidTo);
        var current = await db.Current.SingleAsync(c => c.UnitId == unit);
        Assert.Equal(LocationKind.Quarantine, current.LocationKind);
        Assert.Equal(UnitDisposition.Returned, current.Disposition);
        Assert.Equal(rows[^1].EpisodeId, current.OpenCustodyEpisodeId);
        Assert.Equal(5, await db.Events.CountAsync(e => e.UnitId == unit && e.EventType == UnitEventType.CustodyChanged));
    }

    [DatabaseFact]
    public async Task Customer_asset_tags_are_unique_per_owner_not_per_tenant()
    {
        var tenant = await fixture.NewTenantAsync("tg");
        var p = "TG" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var a = await UnitAsync(tenant, $"{p}-A");
        var b = await UnitAsync(tenant, $"{p}-B");
        var c = await UnitAsync(tenant, $"{p}-C");
        var owner1 = Guid.NewGuid();
        var owner2 = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), a, owner1, PartyType.Customer, EpisodeReason.Sale, At.AddDays(1), "s", Entity));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), b, owner1, PartyType.Customer, EpisodeReason.Sale, At.AddDays(1), "s", Entity));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), c, owner2, PartyType.Customer, EpisodeReason.Sale, At.AddDays(1), "s", Entity));

        var tag = new IdentifierInput(IdentifierType.CustomerAssetTag, "LAB2-0088");
        await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), a, tag, At.AddDays(2), "cust", Entity));
        var sameOwner = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), b, tag, At.AddDays(2), "cust", Entity)));
        Assert.Equal("identifier_in_use", sameOwner.Rule);
        var otherOwner = await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), c, tag, At.AddDays(2), "cust", Entity));
        Assert.Equal("LAB2-0088", otherOwner.NormalisedValue);
    }
}
