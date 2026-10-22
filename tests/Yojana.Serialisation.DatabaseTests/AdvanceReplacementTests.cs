using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Replacements;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class AdvanceReplacementTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Cross_shipment_closes_in_both_orders_and_a_mismatch_never_closes_the_link()
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, "advance-order");

        var replacementFirst = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "ship-first");
        var first = await CreateAndShipAdvanceAsync(replacementFirst, new DateOnly(2026, 9, 15));
        Assert.Equal(ReplacementLinkState.AwaitingReturn, first.LinkState);
        var firstRma = await P5AdvanceTestData.SeedAuthorisedRmaAsync(fixture, replacementFirst);
        await P5AdvanceTestData.ReceiveAsync(fixture, replacementFirst, firstRma, replacementFirst.OriginalSerial, At.AddDays(6));
        await WaitForLinkStateAsync(replacementFirst, first.ReplacementLinkId, ReplacementLinkState.ClosedReturned);

        var receiptFirst = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "receipt-first");
        var secondRma = await P5AdvanceTestData.SeedAuthorisedRmaAsync(fixture, receiptFirst);
        await P5AdvanceTestData.ReceiveAsync(fixture, receiptFirst, secondRma, receiptFirst.OriginalSerial, At.AddDays(2));
        await P5AdvanceTestData.InvokeAsync<DispositionRmaResult>(fixture, world.Tenant,
            new DispositionRma(Guid.NewGuid(), secondRma.RmaId, [new(secondRma.RmaLineId, RmaDisposition.Replace)], new(2026, 9, 3), At.AddDays(3)));
        using (var pendingScope = fixture.ScopeFor(world.Tenant))
        {
            var pending = await pendingScope.ServiceProvider.GetRequiredService<ReplacementLinkQueries>().PendingAsync(0, 50, CancellationToken.None);
            Assert.Contains(pending.Rows, row => row.RmaLineId == secondRma.RmaLineId);
        }
        var createdSecond = await P5AdvanceTestData.InvokeAsync<CreateWarrantyReplacementResult>(fixture, world.Tenant,
            P5AdvanceTestData.CreateCommand(receiptFirst, ReplacementSourceKind.Rma, secondRma.RmaId, At.AddDays(4), new DateOnly(2026, 9, 15)));
        var second = await P5AdvanceTestData.InvokeAsync<ShipWarrantyReplacementResult>(fixture, world.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), createdSecond.Document.Document.WarrantyReplacementId, new(2026, 9, 5), At.AddDays(4)));
        Assert.Equal(ReplacementLinkState.ClosedReturned, second.LinkState);
        using (var pendingScope = fixture.ScopeFor(world.Tenant))
        {
            var pending = await pendingScope.ServiceProvider.GetRequiredService<ReplacementLinkQueries>().PendingAsync(0, 50, CancellationToken.None);
            Assert.DoesNotContain(pending.Rows, row => row.RmaLineId == secondRma.RmaLineId);
        }

        var mismatchCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "mismatch");
        var mismatchLink = await CreateAndShipAdvanceAsync(mismatchCase, new DateOnly(2026, 9, 15));
        var mismatchRma = await P5AdvanceTestData.SeedAuthorisedRmaAsync(fixture, mismatchCase);
        var other = await P5AdvanceTestData.CommissionAsync(fixture, world, "OTHER");
        var mismatch = await P5AdvanceTestData.ReceiveAsync(fixture, mismatchCase, mismatchRma, other.Serial, At.AddDays(7));
        Assert.Equal(ReturnOutcome.Mismatch, Assert.Single(mismatch.Lines).Outcome);
        await PublishedEventSink.WaitForAsync<RmaReceived>(message => message.RmaLineId == mismatchRma.RmaLineId, 1, TimeSpan.FromSeconds(20));
        await Task.Delay(250);
        using (var scope = fixture.ScopeFor(world.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(ReplacementLinkState.AwaitingReturn, (await db.ReplacementLinks.SingleAsync(row => row.ReplacementLinkId == mismatchLink.ReplacementLinkId)).State);

            var originalOwner = await db.Episodes.SingleAsync(row => row.UnitId == replacementFirst.OriginalUnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
            var originalCustody = await db.Episodes.SingleAsync(row => row.UnitId == replacementFirst.OriginalUnitId && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
            var replacementOwner = await db.Episodes.SingleAsync(row => row.UnitId == replacementFirst.ReplacementUnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
            var replacementCustody = await db.Episodes.SingleAsync(row => row.UnitId == replacementFirst.ReplacementUnitId && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
            Assert.Equal(world.Customer, originalOwner.PartyRef);
            Assert.Equal(LocationKind.Quarantine, originalCustody.LocationKind);
            Assert.Equal(world.Customer, replacementOwner.PartyRef);
            Assert.Equal(LocationKind.CustomerSite, replacementCustody.LocationKind);
            Assert.Equal(EntitlementStatus.Closed, (await db.Entitlements.SingleAsync(row => row.EntitlementId == replacementFirst.SourceEntitlementId)).Status);
            Assert.Single(await db.Entitlements.Where(row => row.ParentEntitlementId == replacementFirst.SourceEntitlementId && row.Status == EntitlementStatus.Active).ToListAsync());
            Assert.Equal(2, await db.Identifiers.CountAsync(row => row.UnitId == replacementFirst.OriginalUnitId || row.UnitId == replacementFirst.ReplacementUnitId));
            Assert.Contains(await db.Events.Where(row => row.UnitId == replacementFirst.OriginalUnitId).Select(row => row.EventType).ToListAsync(), type => type == RmaEventType.Received);
            Assert.Contains(await db.Events.Where(row => row.UnitId == replacementFirst.ReplacementUnitId).Select(row => row.EventType).ToListAsync(), type => type == ReplacementEventType.Shipped);
        }
    }

    [DatabaseFact]
    public async Task Overdue_is_published_once_hold_blocks_then_extend_bill_and_lift_are_audited()
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, "advance-overdue");
        var overdueCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "overdue");
        var shipped = await CreateAndShipAdvanceAsync(overdueCase, new DateOnly(2026, 9, 6), 1250m);
        var runAt = At.AddDays(10);
        var first = await P5AdvanceTestData.InvokeAsync<ReplacementOverdueRunResult>(fixture, world.Tenant, new RunReplacementOverdue(new(2026, 9, 12), runAt));
        var second = await P5AdvanceTestData.InvokeAsync<ReplacementOverdueRunResult>(fixture, world.Tenant, new RunReplacementOverdue(new(2026, 9, 12), runAt));
        Assert.Equal((1, 1), (first.Published, first.HoldsSet));
        Assert.Equal((0, 0), (second.Published, second.HoldsSet));
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<ReplacementReturnOverdue>(message => message.ReplacementLinkId == shipped.ReplacementLinkId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((shipped.ReplacementLinkId, overdueCase.OriginalUnitId, overdueCase.ReplacementUnitId,
                world.Customer, new DateOnly(2026, 9, 6), NonReturnPriceBasis.List, 6),
            (published.ReplacementLinkId, published.OriginalUnitId, published.ReplacementUnitId,
                published.PartyRef, published.ReturnDue, published.NonReturnPriceBasis, published.DaysOverdue));
        Assert.Equal((ReplacementReturnOverdue.Schema, ReplacementReturnOverdue.Version, overdueCase.OriginalUnitId),
            (published.Header.Schema, published.Header.Version, published.Header.UnitId));
        Assert.Equal(new DateTime(2026, 9, 7, 0, 0, 0, DateTimeKind.Utc), published.Header.EventTime);

        var blockedCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "blocked");
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => P5AdvanceTestData.InvokeAsync<CreateWarrantyReplacementResult>(fixture, world.Tenant,
            P5AdvanceTestData.CreateCommand(blockedCase, ReplacementSourceKind.Claim, blockedCase.ClaimId, At.AddDays(12), new DateOnly(2026, 9, 20))));
        Assert.Equal("advance_replacement_hold", refused.Rule);

        var firstExtension = new ExtendReturnDue(Guid.NewGuid(), shipped.ReplacementLinkId, new(2026, 9, 25),
            "carrier collection rescheduled", At.AddDays(12));
        var extended = await P5AdvanceTestData.InvokeAsync<ExtendReturnDueResult>(fixture, world.Tenant, firstExtension);
        Assert.Equal((new DateOnly(2026, 9, 6), new DateOnly(2026, 9, 25)), (extended.PreviousReturnDue, extended.NewReturnDue));
        var secondExtension = await P5AdvanceTestData.InvokeAsync<ExtendReturnDueResult>(fixture, world.Tenant,
            new ExtendReturnDue(Guid.NewGuid(), shipped.ReplacementLinkId, new(2026, 9, 30), "replacement collection slot agreed", At.AddDays(13)));
        Assert.Equal((new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 30)), (secondExtension.PreviousReturnDue, secondExtension.NewReturnDue));
        var oldReplay = await P5AdvanceTestData.InvokeAsync<ExtendReturnDueResult>(fixture, world.Tenant, firstExtension);
        Assert.Equal((new DateOnly(2026, 9, 6), new DateOnly(2026, 9, 25), true),
            (oldReplay.PreviousReturnDue, oldReplay.NewReturnDue, oldReplay.Replayed));
        var billed = await P5AdvanceTestData.InvokeAsync<BillNonReturnResult>(fixture, world.Tenant,
            new BillNonReturn(Guid.NewGuid(), shipped.ReplacementLinkId, NonReturnPriceBasis.FairMarketValue, 900m,
                new(2026, 9, 14), At.AddDays(14), Reason: "agreed return window expired"));
        Assert.Equal(ReplacementLinkState.ClosedBilled, billed.State);
        var lifted = await P5AdvanceTestData.InvokeAsync<LiftReplacementHoldResult>(fixture, world.Tenant,
            new LiftReplacementHold(Guid.NewGuid(), world.Customer, "account reviewed after billing", At.AddDays(15)));
        Assert.False(lifted.Replayed);
        Assert.Equal(At.AddDays(15), lifted.LiftedAt);

        using var scope = fixture.ScopeFor(world.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var link = await db.ReplacementLinks.SingleAsync(row => row.ReplacementLinkId == shipped.ReplacementLinkId);
        Assert.Equal((new DateOnly(2026, 9, 30), NonReturnPriceBasis.FairMarketValue, 900m), (link.ReturnDue, link.BilledPriceBasis, link.BilledAmount));
        var detail = await scope.ServiceProvider.GetRequiredService<WarrantyReplacementQueries>()
            .DetailAsync(link.WarrantyReplacementId!.Value, CancellationToken.None);
        Assert.Equal(("billed", NonReturnPriceBasis.FairMarketValue, 900m, new DateOnly(2026, 9, 14)),
            (detail!.NonReturnCharge!.State, detail.NonReturnCharge.PriceBasis,
                detail.NonReturnCharge.Amount, detail.NonReturnCharge.BilledOn));
        Assert.NotNull(link.OverdueEventId);
        Assert.NotNull((await db.PartyReplacementHolds.SingleAsync(row => row.PartyRef == world.Customer)).LiftedAt);
        var owner = await db.Episodes.SingleAsync(row => row.UnitId == overdueCase.OriginalUnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var originalCustody = await db.Episodes.SingleAsync(row => row.UnitId == overdueCase.OriginalUnitId && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        var replacementOwner = await db.Episodes.SingleAsync(row => row.UnitId == overdueCase.ReplacementUnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var replacementCustody = await db.Episodes.SingleAsync(row => row.UnitId == overdueCase.ReplacementUnitId && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal(world.Customer, owner.PartyRef);
        Assert.Equal((LocationKind.CustomerSite, world.Customer), (originalCustody.LocationKind, originalCustody.PartyRef));
        Assert.Equal(world.Customer, replacementOwner.PartyRef);
        Assert.Equal((LocationKind.CustomerSite, world.Customer), (replacementCustody.LocationKind, replacementCustody.PartyRef));
        Assert.Equal(EntitlementStatus.Closed, (await db.Entitlements.SingleAsync(row => row.EntitlementId == overdueCase.SourceEntitlementId)).Status);
        Assert.Equal(EntitlementStatus.Active, (await db.Entitlements.SingleAsync(row => row.EntitlementId == link.TargetEntitlementId)).Status);
        Assert.Equal(2, await db.Identifiers.CountAsync(row => row.UnitId == overdueCase.OriginalUnitId || row.UnitId == overdueCase.ReplacementUnitId));
        Assert.Contains(await db.Events.Where(row => row.UnitId == overdueCase.OriginalUnitId).Select(row => row.EventType).ToListAsync(), type => type == ReplacementEventType.ReturnDueExtended);
        Assert.Contains(await db.Events.Where(row => row.UnitId == overdueCase.OriginalUnitId).Select(row => row.EventType).ToListAsync(), type => type == ReplacementEventType.NonReturnBilled);
    }

    [DatabaseFact]
    public async Task Loan_conversion_swaps_ownership_transfers_cover_and_does_not_issue_stock()
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, "loan-convert");
        var customerCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "loan");
        var loaner = await P5AdvanceTestData.CommissionAsync(fixture, world, "LOANER");
        await P5AdvanceTestData.InvokeAsync<OpenOwnershipResult>(fixture, world.Tenant,
            new OpenOwnership(Guid.NewGuid(), loaner.UnitId, world.Entity, PartyType.Us, EpisodeReason.Loan, At.AddDays(-4), "test", world.Entity));
        await P5AdvanceTestData.InvokeAsync<ChangeCustodyResult>(fixture, world.Tenant,
            new ChangeCustody(Guid.NewGuid(), loaner.UnitId, LocationKind.CustomerSite, EpisodeReason.Loan, At.AddDays(-3), "test", world.Entity,
                PartyRef: world.Customer, PartyType: PartyType.Customer));
        var issueRef = new DocumentRef("loaner_issue", Guid.NewGuid(), 1);
        fixture.Services.GetRequiredService<InMemoryDocumentPorts>().Add(new LoanerIssue(issueRef, world.Customer, At.AddDays(-3), world.Site,
            new(2026, 9, 20), [new DocumentLine(1, world.ItemId, 1, [new(loaner.Serial)])]));
        var stockBefore = fixture.Services.GetRequiredService<InMemoryStockUnitEventPort>().ReplacementIssues.Count;
        var converted = await P5AdvanceTestData.InvokeAsync<ConvertLoanToReplacementResult>(fixture, world.Tenant,
            new ConvertLoanToReplacement(Guid.NewGuid(), issueRef, customerCase.OriginalUnitId, customerCase.SourceEntitlementId,
                loaner.UnitId, ReplacementReason.WarrantyDefect, new(2026, 9, 5), At.AddDays(4), world.Entity, "DE", true, false));
        Assert.False(converted.Replayed);
        Assert.Equal(stockBefore, fixture.Services.GetRequiredService<InMemoryStockUnitEventPort>().ReplacementIssues.Count);

        using var scope = fixture.ScopeFor(world.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(world.Entity, (await db.Episodes.SingleAsync(row => row.UnitId == customerCase.OriginalUnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null)).PartyRef);
        Assert.Equal(world.Customer, (await db.Episodes.SingleAsync(row => row.UnitId == loaner.UnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null)).PartyRef);
        var originalCustody = await db.Episodes.SingleAsync(row => row.UnitId == customerCase.OriginalUnitId && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        var loanCustody = await db.Episodes.SingleAsync(row => row.UnitId == loaner.UnitId && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((LocationKind.CustomerSite, world.Customer), (originalCustody.LocationKind, originalCustody.PartyRef));
        Assert.Equal((LocationKind.CustomerSite, EpisodeReason.Replacement, world.Customer), (loanCustody.LocationKind, loanCustody.Reason, loanCustody.PartyRef));
        Assert.Equal(EntitlementStatus.Closed, (await db.Entitlements.SingleAsync(row => row.EntitlementId == customerCase.SourceEntitlementId)).Status);
        var target = await db.Entitlements.SingleAsync(row => row.EntitlementId == converted.TargetEntitlementId);
        Assert.Equal((loaner.UnitId, customerCase.SourceEntitlementId, customerCase.SourceEntitlementId), (target.UnitId, target.ParentEntitlementId, target.RootEntitlementId));
        Assert.Equal(2, await db.Identifiers.CountAsync(row => row.UnitId == customerCase.OriginalUnitId || row.UnitId == loaner.UnitId));
        Assert.Contains(await db.Events.Where(row => row.UnitId == loaner.UnitId).Select(row => row.EventType).ToListAsync(), type => type == ReplacementEventType.Shipped);
        Assert.Contains(await db.Events.Where(row => row.UnitId == customerCase.OriginalUnitId).Select(row => row.EventType).ToListAsync(), type => type == UnitEventType.OwnershipTransferred);
    }

    private async Task<ShipWarrantyReplacementResult> CreateAndShipAdvanceAsync(P5CustomerCase customerCase, DateOnly due, decimal nonReturnAmount = 1000m)
    {
        var created = await P5AdvanceTestData.InvokeAsync<CreateWarrantyReplacementResult>(fixture, customerCase.World.Tenant,
            P5AdvanceTestData.CreateCommand(customerCase, ReplacementSourceKind.Claim, customerCase.ClaimId, At.AddDays(2), due, nonReturnAmount));
        return await P5AdvanceTestData.InvokeAsync<ShipWarrantyReplacementResult>(fixture, customerCase.World.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), created.Document.Document.WarrantyReplacementId, new(2026, 9, 4), At.AddDays(3)));
    }

    private async Task WaitForLinkStateAsync(P5CustomerCase customerCase, Guid linkId, string state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            using var scope = fixture.ScopeFor(customerCase.World.Tenant);
            var value = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ReplacementLinks.AsNoTracking()
                .Where(row => row.ReplacementLinkId == linkId).Select(row => row.State).SingleAsync();
            if (value == state) return;
            await Task.Delay(100);
        }
        throw new Xunit.Sdk.XunitException($"Replacement link {linkId} did not reach {state}.");
    }
}

