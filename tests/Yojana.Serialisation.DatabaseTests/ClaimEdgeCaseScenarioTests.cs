using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/*
P4-T10 coverage map (one end-to-end database scenario per owned case):
  65  -> Case_65_an_end_user_claims_and_the_distributor_is_the_settlement_party
  67  -> Case_67_a_recall_repair_posts_to_recall_and_never_to_a_warranty_cohort
  70  -> Case_70_month_nine_raises_supplier_recovery_and_month_fifteen_does_not
  73  -> Case_73_an_unknown_unit_is_reconstructed_for_review_without_invented_history (P4-T04)
  78  -> Case_78_parts_are_covered_labour_is_not_and_a_chargeable_quote_is_requested
  108 -> Case_108_a_recall_notice_uses_the_owner_and_custodian_after_resale

Reference scenario step 2 and exit criterion 1 are already covered exactly by
ClaimTests.Reference_claim_is_approved_for_replacement_and_publishes_the_warranty_cohort.
*/

/// <summary>P4-T10: claim and campaign edge cases through public commands and the published outbox.</summary>
[Collection("serialisation host")]
public sealed class ClaimEdgeCaseScenarioTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("10000000-0000-0000-0000-000000000010");
    private static readonly DateTime CoverStarted = new(2025, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<CoveredUnit> CoveredUnitAsync(string prefix, string entitlementType = EntitlementType.Manufacturer,
        CoverageTerms? coverage = null)
    {
        var tenant = await fixture.NewTenantAsync(prefix, "EUR");
        var obligor = Guid.NewGuid();
        await InvokeAsync<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant,
            new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            Coverage = coverage ?? PolicyValidatorTestsTerms.Standard24().Coverage,
            ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
            Refurbished = [],
        };
        var policyCode = "EC-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), policyCode,
            policyCode, CoverageFamilyCode.Base, entitlementType, new DateOnly(2024, 1, 1), terms, "scenario"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "scenario"));
        var item = Guid.NewGuid();
        var serial = prefix.ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            CoverStarted.AddDays(-1), "scenario", obligor, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        var unitId = Assert.Single(commissioned.Lines).UnitId!.Value;
        var entitlement = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unitId,
            CoverageFamilyCode.Base, DateOnly.FromDateTime(CoverStarted), obligor, CoverStarted, "scenario",
            new Dictionary<string, DateTime> { [StartTrigger.Deliver] = CoverStarted }, draft.PolicyVersionId,
            Country: "DE"));
        return new(tenant, unitId, obligor, entitlement);
    }

    private Task<SubmitClaimResult> SubmitAsync(CoveredUnit setup, Guid claimant, Guid settlement,
        IReadOnlyList<ClaimLineInput> lines, DateOnly? claimDate = null, DateTime? at = null) =>
        InvokeAsync<SubmitClaimResult>(setup.Tenant, new SubmitClaim(Guid.NewGuid(), setup.UnitId, null,
            claimDate ?? DateOnly.FromDateTime(At), claimant, settlement, Entity, setup.Obligor, "DE", "EUR", lines,
            [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "evidence:defect")], ClaimRemedy.Repair,
            "choice:repair", at ?? At, "service.desk", "proof:invoice"));

    [DatabaseFact]
    public async Task Case_65_an_end_user_claims_and_the_distributor_is_the_settlement_party()
    {
        var setup = await CoveredUnitAsync("ec65");
        var endUser = Guid.NewGuid();
        var distributor = Guid.NewGuid();
        var submitted = await SubmitAsync(setup, endUser, distributor,
            [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 125m)]);

        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, At.AddHours(1), "service.manager"));

        Assert.Equal(ClaimStatus.Approved, adjudicated.Status);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var claim = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims.AsNoTracking()
                .SingleAsync(c => c.ClaimId == submitted.ClaimId);
            Assert.Equal((endUser, distributor), (claim.ClaimantPartyRef, claim.SettlementPartyRef));
        }

        var published = Assert.Single(await PublishedEventSink.WaitForAsync<ClaimAdjudicated>(
            e => e.ClaimId == submitted.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((endUser, distributor), (published.ClaimantPartyRef, published.SettlementPartyRef));
    }

    [DatabaseFact]
    public async Task Case_67_a_recall_repair_posts_to_recall_and_never_to_a_warranty_cohort()
    {
        var setup = await CoveredUnitAsync("ec67", EntitlementType.RecallCampaign);
        Assert.NotNull(setup.Entitlement.AccrualCohortId);
        var submitted = await SubmitAsync(setup, Guid.NewGuid(), Guid.NewGuid(),
            [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 240m)]);

        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, At.AddHours(1), "service.manager"));

        Assert.Equal((ClaimStatus.Approved, ClaimCoverageSource.Recall), (adjudicated.Status, adjudicated.CoverageSource));
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<ClaimAdjudicated>(
            e => e.ClaimId == submitted.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(ClaimCoverageSource.Recall, published.CoverageSource);
        Assert.Null(published.CohortId);
        Assert.All(published.ApprovedAmountsByLine, line => Assert.Equal(ClaimCoverageSource.Recall, line.CoverageSource));
    }

    [DatabaseFact]
    public async Task Case_70_month_nine_raises_supplier_recovery_and_month_fifteen_does_not()
    {
        var setup = await CoveredUnitAsync("ec70");
        var vendor = Guid.NewGuid();
        var childItem = Guid.NewGuid();
        await InvokeAsync<CoverageFamilyRow>(setup.Tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Supplier, "Supplier warranty", true, AssuranceOrService.Assurance));
        var supplierTerms = PolicyValidatorTestsTerms.Standard24() with
        {
            Duration = new DurationRule(12, DurationRule.Months),
            Start = new StartRule(StartTrigger.Receipt, [], null),
            ReplacementPolicyCode = null,
            ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
            Refurbished = [],
        };
        var supplierPolicy = await InvokeAsync<PolicyDraftResult>(setup.Tenant, new CreatePolicyDraft(Guid.NewGuid(),
            "SUP-" + Guid.NewGuid().ToString("N")[..6], "Supplier warranty", CoverageFamilyCode.Supplier,
            EntitlementType.Manufacturer, new DateOnly(2024, 1, 1), supplierTerms, "scenario"));
        await InvokeAsync<PolicyDraftResult>(setup.Tenant, new PublishPolicyVersion(supplierPolicy.PolicyVersionId, "scenario"));
        fixture.Services.GetRequiredService<InMemoryWarrantyPorts>()
            .Register(new VendorWarrantyTerm(vendor, childItem, supplierPolicy.PolicyVersionId));

        var childSerial = "EC70-C-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), vendor, CoverStarted,
            [new DocumentLine(1, childItem, 1, [new DocumentSerial(childSerial)], Guid.NewGuid(), LocationKind.Warehouse)]);
        fixture.Services.GetRequiredService<InMemoryDocumentPorts>().Add(receipt);
        await InvokeAsync<ConsumedEventResult>(setup.Tenant, new PurchasingReceiptPosted(
            new ThinEnvelope(Guid.NewGuid(), Entity, CoverStarted, "scenario"), receipt.Document));

        Guid? childUnitId = null;
        for (var attempt = 0; attempt < 200 && childUnitId is null; attempt++)
        {
            using var scope = fixture.ScopeFor(setup.Tenant);
            childUnitId = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking()
                .Where(e => e.SourceDocumentId == receipt.Document.Id && e.Direction == EntitlementDirection.Inbound)
                .Select(e => (Guid?)e.UnitId).SingleOrDefaultAsync();
            if (childUnitId is null) await Task.Delay(100);
        }
        Assert.NotNull(childUnitId);
        await InvokeAsync<RecordAssemblyResult>(setup.Tenant, new RecordAssembly(Guid.NewGuid(), setup.UnitId,
            [new ChildInput(childUnitId)], CoverStarted.AddDays(1), "scenario", Entity));

        var monthNine = await SubmitAsync(setup, Guid.NewGuid(), Guid.NewGuid(),
            [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m)],
            new DateOnly(2025, 9, 1), new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        var inTerm = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), monthNine.ClaimId, new DateTime(2025, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager"));
        Assert.Single(inTerm.RecoveryClaimIds);
        var raised = Assert.Single(await PublishedEventSink.WaitForAsync<RecoveryClaimRaised>(
            e => e.CustomerClaimId == monthNine.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(childUnitId, raised.ChildUnitId);

        var monthFifteen = await SubmitAsync(setup, Guid.NewGuid(), Guid.NewGuid(),
            [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m)],
            new DateOnly(2026, 3, 1), new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc));
        var expired = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), monthFifteen.ClaimId, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager"));
        Assert.Empty(expired.RecoveryClaimIds);
        Assert.Single(await PublishedEventSink.WaitForAsync<ClaimAdjudicated>(
            e => e.ClaimId == monthFifteen.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.DoesNotContain(PublishedEventSink.Received.OfType<RecoveryClaimRaised>(),
            e => e.CustomerClaimId == monthFifteen.ClaimId);
    }

    [DatabaseFact]
    public async Task Case_73_an_unknown_unit_is_reconstructed_for_review_without_invented_history()
    {
        var tenant = await fixture.NewTenantAsync("ec73", "EUR");
        var item = Guid.NewGuid();
        var serial = "EC73-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("EC73-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with { ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc, ReplacementPolicyCode = "EC73-90" };
        var code = "E73-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, CoverageFamilyCode.Base,
            EntitlementType.Manufacturer, new DateOnly(2024, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        fixture.Services.GetRequiredService<InMemoryWarrantyPorts>().Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, code, true));

        // Proof of purchase is the only evidence: the unit is reconstructed from it, never from an invented shipment.
        var submitted = await InvokeAsync<SubmitClaimResult>(tenant, new SubmitClaim(Guid.NewGuid(), null,
            new DocumentSerial(serial, IdentifierType: IdentifierType.InternalSerial), DateOnly.FromDateTime(At), Guid.NewGuid(), Guid.NewGuid(),
            Entity, Entity, "DE", "EUR", [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m)],
            [new ClaimEvidenceInput(ClaimEvidenceKind.ProofOfPurchase, "proof:legacy-invoice")], ClaimRemedy.Repair,
            "choice:repair", At, "service.desk", ProofOfPurchaseRef: "proof:legacy-invoice",
            ProofOfPurchaseDate: DateOnly.FromDateTime(At).AddMonths(-10), ItemId: item));

        Assert.Equal(ClaimStatus.Submitted, submitted.Status);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = await db.Units.AsNoTracking().SingleAsync(u => u.UnitId == submitted.UnitId);
        var entitlement = await db.Entitlements.AsNoTracking().SingleAsync(e => e.UnitId == submitted.UnitId);
        Assert.Equal(UnitOrigin.LegacyUnknown, unit.Origin);
        Assert.Equal(Confidence.Low, entitlement.Confidence);
        var certificate = Assert.Single(await PublishedEventSink.WaitForAsync<CertificateIssueRequested>(
            e => e.UnitId == submitted.UnitId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(CertificateProvenance.Reconstructed, certificate.Provenance);
        Assert.Equal(submitted.ClaimId, certificate.TriggeringClaimId);
    }

    [DatabaseFact]
    public async Task Case_78_parts_are_covered_labour_is_not_and_a_chargeable_quote_is_requested()
    {
        var setup = await CoveredUnitAsync("ec78", coverage: new CoverageTerms(100m, 0m, 0m, "return_to_base"));
        var submitted = await SubmitAsync(setup, Guid.NewGuid(), Guid.NewGuid(),
        [
            new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 150m),
            new ClaimLineInput(ClaimLineKind.Labour, CoverageFamilyCode.Base, 80m),
        ]);

        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, At.AddHours(1), "service.manager"));

        Assert.Equal(ClaimStatus.PartiallyApproved, adjudicated.Status);
        Assert.Equal((100m, 150m), (adjudicated.Lines[0].CoveragePct, adjudicated.Lines[0].ApprovedAmount));
        Assert.Equal((0m, 0m), (adjudicated.Lines[1].CoveragePct, adjudicated.Lines[1].ApprovedAmount));
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<ClaimAdjudicated>(
            e => e.ClaimId == submitted.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal([150m, 0m], published.ApprovedAmountsByLine.Select(line => line.ApprovedAmount));
        var quote = Assert.Single(await PublishedEventSink.WaitForAsync<ChargeableQuoteRequested>(
            e => e.ClaimId == submitted.ClaimId, 1, TimeSpan.FromSeconds(20)));
        var labour = Assert.Single(quote.Lines);
        Assert.Equal((ClaimLineKind.Labour, 80m), (labour.Kind, labour.UncoveredAmount));
    }

    [DatabaseFact]
    public async Task Case_108_a_recall_notice_uses_the_owner_and_custodian_after_resale()
    {
        var tenant = await fixture.NewTenantAsync("ec108", "EUR");
        var serial = "10800001";
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(),
            UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            At.AddDays(-10), "scenario", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        var unitId = Assert.Single(commissioned.Lines).UnitId!.Value;
        var invoiceCustomer = Guid.NewGuid();
        var currentOwner = Guid.NewGuid();
        var currentCustodian = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unitId, invoiceCustomer,
            PartyType.Distributor, EpisodeReason.Sale, At.AddDays(-9), "sales", Entity,
            Document: new DocumentRef("invoice", Guid.NewGuid())));
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unitId, currentOwner,
            PartyType.EndUser, EpisodeReason.Sale, At.AddDays(-8), "registration", Entity,
            Source: EpisodeSource.Registration));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unitId,
            LocationKind.CustomerSite, EpisodeReason.Sale, At.AddDays(-7), "carrier", Entity,
            LocationId: Guid.NewGuid(), PartyRef: currentCustodian, PartyType: PartyType.Customer));
        var campaignId = Guid.NewGuid();
        await InvokeAsync<CampaignResult>(tenant, new CreateCampaign(campaignId, "EC108-" + Guid.NewGuid().ToString("N")[..6],
            "Recall notice", CampaignCoverageSource.Recall, "recall:ec108",
            [new CampaignCriterionInput(CampaignCriterionKind.IdentifierRange, Scheme: "internal", RangeStart: serial, RangeEnd: serial)],
            At.AddDays(-2), "service.manager"));
        var selectionId = Guid.NewGuid();

        await InvokeAsync<SelectCampaignUnitsResult>(tenant,
            new SelectCampaignUnits(selectionId, campaignId, At.AddDays(-1), Entity, "service.manager"));

        var published = Assert.Single(await PublishedEventSink.WaitForAsync<CampaignUnitsSelected>(
            e => e.CampaignId == campaignId && e.SelectionCommandId == selectionId, 1, TimeSpan.FromSeconds(20)));
        var recipient = Assert.Single(published.Units);
        Assert.Equal((currentOwner, currentCustodian), (recipient.OwnerPartyRef, recipient.CustodianPartyRef));
        Assert.NotEqual(invoiceCustomer, recipient.OwnerPartyRef);
    }

    private sealed record CoveredUnit(TenantId Tenant, Guid UnitId, Guid Obligor, CreateEntitlementResult Entitlement);
}
