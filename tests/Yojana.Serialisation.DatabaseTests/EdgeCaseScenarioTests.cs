using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>
/// P1-T22: the edge cases this phase owns that no earlier task's test exercises directly (ADR-0001 coverage matrix;
/// `docs/plan/edge-case-allocation.md`). Each test names its cases; the phase file maps every one of the 44 cases to a test.
/// </summary>
[Collection("serialisation host")]
public sealed class EdgeCaseScenarioTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();

    private InMemoryItemPorts Items => fixture.Services.GetRequiredService<InMemoryItemPorts>();

    private static ThinEnvelope Env(DateTime at) => new(Guid.NewGuid(), Entity, at, "cx01-test");

    private static DocumentLine Line(int no, Guid item, Guid? location, params string[] serials) => new(no, item, serials.Length, serials.Select(s => new DocumentSerial(s)).ToList(), location, location is null ? null : LocationKind.Warehouse);

    private static string Prefix(string tag) => tag + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();

    private async Task<Guid> UnitAsync(TenantId tenant, Guid item, string serial, string origin = UnitOrigin.Purchased, Guid? location = null, Guid? lot = null, bool? brandIsOurs = null)
    {
        var r = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, origin, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)], LotId: lot)], At, "t", Entity, LocationId: location ?? Guid.NewGuid(), LocationKind: LocationKind.Warehouse, BrandIsOurs: brandIsOurs));
        Assert.Equal(1, r.Commissioned);
        return r.Lines[0].UnitId!.Value;
    }

    private async Task<T> WithDbAsync<T>(TenantId tenant, Func<SerialisationDbContext, Task<T>> body)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await body(scope.ServiceProvider.GetRequiredService<SerialisationDbContext>());
    }

    [DatabaseFact] // cases 18, 19: serial and lot on one unit; expiry required by the item is an error, never a silent omission
    public async Task Lot_rides_with_the_serial_and_a_controlled_item_refuses_a_blank_expiry()
    {
        var tenant = await fixture.NewTenantAsync("e18");
        var p = Prefix("E18");
        var lot = Guid.NewGuid();
        var unit = await UnitAsync(tenant, Guid.NewGuid(), $"{p}-01", lot: lot);
        var (unitLot, currentLot) = await WithDbAsync(tenant, async db => ((await db.Units.SingleAsync(u => u.UnitId == unit)).LotId, (await db.Current.SingleAsync(c => c.UnitId == unit)).LotId));
        Assert.Equal(lot, unitLot);
        Assert.Equal(lot, currentLot);

        var controlled = Guid.NewGuid();
        Items.Register(InMemoryItemPorts.DefaultProfile(controlled) with { ExpiryRequired = true, SerialisationPoint = SerialisationPoint.Receipt });
        var blank = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CommissionUnitsResult>(tenant,
            new CommissionUnits(Guid.NewGuid(), controlled, UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-02")], LotId: lot)], At, "t", Entity)));
        Assert.Equal("line_refused", blank.Rule);
        Assert.Contains("expiry_required", blank.Message);
        var dated = await InvokeAsync<CommissionUnitsResult>(tenant,
            new CommissionUnits(Guid.NewGuid(), controlled, UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-02")], LotId: lot, ExpiryDate: new DateOnly(2028, 12, 31))], At, "t", Entity));
        Assert.Equal(1, dated.Commissioned);
        Assert.Equal(new DateOnly(2028, 12, 31), await WithDbAsync(tenant, async db => (await db.Units.SingleAsync(u => u.UnitId == dated.Lines[0].UnitId)).ExpiryDate));
    }

    [DatabaseFact] // cases 20, 41, 46: consignment and subcontract keep ownership with us while custody is elsewhere; a site move is a move
    public async Task Consignment_and_subcontract_custody_leave_ownership_with_us()
    {
        var tenant = await fixture.NewTenantAsync("e20");
        var p = Prefix("E20");
        var item = Guid.NewGuid();
        var us = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var subcontractor = Guid.NewGuid();
        var a = await UnitAsync(tenant, item, $"{p}-01");
        var b = await UnitAsync(tenant, item, $"{p}-02");
        foreach (var u in new[] { a, b }) await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), u, us, PartyType.Us, EpisodeReason.Receipt, At, "t", Entity, Source: EpisodeSource.Receipt));

        var site1 = Guid.NewGuid();
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), a, LocationKind.CustomerSite, EpisodeReason.Consignment, At.AddDays(1), "sales", Entity, LocationId: site1, PartyRef: customer, PartyType: PartyType.Customer, Document: new DocumentRef("consignment_delivery", Guid.NewGuid(), 1)));
        var site2 = Guid.NewGuid();
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), a, LocationKind.CustomerSite, "site move", At.AddDays(30), "customer", Entity, LocationId: site2, PartyRef: customer, PartyType: PartyType.Customer));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), b, LocationKind.AtSubcontractor, EpisodeReason.Subcontract, At.AddDays(1), "api:acme-coatings", Entity, PartyRef: subcontractor, PartyType: PartyType.Subcontractor, Document: new DocumentRef("subcontract_order", Guid.NewGuid(), 1)));

        await WithDbAsync(tenant, async db =>
        {
            var ca = await db.Current.SingleAsync(c => c.UnitId == a);
            Assert.Equal(us, ca.OwnerPartyRef);
            Assert.Equal(customer, ca.CustodyPartyRef);
            Assert.Equal(site2, ca.LocationId);
            var episodes = await db.Episodes.Where(e => e.UnitId == a && e.Kind == EpisodeKind.Custody).OrderBy(e => e.ValidFrom).ToListAsync();
            Assert.Equal(EpisodeReason.Consignment, episodes[1].Reason);
            Assert.Equal(site1, episodes[1].LocationId);
            Assert.Equal(At.AddDays(30), episodes[1].ValidTo);
            Assert.Single(episodes, e => e.ValidTo == null);
            var cb = await db.Current.SingleAsync(c => c.UnitId == b);
            Assert.Equal(us, cb.OwnerPartyRef);
            Assert.Equal(LocationKind.AtSubcontractor, cb.LocationKind);
            Assert.Equal(subcontractor, cb.CustodyPartyRef);
            Assert.Equal("api:acme-coatings", (await db.Events.SingleAsync(e => e.UnitId == b && e.EventType == UnitEventType.CustodyChanged)).ActorRef);
            return 0;
        });
    }

    [DatabaseFact] // case 30 (and 41's loss path): lost and stolen are overlay flags; a reappearance after a custody fact is an investigation; nothing is deleted
    public async Task Lost_and_stolen_flags_overlay_the_unit_and_a_reappearance_raises_an_investigation()
    {
        var tenant = await fixture.NewTenantAsync("e30");
        var p = Prefix("E30");
        var unit = await UnitAsync(tenant, Guid.NewGuid(), $"{p}-01");
        var lost = await InvokeAsync<UnitFlagResult>(tenant, new SetUnitFlag(Guid.NewGuid(), unit, OverlayFlag.Lost, At.AddDays(1), "stores", Entity, "missing at cycle count"));
        Assert.True(lost.Changed);
        Assert.Equal([OverlayFlag.Lost], lost.Flags);
        var quiet = await InvokeAsync<UnitFlagResult>(tenant, new ClearUnitFlag(Guid.NewGuid(), unit, OverlayFlag.Lost, At.AddDays(2), "stores", Entity, "found on the next shelf"));
        Assert.True(quiet.Changed);
        Assert.False(quiet.InvestigationRaised);

        var stolen = new SetUnitFlag(Guid.NewGuid(), unit, OverlayFlag.Stolen, At.AddDays(3), "security", Entity, "police ref 4471");
        await InvokeAsync<UnitFlagResult>(tenant, stolen);
        var replay = await InvokeAsync<UnitFlagResult>(tenant, stolen);
        Assert.False(replay.Changed);
        var unknown = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<UnitFlagResult>(tenant, new SetUnitFlag(Guid.NewGuid(), unit, "misplaced", At.AddDays(3), "x", Entity)));
        Assert.Equal("flag_unknown", unknown.Rule);

        // the unit turns up on a return receipt while flagged stolen
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.Quarantine, "returned by a third party", At.AddDays(10), "returns", Entity, LocationId: Guid.NewGuid()));
        var cleared = await InvokeAsync<UnitFlagResult>(tenant, new ClearUnitFlag(Guid.NewGuid(), unit, OverlayFlag.Stolen, At.AddDays(11), "security", Entity, "recovered"));
        Assert.True(cleared.InvestigationRaised);
        Assert.Empty(cleared.Flags);

        await WithDbAsync(tenant, async db =>
        {
            Assert.Equal("[]", (await db.Units.SingleAsync(u => u.UnitId == unit)).OverlayFlags);
            Assert.Equal("[]", (await db.Current.SingleAsync(c => c.UnitId == unit)).OverlayFlags);
            var flagEvents = await db.Events.Where(e => e.UnitId == unit && (e.EventType == UnitEventType.FlagSet || e.EventType == UnitEventType.FlagCleared)).OrderBy(e => e.Seq).ToListAsync();
            Assert.Equal(4, flagEvents.Count);
            using var last = JsonDocument.Parse(flagEvents[^1].Attributes ?? "{}");
            Assert.True(last.RootElement.GetProperty("investigation_raised").GetBoolean());
            using var stolenEvent = JsonDocument.Parse(flagEvents[2].Attributes ?? "{}");
            Assert.Equal("police ref 4471", stolenEvent.RootElement.GetProperty("reason").GetString());
            return 0;
        });
    }

    [DatabaseFact] // cases 22, 43, 51, 109: trade-ins of a foreign brand; two-tier ownership through a distributor; marketplace buyers; group entities
    public async Task Ownership_chains_record_trade_ins_distributors_marketplaces_and_group_entities()
    {
        var tenant = await fixture.NewTenantAsync("e43");
        var p = Prefix("E43");
        var item = Guid.NewGuid();
        var us = Guid.NewGuid();
        var entityB = Guid.NewGuid();

        // case 22: a trade-in of a foreign brand is a unit of ours to track, not a brand of ours
        var tradeIn = await UnitAsync(tenant, item, $"{p}-TI", origin: UnitOrigin.TradeIn);
        var previousOwner = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), tradeIn, previousOwner, PartyType.Customer, EpisodeReason.TradeIn, At, "t", Entity, Source: EpisodeSource.Claim, Confidence: Confidence.Medium));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), tradeIn, us, PartyType.Us, EpisodeReason.TradeIn, At.AddDays(1), "sales", Entity, Document: new DocumentRef("trade_in_credit", Guid.NewGuid()), Source: EpisodeSource.Invoice));
        Assert.False(await WithDbAsync(tenant, async db => (await db.Units.SingleAsync(u => u.UnitId == tradeIn)).BrandIsOurs));
        var ours = await UnitAsync(tenant, item, $"{p}-OURS");
        Assert.True(await WithDbAsync(tenant, async db => (await db.Units.SingleAsync(u => u.UnitId == ours)).BrandIsOurs));

        // case 43: us → distributor (invoice, high) → end user (registration, medium); both hops recorded and the current owner is the end user
        var chain = await UnitAsync(tenant, item, $"{p}-D1");
        var distributor = Guid.NewGuid();
        var endUser = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), chain, us, PartyType.Us, EpisodeReason.Receipt, At, "t", Entity, Source: EpisodeSource.Receipt));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), chain, distributor, PartyType.Distributor, EpisodeReason.Sale, At.AddDays(2), "sales", Entity, Document: new DocumentRef("invoice", Guid.NewGuid())));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), chain, endUser, PartyType.EndUser, EpisodeReason.Sale, At.AddDays(20), "portal", Entity, Source: EpisodeSource.Registration, Confidence: Confidence.Medium));

        // case 51: a marketplace buyer is pseudonymous; the channel order reference is an identifier of the unit
        var mp = await UnitAsync(tenant, item, $"{p}-M1");
        var buyer = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), mp, buyer, PartyType.MarketplaceBuyer, EpisodeReason.Sale, At.AddDays(2), "channel", Entity, Source: EpisodeSource.Delivery, ExternalOrderRef: "114-7781-2206"));
        await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), mp, new IdentifierInput(IdentifierType.ChannelOrderRef, "114-7781-2206"), At.AddDays(2), "channel", Entity));

        // case 109 (and 15): one unit moves between legal entities of the group; the ledgers are per entity, the identity is one
        var grp = await UnitAsync(tenant, item, $"{p}-G1");
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), grp, us, PartyType.Us, EpisodeReason.Production, At, "t", Entity, Source: EpisodeSource.Receipt));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), grp, entityB, PartyType.GroupEntity, EpisodeReason.Transfer, At.AddDays(3), "ic-transfer", entityB, PartyIsGroupMember: true, Document: new DocumentRef("intercompany_invoice", Guid.NewGuid())));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), grp, Guid.NewGuid(), PartyType.Customer, EpisodeReason.Sale, At.AddDays(9), "sales-b", entityB, Document: new DocumentRef("invoice", Guid.NewGuid())));

        await WithDbAsync(tenant, async db =>
        {
            var hops = await db.Episodes.Where(e => e.UnitId == chain && e.Kind == EpisodeKind.Ownership).OrderBy(e => e.ValidFrom).ToListAsync();
            Assert.Equal([PartyType.Us, PartyType.Distributor, PartyType.EndUser], hops.Select(h => h.PartyType));
            Assert.Equal([EpisodeSource.Receipt, EpisodeSource.Invoice, EpisodeSource.Registration], hops.Select(h => h.Source));
            Assert.Equal([Confidence.High, Confidence.High, Confidence.Medium], hops.Select(h => h.Confidence));
            Assert.Equal(endUser, (await db.Current.SingleAsync(c => c.UnitId == chain)).OwnerPartyRef);

            var market = await db.Episodes.SingleAsync(e => e.UnitId == mp && e.Kind == EpisodeKind.Ownership);
            Assert.Equal("114-7781-2206", market.ExternalOrderRef);
            Assert.Equal(mp, (await db.Identifiers.SingleAsync(i => i.Type == IdentifierType.ChannelOrderRef && i.NormalisedValue == "114-7781-2206")).UnitId);

            var group = await db.Episodes.Where(e => e.UnitId == grp && e.Kind == EpisodeKind.Ownership).OrderBy(e => e.ValidFrom).ToListAsync();
            Assert.Equal([Entity, entityB, entityB], group.Select(g => g.LegalEntityId!.Value));
            Assert.True(group[1].PartyIsGroupMember);
            Assert.Equal(1, await db.Units.CountAsync(u => u.UnitId == grp));
            Assert.Equal(2, await db.Events.CountAsync(e => e.UnitId == grp && e.LegalEntityId == entityB && e.EventType == UnitEventType.OwnershipTransferred));
            return 0;
        });
    }

    [DatabaseFact] // case 100: a regulatory relabel supersedes the UDI device identifier and keeps the relationship record
    public async Task A_regulatory_relabel_supersedes_the_udi_and_keeps_the_prior_identifier()
    {
        var tenant = await fixture.NewTenantAsync("e100");
        var p = Prefix("E100");
        var unit = await UnitAsync(tenant, Guid.NewGuid(), $"{p}-01");
        var first = await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), unit, new IdentifierInput(IdentifierType.UdiDi, "09506000134352"), At, "labelling", Entity));
        var relabel = await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unit, IdentifierType.UdiDi, "04006381333931", IdentifierCorrectionReason.Regulatory, At.AddDays(5), "ra", Entity));
        Assert.Equal(first.IdentifierId, relabel.SupersededIdentifierId);
        await WithDbAsync(tenant, async db =>
        {
            var old = await db.Identifiers.SingleAsync(i => i.IdentifierId == first.IdentifierId);
            Assert.Equal(At.AddDays(5), old.ValidTo);
            Assert.Equal(relabel.NewIdentifierId, old.SupersededByIdentifierId);
            Assert.Equal(IdentifierCorrectionReason.Regulatory, old.CorrectionReason);
            Assert.Equal(1, await db.Identifiers.CountAsync(i => i.UnitId == unit && i.Type == IdentifierType.UdiDi && i.ValidTo == null));
            Assert.Equal(2, await db.Identifiers.CountAsync(i => i.UnitId == unit && i.Type == IdentifierType.UdiDi));
            return 0;
        });
    }

    [DatabaseFact] // cases 10, 37, 40, 134: kits are parents; a linked child cannot come back into stock under a shipped parent; a scrapped pre-release serial explains its range gap; standalone sale vs component
    public async Task Kits_tree_integrity_scrapped_pre_release_and_the_unit_of_sale()
    {
        var tenant = await fixture.NewTenantAsync("e37");
        var p = Prefix("E37");
        var kitItem = Guid.NewGuid();
        var moduleItem = Guid.NewGuid();
        var leeds = Guid.NewGuid();
        var kit = await UnitAsync(tenant, kitItem, $"{p}-KIT", UnitOrigin.Manufactured, leeds);
        var standalone = await UnitAsync(tenant, moduleItem, $"{p}-M1", UnitOrigin.Manufactured, leeds);
        var component = await UnitAsync(tenant, moduleItem, $"{p}-M2", UnitOrigin.Manufactured, leeds);
        var failed = await UnitAsync(tenant, moduleItem, $"{p}-M3", UnitOrigin.Manufactured, leeds);

        // case 10: a kit is a parent unit with child units, whatever the BOM explosion did
        var assembled = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), kit, [new ChildInput(component, Relation: AssemblyRelation.Kit, Slot: "K1")], At.AddHours(1), "packing", Entity));
        Assert.Single(assembled.LinkIds);
        foreach (var u in new[] { kit, standalone }) await InvokeAsync<ReleaseUnitResult>(tenant, new ReleaseUnit(Guid.NewGuid(), u, At.AddHours(2), "qa", Entity, "sig:qa"));

        // case 134: the standalone module ships as a top-level unit; its twin inside the kit is a component of the kit
        var customer = Guid.NewGuid();
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), standalone, LocationKind.CustomerSite, EpisodeReason.Sale, At.AddDays(1), "shipping", Entity, LocationId: Guid.NewGuid(), PartyRef: customer, PartyType: PartyType.Customer, Document: new DocumentRef("delivery", Guid.NewGuid(), 1)));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), kit, LocationKind.InTransit, EpisodeReason.Sale, At.AddDays(1), "shipping", Entity, PartyRef: Guid.NewGuid(), PartyType: PartyType.ThreePl, Document: new DocumentRef("delivery", Guid.NewGuid(), 2)));

        // case 37: the component is still linked into the kit that is in transit; receiving it alone is a mismatch, not a quiet move
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), component, LocationKind.InTransit, "found loose at the carrier", At.AddDays(2), "carrier", Entity));
        var mismatch = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), component, LocationKind.Warehouse, "received loose", At.AddDays(3), "stores", Entity, LocationId: leeds)));
        Assert.Equal("mismatch_instance", mismatch.Rule);
        Assert.NotNull(mismatch.UnblockingStep);
        await InvokeAsync<RecordDisassemblyResult>(tenant, new RecordDisassembly(Guid.NewGuid(), kit, assembled.LinkIds, At.AddDays(3), "stores", Entity, "component separated in transit"));
        var received = await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), component, LocationKind.Warehouse, "received loose", At.AddDays(3).AddMinutes(1), "stores", Entity, LocationId: leeds));
        Assert.Equal(LocationKind.Warehouse, received.ToLocationKind);

        // case 40: a unit that failed final test keeps its serial as scrapped_pre_release; the range gap is explained by the row, and the serial is not re-issued
        var scrap = new AdjustmentDocument(new DocumentRef("adjustment", Guid.NewGuid()), At.AddHours(3), AdjustmentDocument.Kinds.Out, "failed_final_test", [Line(1, moduleItem, leeds, $"{p}-M3")]);
        Documents.Add(scrap);
        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant, new InventoryAdjustmentPosted(Env(At.AddHours(3)), scrap.Document))).UnitsAffected);
        var reuse = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), moduleItem, UnitOrigin.Manufactured, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{p}-M3")])], At.AddDays(4), "t", Entity, SkipInvalidLines: true));
        Assert.Equal(1, reuse.Refused);

        await WithDbAsync(tenant, async db =>
        {
            Assert.Equal(AssemblyRelation.Kit, (await db.AssemblyLinks.SingleAsync(l => l.ParentUnitId == kit)).Relation);
            Assert.Equal(0, await db.AssemblyLinks.CountAsync(l => l.ChildUnitId == standalone));
            Assert.Equal(LifecycleStatus.ScrappedPreRelease, (await db.Units.SingleAsync(u => u.UnitId == failed)).LifecycleStatus);
            Assert.Null((await db.Identifiers.SingleAsync(i => i.UnitId == failed)).ValidTo);
            return 0;
        });
    }

    [DatabaseFact] // cases 12, 25, 26, 28: line quantity equals serial count; two-step transfers sit in transit; drop-ships take the carrier's dates; partial lines carry their line number
    public async Task Document_lines_keep_quantity_transit_carrier_dates_and_line_numbers_straight()
    {
        var tenant = await fixture.NewTenantAsync("e26");
        var p = Prefix("E26");
        var item = Guid.NewGuid();
        var leeds = Guid.NewGuid();
        var bristol = Guid.NewGuid();

        // case 12: a line with quantity 3 and two serials is a document error; the two serials still commission
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), At, [Line(1, item, leeds, $"{p}-01", $"{p}-02") with { Quantity = 3 }]);
        Documents.Add(receipt);
        var posted = await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Env(At), receipt.Document));
        Assert.Equal(2, posted.UnitsAffected);
        Assert.Contains(posted.Exceptions, x => x.Contains("quantity_serial_mismatch"));

        // case 25: a two-step transfer leaves the units in transit with no location until the receiving leg; a serial that never left is an exception there
        var leg1 = new TransferDocument(new DocumentRef("transfer", Guid.NewGuid()), At.AddDays(1), leeds, LocationKind.Warehouse, bristol, LocationKind.Warehouse, [Line(1, item, null, $"{p}-01", $"{p}-02")], TwoStep: true);
        Documents.Add(leg1);
        Assert.Equal(2, (await InvokeAsync<ConsumedEventResult>(tenant, new InventoryTransferPosted(Env(At.AddDays(1)), leg1.Document))).UnitsAffected);
        await WithDbAsync(tenant, async db =>
        {
            var rows = await db.Current.Where(c => db.Identifiers.Any(i => i.UnitId == c.UnitId && i.NormalisedValue.StartsWith(p))).ToListAsync();
            Assert.All(rows, r => { Assert.Equal(LocationKind.InTransit, r.LocationKind); Assert.Null(r.LocationId); });
            return 0;
        });
        var leg2 = new TransferDocument(new DocumentRef("transfer", Guid.NewGuid()), At.AddDays(3), leeds, LocationKind.Warehouse, bristol, LocationKind.Warehouse, [Line(1, item, null, $"{p}-01", $"{p}-02", $"{p}-99")]);
        Documents.Add(leg2);
        var arrived = await InvokeAsync<ConsumedEventResult>(tenant, new InventoryTransferPosted(Env(At.AddDays(3)), leg2.Document));
        Assert.Equal(2, arrived.UnitsAffected);
        Assert.Contains(arrived.Exceptions, x => x.Contains($"unknown serial '{p}-99'"));
        Assert.Equal(2, await WithDbAsync(tenant, db => db.Current.CountAsync(c => c.LocationId == bristol && c.LocationKind == LocationKind.Warehouse && db.Identifiers.Any(i => i.UnitId == c.UnitId && i.NormalisedValue.StartsWith(p)))));

        // case 28: one order line split over two shipment lines; each unit.shipped names its own line, so a return finds its shipment by serial
        var customer = Guid.NewGuid();
        var site = Guid.NewGuid();
        var shipment = new ShipmentDocument(new DocumentRef("delivery", Guid.NewGuid()), customer, At.AddDays(5), [Line(1, item, null, $"{p}-01"), Line(2, item, null, $"{p}-02")], site, Guid.NewGuid(), BaseOrder: new DocumentRef("sales_order", Guid.NewGuid(), 1));
        Documents.Add(shipment);
        Assert.Equal(2, (await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Env(At.AddDays(5)), shipment.Document))).UnitsAffected);
        var returned = new ReturnDocument(new DocumentRef("return", Guid.NewGuid()), customer, At.AddDays(20), bristol, [Line(1, item, null, $"{p}-02")], ReturnReasonCode: "dead_on_arrival");
        Documents.Add(returned);
        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant, new SalesReturnPosted(Env(At.AddDays(20)), returned.Document))).UnitsAffected);

        // case 26: a drop-ship never touches our stock; custody and ownership are dated by the carrier's delivery, not by our posting
        var vendor = Guid.NewGuid();
        var dropShip = new VendorShipment(new DocumentRef("vendor_shipment", Guid.NewGuid()), vendor, customer, site, At.AddDays(6), [Line(1, item, null, $"{p}-DS1")], "UPS 1Z999", DeliveredAt: At.AddDays(8));
        Documents.Add(dropShip);
        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingVendorShipConfirmed(Env(At.AddDays(30)), dropShip.Document))).UnitsAffected);

        await WithDbAsync(tenant, async db =>
        {
            var u1 = await db.Identifiers.Where(i => i.NormalisedValue == $"{p}01").Select(i => i.UnitId).SingleAsync();
            var u2 = await db.Identifiers.Where(i => i.NormalisedValue == $"{p}02").Select(i => i.UnitId).SingleAsync();
            Assert.Equal(1, (await db.Events.SingleAsync(e => e.UnitId == u1 && e.EventType == UnitEventType.Shipped)).DocumentLine);
            Assert.Equal(2, (await db.Events.SingleAsync(e => e.UnitId == u2 && e.EventType == UnitEventType.Shipped)).DocumentLine);
            Assert.Equal(LocationKind.Quarantine, (await db.Current.SingleAsync(c => c.UnitId == u2)).LocationKind);
            Assert.Equal(LocationKind.InTransit, (await db.Current.SingleAsync(c => c.UnitId == u1)).LocationKind);

            var ds = await db.Identifiers.Where(i => i.NormalisedValue == $"{p}DS1").Select(i => i.UnitId).SingleAsync();
            var dsUnit = await db.Units.SingleAsync(u => u.UnitId == ds);
            Assert.Equal(UnitOrigin.VendorAsn, dsUnit.Origin);
            var custody = await db.Episodes.Where(e => e.UnitId == ds && e.Kind == EpisodeKind.Custody && e.ValidTo == null).SingleAsync();
            Assert.Equal(LocationKind.CustomerSite, custody.LocationKind);
            Assert.Equal(At.AddDays(8), custody.ValidFrom);
            Assert.Equal(EpisodeSource.CarrierPod, custody.Source);
            var owner = await db.Episodes.Where(e => e.UnitId == ds && e.Kind == EpisodeKind.Ownership && e.ValidTo == null).SingleAsync();
            Assert.Equal(customer, owner.PartyRef);
            Assert.Equal(At.AddDays(8), owner.ValidFrom);
            Assert.Equal(0, await db.Events.CountAsync(e => e.UnitId == ds && e.EventTime == At.AddDays(30)));
            return 0;
        });
    }
}
