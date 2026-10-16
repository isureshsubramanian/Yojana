using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P2-T05: entitlement commands through the bus on real units: creation from evidence, rebase with the cap, suspension, void, close, lazy expiry, and the coverage on unit_current.</summary>
[Collection("serialisation host")]
public sealed class EntitlementCommandTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Obligor = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly DateTime Invoice = new(2026, 9, 2, 8, 40, 0, DateTimeKind.Utc);

    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<(TenantId Tenant, Guid Item, List<Guid> Units)> SetUpAsync(string prefix, int units)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var code = "EC-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, "base_warranty", EntitlementType.Manufacturer, new DateOnly(2026, 7, 31), PolicyValidatorTestsTerms.Standard24(), "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        Ports.Register(new ItemWarrantyPolicy(item, "base_warranty", code, true));
        Ports.RegisterZone(Site, "Europe/Berlin");
        var marker = prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            Enumerable.Range(1, units).Select(i => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{marker}-{i}")])).ToList(), Invoice.AddDays(-30), "test", Obligor, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return (tenant, item, commissioned.Lines.Select(l => l.UnitId!.Value).ToList());
    }

    private CreateEntitlement Create(Guid unit, Dictionary<string, DateTime> evidence, string? grade = null, string? channel = null, string? country = "DE") =>
        new(Guid.NewGuid(), unit, "base_warranty", new DateOnly(2026, 9, 2), Obligor, Invoice, "service.desk", evidence, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer,
            ConditionGrade: grade, Channel: channel, Country: country, SiteLocationId: Site, SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 2));

    private async Task<(string Ids, DateOnly? EndMax)> CoverageAsync(TenantId tenant, Guid unit)
    {
        using var scope = fixture.ScopeFor(tenant);
        var row = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Current.AsNoTracking().SingleAsync(c => c.UnitId == unit);
        return (row.LiveEntitlementIds, row.WarrantyEndMax);
    }

    [DatabaseFact]
    public async Task Creation_takes_the_first_evidence_in_the_chain_a_rebase_moves_it_within_the_cap_and_unit_current_follows()
    {
        var (tenant, _, units) = await SetUpAsync("ea", 1);
        var unit = units[0];
        var command = Create(unit, new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Invoice });
        var created = await InvokeAsync<CreateEntitlementResult>(tenant, command);
        // invoice + 14 d in Berlin, 24 months inclusive; assurance, so a cohort
        Assert.Equal((EntitlementStatus.Active, StartTrigger.Invoice, new DateOnly(2026, 9, 16), new DateOnly(2028, 9, 15), "Europe/Berlin", PolicyLevel.ProductPolicy),
            (created.Status, created.StartBasis, created.StartDate, created.EndDate, created.TimeZone, created.PolicyLevel));
        Assert.NotNull(created.AccrualCohortId);
        Assert.Equal(created.EntitlementId, (await InvokeAsync<CreateEntitlementResult>(tenant, command)).EntitlementId);
        var coverage = await CoverageAsync(tenant, unit);
        Assert.Contains(created.EntitlementId.ToString(), coverage.Ids, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new DateOnly(2028, 9, 15), coverage.EndMax);
        Assert.Equal("family_occupied", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CreateEntitlementResult>(tenant, Create(unit, new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Invoice })))).Rule);

        // delivery at 23:30 UTC is the next day in Berlin, and delivery outranks invoice
        var pod = new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Utc);
        var rebased = await InvokeAsync<EntitlementChangeResult>(tenant, new RebaseEntitlementStart(Guid.NewGuid(), created.EntitlementId, StartTrigger.Deliver, pod, "POD HL-88120031", pod.AddHours(1), "service.desk"));
        Assert.Equal((new DateOnly(2026, 10, 2), new DateOnly(2028, 10, 1)), (rebased.StartDate, rebased.EndDate));
        Assert.Equal(new DateOnly(2028, 10, 1), (await CoverageAsync(tenant, unit)).EndMax);
        // ship ranks below delivery in this chain: weaker evidence cannot move the start back
        Assert.Equal("weaker_evidence", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<EntitlementChangeResult>(tenant, new RebaseEntitlementStart(Guid.NewGuid(), created.EntitlementId, StartTrigger.Ship, pod, null, pod.AddHours(2), "x")))).Rule);
        // a delivery recorded half a year late is held to invoice + 90 d
        var late = await InvokeAsync<EntitlementChangeResult>(tenant, new RebaseEntitlementStart(Guid.NewGuid(), created.EntitlementId, StartTrigger.Deliver, Invoice.AddDays(180), "late POD", Invoice.AddDays(181), "x"));
        Assert.Equal(new DateOnly(2026, 12, 1), late.StartDate);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(2, await db.EntitlementRebases.CountAsync(r => r.EntitlementId == created.EntitlementId));
        Assert.True(await db.EntitlementRebases.AnyAsync(r => r.EntitlementId == created.EntitlementId && r.PolicyCapApplied));
        var types = await db.Events.Where(e => e.UnitId == unit && e.EventType.StartsWith("entitlement.")).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync();
        Assert.Equal([EntitlementEventType.Created, EntitlementEventType.Rebased, EntitlementEventType.Rebased], types);
        Assert.Equal(0, await db.EntitlementUsageLimits.CountAsync(u => u.EntitlementId == created.EntitlementId));
    }

    /// <summary>P2-T18: the Finance-facing entitlement events leave through the outbox after commit, with the coverage change beside each fact that moved it.</summary>
    [DatabaseFact]
    public async Task Creation_rebase_and_close_are_published_with_their_coverage_changes()
    {
        var (tenant, item, units) = await SetUpAsync("ep", 1);
        var unit = units[0];
        var command = Create(unit, new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Invoice });
        var created = await InvokeAsync<CreateEntitlementResult>(tenant, command);
        var pod = new DateTime(2026, 10, 1, 23, 30, 0, DateTimeKind.Utc);
        await InvokeAsync<EntitlementChangeResult>(tenant, new RebaseEntitlementStart(Guid.NewGuid(), created.EntitlementId, StartTrigger.Deliver, pod, "POD HL-88120031", pod.AddHours(1), "service.desk"));
        await InvokeAsync<EntitlementChangeResult>(tenant, new CloseEntitlement(Guid.NewGuid(), created.EntitlementId, EntitlementClosedReason.Refunded, new DateOnly(2026, 10, 3), pod.AddDays(2), "service.desk"));

        var timeout = TimeSpan.FromSeconds(20);
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<EntitlementCreated>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal((EntitlementCreated.Schema, "1.1", created.EntitlementId, item, "base_warranty", Obligor), (published.Header.Schema, published.Header.Version, published.EntitlementId, published.ItemId, published.CoverageFamily, published.ObligorEntityId));
        Assert.Equal((new DateOnly(2026, 9, 16), new DateOnly(2028, 9, 15), created.AccrualCohortId, AssuranceOrService.Assurance, false), (published.StartDate, published.EndDate, published.AccrualCohortId, published.AssuranceOrService, published.AccrualSuppressed));
        Assert.Equal(command.SourceDocument, published.SourceDocument);
        var rebased = Assert.Single(await PublishedEventSink.WaitForAsync<EntitlementRebased>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal((new DateOnly(2026, 9, 16), new DateOnly(2026, 10, 2), new DateOnly(2028, 10, 1), StartTrigger.Deliver, "POD HL-88120031"), (rebased.OldStart, rebased.NewStart, rebased.NewEnd, rebased.Basis, rebased.EvidenceRef));
        var closed = Assert.Single(await PublishedEventSink.WaitForAsync<EntitlementClosed>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal(("base_warranty", EntitlementClosedReason.Refunded, new DateOnly(2026, 10, 3)), (closed.CoverageFamily, closed.ClosedReason, closed.ClosedEffective));

        // one coverage change per fact that moved the live set or the latest end, in ledger order
        var changes = (await PublishedEventSink.WaitForAsync<EntitlementCoverageChanged>(e => e.Header.UnitId == unit, 3, timeout)).OrderBy(e => e.Header.Seq).ToList();
        Assert.Equal([EntitlementEventType.Created, EntitlementEventType.Rebased, EntitlementEventType.Closed], changes.Select(c => c.Cause));
        Assert.Empty(changes[0].LiveEntitlementIdsBefore);
        Assert.Equal([created.EntitlementId], changes[0].LiveEntitlementIdsAfter);
        Assert.Equal(((DateOnly?)null, (DateOnly?)new DateOnly(2028, 9, 15)), (changes[0].WarrantyEndMaxBefore, changes[0].WarrantyEndMaxAfter));
        Assert.Equal((new DateOnly(2028, 9, 15), new DateOnly(2028, 10, 1)), (changes[1].WarrantyEndMaxBefore, changes[1].WarrantyEndMaxAfter));
        Assert.Equal([created.EntitlementId], changes[2].LiveEntitlementIdsBefore);
        Assert.Empty(changes[2].LiveEntitlementIdsAfter);
    }

    [DatabaseFact]
    public async Task Pending_evidence_suspension_void_close_and_lazy_expiry()
    {
        var (tenant, _, units) = await SetUpAsync("eb", 4);

        // no evidence at all: pending, no dates, no coverage end
        var pending = await InvokeAsync<CreateEntitlementResult>(tenant, Create(units[0], []));
        Assert.Equal((EntitlementStatus.PendingEvidence, (DateOnly?)null, (DateOnly?)null), (pending.Status, pending.StartDate, pending.EndDate));
        Assert.Null((await CoverageAsync(tenant, units[0])).EndMax);
        Assert.Equal("transition_not_allowed", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<EntitlementChangeResult>(tenant, new SuspendEntitlement(Guid.NewGuid(), pending.EntitlementId, new DateOnly(2026, 10, 1), "dispute", true, Invoice.AddDays(1), "x")))).Rule);
        var attached = await InvokeAsync<EntitlementChangeResult>(tenant, new RebaseEntitlementStart(Guid.NewGuid(), pending.EntitlementId, StartTrigger.Ship, Invoice, "DN-1", Invoice.AddDays(1), "x"));
        Assert.Equal((EntitlementStatus.Active, new DateOnly(2026, 9, 23)), (attached.Status, attached.StartDate));

        // suspension with the clock paused moves the end by the paused days; without, it does not
        var a = await InvokeAsync<CreateEntitlementResult>(tenant, Create(units[1], new Dictionary<string, DateTime> { [StartTrigger.Deliver] = Invoice }));
        await InvokeAsync<EntitlementChangeResult>(tenant, new SuspendEntitlement(Guid.NewGuid(), a.EntitlementId, new DateOnly(2026, 11, 1), "unit_in_dispute", true, Invoice.AddDays(60), "x"));
        var resumed = await InvokeAsync<EntitlementChangeResult>(tenant, new ResumeEntitlement(Guid.NewGuid(), a.EntitlementId, new DateOnly(2026, 11, 11), Invoice.AddDays(70), "x"));
        Assert.Equal(a.EndDate!.Value.AddDays(10), resumed.EndDate);
        var b = await InvokeAsync<CreateEntitlementResult>(tenant, Create(units[2], new Dictionary<string, DateTime> { [StartTrigger.Deliver] = Invoice }));
        await InvokeAsync<EntitlementChangeResult>(tenant, new SuspendEntitlement(Guid.NewGuid(), b.EntitlementId, new DateOnly(2026, 11, 1), "paperwork", false, Invoice.AddDays(60), "x"));
        Assert.Equal(b.EndDate, (await InvokeAsync<EntitlementChangeResult>(tenant, new ResumeEntitlement(Guid.NewGuid(), b.EntitlementId, new DateOnly(2026, 11, 11), Invoice.AddDays(70), "x"))).EndDate);

        // void needs evidence; close as expired only after the end; a closed entitlement stays closed
        Assert.Equal("evidence_required", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<EntitlementChangeResult>(tenant, new VoidEntitlement(Guid.NewGuid(), b.EntitlementId, EntitlementClosedReason.VoidedShippingError, "", [], new DateOnly(2026, 12, 1), Invoice.AddDays(90), "x")))).Rule);
        var voided = await InvokeAsync<EntitlementChangeResult>(tenant, new VoidEntitlement(Guid.NewGuid(), b.EntitlementId, EntitlementClosedReason.VoidedShippingError, "wrong unit shipped, returned unopened", ["RET-2026-0507"], new DateOnly(2026, 12, 1), Invoice.AddDays(90), "x"));
        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.VoidedShippingError), (voided.Status, voided.ClosedReason));
        Assert.Equal("[]", (await CoverageAsync(tenant, units[2])).Ids);
        Assert.Null((await CoverageAsync(tenant, units[2])).EndMax);
        Assert.Equal("entitlement_closed", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<EntitlementChangeResult>(tenant, new CloseEntitlement(Guid.NewGuid(), b.EntitlementId, EntitlementClosedReason.Refunded, new DateOnly(2026, 12, 2), Invoice.AddDays(91), "x")))).Rule);
        Assert.Equal("not_expired", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<EntitlementChangeResult>(tenant, new CloseEntitlement(Guid.NewGuid(), a.EntitlementId, EntitlementClosedReason.Expired, new DateOnly(2027, 1, 1), Invoice.AddDays(120), "x")))).Rule);
        Assert.Equal("close_reason_not_here", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<EntitlementChangeResult>(tenant, new CloseEntitlement(Guid.NewGuid(), a.EntitlementId, EntitlementClosedReason.Replaced, new DateOnly(2027, 1, 1), Invoice.AddDays(120), "x")))).Rule);

        // lazy expiry: stored active, effectively expired the day after the end; nothing is written by the read
        using (var scope = fixture.ScopeFor(tenant))
        {
            var queries = scope.ServiceProvider.GetRequiredService<EntitlementQueries>();
            var view = Assert.Single(await queries.ForUnitAsync(units[1], resumed.EndDate!.Value.AddDays(1), CancellationToken.None));
            Assert.Equal((EntitlementStatus.Active, "expired", 0), (view.StoredStatus, view.EffectiveStatus, view.DaysRemaining));
            Assert.Equal(EntitlementStatus.Active, Assert.Single(await queries.ForUnitAsync(units[1], resumed.EndDate!.Value, CancellationToken.None)).EffectiveStatus);
        }

        var closed = await InvokeAsync<EntitlementChangeResult>(tenant, new CloseEntitlement(Guid.NewGuid(), a.EntitlementId, EntitlementClosedReason.Expired, resumed.EndDate!.Value.AddDays(1), Invoice.AddDays(800), "nightly"));
        Assert.Equal(EntitlementClosedReason.Expired, closed.ClosedReason);

        // a refurbished grade C unit sold to an EU consumer gets the 12-month floor, not the row's 6 months
        var refurbished = await InvokeAsync<CreateEntitlementResult>(tenant, Create(units[3], new Dictionary<string, DateTime> { [StartTrigger.Deliver] = Invoice }, grade: "C", channel: "portal_consumer"));
        Assert.True(refurbished.FloorApplied);
        Assert.Equal(new DateOnly(2027, 9, 1), refurbished.EndDate);
    }

    [DatabaseFact]
    public async Task Refurbished_resale_snapshots_the_floor_and_disclosure_and_starts_a_new_chain()
    {
        var (tenant, _, units) = await SetUpAsync("er", 1);
        var unit = units[0];
        var earlier = await InvokeAsync<CreateEntitlementResult>(tenant, Create(unit, new() { [StartTrigger.Deliver] = Invoice }));
        await InvokeAsync<EntitlementChangeResult>(tenant, new CloseEntitlement(Guid.NewGuid(), earlier.EntitlementId, EntitlementClosedReason.ReturnedToStock, new DateOnly(2026, 10, 1), Invoice.AddDays(30), "returns"));
        var refurbisher = Guid.NewGuid();
        var sold = Create(unit, new() { [StartTrigger.Deliver] = Invoice.AddDays(60) }, grade: "C", channel: "portal_consumer") with
        {
            BuyerKind = RefurbishedBuyerKind.Consumer,
            RefurbisherPartyRef = refurbisher,
        };
        var created = await InvokeAsync<CreateEntitlementResult>(tenant, sold);
        Assert.Equal((12, true, "jurisdiction", refurbisher, RefurbishedWarrantyStatement.NoOemWarranty),
            (created.Disclosure!.TermMonths, created.Disclosure.FloorApplied, created.Disclosure.FloorSource, created.Disclosure.RefurbisherPartyRef, created.Disclosure.StatementKind));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var row = await db.Entitlements.SingleAsync(e => e.EntitlementId == created.EntitlementId);
            Assert.Equal("C", row.ConditionAtCreation);
            Assert.Null(row.ParentEntitlementId);
            Assert.Null(row.RootEntitlementId);
            var disclosure = await scope.ServiceProvider.GetRequiredService<EntitlementQueries>().DisclosureAsync(created.EntitlementId, CancellationToken.None);
            Assert.Equal(created.Disclosure, disclosure);
        }

        using (var scope = fixture.ScopeFor(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<RefurbishedGoodsSettings>().SetAsync(new([new("EU", RefurbishedBuyerKind.Consumer, 18)]), CancellationToken.None);
        }
        await InvokeAsync<EntitlementChangeResult>(tenant, new CloseEntitlement(Guid.NewGuid(), created.EntitlementId, EntitlementClosedReason.ReturnedToStock, new DateOnly(2027, 1, 1), Invoice.AddDays(120), "returns"));
        var later = await InvokeAsync<CreateEntitlementResult>(tenant, sold with { CommandId = Guid.NewGuid(), EventTime = Invoice.AddDays(121) });
        Assert.Equal(18, later.Disclosure!.TermMonths);
        Assert.Equal(12, (await InScopeDisclosure(tenant, created.EntitlementId)).TermMonths);
    }

    private async Task<RefurbishedEntitlementDisclosure> InScopeDisclosure(TenantId tenant, Guid entitlementId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return (await scope.ServiceProvider.GetRequiredService<EntitlementQueries>().DisclosureAsync(entitlementId, CancellationToken.None))!;
    }

    [DatabaseFact]
    public async Task Transfer_closes_the_source_builds_one_rooted_chain_and_suppresses_the_second_accrual()
    {
        var (tenant, _, units) = await SetUpAsync("et", 3);
        var source = await InvokeAsync<CreateEntitlementResult>(tenant, Create(units[0], new Dictionary<string, DateTime> { [StartTrigger.Deliver] = Invoice }));
        var firstCommand = new TransferEntitlement(Guid.NewGuid(), source.EntitlementId, units[1], Guid.NewGuid(), new DateOnly(2028, 8, 1), Invoice.AddDays(2), "service.desk");
        var first = await InvokeAsync<TransferEntitlementResult>(tenant, firstCommand);
        // max_remaining_or_min_days(90): the source ends 2028-09-01, the replacement on 2028-08-01 is owed 90 days, so 2028-10-30
        Assert.Equal((source.EntitlementId, "STD-90", new DateOnly(2028, 10, 30), 1), (first.RootEntitlementId, first.PolicyApplied, first.ComputedEnd, first.ChainLength));
        Assert.Equal(new DateOnly(2028, 9, 1), source.EndDate);
        Assert.Equal(first.TargetEntitlementId, (await InvokeAsync<TransferEntitlementResult>(tenant, firstCommand)).TargetEntitlementId);

        var second = await InvokeAsync<TransferEntitlementResult>(tenant, new TransferEntitlement(Guid.NewGuid(), first.TargetEntitlementId, units[2], Guid.NewGuid(), new DateOnly(2028, 9, 1), Invoice.AddDays(3), "service.desk"));
        Assert.Equal((source.EntitlementId, 2, new DateOnly(2028, 11, 30)), (second.RootEntitlementId, second.ChainLength, second.ComputedEnd));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var rows = await db.Entitlements.Where(e => e.EntitlementId == source.EntitlementId || e.RootEntitlementId == source.EntitlementId).OrderBy(e => e.CreatedAt).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.Replaced, units[1], first.TargetEntitlementId), (rows[0].Status, rows[0].ClosedReason, rows[0].SuccessorUnitId, rows[0].SuccessorEntitlementId));
        Assert.Equal((source.EntitlementId, source.EntitlementId, (Guid?)null), (rows[1].ParentEntitlementId, rows[1].RootEntitlementId, rows[1].AccrualCohortId));
        Assert.Equal((first.TargetEntitlementId, source.EntitlementId, (Guid?)null), (rows[2].ParentEntitlementId, rows[2].RootEntitlementId, rows[2].AccrualCohortId));

        var published = Assert.Single(await PublishedEventSink.WaitForAsync<EntitlementTransferred>(e => e.TargetEntitlementId == second.TargetEntitlementId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((source.EntitlementId, 2, true), (published.RootEntitlementId, published.ChainLength, published.AccrualSuppressed));
    }

    [DatabaseFact]
    public async Task Statutory_extension_is_only_once_and_only_for_an_eligible_EU_repair()
    {
        var (tenant, item, units) = await SetUpAsync("es", 3);
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("EU-REPAIR", ReplacementRule.StatutoryExtensionOnce, null, 12, null));
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("statutory", "Statutory warranty", false, AssuranceOrService.Assurance));
        var code = "ES-" + Guid.NewGuid().ToString("N")[..6];
        var terms = PolicyValidatorTestsTerms.Standard24() with { ReplacementPolicyCode = "EU-REPAIR" };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, "statutory", EntitlementType.Statutory, new DateOnly(2026, 7, 31), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        Ports.Register(new ItemWarrantyPolicy(item, "statutory", code, true));
        var source = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), units[0], "statutory", new DateOnly(2026, 7, 31), Obligor, Invoice, "service.desk",
            new Dictionary<string, DateTime> { [StartTrigger.Deliver] = Invoice }, PolicyVersionId: draft.PolicyVersionId, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, Country: "DE", SiteLocationId: Site));
        var ineligible = new TransferEntitlement(Guid.NewGuid(), source.EntitlementId, units[1], null, new DateOnly(2027, 1, 1), Invoice.AddDays(2), "service.desk", "EU-REPAIR", new DateOnly(2026, 7, 30), "DE", true, "repair");
        Assert.Equal("statutory_extension_not_applicable", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<TransferEntitlementResult>(tenant, ineligible))).Rule);

        var first = await InvokeAsync<TransferEntitlementResult>(tenant, ineligible with { CommandId = Guid.NewGuid(), ContractDate = new DateOnly(2026, 7, 31) });
        Assert.Equal(source.EndDate!.Value.AddMonths(12), first.ComputedEnd);
        var repeated = ineligible with { CommandId = Guid.NewGuid(), SourceEntitlementId = first.TargetEntitlementId, TargetUnitId = units[2], ContractDate = new DateOnly(2026, 7, 31), EventTime = Invoice.AddDays(3) };
        Assert.Equal("statutory_extension_already_applied", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<TransferEntitlementResult>(tenant, repeated))).Rule);
    }
}
