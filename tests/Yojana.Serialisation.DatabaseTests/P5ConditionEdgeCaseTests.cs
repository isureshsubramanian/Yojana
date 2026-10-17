using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Campaigns;
using Yojana.Serialisation.Condition;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Refurbishments;
using Yojana.Serialisation.Replacements;
using Yojana.Serialisation.Rmas;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T18 part B: the condition, refurbishment and coverage cases allocated to this worktree.</summary>
[Collection("serialisation host")]
public sealed class P5ConditionEdgeCaseTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("b0000000-0000-0000-0000-000000000018");
    private static readonly Guid Warehouse = Guid.Parse("b0000000-0000-0000-0000-000000000019");
    private static readonly Guid Repair = Guid.Parse("b0000000-0000-0000-0000-000000000020");

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task InvokeEventAsync(TenantId tenant, object message)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None, TimeSpan.FromSeconds(20));
    }

    [DatabaseFact]
    public async Task Case_23_rental_keeps_our_ownership_renter_custody_and_usage_history()
    {
        var tenant = await fixture.NewTenantAsync("p5-case23");
        var unit = await CommissionAsync(tenant, "RENTAL");
        var renter = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit.UnitId, Entity, PartyType.Us,
            EpisodeReason.Rental, At.AddMinutes(1), "rental-desk", Entity));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit.UnitId, LocationKind.CustomerSite,
            EpisodeReason.Rental, At.AddHours(1), "rental-desk", Entity, Guid.NewGuid(), renter, PartyType.Customer, DueBack: new(2026, 10, 1)));
        await InvokeAsync<RecordUsageReadingResult>(tenant, new RecordUsageReading(Guid.NewGuid(), unit.UnitId, "runtime", 248m,
            "hours", At.AddDays(10), "rental-desk", Entity));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertOwnerAndHolderAsync(db, unit.UnitId, Entity, PartyType.Us, LocationKind.CustomerSite, renter);
        Assert.Equal(248m, (await db.UsageReadings.SingleAsync(r => r.UnitId == unit.UnitId)).Value);
        await AssertOneOpenSerialAndNoEntitlementAsync(db, unit.UnitId);
        Assert.Contains(UnitEventType.UsageRecorded, await EventTypesAsync(db, unit.UnitId));
    }

    [DatabaseFact]
    public async Task Case_29_return_without_a_resolved_serial_records_a_provisional_identity_and_preserves_the_known_unit()
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, "p5-case29");
        var customerCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "provisional");
        var rma = await P5AdvanceTestData.SeedAuthorisedRmaAsync(fixture, customerCase);
        var result = await InvokeAsync<ReceiveReturnResult>(world.Tenant, new ReceiveReturn(Guid.NewGuid(), rma.RmaId, Guid.NewGuid(),
            [new(rma.RmaLineId, new DocumentSerial("UNREADABLE-" + Guid.NewGuid().ToString("N")[..8]), ConditionGrades.Defective,
                "graded_value", ReturnOutcome.Unauthorised, "serial cannot be resolved")], At.AddHours(2), At.AddHours(3), At.AddHours(3), "returns"));

        var receipt = Assert.Single(result.Lines);
        Assert.Null(receipt.ReceivedUnitId);
        Assert.NotNull(receipt.ProvisionalUnitId);
        Assert.True(receipt.ExceptionRaised);
        using var scope = fixture.ScopeFor(world.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(receipt.ProvisionalUnitId, (await db.ReturnReceipts.SingleAsync(r => r.ReturnReceiptId == receipt.ReturnReceiptId)).ProvisionalUnitId);
        await AssertOwnerAndHolderAsync(db, customerCase.OriginalUnitId, world.Customer, PartyType.Customer, LocationKind.CustomerSite, world.Customer);
        Assert.Equal(EntitlementStatus.Active, (await db.Entitlements.SingleAsync(e => e.EntitlementId == customerCase.SourceEntitlementId)).Status);
        Assert.Single(await db.Identifiers.Where(i => i.UnitId == customerCase.OriginalUnitId && i.ValidTo == null).ToListAsync());
        Assert.Contains(RmaEventType.Received, await EventTypesAsync(db, customerCase.OriginalUnitId));
    }

    [DatabaseFact]
    public async Task Case_48_loaner_becomes_the_replacement_without_a_stock_issue()
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, "p5-case48");
        var customerCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "loan-original");
        var loaner = await P5AdvanceTestData.CommissionAsync(fixture, world, "loaner");
        await InvokeAsync<OpenOwnershipResult>(world.Tenant, new OpenOwnership(Guid.NewGuid(), loaner.UnitId, world.Entity,
            PartyType.Us, EpisodeReason.Loan, At.AddDays(-4), "service", world.Entity));
        await InvokeAsync<ChangeCustodyResult>(world.Tenant, new ChangeCustody(Guid.NewGuid(), loaner.UnitId,
            LocationKind.CustomerSite, EpisodeReason.Loan, At.AddDays(-3), "service", world.Entity,
            PartyRef: world.Customer, PartyType: PartyType.Customer));
        var issue = new DocumentRef("loaner_issue", Guid.NewGuid(), 1);
        fixture.Services.GetRequiredService<InMemoryDocumentPorts>().Add(new LoanerIssue(issue, world.Customer, At.AddDays(-3),
            world.Site, new(2026, 9, 20), [new DocumentLine(1, world.ItemId, 1, [new(loaner.Serial)])]));
        var stock = fixture.Services.GetRequiredService<InMemoryStockUnitEventPort>();
        var stockBefore = stock.ReplacementIssues.Count;

        var converted = await InvokeAsync<ConvertLoanToReplacementResult>(world.Tenant,
            new ConvertLoanToReplacement(Guid.NewGuid(), issue, customerCase.OriginalUnitId, customerCase.SourceEntitlementId,
                loaner.UnitId, ReplacementReason.WarrantyDefect, new(2026, 9, 5), At.AddDays(4), world.Entity, "DE", true, false));

        Assert.Equal(stockBefore, stock.ReplacementIssues.Count);
        using var scope = fixture.ScopeFor(world.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertOwnerAndHolderAsync(db, loaner.UnitId, world.Customer, PartyType.Customer, LocationKind.CustomerSite, world.Customer);
        await AssertOwnerAndHolderAsync(db, customerCase.OriginalUnitId, world.Entity, PartyType.Us, LocationKind.CustomerSite, world.Customer);
        Assert.Equal(EntitlementStatus.Closed, (await db.Entitlements.SingleAsync(e => e.EntitlementId == customerCase.SourceEntitlementId)).Status);
        Assert.Equal(EntitlementStatus.Active, (await db.Entitlements.SingleAsync(e => e.EntitlementId == converted.TargetEntitlementId)).Status);
        Assert.Equal(2, await db.Identifiers.CountAsync(i => (i.UnitId == customerCase.OriginalUnitId || i.UnitId == loaner.UnitId) && i.ValidTo == null));
        Assert.Contains(ReplacementEventType.Shipped, await EventTypesAsync(db, loaner.UnitId));
        Assert.Contains(UnitEventType.OwnershipTransferred, await EventTypesAsync(db, customerCase.OriginalUnitId));
    }

    [DatabaseFact]
    public async Task Case_55_service_part_cover_uses_the_later_of_remaining_cover_and_the_minimum()
    {
        var setup = await CreateCoveredUnitAsync("p5-case55");
        var part = await CommissionAsync(setup.Tenant, "SERVICE-PART");
        await InvokeAsync<ChangeCustodyResult>(setup.Tenant, new ChangeCustody(Guid.NewGuid(), setup.UnitId,
            LocationKind.Returning, EpisodeReason.Return, At.AddMinutes(30), "service", Entity, Repair));
        await InvokeAsync<ChangeCustodyResult>(setup.Tenant, new ChangeCustody(Guid.NewGuid(), setup.UnitId,
            LocationKind.Quarantine, EpisodeReason.Return, At.AddMinutes(45), "service", Entity, Repair));
        await InvokeAsync<ChangeCustodyResult>(setup.Tenant, new ChangeCustody(Guid.NewGuid(), setup.UnitId,
            LocationKind.InRepair, EpisodeReason.Return, At.AddDays(1), "service", Entity, Repair));
        using (var seed = fixture.ScopeFor(setup.Tenant))
        {
            await seed.ServiceProvider.GetRequiredService<ServicePartEntitlementSettings>()
                .SetAsync(new ServicePartEntitlementPolicy(90, new Dictionary<Guid, int> { [part.ItemId] = 120 }), CancellationToken.None);
            var db = seed.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Rmas.Add(new Rma
            {
                TenantId = setup.Tenant, RmaId = setup.WorkId, CommandId = Guid.NewGuid(), RmaNumber = "RMA-FICTIONAL-55",
                Stage = RmaStage.Closed, RequestDate = new(2026, 9, 1), ReturningPartyRef = setup.Customer,
                SettlementPartyRef = setup.Customer, ServiceEntityId = Entity, ObligorEntityId = Entity, ServiceCountry = "DE",
                Currency = "EUR", RequestedAt = At, DispositionedAt = At.AddHours(1), ClosedAt = At.AddHours(1),
                RequestedBy = "service", LastActorRef = "service", LastEventId = Guid.NewGuid(),
            });
            db.RmaLines.Add(new RmaLine
            {
                TenantId = setup.Tenant, RmaLineId = Guid.NewGuid(), RmaId = setup.WorkId, Sequence = 1,
                RequestedUnitId = setup.UnitId, AuthorisedUnitId = setup.UnitId, OwnershipEpisodeId = setup.OwnershipEpisodeId,
                OriginalSaleDocumentType = "invoice", OriginalSaleDocumentId = Guid.NewGuid(), OriginalSaleDocumentLine = 1,
                CoverageFamily = CoverageFamilyCode.Base, ClaimLineKind = ClaimLineKind.Parts, RequestedAmount = 100m,
                CoveragePct = 100m, ApprovedAmount = 100m, CoverageSource = ClaimCoverageSource.Warranty,
                EntitlementId = setup.EntitlementId, PolicyVersionId = setup.PolicyVersionId, ReceivedUnitId = setup.UnitId,
                ReceiptOutcome = ReturnOutcome.Accepted, Disposition = RmaDisposition.RepairAndReturn,
            });
            await db.SaveChangesAsync();
        }
        var installedAt = At.AddDays(2);
        var linkId = Guid.NewGuid();
        var header = new PublishedEventHeader(UnitPartInstalled.Schema, UnitPartInstalled.Version, Guid.NewGuid(), part.UnitId,
            installedAt, installedAt, 1, Entity, "technician", new DocumentRef(CoverageWorkType.Rma, setup.WorkId, 1));
        await InvokeEventAsync(setup.Tenant, new UnitPartInstalled(header, setup.UnitId, linkId, part.ItemId, AssemblyRelation.Component, "service"));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db2 = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var cover = await db2.Entitlements.SingleAsync(e => e.UnitId == part.UnitId && e.Provenance == EntitlementProvenance.ServicePart);
        Assert.Equal((setup.EndDate, setup.EntitlementId, linkId), (cover.EndDate, cover.ServiceParentEntitlementId, cover.InstallationLinkId));
        await AssertOwnerAndHolderAsync(db2, part.UnitId, setup.Customer, PartyType.Customer, LocationKind.InRepair, Repair);
        await AssertOwnerAndHolderAsync(db2, setup.UnitId, setup.Customer, PartyType.Customer, LocationKind.InRepair, Repair);
        Assert.Equal(EntitlementStatus.Active, (await db2.Entitlements.SingleAsync(e => e.EntitlementId == setup.EntitlementId)).Status);
        Assert.Equal(2, await db2.Identifiers.CountAsync(i => (i.UnitId == setup.UnitId || i.UnitId == part.UnitId) && i.ValidTo == null));
        Assert.Contains(EntitlementEventType.Created, await EventTypesAsync(db2, part.UnitId));
        Assert.Contains(UnitEventType.CustodyChanged, await EventTypesAsync(db2, setup.UnitId));
    }

    [DatabaseFact]
    public async Task Case_87_grading_publishes_the_inventory_valuation_rule_without_changing_the_unit_parties()
    {
        var tenant = await fixture.NewTenantAsync("p5-case87");
        var unit = await StockUnitAsync(tenant, "VALUE");
        var graded = await InvokeAsync<GradeUnitResult>(tenant, new GradeUnit(Guid.NewGuid(), unit.UnitId,
            ConditionGrades.Defective, "graded_receipt_35pct", At.AddHours(1), "quality", Entity, "F5", "C7"));
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<UnitGraded>(e => e.Header.EventId == graded.EventId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((ConditionGrades.Defective, "graded_receipt_35pct"), (published.Grade, published.ValuationRule));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertOwnerAndHolderAsync(db, unit.UnitId, Entity, PartyType.Us, LocationKind.Warehouse, Warehouse);
        await AssertOneOpenSerialAndNoEntitlementAsync(db, unit.UnitId);
        Assert.Contains(UnitEventType.Graded, await EventTypesAsync(db, unit.UnitId));
    }

    [DatabaseFact]
    public async Task Case_90_condition_grade_is_current_state_derived_from_immutable_history()
    {
        var tenant = await fixture.NewTenantAsync("p5-case90");
        var unit = await StockUnitAsync(tenant, "GRADE-HISTORY");
        await InvokeAsync<GradeUnitResult>(tenant, new GradeUnit(Guid.NewGuid(), unit.UnitId, ConditionGrades.Defective,
            "defective_20pct", At.AddHours(1), "quality", Entity));
        await InvokeAsync<GradeUnitResult>(tenant, new GradeUnit(Guid.NewGuid(), unit.UnitId, ConditionGrades.RefurbishedB,
            "graded_plus_refurbishment", At.AddHours(2), "quality", Entity, "F2", "C4"));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal([ConditionGrades.Defective, ConditionGrades.RefurbishedB],
            await db.ConditionEvents.Where(e => e.UnitId == unit.UnitId).OrderBy(e => e.GradedAt).Select(e => e.Grade).ToListAsync());
        Assert.Equal(ConditionGrades.RefurbishedB, (await db.Current.SingleAsync(c => c.UnitId == unit.UnitId)).ConditionGrade);
        await AssertOwnerAndHolderAsync(db, unit.UnitId, Entity, PartyType.Us, LocationKind.Warehouse, Warehouse);
        await AssertOneOpenSerialAndNoEntitlementAsync(db, unit.UnitId);
        Assert.Equal(2, (await EventTypesAsync(db, unit.UnitId)).Count(t => t == UnitEventType.Graded));
    }

    [DatabaseFact]
    public async Task Case_91_refurbishment_parts_and_labour_roll_into_the_released_total()
    {
        var setup = await CreateRefurbishmentUnitAsync("p5-case91", RefurbishmentIdentity.SameUnitSameSerial, 3);
        var opened = await OpenRefurbishmentAsync(setup, At.AddHours(1));
        await InvokeAsync<RefurbishmentCostResult>(setup.Tenant, new RecordRefurbishmentCost(Guid.NewGuid(), opened.WorkOrderId,
            RefurbishmentCostKind.GradedReceipt, 600m, "EUR", "Graded receipt", At.AddHours(1.2), "finance", Entity));
        await InvokeAsync<RefurbishmentLabourResult>(setup.Tenant, new RecordRefurbishmentLabour(Guid.NewGuid(), opened.WorkOrderId,
            null, 2m, 50m, "EUR", At.AddHours(1.3), "technician", Entity));
        await InvokeAsync<RefurbishmentPartResult>(setup.Tenant, new InstallRefurbishmentPart(Guid.NewGuid(), opened.WorkOrderId,
            new ChildInput(null, Guid.NewGuid(), Quantity: 1, Slot: "module"), 80m, "EUR", At.AddHours(1.4), "technician", Entity, Repair));
        await PassingTestAsync(setup.Tenant, setup.UnitId, At.AddHours(2));
        var released = await ReleaseRefurbishmentAsync(setup, opened, At.AddHours(3));

        Assert.Equal((780m, "EUR"), (released.TotalCost, released.Currency));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(780m, await db.RefurbishmentCosts.Where(c => c.WorkOrderId == opened.WorkOrderId).SumAsync(c => c.Amount));
        await AssertOwnerAndHolderAsync(db, setup.UnitId, Entity, PartyType.Us, LocationKind.Warehouse, Warehouse);
        await AssertOneOpenSerialAndNoEntitlementAsync(db, setup.UnitId);
        Assert.Contains(RefurbishmentEventType.Released, await EventTypesAsync(db, setup.UnitId));
    }

    [DatabaseFact]
    public async Task Case_94_refurbished_resale_uses_the_condition_grade_and_applies_the_EU_floor()
    {
        var tenant = await fixture.NewTenantAsync("p5-case94");
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
            Refurbished = [new RefurbishedTerm(null, "A", "direct", "EU", 3, null)],
        };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "REF-A-" + Guid.NewGuid().ToString("N")[..6],
            "Refurbished A", CoverageFamilyCode.Base, EntitlementType.Manufacturer, new(2026, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var unit = await CommissionAsync(tenant, "REFURB-SALE");
        var customer = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit.UnitId, customer, PartyType.Customer,
            EpisodeReason.Sale, At.AddMinutes(1), "sales", Entity));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit.UnitId, LocationKind.CustomerSite,
            EpisodeReason.Sale, At.AddMinutes(2), "sales", Entity, Guid.NewGuid(), customer, PartyType.Customer));
        await InvokeAsync<GradeUnitResult>(tenant, new GradeUnit(Guid.NewGuid(), unit.UnitId, ConditionGrades.RefurbishedA,
            "graded_plus_refurbishment", At.AddMinutes(3), "quality", Entity));
        var created = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit.UnitId,
            CoverageFamilyCode.Base, new(2026, 9, 1), Entity, At.AddMinutes(4), "sales",
            new Dictionary<string, DateTime> { [StartTrigger.Deliver] = At }, draft.PolicyVersionId, customer, PartyType.Customer,
            Channel: "direct", Country: "DE", BuyerKind: RefurbishedBuyerKind.Consumer, RefurbisherPartyRef: Entity));

        Assert.Equal((12, true, "jurisdiction", "A"),
            (created.Disclosure!.TermMonths, created.Disclosure.FloorApplied, created.Disclosure.FloorSource, created.Disclosure.MatchedRow.Grade));
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertOwnerAndHolderAsync(db, unit.UnitId, customer, PartyType.Customer, LocationKind.CustomerSite, customer);
        Assert.Equal(created.EntitlementId, (await db.Entitlements.SingleAsync(e => e.UnitId == unit.UnitId)).EntitlementId);
        Assert.Single(await db.Identifiers.Where(i => i.UnitId == unit.UnitId && i.ValidTo == null).ToListAsync());
        Assert.Contains(EntitlementEventType.Created, await EventTypesAsync(db, unit.UnitId));
    }

    [DatabaseFact]
    public async Task Case_95_returned_replacement_closes_inherited_cover_and_a_resale_cannot_reuse_the_chain()
    {
        var setup = await CreateCoveredUnitAsync("p5-case95");
        var returned = await CommissionAsync(setup.Tenant, "INHERITED");
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), returned.UnitId, setup.Customer,
            PartyType.Customer, EpisodeReason.Replacement, At.AddMinutes(1), "replacement", Entity));
        await InvokeAsync<ChangeCustodyResult>(setup.Tenant, new ChangeCustody(Guid.NewGuid(), returned.UnitId,
            LocationKind.CustomerSite, EpisodeReason.Replacement, At.AddMinutes(2), "replacement", Entity,
            Guid.NewGuid(), setup.Customer, PartyType.Customer));
        var inherited = await InvokeAsync<TransferEntitlementResult>(setup.Tenant, new TransferEntitlement(Guid.NewGuid(),
            setup.EntitlementId, returned.UnitId, Guid.NewGuid(), new(2026, 9, 1), At.AddMinutes(3), "replacement", "EDGE-90"));
        var ownership = await CurrentOwnershipAsync(setup.Tenant, returned.UnitId);
        var rmaId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        using (var seed = fixture.ScopeFor(setup.Tenant))
        {
            var db = seed.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Rmas.Add(new Rma
            {
                TenantId = setup.Tenant, RmaId = rmaId, CommandId = Guid.NewGuid(), RmaNumber = "RMA-FICTIONAL-95",
                Stage = RmaStage.Authorised, RequestDate = new(2026, 9, 1), ReturningPartyRef = setup.Customer,
                SettlementPartyRef = setup.Customer, ServiceEntityId = Entity, ObligorEntityId = Entity, ServiceCountry = "DE",
                Currency = "EUR", RequestedAt = At, AuthorisedAt = At.AddHours(1), RequestedBy = "returns", LastEventId = Guid.NewGuid(),
            });
            db.RmaLines.Add(new RmaLine
            {
                TenantId = setup.Tenant, RmaLineId = lineId, RmaId = rmaId, Sequence = 1, RequestedUnitId = returned.UnitId,
                AuthorisedUnitId = returned.UnitId, OwnershipEpisodeId = ownership.EpisodeId, OriginalSaleDocumentType = "invoice",
                OriginalSaleDocumentId = Guid.NewGuid(), OriginalSaleDocumentLine = 1, CoverageFamily = CoverageFamilyCode.Base,
                ClaimLineKind = ClaimLineKind.Parts, RequestedAmount = 100m, CoveragePct = 100m, ApprovedAmount = 100m,
                CoverageSource = ClaimCoverageSource.Warranty, EntitlementId = inherited.TargetEntitlementId,
                PolicyVersionId = setup.PolicyVersionId,
            });
            await db.SaveChangesAsync();
        }
        await InvokeAsync<ReceiveReturnResult>(setup.Tenant, new ReceiveReturn(Guid.NewGuid(), rmaId, Warehouse,
            [new(lineId, new DocumentSerial(returned.Serial), ConditionGrades.Defective, "graded_value")], At.AddHours(2), At.AddHours(3), At.AddHours(3), "returns"));
        await InvokeAsync<DispositionRmaResult>(setup.Tenant, new DispositionRma(Guid.NewGuid(), rmaId,
            [new(lineId, RmaDisposition.ReturnToStock)], new(2026, 9, 1), At.AddHours(4), "returns"));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db2 = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var closed = await db2.Entitlements.SingleAsync(e => e.EntitlementId == inherited.TargetEntitlementId);
        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.ReturnedToStock), (closed.Status, closed.ClosedReason));
        await AssertOwnerAndHolderAsync(db2, returned.UnitId, Entity, PartyType.Us, LocationKind.Warehouse, null);
        await AssertOwnerAndHolderAsync(db2, setup.UnitId, setup.Customer, PartyType.Customer, LocationKind.CustomerSite, setup.Customer);
        Assert.Equal(EntitlementStatus.Closed, (await db2.Entitlements.SingleAsync(e => e.EntitlementId == setup.EntitlementId)).Status);
        Assert.Equal(2, await db2.Identifiers.CountAsync(i => (i.UnitId == setup.UnitId || i.UnitId == returned.UnitId) && i.ValidTo == null));
        var events = await EventTypesAsync(db2, returned.UnitId);
        Assert.Contains(RmaEventType.Received, events);
        Assert.Contains(EntitlementEventType.Closed, events);
        Assert.Contains(RmaEventType.Dispositioned, events);
        Assert.Contains(EntitlementEventType.Closed, await EventTypesAsync(db2, setup.UnitId));
    }

    [DatabaseFact]
    public async Task Case_96_maximum_refurbishment_cycles_blocks_the_next_work_order()
    {
        var setup = await CreateRefurbishmentUnitAsync("p5-case96", RefurbishmentIdentity.SameUnitSameSerial, 1);
        var opened = await OpenRefurbishmentAsync(setup, At.AddHours(1));
        await PassingTestAsync(setup.Tenant, setup.UnitId, At.AddHours(2));
        await ReleaseRefurbishmentAsync(setup, opened, At.AddHours(3));

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<OpenRefurbishmentResult>(setup.Tenant,
            new OpenRefurbishment(Guid.NewGuid(), setup.UnitId, RefurbishmentKind.Refurbish, At.AddHours(4), "quality", Entity, Repair)));
        Assert.Equal("max_refurb_cycles", refusal.Rule);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertOwnerAndHolderAsync(db, setup.UnitId, Entity, PartyType.Us, LocationKind.Warehouse, Warehouse);
        await AssertOneOpenSerialAndNoEntitlementAsync(db, setup.UnitId);
        Assert.Single(await EventTypesAsync(db, setup.UnitId), t => t == RefurbishmentEventType.Released);
    }

    [DatabaseFact]
    public async Task Case_97_data_bearing_return_is_blocked_until_a_post_return_sanitisation_record_exists()
    {
        var tenant = await fixture.NewTenantAsync("p5-case97");
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with
        {
            ItemClass = "data-bearing", RequiresSanitisation = true,
        });
        var unit = await StockUnitAsync(tenant, "WIPE", item);
        var customer = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit.UnitId, customer, PartyType.Customer,
            EpisodeReason.Sale, At.AddHours(1), "sales", Entity));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit.UnitId, Entity, PartyType.Us,
            EpisodeReason.Return, At.AddHours(2), "returns", Entity));
        var guard = fixture.ScopeFor(tenant);
        using (guard)
        {
            var service = guard.ServiceProvider.GetRequiredService<IUnitSaleShipmentGuard>();
            var blocked = (await service.CheckAsync([new(unit.UnitId, item, At.AddHours(3))], CancellationToken.None))[unit.UnitId];
            Assert.Equal((false, "sanitisation_required"), (blocked.Allowed, blocked.Rule));
        }
        await InvokeAsync<RecordSanitisationResult>(tenant, new RecordSanitisation(Guid.NewGuid(), unit.UnitId,
            "Fictional Systems", "Storage Node", "MEDIA-97", "solid_state", SanitisationMethod.Purge, "Erase Bench",
            "full verification", "CERT-97", At.AddHours(2.5), "quality", Entity));
        using (var verifyGuard = fixture.ScopeFor(tenant))
        {
            var allowed = (await verifyGuard.ServiceProvider.GetRequiredService<IUnitSaleShipmentGuard>()
                .CheckAsync([new(unit.UnitId, item, At.AddHours(3))], CancellationToken.None))[unit.UnitId];
            Assert.True(allowed.Allowed);
        }
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        await AssertOwnerAndHolderAsync(db, unit.UnitId, Entity, PartyType.Us, LocationKind.Warehouse, Warehouse);
        await AssertOneOpenSerialAndNoEntitlementAsync(db, unit.UnitId);
        Assert.Contains(UnitEventType.SanitisationRecorded, await EventTypesAsync(db, unit.UnitId));
    }

    [DatabaseFact]
    public async Task Case_98_firmware_history_supports_current_and_effective_dated_campaign_criteria()
    {
        var tenant = await fixture.NewTenantAsync("p5-case98");
        var unit = await StockUnitAsync(tenant, "FIRMWARE");
        await InvokeAsync<RecordUnitAttributesResult>(tenant, new RecordUnitAttributes(Guid.NewGuid(), unit.UnitId,
            new Dictionary<string, string?> { ["firmware"] = "1.4.0" }, At.AddDays(1), "device-management", Entity));
        await InvokeAsync<RecordUnitAttributesResult>(tenant, new RecordUnitAttributes(Guid.NewGuid(), unit.UnitId,
            new Dictionary<string, string?> { ["firmware"] = "2.0.0" }, At.AddDays(3), "device-management", Entity));
        var historical = await CampaignAsync(tenant, "FW-H", new CampaignCriterionInput(CampaignCriterionKind.Firmware,
            FirmwareValue: "1.4.0", EffectiveAt: At.AddDays(2)));
        var current = await CampaignAsync(tenant, "FW-C", new CampaignCriterionInput(CampaignCriterionKind.Firmware, FirmwareValue: "2.0.0"));

        Assert.Equal(1, (await InvokeAsync<CampaignPreview>(tenant, new PreviewCampaign(historical.Campaign.CampaignId))).Page.Total);
        Assert.Equal(1, (await InvokeAsync<CampaignPreview>(tenant, new PreviewCampaign(current.Campaign.CampaignId))).Page.Total);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(["1.4.0", "2.0.0"], await db.AttributeHistory.Where(a => a.UnitId == unit.UnitId && a.Key == "firmware")
            .OrderBy(a => a.EffectiveFrom).Select(a => a.Value).ToListAsync());
        await AssertOwnerAndHolderAsync(db, unit.UnitId, Entity, PartyType.Us, LocationKind.Warehouse, Warehouse);
        await AssertOneOpenSerialAndNoEntitlementAsync(db, unit.UnitId);
        Assert.Equal(2, (await EventTypesAsync(db, unit.UnitId)).Count(t => t == UnitEventType.AttributeChanged));
    }

    [DatabaseFact]
    public async Task Case_120_repeated_reserialisation_keeps_one_unit_and_an_arbitrarily_deep_identifier_chain()
    {
        var setup = await CreateRefurbishmentUnitAsync("p5-case120", RefurbishmentIdentity.SameUnitNewSerial, 5, withEntitlement: true);
        for (var cycle = 1; cycle <= 3; cycle++)
        {
            var opened = await OpenRefurbishmentAsync(setup, At.AddHours(cycle * 3));
            await PassingTestAsync(setup.Tenant, setup.UnitId, At.AddHours(cycle * 3 + 1));
            var released = await ReleaseRefurbishmentAsync(setup, opened, At.AddHours(cycle * 3 + 2), $"CHAIN-{cycle}-{Guid.NewGuid():N}");
            Assert.Equal(setup.UnitId, released.ReleasedUnitId);
        }

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var serials = await db.Identifiers.Where(i => i.UnitId == setup.UnitId && i.Type == IdentifierType.InternalSerial)
            .OrderBy(i => i.ValidFrom).ThenBy(i => i.IdentifierId).ToListAsync();
        Assert.Equal(4, serials.Count);
        for (var i = 1; i < serials.Count; i++) Assert.Equal(serials[i - 1].IdentifierId, serials[i].SupersedesIdentifierId);
        Assert.Single(serials, i => i.ValidTo == null);
        await AssertOwnerAndHolderAsync(db, setup.UnitId, Entity, PartyType.Us, LocationKind.Warehouse, Warehouse);
        Assert.Equal(setup.EntitlementId, (await db.Entitlements.SingleAsync(e => e.UnitId == setup.UnitId)).EntitlementId);
        Assert.Equal(3, (await EventTypesAsync(db, setup.UnitId)).Count(t => t == RefurbishmentEventType.Released));
    }

    private async Task<CampaignResult> CampaignAsync(TenantId tenant, string marker, CampaignCriterionInput criterion) =>
        await InvokeAsync<CampaignResult>(tenant, new CreateCampaign(Guid.NewGuid(), marker + "-" + Guid.NewGuid().ToString("N")[..6],
            "Firmware campaign", CampaignCoverageSource.Recall, "recall:firmware", [criterion], At.AddDays(10),
            "service-manager", CampaignReason.Firmware, null, null));

    private async Task<UnitSetup> CommissionAsync(TenantId tenant, string marker, Guid? itemId = null)
    {
        var item = itemId ?? Guid.NewGuid();
        var serial = marker + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At,
            "receiving", Entity, LocationId: Warehouse, LocationKind: LocationKind.Warehouse));
        return new(tenant, item, Assert.Single(commissioned.Lines).UnitId!.Value, serial, null);
    }

    private async Task<UnitSetup> StockUnitAsync(TenantId tenant, string marker, Guid? itemId = null)
    {
        var unit = await CommissionAsync(tenant, marker, itemId);
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit.UnitId, Entity, PartyType.Us,
            "company_stock", At.AddMinutes(1), "receiving", Entity));
        return unit;
    }

    private async Task<CoveredSetup> CreateCoveredUnitAsync(string tenantName)
    {
        var tenant = await fixture.NewTenantAsync(tenantName, "EUR");
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("EDGE-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var code = "EDGE-" + Guid.NewGuid().ToString("N")[..6];
        var terms = PolicyValidatorTestsTerms.Standard24() with { ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc, ReplacementPolicyCode = "EDGE-90" };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new(2026, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var unit = await CommissionAsync(tenant, "COVERED");
        var customer = Guid.NewGuid();
        var ownership = await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit.UnitId, customer,
            PartyType.Customer, EpisodeReason.Sale, At.AddMinutes(1), "sales", Entity));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit.UnitId, LocationKind.CustomerSite,
            EpisodeReason.Sale, At.AddMinutes(2), "sales", Entity, Guid.NewGuid(), customer, PartyType.Customer));
        var entitlement = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit.UnitId,
            CoverageFamilyCode.Base, new(2026, 9, 1), Entity, At.AddMinutes(3), "sales",
            new Dictionary<string, DateTime> { [StartTrigger.Deliver] = At }, draft.PolicyVersionId, customer, PartyType.Customer,
            Country: "DE", SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        return new(tenant, unit.UnitId, unit.ItemId, unit.Serial, customer, ownership.EpisodeId,
            entitlement.EntitlementId, entitlement.EndDate!.Value, draft.PolicyVersionId, Guid.NewGuid());
    }

    private async Task<UnitSetup> CreateRefurbishmentUnitAsync(string tenantName, string identity, int maxCycles, bool withEntitlement = false)
    {
        var tenant = await fixture.NewTenantAsync(tenantName);
        var unit = await StockUnitAsync(tenant, "REFURB");
        using (var scope = fixture.ScopeFor(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<RefurbishmentPolicySettings>()
                .SetAsync(new RefurbishmentPolicy(identity, maxCycles, []), CancellationToken.None);
        }
        if (!withEntitlement) return unit;
        var covered = await CreatePolicyAndEntitlementAsync(unit);
        return unit with { EntitlementId = covered };
    }

    private async Task<Guid> CreatePolicyAndEntitlementAsync(UnitSetup unit)
    {
        var family = "refurb_lineage";
        await InvokeAsync<CoverageFamilyRow>(unit.Tenant, new DefineCoverageFamily(family, "Refurbishment lineage", false, AssuranceOrService.Assurance));
        var code = "LINEAGE-" + Guid.NewGuid().ToString("N")[..6];
        var terms = PolicyValidatorTestsTerms.Standard24() with { ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc };
        var draft = await InvokeAsync<PolicyDraftResult>(unit.Tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, family,
            EntitlementType.Manufacturer, new(2026, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(unit.Tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var created = await InvokeAsync<CreateEntitlementResult>(unit.Tenant, new CreateEntitlement(Guid.NewGuid(), unit.UnitId, family,
            new(2026, 9, 1), Entity, At.AddMinutes(2), "service", new Dictionary<string, DateTime> { [StartTrigger.Invoice] = At },
            draft.PolicyVersionId, Entity, PartyType.Us, Country: "DE", SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        return created.EntitlementId;
    }

    private Task<OpenRefurbishmentResult> OpenRefurbishmentAsync(UnitSetup setup, DateTime at) =>
        InvokeAsync<OpenRefurbishmentResult>(setup.Tenant, new OpenRefurbishment(Guid.NewGuid(), setup.UnitId,
            RefurbishmentKind.Refurbish, at, "quality", Entity, Repair));

    private Task<RecordTestResult> PassingTestAsync(TenantId tenant, Guid unitId, DateTime at) =>
        InvokeAsync<RecordTestResult>(tenant, new RecordTest(Guid.NewGuid(), unitId, RefurbishmentTestKind.KeyFunctions,
            UnitTestResult.Pass, "{}", "Fictional Test Bench", at, "quality", Entity));

    private Task<ReleaseRefurbishmentResult> ReleaseRefurbishmentAsync(UnitSetup setup, OpenRefurbishmentResult opened,
        DateTime at, string? serial = null) => InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant,
        new ReleaseRefurbishment(Guid.NewGuid(), opened.WorkOrderId, ConditionGrades.RefurbishedA,
            "graded_receipt_plus_postings", at, "quality", Entity, Warehouse, serial, "F2", "C2"));

    private async Task<CustodyEpisode> CurrentOwnershipAsync(TenantId tenant, Guid unitId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await OpenEpisodeAsync(scope.ServiceProvider.GetRequiredService<SerialisationDbContext>(), unitId, EpisodeKind.Ownership);
    }

    private static Task<CustodyEpisode> OpenEpisodeAsync(SerialisationDbContext db, Guid unitId, string kind) =>
        db.Episodes.SingleAsync(e => e.UnitId == unitId && e.Kind == kind && e.ValidTo == null);

    private static async Task AssertOwnerAndHolderAsync(SerialisationDbContext db, Guid unitId, Guid owner, string ownerType,
        string locationKind, Guid? holderOrLocation)
    {
        var ownership = await OpenEpisodeAsync(db, unitId, EpisodeKind.Ownership);
        var custody = await OpenEpisodeAsync(db, unitId, EpisodeKind.Custody);
        Assert.Equal((owner, ownerType), (ownership.PartyRef, ownership.PartyType));
        if (locationKind == LocationKind.CustomerSite)
            Assert.Equal((locationKind, holderOrLocation), (custody.LocationKind, custody.PartyRef));
        else
            Assert.Equal((locationKind, holderOrLocation), (custody.LocationKind, custody.LocationId));
    }

    private static async Task AssertOneOpenSerialAndNoEntitlementAsync(SerialisationDbContext db, Guid unitId)
    {
        Assert.Single(await db.Identifiers.Where(i => i.UnitId == unitId && i.Type == IdentifierType.InternalSerial && i.ValidTo == null).ToListAsync());
        Assert.Empty(await db.Entitlements.Where(e => e.UnitId == unitId).ToListAsync());
    }

    private static Task<List<string>> EventTypesAsync(SerialisationDbContext db, Guid unitId) =>
        db.Events.Where(e => e.UnitId == unitId).OrderBy(e => e.Seq).Select(e => e.EventType).ToListAsync();

    private sealed record UnitSetup(TenantId Tenant, Guid ItemId, Guid UnitId, string Serial, Guid? EntitlementId);
    private sealed record CoveredSetup(TenantId Tenant, Guid UnitId, Guid ItemId, string Serial, Guid Customer,
        Guid OwnershipEpisodeId, Guid EntitlementId, DateOnly EndDate, Guid PolicyVersionId, Guid WorkId);
}
