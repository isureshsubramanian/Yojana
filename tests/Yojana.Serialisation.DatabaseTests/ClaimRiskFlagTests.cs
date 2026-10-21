using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Claims.Risk;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P9-T18 deterministic rule boundaries, second review, reporting and tenant isolation.</summary>
[Collection("serialisation host")]
public sealed class ClaimRiskFlagTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Incident = new(2026, 9, 1);

    [DatabaseFact]
    public async Task Recent_cover_repeat_unit_and_repeat_fault_use_inclusive_window_boundaries()
    {
        var recent = await NewScenarioAsync("risk-recent", 2);
        await EnableAsync(recent.Tenant, policy => policy with { RecentCoverPurchase = new(true, 60) });
        await AddFamilyAsync(recent.Tenant, "accidental_damage", stacking: true);
        var hit = await AddClaimAsync(recent.Tenant, recent.Units[0], Incident, Guid.NewGuid(), "fan_failure");
        var miss = await AddClaimAsync(recent.Tenant, recent.Units[1], Incident, Guid.NewGuid(), "fan_failure");
        await EvaluateAsync(recent.Tenant, hit, PurchaseOutcome(Incident.AddDays(-60), "accidental_damage"));
        await EvaluateAsync(recent.Tenant, miss, PurchaseOutcome(Incident.AddDays(-61), "accidental_damage"));
        Assert.Equal([ClaimRiskRuleCode.RecentCoverPurchase], await RulesAsync(recent.Tenant, hit));
        Assert.Empty(await RulesAsync(recent.Tenant, miss));

        var repeat = await NewScenarioAsync("risk-repeat", 2);
        await EnableAsync(repeat.Tenant, policy => policy with { RepeatUnitClaims = new(true, 2, 90) });
        await AddClaimAsync(repeat.Tenant, repeat.Units[0], Incident.AddDays(-90), Guid.NewGuid());
        await AddClaimAsync(repeat.Tenant, repeat.Units[0], Incident.AddDays(-1), Guid.NewGuid());
        await AddClaimAsync(repeat.Tenant, repeat.Units[1], Incident.AddDays(-90), Guid.NewGuid());
        var repeatHit = await AddClaimAsync(repeat.Tenant, repeat.Units[0], Incident, Guid.NewGuid());
        var repeatMiss = await AddClaimAsync(repeat.Tenant, repeat.Units[1], Incident, Guid.NewGuid());
        await EvaluateAsync(repeat.Tenant, repeatHit);
        await EvaluateAsync(repeat.Tenant, repeatMiss);
        Assert.Equal([ClaimRiskRuleCode.RepeatUnitClaims], await RulesAsync(repeat.Tenant, repeatHit));
        Assert.Empty(await RulesAsync(repeat.Tenant, repeatMiss));

        var fault = await NewScenarioAsync("risk-fault", 2);
        await EnableAsync(fault.Tenant, policy => policy with { RepeatFault = new(true, 180) });
        await AddClaimAsync(fault.Tenant, fault.Units[0], Incident.AddDays(-180), Guid.NewGuid(), "power_supply");
        await AddClaimAsync(fault.Tenant, fault.Units[1], Incident.AddDays(-181), Guid.NewGuid(), "power_supply");
        var faultHit = await AddClaimAsync(fault.Tenant, fault.Units[0], Incident, Guid.NewGuid(), "power_supply");
        var faultMiss = await AddClaimAsync(fault.Tenant, fault.Units[1], Incident, Guid.NewGuid(), "power_supply");
        await EvaluateAsync(fault.Tenant, faultHit);
        await EvaluateAsync(fault.Tenant, faultMiss);
        Assert.Equal([ClaimRiskRuleCode.RepeatFault], await RulesAsync(fault.Tenant, faultHit));
        Assert.Empty(await RulesAsync(fault.Tenant, faultMiss));
    }

    [DatabaseFact]
    public async Task Claimant_rate_is_strictly_above_the_multiplier_and_uses_customer_owned_installed_base()
    {
        var hit = await NewScenarioAsync("risk-rate-hit", 5);
        var hitParty = Guid.NewGuid();
        await OwnAsync(hit.Tenant, hit.Units.Take(2), hitParty);
        await OwnAsync(hit.Tenant, hit.Units.Skip(2), Guid.NewGuid());
        await EnableAsync(hit.Tenant, policy => policy with { ClaimantRate = new(true, 365, 2m, 2, 2) });
        await AddClaimAsync(hit.Tenant, hit.Units[0], Incident.AddDays(-1), hitParty);
        var hitClaim = await AddClaimAsync(hit.Tenant, hit.Units[1], Incident, hitParty);
        await EvaluateAsync(hit.Tenant, hitClaim);
        Assert.Equal([ClaimRiskRuleCode.ClaimantRate], await RulesAsync(hit.Tenant, hitClaim));

        var boundary = await NewScenarioAsync("risk-rate-boundary", 4);
        var boundaryParty = Guid.NewGuid();
        await OwnAsync(boundary.Tenant, boundary.Units.Take(2), boundaryParty);
        await OwnAsync(boundary.Tenant, boundary.Units.Skip(2), Guid.NewGuid());
        await EnableAsync(boundary.Tenant, policy => policy with { ClaimantRate = new(true, 365, 2m, 2, 2) });
        await AddClaimAsync(boundary.Tenant, boundary.Units[0], Incident.AddDays(-1), boundaryParty);
        var boundaryClaim = await AddClaimAsync(boundary.Tenant, boundary.Units[1], Incident, boundaryParty);
        await EvaluateAsync(boundary.Tenant, boundaryClaim);
        Assert.Empty(await RulesAsync(boundary.Tenant, boundaryClaim));
    }

    [DatabaseFact]
    public async Task Channel_and_ownership_rules_have_one_hit_and_one_near_miss()
    {
        var channel = await NewScenarioAsync("risk-channel", 2);
        var owner = Guid.NewGuid();
        await OwnAsync(channel.Tenant, channel.Units, owner);
        await EnableAsync(channel.Tenant, policy => policy with { ChannelMismatch = new(true) });
        var mismatch = await AddClaimAsync(channel.Tenant, channel.Units[0], Incident, Guid.NewGuid());
        var match = await AddClaimAsync(channel.Tenant, channel.Units[1], Incident, owner);
        await EvaluateAsync(channel.Tenant, mismatch);
        await EvaluateAsync(channel.Tenant, match);
        Assert.Equal([ClaimRiskRuleCode.ChannelMismatch], await RulesAsync(channel.Tenant, mismatch));
        Assert.Empty(await RulesAsync(channel.Tenant, match));

        var ownership = await NewScenarioAsync("risk-ownership", 2);
        await EnableAsync(ownership.Tenant, policy => policy with { RecentOwnershipChange = new(true, 30) });
        await OwnershipEpisodeAsync(ownership.Tenant, ownership.Units[0], Incident.AddDays(-30));
        await OwnershipEpisodeAsync(ownership.Tenant, ownership.Units[1], Incident.AddDays(-31));
        var ownershipHit = await AddClaimAsync(ownership.Tenant, ownership.Units[0], Incident, Guid.NewGuid());
        var ownershipMiss = await AddClaimAsync(ownership.Tenant, ownership.Units[1], Incident, Guid.NewGuid());
        await EvaluateAsync(ownership.Tenant, ownershipHit);
        await EvaluateAsync(ownership.Tenant, ownershipMiss);
        Assert.Equal([ClaimRiskRuleCode.RecentOwnershipChange], await RulesAsync(ownership.Tenant, ownershipHit));
        Assert.Empty(await RulesAsync(ownership.Tenant, ownershipMiss));
    }

    [DatabaseFact]
    public async Task Outstanding_part_and_duplicate_open_claim_use_due_and_terminal_status_boundaries()
    {
        var outstanding = await NewScenarioAsync("risk-part", 6);
        await EnableAsync(outstanding.Tenant, policy => policy with { PartNotReturned = new(true) });
        var sourceHit = await AddClaimAsync(outstanding.Tenant, outstanding.Units[0], Incident.AddDays(-10), Guid.NewGuid());
        var sourceMiss = await AddClaimAsync(outstanding.Tenant, outstanding.Units[1], Incident.AddDays(-10), Guid.NewGuid());
        await ReplacementAsync(outstanding.Tenant, sourceHit, outstanding.Units[2], outstanding.Units[3], Incident.AddDays(-1));
        await ReplacementAsync(outstanding.Tenant, sourceMiss, outstanding.Units[4], outstanding.Units[5], Incident);
        var partHit = await AddClaimAsync(outstanding.Tenant, outstanding.Units[0], Incident, Guid.NewGuid());
        var partMiss = await AddClaimAsync(outstanding.Tenant, outstanding.Units[1], Incident, Guid.NewGuid());
        await EvaluateAsync(outstanding.Tenant, partHit);
        await EvaluateAsync(outstanding.Tenant, partMiss);
        Assert.Equal([ClaimRiskRuleCode.PartNotReturned], await RulesAsync(outstanding.Tenant, partHit));
        Assert.Empty(await RulesAsync(outstanding.Tenant, partMiss));

        var duplicate = await NewScenarioAsync("risk-duplicate", 2);
        await EnableAsync(duplicate.Tenant, policy => policy with { DuplicateOpenClaim = new(true) });
        await AddClaimAsync(duplicate.Tenant, duplicate.Units[0], Incident.AddDays(-1), Guid.NewGuid(), status: ClaimStatus.Approved);
        await AddClaimAsync(duplicate.Tenant, duplicate.Units[1], Incident.AddDays(-1), Guid.NewGuid(), status: ClaimStatus.Denied);
        var duplicateHit = await AddClaimAsync(duplicate.Tenant, duplicate.Units[0], Incident, Guid.NewGuid());
        var duplicateMiss = await AddClaimAsync(duplicate.Tenant, duplicate.Units[1], Incident, Guid.NewGuid());
        await EvaluateAsync(duplicate.Tenant, duplicateHit);
        await EvaluateAsync(duplicate.Tenant, duplicateMiss);
        Assert.Equal([ClaimRiskRuleCode.DuplicateOpenClaim], await RulesAsync(duplicate.Tenant, duplicateHit));
        Assert.Empty(await RulesAsync(duplicate.Tenant, duplicateMiss));
    }

    [DatabaseFact]
    public async Task Review_is_four_eyes_immutable_and_reported_and_Rls_hides_flags()
    {
        var scenario = await NewScenarioAsync("risk-review", 1);
        var claimId = await AddClaimAsync(scenario.Tenant, scenario.Units[0], Incident, Guid.NewGuid(), submittedBy: "submitter");
        var first = await AddFlagAsync(scenario.Tenant, claimId, ClaimRiskRuleCode.ChannelMismatch);
        var second = await AddFlagAsync(scenario.Tenant, claimId, ClaimRiskRuleCode.RepeatFault);

        var noRole = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ClaimRiskFlagDecisionResult>(scenario.Tenant,
            new DecideClaimRiskFlag(claimId, first, ClaimRiskFlagStatus.Cleared, "Verified sales chain", At, "reviewer", [YojanaRole.ServiceDesk])));
        Assert.Equal("claim_risk_reviewer_required", noRole.Rule);
        var sameUser = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ClaimRiskFlagDecisionResult>(scenario.Tenant,
            new DecideClaimRiskFlag(claimId, first, ClaimRiskFlagStatus.Cleared, "Checked source documents", At, "submitter", [YojanaRole.ServiceManager])));
        Assert.Equal("claim_risk_different_reviewer_required", sameUser.Rule);

        await InvokeAsync<ClaimRiskFlagDecisionResult>(scenario.Tenant,
            new DecideClaimRiskFlag(claimId, first, ClaimRiskFlagStatus.Cleared, "Verified sales chain", At, "reviewer-a", [YojanaRole.ServiceManager]));
        await InvokeAsync<ClaimRiskFlagDecisionResult>(scenario.Tenant,
            new DecideClaimRiskFlag(claimId, second, ClaimRiskFlagStatus.Confirmed, "Repeated power event confirmed", At.AddMinutes(1), "reviewer-b", [YojanaRole.ServiceManager]));
        var immutable = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ClaimRiskFlagDecisionResult>(scenario.Tenant,
            new DecideClaimRiskFlag(claimId, second, ClaimRiskFlagStatus.Cleared, "Changed mind", At.AddMinutes(2), "reviewer-c", [YojanaRole.ServiceManager])));
        Assert.Equal("claim_risk_flag_already_decided", immutable.Rule);

        using (var scope = fixture.ScopeFor(scenario.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var claim = await db.Claims.SingleAsync(row => row.ClaimId == claimId);
            claim.Status = ClaimStatus.Denied;
            claim.AdjudicatedAt = At.AddMinutes(3);
            await db.SaveChangesAsync();
            var report = await scope.ServiceProvider.GetRequiredService<ClaimRiskQueries>()
                .ReportAsync(new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 30), CancellationToken.None);
            Assert.Equal((1, 1, 0m), (report.Single(row => row.RuleCode == ClaimRiskRuleCode.ChannelMismatch).Hits,
                report.Single(row => row.RuleCode == ClaimRiskRuleCode.ChannelMismatch).Cleared,
                report.Single(row => row.RuleCode == ClaimRiskRuleCode.ChannelMismatch).ConfirmationRate));
            var confirmed = report.Single(row => row.RuleCode == ClaimRiskRuleCode.RepeatFault);
            Assert.Equal((1, 1, 1, 1m), (confirmed.Hits, confirmed.Confirmed, confirmed.ConfirmedClaimsDeniedAfterReview, confirmed.ConfirmationRate));
            var firstPage = await scope.ServiceProvider.GetRequiredService<ClaimRiskQueries>()
                .ListAsync("decided", null, null, null, null, 1, CancellationToken.None);
            Assert.True(firstPage.HasMore);
            Assert.NotNull(firstPage.NextCursor);
            var secondPage = await scope.ServiceProvider.GetRequiredService<ClaimRiskQueries>()
                .ListAsync("decided", null, null, null, firstPage.NextCursor, 1, CancellationToken.None);
            Assert.Single(secondPage.Items);
            Assert.NotEqual(Assert.Single(firstPage.Items).FlagId, Assert.Single(secondPage.Items).FlagId);
        }

        var other = await fixture.NewTenantAsync("risk-rls-other");
        using var otherScope = fixture.ScopeFor(other);
        Assert.Equal(0, await otherScope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .ClaimRiskFlags.IgnoreQueryFilters().CountAsync());

        var sameReviewerClaim = await AddClaimAsync(scenario.Tenant, scenario.Units[0], Incident.AddDays(1), Guid.NewGuid(), submittedBy: "same-reviewer");
        var sameReviewerFlag = await AddFlagAsync(scenario.Tenant, sameReviewerClaim, ClaimRiskRuleCode.ChannelMismatch);
        using (var settingsScope = fixture.ScopeFor(scenario.Tenant))
        {
            var settings = settingsScope.ServiceProvider.GetRequiredService<ClaimRiskRulesSettings>();
            await settings.SetAsync((await settings.GetAsync(CancellationToken.None)) with { RequireDifferentReviewer = false }, CancellationToken.None);
        }
        var allowed = await InvokeAsync<ClaimRiskFlagDecisionResult>(scenario.Tenant,
            new DecideClaimRiskFlag(sameReviewerClaim, sameReviewerFlag, ClaimRiskFlagStatus.Cleared,
                "Independent source records checked", At.AddMinutes(4), "same-reviewer", [YojanaRole.ServiceManager]));
        Assert.Equal(ClaimRiskFlagStatus.Cleared, allowed.Status);
    }

    [DatabaseFact]
    public async Task Disabled_rules_validation_and_re_evaluation_behave_deterministically()
    {
        var scenario = await NewScenarioAsync("risk-reevaluate", 1);
        var claimant = Guid.NewGuid();
        var current = await AddClaimAsync(scenario.Tenant, scenario.Units[0], Incident, claimant, "network_drop");
        await EvaluateAsync(scenario.Tenant, current);
        Assert.Empty(await RulesAsync(scenario.Tenant, current));

        await EnableAsync(scenario.Tenant, policy => policy with { RepeatFault = new(true, 180) });
        await AddClaimAsync(scenario.Tenant, scenario.Units[0], Incident.AddDays(-1), claimant, "network_drop");
        await EvaluateAsync(scenario.Tenant, current, evaluation: ClaimRiskEvaluation.Adjudication);
        await EvaluateAsync(scenario.Tenant, current, evaluation: ClaimRiskEvaluation.Adjudication);
        Assert.Equal([ClaimRiskRuleCode.RepeatFault], await RulesAsync(scenario.Tenant, current));

        using var scope = fixture.ScopeFor(scenario.Tenant);
        var settings = scope.ServiceProvider.GetRequiredService<ClaimRiskRulesSettings>();
        var defaults = ClaimRiskRulesSettings.DefaultPolicy;
        Assert.True(defaults.RecentCoverPurchase.Enabled);
        Assert.True(defaults.PartNotReturned.Enabled);
        var invalid = new ClaimRiskRulesPolicy(new(true, 0), defaults.RepeatUnitClaims, defaults.RepeatFault,
            defaults.ClaimantRate, defaults.ChannelMismatch, defaults.RecentOwnershipChange,
            defaults.PartNotReturned, defaults.DuplicateOpenClaim, "invented_role", true);
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(invalid, CancellationToken.None));
        Assert.Equal("claim_risk_rules_invalid", refusal.Rule);
    }

    [DatabaseFact]
    public async Task Adjudication_re_evaluates_holds_approval_allows_denial_and_proceeds_after_review()
    {
        var scenario = await NewScenarioAsync("risk-adjudication", 2);
        await EnableAsync(scenario.Tenant, policy => policy with { RepeatFault = new(true, 180) });
        var claimant = Guid.NewGuid();
        await AddClaimAsync(scenario.Tenant, scenario.Units[0], Incident.AddDays(-1), claimant, "network_drop", status: ClaimStatus.Denied);
        var heldClaim = await AddClaimAsync(scenario.Tenant, scenario.Units[0], Incident, claimant, "network_drop");
        await AddCampaignAsync(scenario.Tenant, heldClaim, scenario.Units[0], "RISK-NETWORK");
        await AddFlagAsync(scenario.Tenant, heldClaim, ClaimRiskRuleCode.ChannelMismatch);

        using (var scope = fixture.ScopeFor(scenario.Tenant))
        {
            var preview = await scope.ServiceProvider.GetRequiredService<ClaimAdjudicationPreviewQuery>()
                .PreviewAsync(heldClaim, YojanaRole.ServiceManager, CancellationToken.None);
            Assert.NotNull(preview);
            Assert.Equal(ClaimStatus.Approved, preview.Outcome);
            Assert.Equal("claim_risk_review_pending", preview.Refusal);
            Assert.Equal(ClaimRiskRuleCode.ChannelMismatch, Assert.Single(preview.OpenRiskFlags!).RuleCode);
        }

        var held = await InvokeAsync<AdjudicateClaimResult>(scenario.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), heldClaim, At.AddHours(1), "service.manager"));
        Assert.Equal((ClaimStatus.Approved, "claim_risk_review_pending"), (held.Status, held.Refusal));
        Assert.Equal([ClaimRiskRuleCode.ChannelMismatch, ClaimRiskRuleCode.RepeatFault], held.OpenRiskRules);

        var heldAgain = await InvokeAsync<AdjudicateClaimResult>(scenario.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), heldClaim, At.AddHours(2), "service.manager"));
        Assert.Equal("claim_risk_review_pending", heldAgain.Refusal);
        Assert.Equal(2, (await RulesAsync(scenario.Tenant, heldClaim)).Count);

        using (var scope = fixture.ScopeFor(scenario.Tenant))
        {
            var flags = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ClaimRiskFlags.AsNoTracking()
                .Where(row => row.ClaimId == heldClaim).OrderBy(row => row.RuleCode).ToListAsync();
            foreach (var flag in flags)
            {
                await InvokeAsync<ClaimRiskFlagDecisionResult>(scenario.Tenant,
                    new DecideClaimRiskFlag(heldClaim, flag.FlagId,
                        flag.RuleCode == ClaimRiskRuleCode.RepeatFault ? ClaimRiskFlagStatus.Confirmed : ClaimRiskFlagStatus.Cleared,
                        "Reviewed against service records", At.AddHours(3), "risk.reviewer", [YojanaRole.ServiceManager]));
            }
        }

        var approved = await InvokeAsync<AdjudicateClaimResult>(scenario.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), heldClaim, At.AddHours(4), "service.manager"));
        Assert.Equal(ClaimStatus.Approved, approved.Status);
        Assert.Null(approved.Refusal);

        var deniedClaim = await AddClaimAsync(scenario.Tenant, scenario.Units[1], Incident, claimant);
        await AddCampaignAsync(scenario.Tenant, deniedClaim, scenario.Units[1], "RISK-DENIAL");
        await AddFlagAsync(scenario.Tenant, deniedClaim, ClaimRiskRuleCode.ChannelMismatch);
        var denied = await InvokeAsync<AdjudicateClaimResult>(scenario.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), deniedClaim, At.AddHours(5), "service.manager",
                DenialReason: ClaimManualDenialReason.NoFaultFound, DenialNote: "No hardware fault reproduced under load."));
        Assert.Equal(ClaimStatus.Denied, denied.Status);
        Assert.Single(await RulesAsync(scenario.Tenant, deniedClaim));
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<Scenario> NewScenarioAsync(string prefix, int units)
    {
        var tenant = await fixture.NewTenantAsync(prefix, "EUR");
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), Guid.NewGuid(), UnitOrigin.Purchased,
            Enumerable.Range(1, units).Select(index => new CommissionLine(
                [new IdentifierInput(IdentifierType.InternalSerial, $"{prefix.ToUpperInvariant()}-{Guid.NewGuid():N}-{index}")])).ToList(),
            new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc), "risk-fixture", Guid.NewGuid()));
        return new(tenant, commissioned.Lines.Select(row => row.UnitId!.Value).ToList());
    }

    private async Task<Guid> AddClaimAsync(TenantId tenant, Guid unitId, DateOnly incident, Guid claimant,
        string? cause = null, string status = ClaimStatus.Submitted, string submittedBy = "submitter")
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        if (cause is not null && !await db.ClaimCauses.AnyAsync(row => row.Code == cause))
        {
            db.ClaimCauses.Add(new ClaimCauseRow
            {
                TenantId = tenant, Code = cause, CustomerWording = cause.Replace('_', ' '), Active = true,
                UpdatedBy = "risk-fixture", UpdatedAt = At,
            });
            await db.SaveChangesAsync();
        }
        var id = TimeOrderedId.New(incident.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc));
        var claim = new Claim
        {
            TenantId = tenant, ClaimId = id, ClaimNumber = "CLM-R-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
            CommandId = Guid.NewGuid(), UnitId = unitId, ClaimDate = incident, OccurredDate = incident,
            ReportedCauseCode = cause, ClaimantPartyRef = claimant, SettlementPartyRef = claimant,
            ServiceEntityId = Guid.NewGuid(), ObligorEntityId = Guid.NewGuid(), ServiceCountry = "GB", Currency = "EUR",
            CoverageSource = ClaimCoverageSource.Chargeable, Remedy = ClaimRemedy.Repair, Status = status,
            SubmittedAt = incident.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc), SubmittedBy = submittedBy,
            LastEventId = Guid.NewGuid(), LastModifiedAt = At, LastModifiedBy = submittedBy,
        };
        db.Claims.Add(claim);
        db.ClaimLines.Add(new ClaimLine
        {
            TenantId = tenant, ClaimLineId = Guid.NewGuid(), ClaimId = id, UnitId = unitId, Sequence = 1,
            Kind = ClaimLineKind.Parts, CoverageFamily = CoverageFamilyCode.Base, RequestedAmount = 100m,
            CoverageSource = ClaimCoverageSource.Chargeable,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task EvaluateAsync(TenantId tenant, Guid claimId, ClaimLineOutcome? outcome = null,
        string evaluation = ClaimRiskEvaluation.Submission)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var claim = await db.Claims.SingleAsync(row => row.ClaimId == claimId);
        var line = await db.ClaimLines.SingleAsync(row => row.ClaimId == claimId);
        if (outcome?.Resolution?.PolicyCode == "accidental_damage") line.CoverageFamily = "accidental_damage";
        await scope.ServiceProvider.GetRequiredService<ClaimRiskEvaluator>().EvaluateAsync(claim,
            [new(line, outcome ?? ClaimLineOutcome.Denied("fixture"))], evaluation, At, false, CancellationToken.None);
        await db.SaveChangesAsync();
    }

    private static ClaimLineOutcome PurchaseOutcome(DateOnly purchaseDate, string family) => new(100m, 100m,
        ClaimCoverageSource.Contract, null, Resolution: new PolicyResolution(PolicyLevel.UnitEntitlement, Guid.NewGuid(),
            Guid.NewGuid(), family, 1, null, false, [], EntitlementType.Extended, EntitlementStatus.Active,
            EntitlementDirection.Outbound, PurchaseDate: purchaseDate));

    private async Task EnableAsync(TenantId tenant, Func<ClaimRiskRulesPolicy, ClaimRiskRulesPolicy> change)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ClaimRiskRulesSettings>().SetAsync(change(DisabledPolicy()), CancellationToken.None);
    }

    private static ClaimRiskRulesPolicy DisabledPolicy()
    {
        var value = ClaimRiskRulesSettings.DefaultPolicy;
        return value with
        {
            RecentCoverPurchase = value.RecentCoverPurchase with { Enabled = false },
            RepeatUnitClaims = value.RepeatUnitClaims with { Enabled = false },
            RepeatFault = value.RepeatFault with { Enabled = false },
            ClaimantRate = value.ClaimantRate with { Enabled = false },
            ChannelMismatch = value.ChannelMismatch with { Enabled = false },
            RecentOwnershipChange = value.RecentOwnershipChange with { Enabled = false },
            PartNotReturned = value.PartNotReturned with { Enabled = false },
            DuplicateOpenClaim = value.DuplicateOpenClaim with { Enabled = false },
        };
    }

    private async Task AddFamilyAsync(TenantId tenant, string code, bool stacking)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.CoverageFamilies.Add(new CoverageFamily
        {
            TenantId = tenant, FamilyCode = code, Name = "Accidental damage", Stacking = stacking,
            AccountingTag = "SERVICE", CreatedAt = At, LastModifiedAt = At, LastModifiedBy = "risk-fixture",
        });
        await db.SaveChangesAsync();
    }

    private async Task OwnAsync(TenantId tenant, IEnumerable<Guid> unitIds, Guid party)
    {
        using var scope = fixture.ScopeFor(tenant);
        var ids = unitIds.ToList();
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        foreach (var row in await db.Current.Where(row => ids.Contains(row.UnitId)).ToListAsync()) row.OwnerPartyRef = party;
        await db.SaveChangesAsync();
    }

    private async Task OwnershipEpisodeAsync(TenantId tenant, Guid unitId, DateOnly opened)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.Episodes.Add(new CustodyEpisode
        {
            TenantId = tenant, EpisodeId = Guid.NewGuid(), UnitId = unitId, Kind = EpisodeKind.Ownership,
            PartyRef = Guid.NewGuid(), PartyType = PartyType.Customer, Reason = EpisodeReason.Transfer,
            Source = EpisodeSource.Registration, Confidence = Confidence.High,
            ValidFrom = opened.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc), OpeningEventId = Guid.NewGuid(),
        });
        await db.SaveChangesAsync();
    }

    private async Task ReplacementAsync(TenantId tenant, Guid sourceClaimId, Guid originalPart, Guid replacementPart, DateOnly due)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var source = await db.Claims.SingleAsync(row => row.ClaimId == sourceClaimId);
        var documentId = Guid.NewGuid();
        db.WarrantyReplacements.Add(new WarrantyReplacement
        {
            TenantId = tenant, WarrantyReplacementId = documentId, CommandId = Guid.NewGuid(),
            ReplacementNumber = "WR-" + Guid.NewGuid().ToString("N")[..8], SourceKind = ReplacementSourceKind.Claim,
            SourceId = sourceClaimId, SourceNumber = source.ClaimNumber, LegalEntityId = Guid.NewGuid(), ServiceCountry = "GB",
            Status = WarrantyReplacementStatus.Shipped, CreatedAt = At.AddDays(-10), CreatedBy = "risk-fixture",
            LastModifiedAt = At, LastModifiedBy = "risk-fixture",
        });
        db.ReplacementLinks.Add(new ReplacementLink
        {
            TenantId = tenant, ReplacementLinkId = Guid.NewGuid(), WarrantyReplacementId = documentId,
            Direction = ReplacementDirection.CustomerFacing, ReplacementReason = ReplacementReason.WarrantyDefect,
            State = ReplacementLinkState.AwaitingReturn, SourceKind = ReplacementSourceKind.Claim, SourceId = sourceClaimId,
            SourceNumber = source.ClaimNumber, OriginalUnitId = originalPart, ReplacementUnitId = replacementPart,
            OriginalItemId = Guid.NewGuid(), ReplacementItemId = Guid.NewGuid(), SourceEntitlementId = Guid.NewGuid(),
            PricePolicy = ReplacementPricePolicy.Zero, Currency = "EUR", ReturnDue = due,
            ReplacementConditionAllowed = ReplacementConditionAllowed.New, CreatedAt = At.AddDays(-10), CreatedBy = "risk-fixture",
        });
        await db.SaveChangesAsync();
    }

    private async Task<Guid> AddFlagAsync(TenantId tenant, Guid claimId, string rule)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var id = Guid.NewGuid();
        db.ClaimRiskFlags.Add(new ClaimRiskFlag
        {
            TenantId = tenant, FlagId = id, ClaimId = claimId, RuleCode = rule,
            ThresholdSnapshot = "{}", Evidence = "{}", RaisedAt = At,
            RaisedByEvaluation = ClaimRiskEvaluation.Submission, Status = ClaimRiskFlagStatus.Open,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task AddCampaignAsync(TenantId tenant, Guid claimId, Guid unitId, string code)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var campaignId = Guid.NewGuid();
        db.Campaigns.Add(new Campaign
        {
            TenantId = tenant, CampaignId = campaignId, Code = code, Name = "Network adapter service action",
            CoverageSource = CampaignCoverageSource.Goodwill, ReserveTag = "service:" + code,
            ReasonCode = CampaignReason.Reliability, Status = CampaignStatus.Active,
            CreatedAt = At, CreatedBy = "risk-fixture", UpdatedAt = At, UpdatedBy = "risk-fixture",
        });
        db.CampaignUnits.Add(new CampaignUnit
        {
            TenantId = tenant, CampaignId = campaignId, UnitId = unitId, SelectedAt = At,
            SelectedBy = "risk-fixture", SelectionCommandId = Guid.NewGuid(),
        });
        (await db.Claims.SingleAsync(row => row.ClaimId == claimId)).CampaignId = campaignId;
        await db.SaveChangesAsync();
    }

    private async Task<IReadOnlyList<string>> RulesAsync(TenantId tenant, Guid claimId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().ClaimRiskFlags.AsNoTracking()
            .Where(row => row.ClaimId == claimId).OrderBy(row => row.RuleCode).Select(row => row.RuleCode).ToListAsync();
    }

    private sealed record Scenario(TenantId Tenant, IReadOnlyList<Guid> Units);
}
