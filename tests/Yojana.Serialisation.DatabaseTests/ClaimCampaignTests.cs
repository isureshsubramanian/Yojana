using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Campaigns;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P4-T14: a claim names campaign cover through the claims-to-campaigns port.</summary>
[Collection("serialisation host")]
public sealed class ClaimCampaignTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly ClaimDate = new(2026, 9, 1);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Options_list_only_active_selected_campaigns_and_keep_ended_cover_explainable()
    {
        var setup = await SetUpAsync("campaign-options", 2);
        var live = await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Active, null, selected: true, "LIVE");
        var ended = await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Active, ClaimDate.AddDays(-1), selected: true, "ENDED");
        await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Draft, null, selected: true, "DRAFT");
        await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Selected, null, selected: true, "SELECTED");
        await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Closed, null, selected: true, "CLOSED");
        await AddCampaignAsync(setup, setup.Units[1], CampaignStatus.Active, null, selected: true, "OTHER-UNIT");

        using var scope = fixture.ScopeFor(setup.Tenant);
        var options = await scope.ServiceProvider.GetRequiredService<ICampaignCoverPort>()
            .ListForUnitAsync(setup.Units[0], ClaimDate, CancellationToken.None);

        Assert.Equal(new[] { live, ended }.Order(), options.Select(option => option.CampaignId).Order());
        Assert.True(options.Single(option => option.CampaignId == live).CoversOn(ClaimDate));
        var endedOption = options.Single(option => option.CampaignId == ended);
        Assert.False(endedOption.CoversOn(ClaimDate));
        Assert.Equal(ClaimCampaignCoverReason.Ended, endedOption.NotCoveredReason(ClaimDate));
    }

    [DatabaseFact]
    public async Task Submission_refuses_unknown_inactive_and_unselected_campaigns()
    {
        var setup = await SetUpAsync("campaign-refusals", 2);
        var inactive = await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Selected, null, selected: true, "INACTIVE");
        var unselected = await AddCampaignAsync(setup, setup.Units[1], CampaignStatus.Active, null, selected: true, "UNSELECTED");

        var unknownRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() => SubmitAsync(setup, setup.Units[0], Guid.NewGuid()));
        var inactiveRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() => SubmitAsync(setup, setup.Units[0], inactive));
        var unselectedRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() => SubmitAsync(setup, setup.Units[0], unselected));

        Assert.Equal("campaign_not_found", unknownRefusal.Rule);
        Assert.Equal("campaign_not_active", inactiveRefusal.Rule);
        Assert.Equal("unit_not_in_campaign", unselectedRefusal.Rule);
    }

    [DatabaseFact]
    public async Task Live_recall_is_full_uses_campaign_reserve_ignores_recalled_and_raises_no_recovery()
    {
        var setup = await SetUpAsync("campaign-live", 2);
        var campaignId = await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Active, null, selected: true, "RECALL");
        await AddInboundChildCoverAsync(setup, setup.Units[0], setup.Units[1]);
        await SetOverlayAsync(setup, setup.Units[0], OverlayFlag.Recalled);
        var submitted = await SubmitAsync(setup, setup.Units[0], campaignId);

        ClaimAdjudicationPreview preview;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            preview = (await scope.ServiceProvider.GetRequiredService<ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(submitted.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
        }

        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, At.AddMonths(1).AddHours(1), "service.manager"));

        Assert.Equal((ClaimStatus.Approved, ClaimCoverageSource.Recall, 100m, 100m),
            (adjudicated.Status, adjudicated.CoverageSource, Assert.Single(adjudicated.Lines).CoveragePct, Assert.Single(adjudicated.Lines).ApprovedAmount));
        Assert.Equal((preview.Outcome, preview.CoverageSource, preview.ApprovedAmount),
            (adjudicated.Status, adjudicated.CoverageSource, adjudicated.Lines.Sum(line => line.ApprovedAmount)));
        Assert.Empty(adjudicated.RecoveryClaimIds);

        var published = Assert.Single(await PublishedEventSink.WaitForAsync<ClaimAdjudicated>(
            message => message.ClaimId == submitted.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal("1.3", published.Header.Version);
        Assert.Equal("EUR", published.Currency);
        Assert.Equal((campaignId, "RECALL", "reserve:RECALL", (string?)null, (Guid?)null),
            (published.CampaignId, published.CampaignCode, published.ReserveTag, published.CampaignCoverReason, published.CohortId));

        using var read = fixture.ScopeFor(setup.Tenant);
        var saved = await read.ServiceProvider.GetRequiredService<SerialisationDbContext>().Claims.SingleAsync(claim => claim.ClaimId == submitted.ClaimId);
        Assert.Equal((campaignId, "reserve:RECALL", (string?)null, RecoveryNotRaisedReason.CampaignClaim),
            (saved.CampaignId, saved.ReserveTag, saved.CampaignCoverReason, saved.RecoveryNotRaisedReason));

        await SetOverlayAsync(setup, setup.Units[0], OverlayFlag.Lost);
        var lost = await SubmitAsync(setup, setup.Units[0], campaignId);
        var denied = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), lost.ClaimId, At.AddMonths(1).AddHours(2), "service.manager"));
        Assert.Equal("overlay_lost", Assert.Single(denied.Lines).DenialReason);
    }

    [DatabaseFact]
    public async Task Campaign_percentages_drive_preview_and_later_adjudication()
    {
        var setup = await SetUpAsync("campaign-percentages", 1);
        var campaignId = await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Active, null, selected: true, "PERCENTAGES");
        var updated = await InvokeAsync<CampaignResult>(setup.Tenant, new UpdateCampaign(Guid.NewGuid(), campaignId,
            "PERCENTAGES", "Campaign PERCENTAGES", CampaignCoverageSource.Recall, "reserve:PERCENTAGES", CampaignStatus.Active,
            [], At.AddDays(1), "service.manager", CampaignReason.Safety, null, null, PartsPct: 100m, LabourPct: 50m, TravelPct: 0m));
        Assert.Equal((100m, 50m, 0m), (updated.Campaign.PartsPct, updated.Campaign.LabourPct, updated.Campaign.TravelPct));

        var submitted = await SubmitAsync(setup, setup.Units[0], campaignId,
        [
            new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m),
            new ClaimLineInput(ClaimLineKind.Labour, CoverageFamilyCode.Base, 100m),
            new ClaimLineInput(ClaimLineKind.Travel, CoverageFamilyCode.Base, 100m),
        ]);
        ClaimAdjudicationPreview preview;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            preview = (await scope.ServiceProvider.GetRequiredService<ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(submitted.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
            var detail = await scope.ServiceProvider.GetRequiredService<CampaignQueries>()
                .DetailAsync(campaignId, CancellationToken.None);
            Assert.Equal((100m, 50m, 0m), (detail!.PartsPct, detail.LabourPct, detail.TravelPct));
        }

        Assert.Equal(150m, preview.ApprovedAmount);
        Assert.Equal(new[] { 100m, 50m, 0m }, preview.Lines.Select(line => line.CoveragePct));
        Assert.Equal(new string?[] { null, "partially_uncovered", "coverage_percentage_zero" }, preview.Lines.Select(line => line.DenialReason));

        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, At.AddMonths(1).AddHours(1), "service.manager"));
        Assert.Equal((preview.Outcome, preview.CoverageSource, preview.ApprovedAmount),
            (adjudicated.Status, adjudicated.CoverageSource, adjudicated.Lines.Sum(line => line.ApprovedAmount)));
        Assert.Equal(preview.Lines.Select(line => (line.CoveragePct, line.ApprovedAmount, line.DenialReason)),
            adjudicated.Lines.Select(line => (line.CoveragePct, line.ApprovedAmount, line.DenialReason)));
    }

    [DatabaseFact]
    public async Task Campaign_cover_percentages_outside_range_are_refused()
    {
        var setup = await SetUpAsync("campaign-percentage-refusal", 1);
        var create = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CampaignResult>(setup.Tenant,
            new CreateCampaign(Guid.NewGuid(), "BAD-PCT", "Bad percentage", CampaignCoverageSource.Recall, "reserve:BAD-PCT", [],
                At, "service.manager", CampaignReason.Safety, null, null, PartsPct: 100.01m)));
        var campaignId = await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Active, null, selected: true, "UPDATE-PCT");
        var update = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CampaignResult>(setup.Tenant,
            new UpdateCampaign(Guid.NewGuid(), campaignId, "UPDATE-PCT", "Campaign UPDATE-PCT", CampaignCoverageSource.Recall,
                "reserve:UPDATE-PCT", CampaignStatus.Active, [], At.AddDays(1), "service.manager", CampaignReason.Safety, null, null,
                LabourPct: -0.01m)));

        Assert.Equal("campaign_cover_pct_out_of_range", create.Rule);
        Assert.Equal("campaign_cover_pct_out_of_range", update.Rule);
    }

    [DatabaseFact]
    public async Task Campaign_claim_recovers_like_warranty_only_when_the_tenant_enables_it()
    {
        var setup = await SetUpAsync("campaign-recovery", 2);
        var campaignId = await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Active, null, selected: true, "RECOVER");
        await AddInboundChildCoverAsync(setup, setup.Units[0], setup.Units[1]);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            await scope.ServiceProvider.GetRequiredService<RecoverySettings>().SetAsync(
                new RecoveryPolicy(RecoveryWholeUnitRule.SingleCoveredPart, [ClaimLineKind.Parts], 7, RecoveryCampaignClaims.RecoverLikeWarranty),
                CancellationToken.None);
        }

        var submitted = await SubmitAsync(setup, setup.Units[0], campaignId);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var preview = (await scope.ServiceProvider.GetRequiredService<ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(submitted.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
            Assert.Equal(setup.Units[1], Assert.Single(preview.Recoveries).ChildUnitId);
            Assert.Null(preview.RecoveryNotRaisedReason);
        }

        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, At.AddMonths(1).AddHours(1), "service.manager"));
        var recoveryId = Assert.Single(adjudicated.RecoveryClaimIds);
        using var read = fixture.ScopeFor(setup.Tenant);
        var db = read.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(setup.Units[1], (await db.RecoveryClaims.SingleAsync(row => row.RecoveryClaimId == recoveryId)).ChildUnitId);
        Assert.Null((await db.Claims.SingleAsync(row => row.ClaimId == submitted.ClaimId)).RecoveryNotRaisedReason);
    }

    [DatabaseFact]
    public async Task Ended_campaign_falls_back_to_warranty_and_preview_list_and_adjudication_agree()
    {
        var setup = await SetUpAsync("campaign-ended", 1);
        var campaignId = await AddCampaignAsync(setup, setup.Units[0], CampaignStatus.Active, ClaimDate.AddDays(-1), selected: true, "ENDED");
        var submitted = await SubmitAsync(setup, setup.Units[0], campaignId);

        ClaimAdjudicationPreview preview;
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            preview = (await scope.ServiceProvider.GetRequiredService<ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(submitted.ClaimId, YojanaRole.ServiceManager, CancellationToken.None))!;
        }
        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), submitted.ClaimId, At.AddMonths(1).AddHours(1), "service.manager"));

        Assert.Equal((ClaimCoverageSource.Warranty, ClaimCampaignCoverReason.Ended), (preview.CoverageSource, preview.CampaignCoverReason));
        Assert.Equal((preview.Outcome, preview.CoverageSource, preview.ApprovedAmount),
            (adjudicated.Status, adjudicated.CoverageSource, adjudicated.Lines.Sum(line => line.ApprovedAmount)));

        using var read = fixture.ScopeFor(setup.Tenant);
        var queries = read.ServiceProvider.GetRequiredService<ClaimQueries>();
        var page = await queries.ListAsync(new ClaimListFilter("all", CampaignId: campaignId), ClaimDate, 0, 50, CancellationToken.None);
        var row = Assert.Single(page.Items);
        var detail = (await queries.DetailAsync(submitted.ClaimId, CancellationToken.None))!;
        // A claim that fell back is a warranty claim: no campaign reserve, and the campaign recovery rule does not apply.
        Assert.Equal((campaignId, "ENDED", (string?)null, ClaimCampaignCoverReason.Ended),
            (row.CampaignId, row.CampaignCode, row.ReserveTag, row.CampaignCoverReason));
        Assert.Null(preview.ReserveTag);
        Assert.NotEqual(RecoveryNotRaisedReason.CampaignClaim, preview.RecoveryNotRaisedReason);
        Assert.Equal((row.CampaignId, row.CampaignCode, row.ReserveTag, row.CampaignCoverReason),
            (detail.CampaignId, detail.CampaignCode, detail.ReserveTag, detail.CampaignCoverReason));
    }

    private async Task<ClaimSetup> SetUpAsync(string prefix, int count)
    {
        var tenant = await fixture.NewTenantAsync(prefix, "EUR");
        var obligor = Guid.NewGuid();
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        var terms = PolicyValidatorTestsTerms.Standard24() with { ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc };
        var code = "CC-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, CoverageFamilyCode.Base,
            EntitlementType.Manufacturer, new DateOnly(2024, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        var marker = prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            Enumerable.Range(1, count).Select(index => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{marker}-{index}")])).ToList(),
            At.AddDays(-1), "test", obligor));
        var units = commissioned.Lines.Select(line => line.UnitId!.Value).ToList();
        foreach (var unit in units)
        {
            await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, CoverageFamilyCode.Base,
                new DateOnly(2026, 1, 1), obligor, At, "test", new Dictionary<string, DateTime> { [StartTrigger.Deliver] = At },
                PolicyVersionId: draft.PolicyVersionId, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, Country: "DE"));
        }
        return new(tenant, units, obligor, draft.PolicyVersionId);
    }

    private async Task<Guid> AddCampaignAsync(ClaimSetup setup, Guid unitId, string status, DateOnly? runsUntil, bool selected, string code)
    {
        var id = Guid.NewGuid();
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.Campaigns.Add(new Campaign
        {
            TenantId = setup.Tenant, CampaignId = id, Code = code, Name = "Campaign " + code,
            CoverageSource = CampaignCoverageSource.Recall, ReserveTag = "reserve:" + code, ReasonCode = CampaignReason.Safety,
            RunsUntil = runsUntil, Status = status, CreatedAt = At, CreatedBy = "test", UpdatedAt = At, UpdatedBy = "test",
        });
        if (selected)
        {
            db.CampaignUnits.Add(new CampaignUnit
            {
                TenantId = setup.Tenant, CampaignId = id, UnitId = unitId, SelectedAt = At,
                SelectedBy = "test", SelectionCommandId = Guid.NewGuid(),
            });
        }
        await db.SaveChangesAsync();
        return id;
    }

    private Task<SubmitClaimResult> SubmitAsync(ClaimSetup setup, Guid unitId, Guid campaignId,
        IReadOnlyList<ClaimLineInput>? lines = null) =>
        InvokeAsync<SubmitClaimResult>(setup.Tenant, new SubmitClaim(Guid.NewGuid(), unitId, null, ClaimDate,
            Guid.NewGuid(), Guid.NewGuid(), setup.Obligor, setup.Obligor, "DE", "EUR",
            lines ?? [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m)],
            [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "DEFECT-PHOTO")], ClaimRemedy.Repair,
            "CHOICE-RECORDED", At.AddMonths(1), "service.desk", CampaignId: campaignId));

    private async Task AddInboundChildCoverAsync(ClaimSetup setup, Guid root, Guid child)
    {
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.AssemblyLinks.Add(new AssemblyLink
        {
            TenantId = setup.Tenant, LinkId = Guid.NewGuid(), ParentUnitId = root, ChildUnitId = child,
            Relation = AssemblyRelation.Component, InstalledAt = At, InstallingEventId = Guid.NewGuid(),
        });
        db.Entitlements.Add(new Entitlement
        {
            TenantId = setup.Tenant, EntitlementId = Guid.NewGuid(), UnitId = child, Scope = EntitlementScope.Unit,
            Type = EntitlementType.Manufacturer, CoverageFamily = CoverageFamilyCode.Supplier, Direction = EntitlementDirection.Inbound,
            ObligorEntityId = Guid.NewGuid(), PayerPartyRef = Guid.NewGuid(), PayerPartyType = PartyType.Vendor, PolicyVersionId = setup.PolicyVersionId,
            StartTrigger = StartTrigger.Receipt, StartDate = new DateOnly(2026, 1, 1), StartTimeZone = "UTC", EndDate = new DateOnly(2026, 12, 31),
            DurationValue = 12, DurationUnit = DurationRule.Months, EndOfMonthRule = DurationRule.Clamp, DurationBasis = DurationBasis.Time,
            Coverage = "{}", RegionScope = "[]", Transferable = Transferability.No, AssuranceOrService = AssuranceOrService.Assurance,
            Status = EntitlementStatus.Active, Confidence = Confidence.High, Provenance = EntitlementProvenance.Created,
            CreatedEventId = Guid.NewGuid(), CreatedAt = At,
        });
        await db.SaveChangesAsync();
    }

    private async Task SetOverlayAsync(ClaimSetup setup, Guid unitId, string flag)
    {
        using var scope = fixture.ScopeFor(setup.Tenant);
        var current = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Current.SingleAsync(row => row.UnitId == unitId);
        current.OverlayFlags = $"[\"{flag}\"]";
        await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().SaveChangesAsync();
    }

    private sealed record ClaimSetup(TenantId Tenant, IReadOnlyList<Guid> Units, Guid Obligor, Guid PolicyVersionId);
}
