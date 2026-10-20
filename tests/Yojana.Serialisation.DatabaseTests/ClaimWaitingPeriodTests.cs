using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Claims;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P9-T17: the published waiting-period term is decided from the covering entitlement's original sale date.</summary>
[Collection("serialisation host")]
public sealed class ClaimWaitingPeriodTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime Purchase = new(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly PurchaseDay = new(2026, 8, 4);

    private async Task<T> Invoke<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Occurred_date_inside_the_period_denies_only_the_add_on_and_the_boundary_day_is_claimable()
    {
        var setup = await SetupAsync("waiting-decision");
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<ClaimWaitingPeriodSettings>();
            await settings.SetAsync(new ClaimWaitingPeriodDefaults(new Dictionary<string, int>
            {
                [CoverageFamilyCode.Base] = 0,
                ["accidental_damage"] = 60,
            }), [CoverageFamilyCode.Base, "accidental_damage"], CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(30, await db.PolicyVersions.Where(x => x.PolicyVersionId == setup.DamagePolicy)
                .Select(x => x.NoClaimWithinDaysOfPurchase).SingleAsync());

            // A null published value is the pre-P9-T17 representation and deliberately means no waiting period.
            (await db.PolicyVersions.SingleAsync(x => x.PolicyVersionId == setup.BasePolicy)).NoClaimWithinDaysOfPurchase = null;
            await db.SaveChangesAsync();
        }

        var inside = await SubmitAsync(setup, setup.Units[0], PurchaseDay.AddDays(40), PurchaseDay.AddDays(10),
            [new(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m), new(ClaimLineKind.Parts, "accidental_damage", 120m)]);
        var decided = await Invoke<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), inside.ClaimId, Purchase.AddDays(41), "service.manager"));

        Assert.Equal(ClaimStatus.PartiallyApproved, decided.Status);
        var baseLine = decided.Lines.Single(x => x.CoverageFamily == CoverageFamilyCode.Base);
        Assert.Equal((100m, (string?)null), (baseLine.ApprovedAmount, baseLine.DenialReason));
        var damageLine = decided.Lines.Single(x => x.CoverageFamily == "accidental_damage");
        Assert.Equal((0m, ClaimDenialReason.ClaimWaitingPeriod, PurchaseDay, PurchaseDay.AddDays(30)),
            (damageLine.ApprovedAmount, damageLine.DenialReason, damageLine.WaitingPeriodPurchaseDate, damageLine.FirstClaimableDate));

        var boundary = await SubmitAsync(setup, setup.Units[0], PurchaseDay.AddDays(30), null,
            [new(ClaimLineKind.Parts, "accidental_damage", 120m)]);
        var approved = await Invoke<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), boundary.ClaimId, Purchase.AddDays(31), "service.manager"));
        Assert.Equal((ClaimStatus.Approved, 120m, PurchaseDay.AddDays(30)),
            (approved.Status, Assert.Single(approved.Lines).ApprovedAmount, Assert.Single(approved.Lines).FirstClaimableDate));
    }

    [DatabaseFact]
    public async Task Missing_purchase_date_is_reported_without_denial()
    {
        var setup = await SetupAsync("waiting-missing");
        await CreateCoverAsync(setup, setup.Units[1], setup.DamagePolicy, "accidental_damage", sourceDocument: false,
            provenance: EntitlementProvenance.Backfilled);
        var claim = await SubmitAsync(setup, setup.Units[1], PurchaseDay.AddDays(10), null,
            [new(ClaimLineKind.Parts, "accidental_damage", 80m)]);
        var result = await Invoke<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, Purchase.AddDays(11), "service.manager"));

        var line = Assert.Single(result.Lines);
        Assert.Equal((ClaimStatus.Approved, 80m, ClaimWaitingPeriodNotAppliedReason.PurchaseDateUnavailable),
            (result.Status, line.ApprovedAmount, line.WaitingPeriodNotAppliedReason));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var preview = (await scope.ServiceProvider.GetRequiredService<ClaimAdjudicationPreviewQuery>()
            .PreviewAsync(claim.ClaimId, "service_manager", CancellationToken.None))!;
        Assert.Equal(ClaimWaitingPeriodNotAppliedReason.PurchaseDateUnavailable, Assert.Single(preview.Lines).WaitingPeriodNotAppliedReason);
    }

    [DatabaseFact]
    public async Task Replacement_transfer_keeps_the_original_purchase_date_and_does_not_restart_the_period()
    {
        var setup = await SetupAsync("waiting-transfer");
        var transferred = await Invoke<TransferEntitlementResult>(setup.Tenant,
            new TransferEntitlement(Guid.NewGuid(), setup.DamageEntitlement, setup.Units[2], Guid.NewGuid(), PurchaseDay.AddDays(5),
                Purchase.AddDays(5), "service.technician"));

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var purchaseDate = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements
                .Where(x => x.EntitlementId == transferred.TargetEntitlementId).Select(x => x.PurchaseDate).SingleAsync();
            Assert.Equal(PurchaseDay, purchaseDate);
        }

        var claim = await SubmitAsync(setup, setup.Units[2], PurchaseDay.AddDays(10), null,
            [new(ClaimLineKind.Parts, "accidental_damage", 90m)]);
        var result = await Invoke<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, Purchase.AddDays(11), "service.manager"));
        var line = Assert.Single(result.Lines);
        Assert.Equal((ClaimDenialReason.ClaimWaitingPeriod, PurchaseDay, PurchaseDay.AddDays(30)),
            (line.DenialReason, line.WaitingPeriodPurchaseDate, line.FirstClaimableDate));
    }

    [DatabaseFact]
    public async Task Publishing_copies_the_family_default_when_absent_and_preserves_an_explicit_value()
    {
        var setup = await SetupAsync("waiting-publish");
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            await scope.ServiceProvider.GetRequiredService<ClaimWaitingPeriodSettings>().SetAsync(
                new ClaimWaitingPeriodDefaults(new Dictionary<string, int>
                {
                    [CoverageFamilyCode.Base] = 0,
                    ["accidental_damage"] = 30,
                }), [CoverageFamilyCode.Base, "accidental_damage"], CancellationToken.None);
        }

        var standard = PolicyValidatorTestsTerms.Standard24() with
        {
            Duration = new(36, DurationRule.Months), Start = new(StartTrigger.Invoice, [], null), Refurbished = [],
            RegionScope = ["WORLD"], ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
            AssuranceOrService = AssuranceOrService.Service,
        };
        var defaulted = await PublishAsync(setup.Tenant, "WAIT-DEFAULT-" + Guid.NewGuid().ToString("N")[..5],
            "accidental_damage", EntitlementType.Extended, standard with { NoClaimWithinDaysOfPurchase = null });
        var explicitValue = await PublishAsync(setup.Tenant, "WAIT-EXPLICIT-" + Guid.NewGuid().ToString("N")[..5],
            "accidental_damage", EntitlementType.Extended, standard with { NoClaimWithinDaysOfPurchase = 12 });

        using var read = fixture.ScopeFor(setup.Tenant);
        var values = await read.ServiceProvider.GetRequiredService<SerialisationDbContext>().PolicyVersions.AsNoTracking()
            .Where(x => x.PolicyVersionId == defaulted.PolicyVersionId || x.PolicyVersionId == explicitValue.PolicyVersionId)
            .ToDictionaryAsync(x => x.PolicyVersionId, x => x.NoClaimWithinDaysOfPurchase);
        Assert.Equal(30, values[defaulted.PolicyVersionId]);
        Assert.Equal(12, values[explicitValue.PolicyVersionId]);
    }

    [DatabaseFact]
    public async Task Changing_the_tenant_default_after_publish_does_not_change_the_term_used_for_adjudication()
    {
        var setup = await SetupAsync("waiting-published-term");
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            await scope.ServiceProvider.GetRequiredService<ClaimWaitingPeriodSettings>().SetAsync(
                new ClaimWaitingPeriodDefaults(new Dictionary<string, int>
                {
                    [CoverageFamilyCode.Base] = 0,
                    ["accidental_damage"] = 60,
                }), [CoverageFamilyCode.Base, "accidental_damage"], CancellationToken.None);
            var stored = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().PolicyVersions.AsNoTracking()
                .Where(x => x.PolicyVersionId == setup.DamagePolicy)
                .Select(x => x.NoClaimWithinDaysOfPurchase)
                .SingleAsync();
            Assert.Equal(30, stored);
        }

        var claim = await SubmitAsync(setup, setup.Units[0], PurchaseDay.AddDays(40), PurchaseDay.AddDays(40),
            [new(ClaimLineKind.Parts, "accidental_damage", 120m)]);
        var result = await Invoke<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, Purchase.AddDays(41), "service.manager"));

        var line = Assert.Single(result.Lines);
        Assert.Equal((ClaimStatus.Approved, 120m, (string?)null, PurchaseDay, PurchaseDay.AddDays(30)),
            (result.Status, line.ApprovedAmount, line.DenialReason, line.WaitingPeriodPurchaseDate, line.FirstClaimableDate));
    }

    [DatabaseFact]
    public async Task Published_null_waiting_period_from_before_the_feature_applies_no_waiting_period()
    {
        var setup = await SetupAsync("waiting-legacy-null");
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var policy = await db.PolicyVersions.SingleAsync(x => x.PolicyVersionId == setup.DamagePolicy);
            Assert.Equal(PolicyVersionStatus.Published, policy.Status);
            policy.NoClaimWithinDaysOfPurchase = null;
            await db.SaveChangesAsync();
        }

        var claim = await SubmitAsync(setup, setup.Units[0], PurchaseDay.AddDays(1), PurchaseDay.AddDays(1),
            [new(ClaimLineKind.Parts, "accidental_damage", 120m)]);
        var result = await Invoke<AdjudicateClaimResult>(setup.Tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, Purchase.AddDays(2), "service.manager"));

        var line = Assert.Single(result.Lines);
        Assert.Equal((ClaimStatus.Approved, 120m, (string?)null, (DateOnly?)null, (DateOnly?)null),
            (result.Status, line.ApprovedAmount, line.DenialReason, line.WaitingPeriodPurchaseDate, line.FirstClaimableDate));
    }

    private async Task<Setup> SetupAsync(string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix, "EUR");
        var obligor = Guid.NewGuid();
        await Invoke<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await Invoke<CoverageFamilyRow>(tenant, new DefineCoverageFamily("accidental_damage", "Accidental damage", true, AssuranceOrService.Service));
        await Invoke<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));

        var standard = PolicyValidatorTestsTerms.Standard24() with
        {
            Duration = new(36, DurationRule.Months), Start = new(StartTrigger.Invoice, [], null), Refurbished = [],
            RegionScope = ["WORLD"], ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
        };
        var basePolicy = await PublishAsync(tenant, "WAIT-BASE-" + Guid.NewGuid().ToString("N")[..5], CoverageFamilyCode.Base,
            EntitlementType.Manufacturer, standard with { NoClaimWithinDaysOfPurchase = 0 });
        var damagePolicy = await PublishAsync(tenant, "WAIT-ADP-" + Guid.NewGuid().ToString("N")[..5], "accidental_damage",
            EntitlementType.Extended, standard with { AssuranceOrService = AssuranceOrService.Service, NoClaimWithinDaysOfPurchase = null });

        var item = Guid.NewGuid();
        var commissioned = await Invoke<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            Enumerable.Range(1, 3).Select(i => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{prefix.ToUpperInvariant()}-{Guid.NewGuid():N}-{i}")])).ToList(),
            Purchase.AddDays(-1), "test", obligor));
        var units = commissioned.Lines.Select(x => x.UnitId!.Value).ToList();
        await CreateCoverAsync(new(tenant, obligor, units, basePolicy.PolicyVersionId, damagePolicy.PolicyVersionId, Guid.Empty),
            units[0], basePolicy.PolicyVersionId, CoverageFamilyCode.Base, true, EntitlementProvenance.Created);
        var damage = await CreateCoverAsync(new(tenant, obligor, units, basePolicy.PolicyVersionId, damagePolicy.PolicyVersionId, Guid.Empty),
            units[0], damagePolicy.PolicyVersionId, "accidental_damage", true, EntitlementProvenance.Created);
        return new(tenant, obligor, units, basePolicy.PolicyVersionId, damagePolicy.PolicyVersionId, damage.EntitlementId);
    }

    private async Task<PolicyDraftResult> PublishAsync(TenantId tenant, string code, string family, string type, PolicyTerms terms)
    {
        var draft = await Invoke<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, family, type,
            new DateOnly(2025, 1, 1), terms, "test"));
        return await Invoke<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
    }

    private Task<CreateEntitlementResult> CreateCoverAsync(Setup setup, Guid unit, Guid policy, string family,
        bool sourceDocument, string provenance) => Invoke<CreateEntitlementResult>(setup.Tenant,
        new CreateEntitlement(Guid.NewGuid(), unit, family, PurchaseDay, setup.Obligor, Purchase, "test",
            new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Purchase }, policy, Guid.NewGuid(), PartyType.Customer,
            Country: "DE", SourceDocument: sourceDocument ? new DocumentRef("invoice", Guid.NewGuid(), 1) : null,
            Provenance: provenance));

    private Task<SubmitClaimResult> SubmitAsync(Setup setup, Guid unit, DateOnly claimDate, DateOnly? occurredDate,
        IReadOnlyList<ClaimLineInput> lines) => Invoke<SubmitClaimResult>(setup.Tenant,
        new SubmitClaim(Guid.NewGuid(), unit, null, claimDate, Guid.NewGuid(), Guid.NewGuid(), setup.Obligor, setup.Obligor,
            "DE", "EUR", lines, [], ClaimRemedy.Repair, "choice-recorded", claimDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc),
            "service.desk", OccurredDate: occurredDate));

    private sealed record Setup(TenantId Tenant, Guid Obligor, IReadOnlyList<Guid> Units, Guid BasePolicy, Guid DamagePolicy, Guid DamageEntitlement);
}
