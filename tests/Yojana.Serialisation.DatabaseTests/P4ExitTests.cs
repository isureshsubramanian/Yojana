using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P4 exit audit: recorded reference step 2 and gaps not already proved by the task-level suites.</summary>
[Collection("serialisation host")]
public sealed class P4ExitTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("44444444-0000-0000-0000-00000000d001");
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Reference_scenario_step_2_replays_from_recorded_messages_and_publishes_the_February_cohort()
    {
        var tenant = await fixture.NewTenantAsync("p4-reference", "EUR");
        await InvokeAsync<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant,
            new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            Duration = new DurationRule(12, DurationRule.Months),
            Start = new StartRule(StartTrigger.Deliver, [], 90),
            ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
            Refurbished = [],
        };
        var policy = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "REF-12",
            "Reference 12-month warranty", CoverageFamilyCode.Base, EntitlementType.Manufacturer,
            new DateOnly(2026, 1, 1), terms, "scenario"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(policy.PolicyVersionId, "scenario"));

        var prefix = "P4" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var foundation = RecordedScenario.Load("reference-scenario-steps-0-1.json", prefix);
        var foundationResults = await foundation.ReplayAsync(fixture, tenant, CancellationToken.None);
        Assert.All(foundationResults.OfType<ConsumedEventResult>(), result => Assert.Empty(result.Exceptions));

        var step = RecordedScenario.Load("reference-scenario-step-2.json", prefix);
        var results = await step.ReplayAsync(fixture, tenant, CancellationToken.None);
        var entitlement = Assert.Single(results.OfType<CreateEntitlementResult>());
        var submitted = Assert.Single(results.OfType<SubmitClaimResult>());
        var adjudicated = Assert.Single(results.OfType<AdjudicateClaimResult>());
        Assert.Equal((ClaimStatus.Approved, ClaimCoverageSource.Warranty),
            (adjudicated.Status, adjudicated.CoverageSource));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var cohortPeriod = await db.AccrualCohorts.AsNoTracking()
                .Where(c => c.CohortId == entitlement.AccrualCohortId).Select(c => c.Period).SingleAsync();
            Assert.Equal(step.Expect.GetProperty("cohortPeriod").GetString(), cohortPeriod);
            var claim = await db.Claims.AsNoTracking().SingleAsync(c => c.ClaimId == submitted.ClaimId);
            Assert.Equal(ClaimRemedy.Replacement, claim.Remedy);
            Assert.True(await db.ClaimEvidence.AsNoTracking().AnyAsync(e => e.ClaimId == claim.ClaimId
                && e.Kind == ClaimEvidenceKind.ConsumerRemedyChoice && e.Description == ClaimRemedy.Replacement));
        }

        var published = Assert.Single(await PublishedEventSink.WaitForAsync<ClaimAdjudicated>(
            e => e.ClaimId == submitted.ClaimId, 1, TimeSpan.FromSeconds(20)));
        var line = Assert.Single(published.ApprovedAmountsByLine);
        Assert.Equal((ClaimAdjudicated.Schema, ClaimAdjudicated.Version, adjudicated.EventId, Entity, "service.manager"),
            (published.Header.Schema, published.Header.Version, published.Header.EventId,
                published.Header.LegalEntityId, published.Header.ActorRef));
        Assert.Equal((entitlement.AccrualCohortId, ClaimCoverageSource.Warranty, Entity, Entity),
            (published.CohortId, published.CoverageSource, published.ServiceEntityId, published.ObligorEntityId));
        Assert.Equal((100m, 100m, 100m, entitlement.EntitlementId),
            (line.RequestedAmount, line.ApprovedAmount, line.CoveragePct, line.EntitlementId));
    }

    [DatabaseFact]
    public async Task Approved_claim_settles_then_closes_and_commands_refuse_illegal_transitions()
    {
        var setup = await CoveredUnitAsync("p4-state");
        var submitted = await SubmitAsync(setup, "DE", ClaimRemedy.Repair);

        var closeBeforeSettlement = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<ClaimChangeResult>(setup.Tenant,
                new CloseClaim(Guid.NewGuid(), submitted.ClaimId, At.AddHours(1), "service.manager")));
        Assert.Equal("claim_transition_not_allowed", closeBeforeSettlement.Rule);

        var approved = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, At.AddHours(2), "service.manager"));
        Assert.Equal(ClaimStatus.Approved, approved.Status);
        var settled = await InvokeAsync<ClaimChangeResult>(setup.Tenant,
            new SettleClaim(Guid.NewGuid(), submitted.ClaimId, At.AddHours(3), "finance"));
        var closed = await InvokeAsync<ClaimChangeResult>(setup.Tenant,
            new CloseClaim(Guid.NewGuid(), submitted.ClaimId, At.AddHours(4), "service.manager"));
        Assert.Equal((ClaimStatus.Settled, ClaimStatus.Closed), (settled.Status, closed.Status));

        var settleAfterClose = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<ClaimChangeResult>(setup.Tenant,
                new SettleClaim(Guid.NewGuid(), submitted.ClaimId, At.AddHours(5), "finance")));
        Assert.Equal("claim_transition_not_allowed", settleAfterClose.Rule);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var saved = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims.AsNoTracking()
            .SingleAsync(c => c.ClaimId == submitted.ClaimId);
        Assert.Equal((ClaimStatus.Closed, At.AddHours(3), At.AddHours(4)),
            (saved.Status, saved.SettledAt, saved.ClosedAt));
    }

    [DatabaseFact]
    public async Task Out_of_region_claim_is_routed_to_a_chargeable_quote_with_the_uncovered_line_contract()
    {
        var setup = await CoveredUnitAsync("p4-region");
        var claimant = Guid.NewGuid();
        var settlement = Guid.NewGuid();
        var submitted = await SubmitAsync(setup, "US", ClaimRemedy.Repair, claimant, settlement);

        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, At.AddHours(1), "service.manager"));

        var decision = Assert.Single(adjudicated.Lines);
        Assert.Equal((ClaimStatus.Denied, ClaimCoverageSource.Chargeable, "service_country_out_of_region", 0m),
            (adjudicated.Status, adjudicated.CoverageSource, decision.DenialReason, decision.ApprovedAmount));
        var quote = Assert.Single(await PublishedEventSink.WaitForAsync<ChargeableQuoteRequested>(
            e => e.ClaimId == submitted.ClaimId, 1, TimeSpan.FromSeconds(20)));
        var line = Assert.Single(quote.Lines);
        Assert.Equal((ChargeableQuoteRequested.Schema, ChargeableQuoteRequested.Version, setup.UnitId, Entity, "service.manager"),
            (quote.Header.Schema, quote.Header.Version, quote.Header.UnitId, quote.Header.LegalEntityId, quote.Header.ActorRef));
        Assert.Equal((claimant, settlement, "EUR"),
            (quote.ClaimantPartyRef, quote.SettlementPartyRef, quote.Currency));
        Assert.Equal((submitted.ClaimLineIds[0], ClaimLineKind.Parts, 100m, "service_country_out_of_region"),
            (line.ClaimLineId, line.Kind, line.UncoveredAmount, line.Reason));
    }

    [DatabaseFact]
    public async Task Recovery_events_publish_every_vendor_facing_field_from_raise_through_approval()
    {
        var setup = await CoveredUnitAsync("p4-recovery");
        var child = await CommissionUnitAsync(setup.Tenant, Guid.NewGuid(), "P4R-CHILD-" + Guid.NewGuid().ToString("N")[..6]);
        var vendor = Guid.NewGuid();
        var inboundEntitlement = Guid.NewGuid();
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.AssemblyLinks.Add(new AssemblyLink
            {
                TenantId = setup.Tenant, LinkId = Guid.NewGuid(), ParentUnitId = setup.UnitId, ChildUnitId = child,
                Relation = AssemblyRelation.Component, InstalledAt = new DateTime(2025, 1, 2, 9, 0, 0, DateTimeKind.Utc),
                InstallingEventId = Guid.NewGuid(),
            });
            db.Entitlements.Add(new Entitlement
            {
                TenantId = setup.Tenant, EntitlementId = inboundEntitlement, UnitId = child, Scope = EntitlementScope.Unit,
                Type = EntitlementType.Manufacturer, CoverageFamily = CoverageFamilyCode.Supplier,
                Direction = EntitlementDirection.Inbound, ObligorEntityId = Entity, PayerPartyRef = vendor,
                PayerPartyType = PartyType.Vendor, PolicyVersionId = setup.PolicyVersionId, StartTrigger = StartTrigger.Receipt,
                StartDate = new DateOnly(2025, 1, 1), StartTimeZone = "UTC", EndDate = new DateOnly(2025, 12, 31),
                DurationValue = 12, DurationUnit = DurationRule.Months, EndOfMonthRule = DurationRule.Clamp,
                DurationBasis = DurationBasis.Time, Coverage = "{}", RegionScope = "[]", Transferable = Transferability.No,
                AssuranceOrService = AssuranceOrService.Assurance, Status = EntitlementStatus.Active,
                Confidence = Confidence.High, Provenance = EntitlementProvenance.Created,
                CreatedEventId = Guid.NewGuid(), CreatedAt = new DateTime(2025, 1, 1, 9, 0, 0, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
        }

        var submitted = await SubmitAsync(setup, "DE", ClaimRemedy.Repair, claimDate: new DateOnly(2025, 9, 1),
            eventTime: new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc));
        var adjudicatedAt = new DateTime(2025, 9, 1, 10, 0, 0, DateTimeKind.Utc);
        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, adjudicatedAt, "service.manager"));
        var recoveryId = Assert.Single(adjudicated.RecoveryClaimIds);
        var raised = Assert.Single(await PublishedEventSink.WaitForAsync<RecoveryClaimRaised>(
            e => e.RecoveryClaimId == recoveryId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((RecoveryClaimRaised.Schema, RecoveryClaimRaised.Version, child, adjudicatedAt, Entity, "service.manager"),
            (raised.Header.Schema, raised.Header.Version, raised.Header.UnitId, raised.Header.EventTime,
                raised.Header.LegalEntityId, raised.Header.ActorRef));
        Assert.Equal((submitted.ClaimId, submitted.ClaimLineIds[0], inboundEntitlement, child, vendor, 100m, "EUR"),
            (raised.CustomerClaimId, raised.ClaimLineId, raised.InboundEntitlementId, raised.ChildUnitId,
                raised.VendorPartyRef, raised.RequestedAmount, raised.Currency));

        var approvedAt = adjudicatedAt.AddHours(1);
        await InvokeAsync<RecoveryClaimChangeResult>(setup.Tenant,
            new ApproveRecoveryClaim(Guid.NewGuid(), recoveryId, 80m, approvedAt, "vendor.manager"));
        var approved = Assert.Single(await PublishedEventSink.WaitForAsync<RecoveryClaimApproved>(
            e => e.RecoveryClaimId == recoveryId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((RecoveryClaimApproved.Schema, RecoveryClaimApproved.Version, child, approvedAt, Entity, "vendor.manager"),
            (approved.Header.Schema, approved.Header.Version, approved.Header.UnitId, approved.Header.EventTime,
                approved.Header.LegalEntityId, approved.Header.ActorRef));
        Assert.Equal((vendor, 80m, "EUR"), (approved.VendorPartyRef, approved.Amount, approved.Currency));
    }

    [DatabaseFact]
    public async Task Campaign_selection_event_publishes_the_selection_and_reserve_contract()
    {
        var tenant = await fixture.NewTenantAsync("p4-campaign", "EUR");
        var lot = Guid.NewGuid();
        var serial = "P4C-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(),
            UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)], LotId: lot)],
            At.AddDays(-10), "scenario", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        var unit = Assert.Single(commissioned.Lines).UnitId!.Value;
        var owner = Guid.NewGuid();
        var custodian = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unit, owner, PartyType.EndUser,
            EpisodeReason.Sale, At.AddDays(-8), "registration", Entity, Source: EpisodeSource.Registration));
        await InvokeAsync<ChangeCustodyResult>(tenant, new ChangeCustody(Guid.NewGuid(), unit, LocationKind.CustomerSite,
            EpisodeReason.Sale, At.AddDays(-7), "carrier", Entity, PartyRef: custodian, PartyType: PartyType.Customer));
        var campaignId = Guid.NewGuid();
        const string code = "P4-EXIT";
        const string reserve = "recall:p4-exit";
        await InvokeAsync<CampaignResult>(tenant, new CreateCampaign(campaignId, code, "Exit campaign",
            CampaignCoverageSource.Recall, reserve,
            [new CampaignCriterionInput(CampaignCriterionKind.Lot, LotId: lot)], At.AddDays(-2), "service.manager",
            CampaignReason.Safety, "Inspection required", new DateOnly(2026, 9, 30)));
        var selectionId = Guid.NewGuid();

        var result = await InvokeAsync<SelectCampaignUnitsResult>(tenant,
            new SelectCampaignUnits(selectionId, campaignId, At, Entity, "service.manager"));
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<CampaignUnitsSelected>(
            e => e.SelectionCommandId == selectionId, 1, TimeSpan.FromSeconds(20)));
        var selected = Assert.Single(published.Units);

        Assert.Equal((1, 1, 1, 1), (result.Matched, result.Added, result.Selected, result.EventBatches));
        Assert.Equal((CampaignUnitsSelected.Schema, CampaignUnitsSelected.Version, At, Entity, "service.manager"),
            (published.Header.Schema, published.Header.Version, published.Header.EventTime,
                published.Header.LegalEntityId, published.Header.ActorRef));
        Assert.Equal((campaignId, code, CampaignCoverageSource.Recall, reserve, CampaignReason.Safety,
                "Inspection required", new DateOnly(2026, 9, 30), selectionId),
            (published.CampaignId, published.CampaignCode, published.CoverageSource, published.ReserveTag,
                published.ReasonCode, published.ReasonNote, published.RunsUntil, published.SelectionCommandId));
        Assert.Equal((1, 1, 1, 1),
            (published.BatchNumber, published.BatchCount, published.MatchedCount, published.TotalSelectedCount));
        Assert.Equal((unit, owner, PartyType.EndUser, custodian, PartyType.Customer),
            (selected.UnitId, selected.OwnerPartyRef, selected.OwnerPartyType,
                selected.CustodianPartyRef, selected.CustodianPartyType));
    }

    private async Task<CoveredUnit> CoveredUnitAsync(string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix, "EUR");
        await InvokeAsync<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant,
            new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
            Refurbished = [],
        };
        var code = "P4-" + Guid.NewGuid().ToString("N")[..6];
        var policy = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2024, 1, 1), terms, "scenario"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(policy.PolicyVersionId, "scenario"));
        var item = Guid.NewGuid();
        var unit = await CommissionUnitAsync(tenant, item, prefix.ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N")[..8]);
        var entitlement = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit,
            CoverageFamilyCode.Base, new DateOnly(2025, 1, 1), Entity,
            new DateTime(2025, 1, 1, 9, 0, 0, DateTimeKind.Utc), "scenario",
            new Dictionary<string, DateTime> { [StartTrigger.Deliver] = new(2025, 1, 1, 9, 0, 0, DateTimeKind.Utc) },
            policy.PolicyVersionId, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, Country: "DE"));
        Assert.NotNull(entitlement.AccrualCohortId);
        return new(tenant, unit, policy.PolicyVersionId);
    }

    private async Task<Guid> CommissionUnitAsync(TenantId tenant, Guid item, string serial)
    {
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            new DateTime(2024, 12, 31, 9, 0, 0, DateTimeKind.Utc), "scenario", Entity));
        return Assert.Single(commissioned.Lines).UnitId!.Value;
    }

    private Task<SubmitClaimResult> SubmitAsync(CoveredUnit setup, string country, string remedy,
        Guid? claimant = null, Guid? settlement = null, DateOnly? claimDate = null, DateTime? eventTime = null) =>
        InvokeAsync<SubmitClaimResult>(setup.Tenant, new SubmitClaim(Guid.NewGuid(), setup.UnitId, null,
            claimDate ?? new DateOnly(2026, 9, 1), claimant ?? Guid.NewGuid(), settlement ?? Guid.NewGuid(), Entity, Entity,
            country, "EUR", [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m)],
            [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "evidence:defect")], remedy, "evidence:consumer-choice",
            eventTime ?? At, "service.desk"));

    private sealed record CoveredUnit(TenantId Tenant, Guid UnitId, Guid PolicyVersionId);
}
