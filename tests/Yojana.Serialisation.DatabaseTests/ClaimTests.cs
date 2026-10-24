using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P4-T03/T06/T08: claim adjudication, publication and historical supplier recovery.</summary>
[Collection("serialisation host")]
public sealed class ClaimTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime Start = new(2025, 1, 1, 9, 0, 0, DateTimeKind.Utc);
    private InMemoryItemPorts Items => fixture.Services.GetRequiredService<InMemoryItemPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Reference_claim_is_approved_for_replacement_and_publishes_the_warranty_cohort()
    {
        var setup = await SetUpAsync("c1", 1);
        var customer = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.Units[0], customer,
            PartyType.Customer, EpisodeReason.Sale, Start.AddMinutes(1), "sales", setup.Obligor));
        await InvokeAsync<ChangeCustodyResult>(setup.Tenant, new ChangeCustody(Guid.NewGuid(), setup.Units[0],
            LocationKind.Warehouse, EpisodeReason.Receipt, Start.AddMinutes(2), "warehouse", setup.Obligor,
            LocationId: warehouse));

        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2026, 9, 1), Start.AddMonths(20), ClaimRemedy.Replacement);
        var result = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant, new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, Start.AddMonths(20).AddHours(1), "service.manager"));

        Assert.Equal(ClaimStatus.Approved, result.Status);
        Assert.Equal(ClaimCoverageSource.Warranty, result.CoverageSource);
        Assert.Equal(100m, Assert.Single(result.Lines).ApprovedAmount);
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<ClaimAdjudicated>(e => e.ClaimId == claim.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((ClaimCoverageSource.Warranty, setup.Cohorts[0], setup.Obligor, ClaimRemedy.Replacement),
            (published.CoverageSource, published.CohortId, published.ObligorEntityId, ClaimRemedy.Replacement));

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var choice = await db.ClaimEvidence.SingleAsync(e => e.ClaimId == claim.ClaimId && e.Kind == ClaimEvidenceKind.ConsumerRemedyChoice);
        Assert.Equal(ClaimRemedy.Replacement, choice.Description);
        var owner = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Units[0]
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var holder = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.Units[0]
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((customer, PartyType.Customer), (owner.PartyRef, owner.PartyType));
        Assert.Equal((warehouse, LocationKind.Warehouse, PartyType.Us),
            (holder.LocationId, holder.LocationKind, holder.PartyType));
        Assert.Equal(1, await db.Entitlements.CountAsync(row => row.UnitId == setup.Units[0]
            && row.Status == EntitlementStatus.Active));
        Assert.Equal(1, await db.Identifiers.CountAsync(row => row.UnitId == setup.Units[0] && row.ValidTo == null));
        var claimFacts = await db.Events.AsNoTracking()
            .Where(row => row.UnitId == setup.Units[0] && (row.EventType == ClaimEventType.Submitted
                || row.EventType == ClaimEventType.Adjudicated))
            .OrderBy(row => row.EventTime).ToListAsync();
        Assert.Equal([ClaimEventType.Submitted, ClaimEventType.Adjudicated], claimFacts.Select(row => row.EventType));
        Assert.All(claimFacts, fact => Assert.False(Projections.UnitCurrentStateProjector.ProjectsCurrentState(fact.Attributes)));
    }

    [DatabaseFact]
    public async Task Unknown_serial_reconstructs_once_and_low_confidence_cover_needs_the_named_reviewer()
    {
        var tenant = await fixture.NewTenantAsync("legacy-claim", "EUR");
        var item = Guid.NewGuid();
        var obligor = Guid.NewGuid();
        var claimant = Guid.NewGuid();
        var settlement = Guid.NewGuid();
        var at = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var purchaseDate = new DateOnly(2025, 1, 1);
        var claimDate = new DateOnly(2026, 9, 1);
        var serial = "LEG-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("LEGACY-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with { ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc, ReplacementPolicyCode = "LEGACY-90" };
        var code = "LG-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, CoverageFamilyCode.Base,
            EntitlementType.Manufacturer, new DateOnly(2024, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        fixture.Services.GetRequiredService<InMemoryWarrantyPorts>().Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, code, true));

        SubmitClaim Command(Guid commandId, DateTime eventTime) => new(commandId, null,
            new DocumentSerial(serial, IdentifierType: IdentifierType.InternalSerial), claimDate, claimant, settlement,
            obligor, obligor, "DE", "EUR", [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m)],
            [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "FAULT-PHOTO")], ClaimRemedy.Repair,
            "CHOICE-RECORDED", eventTime, "service-desk", ProofOfPurchaseRef: "POP-2025-1",
            ProofOfPurchaseDate: purchaseDate, ItemId: item);

        var submitted = await InvokeAsync<SubmitClaimResult>(tenant, Command(Guid.NewGuid(), at));
        Assert.Equal(ClaimStatus.Submitted, submitted.Status);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var unit = await db.Units.SingleAsync(u => u.UnitId == submitted.UnitId);
            var entitlement = await db.Entitlements.SingleAsync(e => e.UnitId == submitted.UnitId && e.CoverageFamily == CoverageFamilyCode.Base);
            var cohortPeriod = await db.AccrualCohorts.Where(c => c.CohortId == entitlement.AccrualCohortId).Select(c => c.Period).SingleAsync();
            var facts = await db.Events.Where(e => e.UnitId == submitted.UnitId).Select(e => e.EventType).ToListAsync();

            Assert.Equal((UnitOrigin.LegacyUnknown, HistoryDepth.OpeningOnly), (unit.Origin, unit.HistoryDepth));
            Assert.Equal((Confidence.Low, EntitlementProvenance.Reconstructed, "2025-01", purchaseDate), (entitlement.Confidence, entitlement.Provenance, cohortPeriod, entitlement.PurchaseDate));
            Assert.DoesNotContain(UnitEventType.Shipped, facts);
            Assert.DoesNotContain(UnitEventType.Delivered, facts);
            Assert.DoesNotContain(UnitEventType.Released, facts);
        }

        var certificate = Assert.Single(await PublishedEventSink.WaitForAsync<CertificateIssueRequested>(
            e => e.TriggeringClaimId == submitted.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((submitted.UnitId, CertificateDocumentKind.ReleaseRecord, CertificateProvenance.Reconstructed),
            (certificate.UnitId, certificate.DocumentKind, certificate.Provenance));
        Assert.Equal(["POP-2025-1"], certificate.EvidenceRefs);

        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AdjudicateClaimResult>(tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, at.AddHours(1), "service-manager")));
        Assert.Equal("low_confidence_reviewer_required", refused.Rule);
        Assert.Contains(YojanaRole.ServiceManager, refused.Message);

        var approved = await InvokeAsync<AdjudicateClaimResult>(tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, at.AddHours(2), "service-manager",
                new ClaimReviewer("service-manager", [YojanaRole.ServiceManager])));
        Assert.Equal(ClaimStatus.Approved, approved.Status);

        var second = await InvokeAsync<SubmitClaimResult>(tenant, Command(Guid.NewGuid(), at.AddDays(1)));
        Assert.Equal(submitted.UnitId, second.UnitId);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(1, await db.Units.CountAsync(u => u.UnitId == submitted.UnitId));
            Assert.Equal(1, await db.Entitlements.CountAsync(e => e.UnitId == submitted.UnitId && e.CoverageFamily == CoverageFamilyCode.Base));
            var claim = await db.Claims.SingleAsync(c => c.ClaimId == submitted.ClaimId);
            Assert.Equal(("service-manager", YojanaRole.ServiceManager), (claim.AdjudicatedBy, claim.ReviewerRole));
        }
    }

    [DatabaseFact]
    public async Task Child_supplier_cover_raises_recovery_in_month_nine_but_not_month_fifteen()
    {
        var setup = await SetUpAsync("rc", 4);
        await AddChildrenAndSupplierCoverAsync(setup, setup.Units[0], setup.Units[1], setup.Units[2], setup.Units[3]);

        var month9 = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2025, 9, 1), new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Repair);
        var inTerm = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant, new AdjudicateClaim(Guid.NewGuid(), month9.ClaimId, new DateTime(2025, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager"));
        Assert.Single(inTerm.RecoveryClaimIds);

        var month15 = await SubmitAsync(setup.Tenant, setup.Units[2], new DateOnly(2026, 3, 1), new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Repair);
        var expired = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant, new AdjudicateClaim(Guid.NewGuid(), month15.ClaimId, new DateTime(2026, 3, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager"));
        Assert.Empty(expired.RecoveryClaimIds);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var numbers = await db.Claims.Where(c => c.ClaimId == month9.ClaimId || c.ClaimId == month15.ClaimId).OrderBy(c => c.SubmittedAt).Select(c => c.ClaimNumber).ToListAsync();
            Assert.Equal(["CLM-2025-00001", "CLM-2026-00001"], numbers);
            var recovery = Assert.Single(await scope.ServiceProvider.GetRequiredService<Claims.ClaimQueries>().RecoveryListAsync(null, CancellationToken.None));
            Assert.Equal(("RCV-2025-0001", "CLM-2025-00001"), (recovery.RecoveryNumber, recovery.CustomerClaimNumber));

            var queries = scope.ServiceProvider.GetRequiredService<Claims.RecoveryQueries>();
            var page = await queries.PageAsync(Claims.RecoveryClaimFilter.Open, null, setup.ItemId, "RCV-2025", [], new DateOnly(2026, 9, 1), 0, 50, CancellationToken.None);
            var enriched = Assert.Single(page.Rows);
            Assert.Equal((setup.Units[1], setup.Units[0], setup.ItemId, 12, 9),
                (enriched.ChildUnitId, enriched.ParentUnitId, enriched.ChildItemId, enriched.VendorTermMonths, enriched.ClaimMonthOfTerm));
            Assert.StartsWith("RC", enriched.ChildSerial);
            Assert.StartsWith("RC", enriched.ParentSerial);

            var notRaised = await queries.NotRaisedAsync(30, new DateOnly(2026, 3, 1), CancellationToken.None);
            var expiredTerm = Assert.Single(notRaised.Rows);
            Assert.Equal((month15.ClaimId, RecoveryNotRaisedReason.VendorTermEnded, 12, 15),
                (expiredTerm.CustomerClaimId, expiredTerm.Reason, expiredTerm.VendorTermMonths, expiredTerm.ClaimMonthOfTerm));
            var summary = await queries.SummaryAsync(new DateOnly(2026, 9, 1), CancellationToken.None);
            Assert.Equal(1, summary.OpenCount);
            Assert.Equal(1, summary.TermExpiredWithoutRecovery);
            Assert.Equal(100m, Assert.Single(summary.OpenAmounts).RequestedAmount);
        }

        var recoveryId = Assert.Single(inTerm.RecoveryClaimIds);
        var answered = await InvokeAsync<RecoveryClaimChangeResult>(setup.Tenant, new ApproveRecoveryClaim(Guid.NewGuid(), recoveryId, 80m,
            new DateTime(2026, 9, 1, 11, 0, 0, DateTimeKind.Utc), VendorReference: "CN-4081", VendorNote: "Labour share refused"));
        Assert.Equal((80m, "CN-4081", "Labour share refused"), (answered.ApprovedAmount, answered.VendorReference, answered.VendorNote));
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var saved = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().RecoveryClaims.SingleAsync(row => row.RecoveryClaimId == recoveryId);
            Assert.Equal(("CN-4081", "Labour share refused"), (saved.VendorReference, saved.VendorNote));
            var summary = await scope.ServiceProvider.GetRequiredService<Claims.RecoveryQueries>().SummaryAsync(new DateOnly(2026, 9, 1), CancellationToken.None);
            Assert.Equal(0, summary.OpenCount);
            Assert.Equal(80m, Assert.Single(summary.ApprovedThisYear).ApprovedAmount);
        }

        var raised = Assert.Single(await PublishedEventSink.WaitForAsync<RecoveryClaimRaised>(e => e.CustomerClaimId == month9.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(setup.Units[1], raised.ChildUnitId);
        Assert.DoesNotContain(PublishedEventSink.Received.OfType<RecoveryClaimRaised>(), e => e.CustomerClaimId == month15.ClaimId);
    }

    [DatabaseFact]
    public async Task Claims_are_written_only_in_the_home_currency()
    {
        var setup = await SetUpAsync("cur", 1);
        var gbp = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<SubmitClaimResult>(setup.Tenant, new SubmitClaim(Guid.NewGuid(), setup.Units[0], null,
            new DateOnly(2025, 9, 1), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "GB", "GBP", [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m)],
            [], ClaimRemedy.Repair, "choice", new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc), "desk")));
        Assert.Equal("currency_not_home", gbp.Rule);
        Assert.Equal("Amounts in this company are in EUR.", gbp.Message);
    }

    [DatabaseFact]
    public async Task Preview_shows_what_approve_records_and_the_screens_read_numbers_serials_and_summary()
    {
        var setup = await SetUpAsync("pv", 4);
        await AddChildrenAndSupplierCoverAsync(setup, setup.Units[0], setup.Units[1], setup.Units[2], setup.Units[3]);
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2025, 9, 1), new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Repair);

        ClaimAdjudicationPreview preview;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            preview = (await scope.ServiceProvider.GetRequiredService<Claims.ClaimAdjudicationPreviewQuery>().PreviewAsync(claim.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
            Assert.Equal((ClaimStatus.Approved, ClaimCoverageSource.Warranty, 100m, (string?)null), (preview.Outcome, preview.CoverageSource, preview.ApprovedAmount, preview.Refusal));
            var line = Assert.Single(preview.Lines);
            Assert.Equal(PolicyLevel.UnitEntitlement, line.Level);
            Assert.Equal("matched", line.Stack.Single(s => s.Level == PolicyLevel.UnitEntitlement).Outcome);
            Assert.All(line.Stack.Where(s => s.Level != PolicyLevel.UnitEntitlement && s.Level != "region"), s => Assert.Equal("not_reached", s.Outcome));
            Assert.Equal(setup.Units[1], Assert.Single(preview.Recoveries).ChildUnitId);
            Assert.Equal(9, preview.Recoveries[0].MonthOfTerm);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Empty(await db.Events.Where(e => e.UnitId == setup.Units[0] && e.EventType == ClaimEventType.Adjudicated).ToListAsync());
        }

        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant, new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, new DateTime(2025, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager"));
        Assert.Equal((preview.Outcome, preview.ApprovedAmount, preview.Recoveries.Count), (adjudicated.Status, adjudicated.Lines.Sum(l => l.ApprovedAmount), adjudicated.RecoveryClaimIds.Count));

        var second = await SubmitAsync(setup.Tenant, setup.Units[2], new DateOnly(2025, 9, 2), new DateTime(2025, 9, 2, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Repair);
        await InvokeAsync<ClaimChangeResult>(setup.Tenant, new RequestClaimEvidence(Guid.NewGuid(), second.ClaimId, "Photo of the drive label", new DateTime(2025, 9, 2, 10, 0, 0, DateTimeKind.Utc), "service.desk"));
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var queries = scope.ServiceProvider.GetRequiredService<Claims.ClaimQueries>();
            var page = await queries.ListAsync(new ClaimListFilter("all"), new DateOnly(2025, 9, 12), 0, 50, CancellationToken.None);
            Assert.Equal(["CLM-2025-00001", "CLM-2025-00002"], page.Items.Select(r => r.ClaimNumber));
            Assert.Equal((true, false), (page.Items[0].RecoveryRaised, page.Items[0].EvidenceRequested));
            Assert.Equal((true, 10), (page.Items[1].EvidenceRequested, page.Items[1].AgeDays));
            Assert.StartsWith("PV", page.Items[0].Serial);
            var open = await queries.ListAsync(new ClaimListFilter("open", Recovery: "none"), new DateOnly(2025, 9, 12), 0, 50, CancellationToken.None);
            Assert.Equal(second.ClaimId, Assert.Single(open.Items).ClaimId);
            var byNumber = await queries.ListAsync(new ClaimListFilter("all", Query: "clm-2025-00002"), new DateOnly(2025, 9, 12), 0, 50, CancellationToken.None);
            Assert.Equal(second.ClaimId, Assert.Single(byNumber.Items).ClaimId);

            var summary = await queries.SummaryAsync(new DateOnly(2025, 9, 12), 5, CancellationToken.None);
            Assert.Equal((2, 1, 1, 1), (summary.Open, summary.Submitted, summary.ApprovedNotSettled, summary.ApprovedCountThisMonth));
            Assert.Equal(new MoneyTotal("EUR", 100m), Assert.Single(summary.ApprovedThisMonth));
            Assert.Equal((1, 1), (summary.RecoveryOpenCount, summary.RecoveryVendorCount));
            Assert.Equal("CLM-2025-00001", summary.OldestOpenNumber);
            Assert.Equal([0, 0, 1, 1, 0], summary.AgeBuckets);

            var detail = (await queries.DetailAsync(claim.ClaimId, CancellationToken.None))!;
            Assert.Equal("RCV-2025-0001", Assert.Single(detail.Recoveries!).RecoveryNumber);
        }
    }

    [DatabaseFact]
    public async Task Adjudicator_denies_a_whole_claim_on_a_finding_with_a_note()
    {
        var setup = await SetUpAsync("md", 1);
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2025, 9, 1), new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Repair);
        var noNote = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, new DateTime(2025, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager", DenialReason: ClaimManualDenialReason.UnauthorisedPartsDamage)));
        Assert.Equal("claim_denial_note_required", noNote.Rule);

        var denied = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant, new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, new DateTime(2025, 9, 1, 10, 5, 0, DateTimeKind.Utc), "service.manager",
            DenialReason: ClaimManualDenialReason.UnauthorisedPartsDamage, DenialNote: "Third-party DIMM fitted in slot A2"));
        Assert.Equal((ClaimStatus.Denied, ClaimManualDenialReason.UnauthorisedPartsDamage, 0m), (denied.Status, denied.DenialReason, denied.Lines.Sum(l => l.ApprovedAmount)));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var note = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ClaimEvidence.SingleAsync(e => e.ClaimId == claim.ClaimId && e.Kind == ClaimEvidenceKind.AdjudicationNote);
        Assert.Equal("Third-party DIMM fitted in slot A2", note.Description);
    }

    [DatabaseFact]
    public async Task Not_raised_report_distinguishes_no_vendor_term_and_recall_recovery()
    {
        var setup = await SetUpAsync("not-raised", 3);
        var at = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var noTerm = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2026, 9, 1), at, ClaimRemedy.Repair);
        var recall = await SubmitAsync(setup.Tenant, setup.Units[2], new DateOnly(2026, 9, 1), at.AddMinutes(1), ClaimRemedy.Repair);
        await InvokeAsync<AdjudicateClaimResult>(setup.Tenant, new AdjudicateClaim(Guid.NewGuid(), noTerm.ClaimId, at.AddHours(1), "service.manager"));
        await InvokeAsync<AdjudicateClaimResult>(setup.Tenant, new AdjudicateClaim(Guid.NewGuid(), recall.ClaimId, at.AddHours(1).AddMinutes(1), "service.manager"));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        (await db.Claims.SingleAsync(row => row.ClaimId == recall.ClaimId)).CoverageSource = ClaimCoverageSource.Recall;
        await db.SaveChangesAsync();

        var report = await scope.ServiceProvider.GetRequiredService<Claims.RecoveryQueries>().NotRaisedAsync(30, new DateOnly(2026, 9, 1), CancellationToken.None);
        Assert.Equal(2, report.Rows.Count);
        Assert.Equal(RecoveryNotRaisedReason.NoVendorTerm, report.Rows.Single(row => row.CustomerClaimId == noTerm.ClaimId).Reason);
        Assert.Equal(RecoveryNotRaisedReason.RecallClaim, report.Rows.Single(row => row.CustomerClaimId == recall.ClaimId).Reason);
    }

    [DatabaseFact]
    public async Task Whole_unit_line_with_several_covered_parts_records_and_reports_why_recovery_was_not_raised()
    {
        var setup = await SetUpAsync("recovery-many", 4);
        await AddSupplierCoverAsync(setup, (setup.Units[0], setup.Units[1]), (setup.Units[0], setup.Units[3]));
        var at = new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2025, 9, 1), at, ClaimRemedy.Repair);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var preview = (await scope.ServiceProvider.GetRequiredService<Claims.ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(claim.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
            Assert.Empty(preview.Recoveries);
            Assert.Equal(RecoveryNotRaisedReason.SeveralCoveredParts, preview.RecoveryNotRaisedReason);
        }

        var result = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, at.AddHours(1), "service.manager"));
        Assert.Empty(result.RecoveryClaimIds);

        using var read = fixture.ScopeFor(setup.Tenant);
        var saved = await read.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims.SingleAsync(row => row.ClaimId == claim.ClaimId);
        Assert.Equal(RecoveryNotRaisedReason.SeveralCoveredParts, saved.RecoveryNotRaisedReason);
        var report = await read.ServiceProvider.GetRequiredService<Claims.RecoveryQueries>()
            .NotRaisedAsync(30, new DateOnly(2025, 9, 1), CancellationToken.None);
        Assert.Equal(RecoveryNotRaisedReason.SeveralCoveredParts, Assert.Single(report.Rows).Reason);
    }

    [DatabaseFact]
    public async Task Whole_unit_line_with_one_covered_part_raises_one_recovery_for_that_part()
    {
        var setup = await SetUpAsync("recovery-one", 2);
        await AddSupplierCoverAsync(setup, (setup.Units[0], setup.Units[1]));
        var at = new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2025, 9, 1), at, ClaimRemedy.Repair);

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var preview = (await scope.ServiceProvider.GetRequiredService<Claims.ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(claim.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
            Assert.Equal(setup.Units[1], Assert.Single(preview.Recoveries).ChildUnitId);
            Assert.Null(preview.RecoveryNotRaisedReason);
        }

        var result = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, at.AddHours(1), "service.manager"));
        var recoveryId = Assert.Single(result.RecoveryClaimIds);
        using var read = fixture.ScopeFor(setup.Tenant);
        var recovery = await read.ServiceProvider.GetRequiredService<SerialisationDbContext>().RecoveryClaims.SingleAsync(row => row.RecoveryClaimId == recoveryId);
        Assert.Equal(setup.Units[1], recovery.ChildUnitId);
    }

    [DatabaseFact]
    public async Task Named_part_policy_requires_the_line_to_name_the_part_and_then_recovers_from_it()
    {
        var setup = await SetUpAsync("recovery-named", 3);
        await AddSupplierCoverAsync(setup, (setup.Units[0], setup.Units[2]));
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            await scope.ServiceProvider.GetRequiredService<Claims.RecoverySettings>().SetAsync(
                new RecoveryPolicy(RecoveryWholeUnitRule.NamedPartOnly, [ClaimLineKind.Parts], 7), CancellationToken.None);
        }

        var at = new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var unnamed = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2025, 9, 1), at, ClaimRemedy.Repair);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var preview = (await scope.ServiceProvider.GetRequiredService<Claims.ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(unnamed.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
            Assert.Empty(preview.Recoveries);
            Assert.Equal(RecoveryNotRaisedReason.PartNotNamed, preview.RecoveryNotRaisedReason);
        }
        await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), unnamed.ClaimId, at.AddHours(1), "service.manager"));

        var named = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2025, 9, 1), at.AddMinutes(1), ClaimRemedy.Repair,
            ClaimLineKind.Parts, setup.Units[2]);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var preview = (await scope.ServiceProvider.GetRequiredService<Claims.ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(named.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
            Assert.Equal(setup.Units[2], Assert.Single(preview.Recoveries).ChildUnitId);
        }
        var namedResult = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), named.ClaimId, at.AddHours(1).AddMinutes(1), "service.manager"));
        Assert.Single(namedResult.RecoveryClaimIds);

        using var read = fixture.ScopeFor(setup.Tenant);
        var db = read.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(RecoveryNotRaisedReason.PartNotNamed, (await db.Claims.SingleAsync(row => row.ClaimId == unnamed.ClaimId)).RecoveryNotRaisedReason);
        Assert.Equal(setup.Units[2], (await db.RecoveryClaims.SingleAsync(row => row.CustomerClaimId == named.ClaimId)).ChildUnitId);
    }

    [DatabaseFact]
    public async Task Line_kind_outside_the_policy_records_and_reports_the_kind()
    {
        var setup = await SetUpAsync("recovery-kind", 2);
        await AddSupplierCoverAsync(setup, (setup.Units[0], setup.Units[1]));
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            await scope.ServiceProvider.GetRequiredService<Claims.RecoverySettings>().SetAsync(
                new RecoveryPolicy(RecoveryWholeUnitRule.SingleCoveredPart, [ClaimLineKind.Parts], 7), CancellationToken.None);
        }

        var at = new DateTime(2025, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2025, 9, 1), at, ClaimRemedy.Repair, ClaimLineKind.Labour);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var preview = (await scope.ServiceProvider.GetRequiredService<Claims.ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(claim.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
            Assert.Empty(preview.Recoveries);
            Assert.Equal(RecoveryNotRaisedReason.LineKindNotRecovered, preview.RecoveryNotRaisedReason);
        }
        await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, at.AddHours(1), "service.manager"));

        using var read = fixture.ScopeFor(setup.Tenant);
        var saved = await read.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims.SingleAsync(row => row.ClaimId == claim.ClaimId);
        Assert.Equal(RecoveryNotRaisedReason.LineKindNotRecovered, saved.RecoveryNotRaisedReason);
        var row = Assert.Single((await read.ServiceProvider.GetRequiredService<Claims.RecoveryQueries>()
            .NotRaisedAsync(30, new DateOnly(2025, 9, 1), CancellationToken.None)).Rows);
        Assert.Equal(RecoveryNotRaisedReason.LineKindNotRecovered, row.Reason);
    }

    [DatabaseFact]
    public async Task Recovery_policy_refuses_empty_kinds_out_of_range_days_and_unknown_rules()
    {
        var tenant = await fixture.NewTenantAsync("recovery-policy", "EUR");
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<Claims.RecoverySettings>();

        var noKinds = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new RecoveryPolicy(RecoveryWholeUnitRule.SingleCoveredPart, [], 7), CancellationToken.None));
        var tooSoon = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new RecoveryPolicy(RecoveryWholeUnitRule.SingleCoveredPart, [ClaimLineKind.Parts], 0), CancellationToken.None));
        var tooLate = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new RecoveryPolicy(RecoveryWholeUnitRule.SingleCoveredPart, [ClaimLineKind.Parts], 91), CancellationToken.None));
        var unknown = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new RecoveryPolicy("guess_vendor", [ClaimLineKind.Parts], 7), CancellationToken.None));
        var unknownCampaignRule = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new RecoveryPolicy(RecoveryWholeUnitRule.SingleCoveredPart, [ClaimLineKind.Parts], 7, "recover_everything"), CancellationToken.None));

        Assert.All([noKinds, tooSoon, tooLate, unknown, unknownCampaignRule], refusal => Assert.Equal("recovery_policy_invalid", refusal.Rule));
    }

    [DatabaseFact]
    public async Task Stored_recovery_policy_without_campaign_rule_defaults_to_no_recovery()
    {
        var tenant = await fixture.NewTenantAsync("recovery-policy-old", "EUR");
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ITenantSettings>().SetAsync(Claims.RecoverySettings.Key,
            """{"wholeUnitLines":"single_covered_part","lineKinds":["parts"],"vendorAnswerDueDays":7}""", CancellationToken.None);

        var policy = await scope.ServiceProvider.GetRequiredService<Claims.RecoverySettings>().GetAsync(CancellationToken.None);

        Assert.Equal(RecoveryCampaignClaims.NoRecovery, policy.CampaignClaims);
    }

    [DatabaseFact]
    public async Task Home_currency_changes_before_an_amount_and_locks_after_the_first_claim()
    {
        var setup = await SetUpAsync("currency-lock", 1);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var currency = scope.ServiceProvider.GetRequiredService<ITenantCurrency>();

        Assert.Equal("USD", await currency.SetAsync("USD", CancellationToken.None));
        await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2026, 9, 1),
            new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Repair, currency: "USD");
        var refusal = await Assert.ThrowsAsync<TenantCurrencyLockedException>(() => currency.SetAsync("EUR", CancellationToken.None));

        Assert.Equal("USD", refusal.Current);
        Assert.Contains(refusal.Usage, usage => usage.Kind == "claims" && usage.Count == 1);
        Assert.Equal("USD", await currency.GetAsync(CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Sales_block_alone_does_not_affect_claim_approval()
    {
        var setup = await SetUpAsync("sales-block", 1);
        Items.Register(InMemoryItemPorts.DefaultProfile(setup.ItemId) with { SalesBlocked = true });
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2026, 9, 1),
            new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Repair);

        var result = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager"));

        Assert.Equal(ClaimStatus.Approved, result.Status);
        Assert.Null(Assert.Single(result.Lines).DenialReason);
    }

    [DatabaseFact]
    public async Task Service_block_denies_parts_with_the_named_reason()
    {
        var setup = await SetUpAsync("service-block", 1);
        var successorItemId = Guid.NewGuid();
        Items.Register(InMemoryItemPorts.DefaultProfile(setup.ItemId) with { ServiceBlocked = true, SuccessorItemId = successorItemId });
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2026, 9, 1),
            new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Repair);

        var result = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager"));

        Assert.Equal(ClaimStatus.Denied, result.Status);
        Assert.Equal(ClaimDenialReason.ItemServiceBlocked, Assert.Single(result.Lines).DenialReason);
        Assert.Equal(successorItemId, result.SuccessorItemId);
    }

    [DatabaseFact]
    public async Task Claim_after_last_service_date_is_denied()
    {
        var setup = await SetUpAsync("last-service", 1);
        Items.Register(InMemoryItemPorts.DefaultProfile(setup.ItemId) with { LastServiceDate = new DateOnly(2026, 8, 31) });
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2026, 9, 1),
            new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Repair);

        var result = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager"));

        Assert.Equal(ClaimDenialReason.AfterLastServiceDate, Assert.Single(result.Lines).DenialReason);
    }

    [DatabaseFact]
    public async Task Discontinued_replacement_uses_and_publishes_the_successor_item()
    {
        var setup = await SetUpAsync("successor", 1);
        var successorItemId = Guid.NewGuid();
        Items.Register(InMemoryItemPorts.DefaultProfile(setup.ItemId) with { SalesBlocked = true, SuccessorItemId = successorItemId });
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2026, 9, 1),
            new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Replacement);

        var result = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager"));

        Assert.Equal(ClaimStatus.Approved, result.Status);
        Assert.Equal(successorItemId, result.SuccessorItemId);
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<ClaimAdjudicated>(e => e.ClaimId == claim.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(successorItemId, published.SuccessorItemId);
        using var scope = fixture.ScopeFor(setup.Tenant);
        Assert.Equal(successorItemId, (await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims.SingleAsync(c => c.ClaimId == claim.ClaimId)).SuccessorItemId);
    }

    [DatabaseFact]
    public async Task Discontinued_replacement_without_a_successor_is_refused_with_the_next_step()
    {
        var setup = await SetUpAsync("no-successor", 1);
        Items.Register(InMemoryItemPorts.DefaultProfile(setup.ItemId) with { SalesBlocked = true });
        var claim = await SubmitAsync(setup.Tenant, setup.Units[0], new DateOnly(2026, 9, 1),
            new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), ClaimRemedy.Replacement);

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.manager")));

        Assert.Equal("replacement_successor_required", refusal.Rule);
        Assert.Equal("choose repair or goodwill", refusal.UnblockingStep);
    }

    private async Task<SubmitClaimResult> SubmitAsync(TenantId tenant, Guid unitId, DateOnly claimDate, DateTime at, string remedy,
        string lineKind = ClaimLineKind.Parts, Guid? lineUnitId = null, string currency = "EUR") =>
        await InvokeAsync<SubmitClaimResult>(tenant, new SubmitClaim(Guid.NewGuid(), unitId, null, claimDate, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "DE", currency,
            [new ClaimLineInput(lineKind, CoverageFamilyCode.Base, 100m, lineUnitId)], [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "DEFECT-PHOTO")], remedy, "CHOICE-RECORDED", at, "service.desk", "INVOICE-1"));

    private async Task<ClaimSetup> SetUpAsync(string prefix, int count)
    {
        var tenant = await fixture.NewTenantAsync(prefix, "EUR");
        var obligor = Guid.NewGuid();
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with { ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc };
        var code = "CL-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2024, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        var marker = prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            Enumerable.Range(1, count).Select(i => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{marker}-{i}")])).ToList(), Start.AddDays(-1), "test", obligor));
        var units = commissioned.Lines.Select(l => l.UnitId!.Value).ToList();
        var cohorts = new List<Guid?>();
        foreach (var unit in units.Where((_, i) => i % 2 == 0 || count == 1))
        {
            var created = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, CoverageFamilyCode.Base,
                new DateOnly(2025, 1, 1), obligor, Start, "test", new Dictionary<string, DateTime> { [StartTrigger.Deliver] = Start },
                PolicyVersionId: draft.PolicyVersionId, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, Country: "DE"));
            cohorts.Add(created.AccrualCohortId);
        }

        return new(tenant, item, units, cohorts, obligor, draft.PolicyVersionId);
    }

    private async Task AddChildrenAndSupplierCoverAsync(ClaimSetup setup, Guid firstRoot, Guid firstChild, Guid secondRoot, Guid secondChild)
        => await AddSupplierCoverAsync(setup, (firstRoot, firstChild), (secondRoot, secondChild));

    private async Task AddSupplierCoverAsync(ClaimSetup setup, params (Guid Root, Guid Child)[] pairs)
    {
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        foreach (var (root, child) in pairs)
        {
            db.AssemblyLinks.Add(new AssemblyLink
            {
                TenantId = setup.Tenant, LinkId = TimeOrderedId.New(Start), ParentUnitId = root, ChildUnitId = child,
                Relation = AssemblyRelation.Component, InstalledAt = Start, InstallingEventId = Guid.NewGuid(),
            });
            db.Entitlements.Add(new Entitlement
            {
                TenantId = setup.Tenant, EntitlementId = TimeOrderedId.New(Start), UnitId = child, Scope = EntitlementScope.Unit,
                Type = EntitlementType.Manufacturer, CoverageFamily = CoverageFamilyCode.Supplier, Direction = EntitlementDirection.Inbound,
                ObligorEntityId = Guid.NewGuid(), PayerPartyRef = Guid.NewGuid(), PayerPartyType = PartyType.Vendor, PolicyVersionId = setup.PolicyVersionId,
                StartTrigger = StartTrigger.Receipt, StartDate = new DateOnly(2025, 1, 1), StartTimeZone = "UTC", EndDate = new DateOnly(2025, 12, 31),
                DurationValue = 12, DurationUnit = DurationRule.Months, EndOfMonthRule = DurationRule.Clamp, DurationBasis = DurationBasis.Time,
                Coverage = "{}", RegionScope = "[]", Transferable = Transferability.No, AssuranceOrService = AssuranceOrService.Assurance,
                Status = EntitlementStatus.Active, Confidence = Confidence.High, Provenance = EntitlementProvenance.Created,
                CreatedEventId = Guid.NewGuid(), CreatedAt = Start,
            });
        }

        await db.SaveChangesAsync();
    }

    private sealed record ClaimSetup(TenantId Tenant, Guid ItemId, IReadOnlyList<Guid> Units, IReadOnlyList<Guid?> Cohorts, Guid Obligor, Guid PolicyVersionId);
}
