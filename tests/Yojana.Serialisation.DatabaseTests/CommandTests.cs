using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P1-T09: CommissionUnits, AssignIdentifier, CorrectIdentifier, ReleaseUnit through the bus, with the lifecycle guards.</summary>
[Collection("serialisation host")]
public sealed class CommandTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 10, 8, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        return await bus.InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static IdentifierInput Serial(string value) => new(IdentifierType.InternalSerial, value);

    private static CommissionUnits Commission(Guid commandId, Guid item, params CommissionLine[] lines) =>
        new(commandId, item, UnitOrigin.Manufactured, lines, At, "test", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse, Document: new DocumentRef("production_order", Guid.NewGuid()));

    [DatabaseFact]
    public async Task Commission_writes_units_identifiers_assignment_custody_and_current_state_and_a_replay_returns_the_same_units()
    {
        var tenant = await fixture.NewTenantAsync("c1");
        var item = Guid.NewGuid();
        var prefix = "C1" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var command = Commission(Guid.NewGuid(), item, new CommissionLine([Serial($"{prefix}-000001")], "a"), new CommissionLine([Serial($"{prefix}-000002"), new IdentifierInput(IdentifierType.Imei, "356938035643809")], "b"));
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, command);
        Assert.Equal(2, result.Commissioned);
        Assert.All(result.Lines, l => Assert.Equal(CommissionLineResult.Commissioned, l.Outcome));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var units = await db.Units.Where(u => result.Lines.Select(l => l.UnitId!.Value).Contains(u.UnitId)).ToListAsync();
            Assert.Equal(2, units.Count);
            Assert.All(units, u => Assert.Equal(LifecycleStatus.PreRelease, u.LifecycleStatus));
            Assert.Equal(3, await db.Identifiers.CountAsync(i => units.Select(u => u.UnitId).Contains(i.UnitId)));
            Assert.Equal(2, await db.ItemAssignments.CountAsync(a => units.Select(u => u.UnitId).Contains(a.UnitId) && a.ItemId == item));
            Assert.Equal(2, await db.Episodes.CountAsync(e => units.Select(u => u.UnitId).Contains(e.UnitId) && e.Kind == EpisodeKind.Custody && e.ValidTo == null));
            var current = await db.Current.Where(c => units.Select(u => u.UnitId).Contains(c.UnitId)).ToListAsync();
            Assert.Equal(2, current.Count);
            Assert.All(current, c => { Assert.Equal(LifecycleStatus.PreRelease, c.LifecycleStatus); Assert.Equal(LocationKind.Warehouse, c.LocationKind); Assert.NotNull(c.OpenCustodyEpisodeId); });
        }

        var replay = await InvokeAsync<CommissionUnitsResult>(tenant, command);
        Assert.Equal(2, replay.Duplicates);
        Assert.Equal(result.Lines.Select(l => l.UnitId), replay.Lines.Select(l => l.UnitId));
    }

    [DatabaseFact]
    public async Task Invalid_and_colliding_lines_are_refused_and_the_rest_commission_only_when_asked()
    {
        var tenant = await fixture.NewTenantAsync("c2");
        var item = Guid.NewGuid();
        var prefix = "C2" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var first = await InvokeAsync<CommissionUnitsResult>(tenant, Commission(Guid.NewGuid(), item, new CommissionLine([Serial($"{prefix}-000001")])));
        Assert.Equal(1, first.Commissioned);

        var lines = new[]
        {
            new CommissionLine([Serial($"{prefix}-000001")], "taken"),
            new CommissionLine([new IdentifierInput(IdentifierType.Imei, "356938035643808")], "bad-luhn"),
            new CommissionLine([Serial($"{prefix}-000002")], "dup-a"),
            new CommissionLine([Serial($"{prefix}-000002")], "dup-b"),
            new CommissionLine([Serial($"{prefix}-000003")], "ok"),
        };
        var strict = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CommissionUnitsResult>(tenant, Commission(Guid.NewGuid(), item, lines)));
        Assert.Equal("line_refused", strict.Rule);

        var lenient = await InvokeAsync<CommissionUnitsResult>(tenant, Commission(Guid.NewGuid(), item, lines) with { SkipInvalidLines = true });
        Assert.Equal(1, lenient.Commissioned);
        Assert.Equal(4, lenient.Refused);
        Assert.Contains(lenient.Lines, l => l.ClientReference == "taken" && l.Reasons[0].Contains("identifier_in_use", StringComparison.Ordinal));
        Assert.Contains(lenient.Lines, l => l.ClientReference == "bad-luhn" && l.Reasons[0] == "imei:imei_luhn");
        Assert.Contains(lenient.Lines, l => l.ClientReference == "dup-a" && l.Reasons[0] == "duplicate_in_batch");
        Assert.Contains(lenient.Lines, l => l.ClientReference == "ok" && l.Outcome == CommissionLineResult.Commissioned);
    }

    [DatabaseFact]
    public async Task Pending_identifier_units_get_their_serial_later_and_an_extra_type_can_be_added()
    {
        var tenant = await fixture.NewTenantAsync("c3");
        var item = Guid.NewGuid();
        var prefix = "C3" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var created = await InvokeAsync<CommissionUnitsResult>(tenant, Commission(Guid.NewGuid(), item, new CommissionLine([], "pending")));
        Assert.Equal(1, created.Pending);
        var unit = created.Lines[0].UnitId!.Value;

        var assigned = await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), unit, Serial($"{prefix}-000001"), At.AddHours(1), "test", Entity));
        Assert.Equal(LifecycleStatus.PreRelease, assigned.LifecycleStatus);
        var again = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), unit, Serial($"{prefix}-000009"), At.AddHours(2), "test", Entity)));
        Assert.Equal("identifier_type_already_open", again.Rule);
        var mac = await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), unit, new IdentifierInput(IdentifierType.Mac, "00:1A:2B:3C:4D:5E"), At.AddHours(2), "test", Entity));
        Assert.Equal("001A2B3C4D5E", mac.NormalisedValue);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(LifecycleStatus.PreRelease, (await db.Units.SingleAsync(u => u.UnitId == unit)).LifecycleStatus);
        Assert.Equal(LifecycleStatus.PreRelease, (await db.Current.SingleAsync(c => c.UnitId == unit)).LifecycleStatus);
        Assert.Equal([UnitEventType.CreatedPendingIdentifier, UnitEventType.IdentifierAssigned, UnitEventType.IdentifierAssigned], await db.Events.Where(e => e.UnitId == unit).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync());
    }

    [DatabaseFact]
    public async Task Correct_identifier_closes_the_old_row_keeps_both_searchable_and_needs_an_approver_after_shipment()
    {
        var tenant = await fixture.NewTenantAsync("c4");
        var item = Guid.NewGuid();
        var prefix = "C4" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var unit = (await InvokeAsync<CommissionUnitsResult>(tenant, Commission(Guid.NewGuid(), item, new CommissionLine([Serial($"{prefix}-000449")])))).Lines[0].UnitId!.Value;

        var corrected = await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unit, IdentifierType.InternalSerial, $"{prefix}-000494", IdentifierCorrectionReason.Typo, At.AddDays(1), "r.okafor", Entity));
        Assert.Equal($"{prefix}000449", corrected.OldNormalisedValue);
        Assert.Equal($"{prefix}000494", corrected.NewNormalisedValue);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var rows = await db.Identifiers.Where(i => i.UnitId == unit && i.Type == IdentifierType.InternalSerial).OrderBy(i => i.ValidFrom).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal(At.AddDays(1), rows[0].ValidTo);
            Assert.Equal(rows[1].IdentifierId, rows[0].SupersededByIdentifierId);
            Assert.Equal(rows[0].IdentifierId, rows[1].SupersedesIdentifierId);
            Assert.Equal(IdentifierCorrectionReason.Typo, rows[0].CorrectionReason);
            Assert.Null(rows[1].ValidTo);
            var corrections = await db.Events.Where(e => e.UnitId == unit && e.EventType == UnitEventType.IdentifierCorrected).ToListAsync();
            Assert.Single(corrections);
            Assert.Contains("\"reason\":\"typo\"", corrections[0].Attributes);

            // Record a shipment the P1 way (the consumed-event handler of P1-T15 will do this from Sales.ShipmentPosted).
            await scope.ServiceProvider.GetRequiredService<IUnitEventWriter>().AppendAsync([new UnitEventDraft { UnitId = unit, EventTime = At.AddDays(2), EventType = UnitEventType.Shipped, LegalEntityId = Entity, ActorRef = "test", IdempotencyKey = IdempotencyKey.ForCommand("t", Guid.NewGuid(), unit, UnitEventType.Shipped, At.AddDays(2)), Delta = new CurrentStateDelta { Disposition = UnitDisposition.InTransit } }], CancellationToken.None);
            await db.SaveChangesAsync();
        }

        var blocked = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unit, IdentifierType.InternalSerial, $"{prefix}-000495", IdentifierCorrectionReason.Misread, At.AddDays(3), "r.okafor", Entity)));
        Assert.Equal("approval_required", blocked.Rule);
        var approved = await InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unit, IdentifierType.InternalSerial, $"{prefix}-000495", IdentifierCorrectionReason.Misread, At.AddDays(3), "r.okafor", Entity, ApproverRef: "q.mahler"));
        Assert.Equal($"{prefix}000495", approved.NewNormalisedValue);
        var same = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CorrectIdentifierResult>(tenant, new CorrectIdentifier(Guid.NewGuid(), unit, IdentifierType.InternalSerial, $"{prefix}000495", IdentifierCorrectionReason.Typo, At.AddDays(4), "r.okafor", Entity, ApproverRef: "q.mahler")));
        Assert.Equal("no_change", same.Rule);
    }

    [DatabaseFact]
    public async Task Release_freezes_a_build_record_and_flips_the_status_once()
    {
        var tenant = await fixture.NewTenantAsync("c5");
        var item = Guid.NewGuid();
        var prefix = "C5" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var unit = (await InvokeAsync<CommissionUnitsResult>(tenant, Commission(Guid.NewGuid(), item, new CommissionLine([Serial($"{prefix}-000001")])))).Lines[0].UnitId!.Value;
        var release = new ReleaseUnit(Guid.NewGuid(), unit, At.AddDays(4), "q.mahler", Entity, "sig:q.mahler:2026-10-14", TestResultsJson: "{\"final_test\":\"pass\",\"passed\":6,\"of\":6}");
        var released = await InvokeAsync<ReleaseUnitResult>(tenant, release);
        Assert.Equal(1, released.BuildRecordVersion);
        Assert.Equal(LifecycleStatus.Released, released.LifecycleStatus);
        var replay = await InvokeAsync<ReleaseUnitResult>(tenant, release);
        Assert.Equal(released.BuildRecordId, replay.BuildRecordId);

        var twice = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReleaseUnitResult>(tenant, release with { CommandId = Guid.NewGuid(), EventTime = At.AddDays(5) }));
        Assert.Equal("lifecycle_transition", twice.Rule);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var record = await db.BuildRecords.SingleAsync(b => b.UnitId == unit);
        Assert.Equal(64, record.PayloadHash.Length);
        Assert.Contains($"{prefix}000001", record.Payload);
        Assert.Contains("\"final_test\":\"pass\"", record.Payload);
        Assert.Equal(LifecycleStatus.Released, (await db.Units.SingleAsync(u => u.UnitId == unit)).LifecycleStatus);
        Assert.Equal(UnitDisposition.Active, (await db.Current.SingleAsync(c => c.UnitId == unit)).Disposition);
    }

    [DatabaseFact]
    public async Task A_scrapped_units_serial_is_reused_only_under_the_item_policy_and_links_the_new_unit()
    {
        var tenant = await fixture.NewTenantAsync("c6");
        var ports = fixture.Services.GetRequiredService<InMemoryItemPorts>();
        var strict = Guid.NewGuid();
        var lenient = Guid.NewGuid();
        ports.Register(InMemoryItemPorts.DefaultProfile(lenient) with { SerialReusePolicy = ItemSerialisationProfile.ReusePolicies.AfterScrap });
        var prefix = "C6" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();

        async Task<Guid> ScrappedAsync(Guid item, string serial)
        {
            var unit = (await InvokeAsync<CommissionUnitsResult>(tenant, Commission(Guid.NewGuid(), item, new CommissionLine([Serial(serial)])))).Lines[0].UnitId!.Value;
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await scope.ServiceProvider.GetRequiredService<IUnitEventWriter>().AppendAsync([new UnitEventDraft { UnitId = unit, EventTime = At.AddDays(1), EventType = UnitEventType.Scrapped, LegalEntityId = Entity, ActorRef = "test", IdempotencyKey = IdempotencyKey.ForCommand("t", Guid.NewGuid(), unit, UnitEventType.Scrapped, At.AddDays(1)), Delta = new CurrentStateDelta { LifecycleStatus = LifecycleStatus.ScrappedPreRelease, Disposition = UnitDisposition.Destroyed } }], CancellationToken.None);
            (await db.Units.SingleAsync(u => u.UnitId == unit)).LifecycleStatus = LifecycleStatus.ScrappedPreRelease;
            await db.SaveChangesAsync();
            return unit;
        }

        await ScrappedAsync(strict, $"{prefix}-000001");
        var refused = await InvokeAsync<CommissionUnitsResult>(tenant, Commission(Guid.NewGuid(), strict, new CommissionLine([Serial($"{prefix}-000001")])) with { SkipInvalidLines = true, EventTime = At.AddDays(2) });
        Assert.Equal(1, refused.Refused);

        var old = await ScrappedAsync(lenient, $"{prefix}-000002");
        var reused = await InvokeAsync<CommissionUnitsResult>(tenant, Commission(Guid.NewGuid(), lenient, new CommissionLine([Serial($"{prefix}-000002")])) with { EventTime = At.AddDays(2) });
        Assert.Equal(1, reused.Commissioned);
        using var check = fixture.ScopeFor(tenant);
        var ctx = check.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var fresh = await ctx.Units.SingleAsync(u => u.UnitId == reused.Lines[0].UnitId);
        Assert.Equal(old, fresh.DerivedFromUnitId);
        Assert.Single(await ctx.Identifiers.Where(i => i.NormalisedValue == $"{prefix}000002" && i.ValidTo == null).ToListAsync());
        Assert.Equal("reused_after_scrap", (await ctx.Identifiers.SingleAsync(i => i.UnitId == old)).CorrectionReason);
    }
}