internal sealed record P5AdvanceWorld(TenantId Tenant, Guid Entity, Guid Customer, Guid Site, Guid ItemId, Guid PolicyVersionId);
internal sealed record P5CustomerCase(P5AdvanceWorld World, Guid OriginalUnitId, string OriginalSerial, Guid ReplacementUnitId,
    string ReplacementSerial, Guid SourceEntitlementId, Guid ClaimId);
internal sealed record P5Rma(Guid RmaId, Guid RmaLineId);
internal sealed record P5Unit(Guid UnitId, string Serial);

internal static class P5AdvanceTestData
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    internal static async Task<T> InvokeAsync<T>(SerialisationHostFixture fixture, TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    internal static async Task<P5AdvanceWorld> NewWorldAsync(SerialisationHostFixture fixture, string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(fixture, tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<CoverageFamilyRow>(fixture, tenant, new DefineCoverageFamily(CoverageFamilyCode.Supplier, "Supplier warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(fixture, tenant, new DefineReplacementPolicy("ADV-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var code = "ADV-" + Guid.NewGuid().ToString("N")[..8];
        var terms = PolicyValidatorTestsTerms.Standard24() with { Duration = new DurationRule(12, DurationRule.Months), ReplacementPolicyCode = "ADV-90" };
        var draft = await InvokeAsync<PolicyDraftResult>(fixture, tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new(2026, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(fixture, tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var entity = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var site = Guid.NewGuid();
        var item = Guid.NewGuid();
        var warrantyPorts = fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();
        warrantyPorts.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, code, true));
        warrantyPorts.RegisterZone(site, "Europe/Berlin");
        return new(tenant, entity, customer, site, item, draft.PolicyVersionId);
    }

    internal static async Task<P5CustomerCase> AddCustomerCaseAsync(SerialisationHostFixture fixture, P5AdvanceWorld world, string marker)
    {
        var original = await CommissionAsync(fixture, world, marker + "-OLD");
        var replacement = await CommissionAsync(fixture, world, marker + "-NEW");
        await InvokeAsync<OpenOwnershipResult>(fixture, world.Tenant, new OpenOwnership(Guid.NewGuid(), original.UnitId, world.Customer,
            PartyType.Customer, EpisodeReason.Sale, At.AddDays(-8), "test", world.Entity));
        await InvokeAsync<ChangeCustodyResult>(fixture, world.Tenant, new ChangeCustody(Guid.NewGuid(), original.UnitId,
            LocationKind.CustomerSite, EpisodeReason.Sale, At.AddDays(-7), "test", world.Entity, world.Site,
            world.Customer, PartyType.Customer));
        var entitlement = await InvokeAsync<CreateEntitlementResult>(fixture, world.Tenant, new CreateEntitlement(Guid.NewGuid(), original.UnitId,
            CoverageFamilyCode.Base, new(2026, 9, 1), world.Entity, At, "desk", new Dictionary<string, DateTime> { [StartTrigger.Deliver] = At },
            PolicyVersionId: world.PolicyVersionId, OwnerPartyRef: world.Customer, OwnerPartyType: PartyType.Customer, Country: "DE",
            SiteLocationId: world.Site, SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        var claimId = Guid.NewGuid();
        using (var scope = fixture.ScopeFor(world.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Claims.Add(new Claim
            {
                TenantId = world.Tenant, ClaimId = claimId, ClaimNumber = "CLM-2026-" + Guid.NewGuid().ToString("N")[..8], CommandId = Guid.NewGuid(),
                UnitId = original.UnitId, ClaimDate = new(2026, 9, 2), ClaimantPartyRef = world.Customer, SettlementPartyRef = world.Customer,
                ServiceEntityId = world.Entity, ObligorEntityId = world.Entity, ServiceCountry = "DE", Currency = "EUR",
                CoverageSource = ClaimCoverageSource.Warranty, Remedy = ClaimRemedy.Replacement, Status = ClaimStatus.Approved,
                EntitlementIds = JsonSerializer.Serialize(new[] { entitlement.EntitlementId }, LedgerJson.Options), SubmittedAt = At,
                AdjudicatedAt = At.AddHours(1), SubmittedBy = "desk", LastEventId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }
        return new(world, original.UnitId, original.Serial, replacement.UnitId, replacement.Serial, entitlement.EntitlementId, claimId);
    }

    internal static async Task<P5Unit> CommissionAsync(SerialisationHostFixture fixture, P5AdvanceWorld world, string marker)
    {
        var serial = marker.ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var result = await InvokeAsync<CommissionUnitsResult>(fixture, world.Tenant, new CommissionUnits(Guid.NewGuid(), world.ItemId, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At.AddDays(-10), "test", world.Entity,
            LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return new(result.Lines[0].UnitId!.Value, serial);
    }

    internal static CreateWarrantyReplacement CreateCommand(P5CustomerCase customerCase, string sourceKind, Guid sourceId, DateTime at,
        DateOnly? due, decimal nonReturnAmount = 1000m) =>
        new(Guid.NewGuid(), sourceKind, sourceId, customerCase.SourceEntitlementId, customerCase.ReplacementUnitId,
            ReplacementReason.WarrantyDefect, ReplacementPricePolicy.Zero, 0m, "EUR", "DE", true, false, at,
            customerCase.World.Entity, ReplacementConditionAllowed: ReplacementConditionAllowed.Any, ReturnDue: due,
            NonReturnPriceBasis: due is null ? null : NonReturnPriceBasis.List, NonReturnAmount: due is null ? null : nonReturnAmount);

    internal static async Task<P5Rma> SeedAuthorisedRmaAsync(SerialisationHostFixture fixture, P5CustomerCase customerCase)
    {
        var rmaId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        using var scope = fixture.ScopeFor(customerCase.World.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var ownership = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == customerCase.OriginalUnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        db.Rmas.Add(new Rma
        {
            TenantId = customerCase.World.Tenant, RmaId = rmaId, CommandId = Guid.NewGuid(), RmaNumber = "RMA-2026-" + Guid.NewGuid().ToString("N")[..8],
            Stage = RmaStage.Authorised, RequestDate = new(2026, 9, 1), ReturningPartyRef = customerCase.World.Customer,
            SettlementPartyRef = customerCase.World.Customer, ServiceEntityId = customerCase.World.Entity, ObligorEntityId = customerCase.World.Entity,
            ServiceCountry = "DE", Currency = "EUR", RequestedAt = At, AuthorisedAt = At.AddHours(1), RequestedBy = "desk", LastEventId = Guid.NewGuid(),
        });
        db.RmaLines.Add(new RmaLine
        {
            TenantId = customerCase.World.Tenant, RmaLineId = lineId, RmaId = rmaId, Sequence = 1,
            RequestedUnitId = customerCase.OriginalUnitId, AuthorisedUnitId = customerCase.OriginalUnitId, OwnershipEpisodeId = ownership.EpisodeId,
            OriginalSaleDocumentType = "invoice", OriginalSaleDocumentId = Guid.NewGuid(), OriginalSaleDocumentLine = 1,
            CoverageFamily = CoverageFamilyCode.Base, ClaimLineKind = ClaimLineKind.Parts, RequestedAmount = 100m,
            CoveragePct = 100m, ApprovedAmount = 100m, CoverageSource = ClaimCoverageSource.Warranty,
            EntitlementId = customerCase.SourceEntitlementId, PolicyVersionId = customerCase.World.PolicyVersionId,
        });
        await db.SaveChangesAsync();
        // This helper deliberately bypasses RequestRma/AuthoriseRma, so repair the synchronous read projection that
        // those commands normally maintain before exercising a real stage-transition command.
        await InvokeAsync<RebuildRmaStageCountersResult>(fixture, customerCase.World.Tenant,
            new RebuildRmaStageCounters(Guid.NewGuid(), "test administrator"));
        return new(rmaId, lineId);
    }

    internal static Task<ReceiveReturnResult> ReceiveAsync(SerialisationHostFixture fixture, P5CustomerCase customerCase, P5Rma rma,
        string serial, DateTime receivedAt) => InvokeAsync<ReceiveReturnResult>(fixture, customerCase.World.Tenant,
            new ReceiveReturn(Guid.NewGuid(), rma.RmaId, Guid.NewGuid(),
                [new(rma.RmaLineId, new DocumentSerial(serial), ConditionGrades.Defective, "graded_value")],
                receivedAt.AddHours(-1), receivedAt, receivedAt));
}
