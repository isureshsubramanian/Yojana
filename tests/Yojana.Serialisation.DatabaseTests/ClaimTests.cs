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

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P4-T03/T06/T08: claim adjudication, publication and historical supplier recovery.</summary>
[Collection("serialisation host")]
public sealed class ClaimTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime Start = new(2025, 1, 1, 9, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Reference_claim_is_approved_for_replacement_and_publishes_the_warranty_cohort()
    {
        var setup = await SetUpAsync("c1", 1);
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

        var raised = Assert.Single(await PublishedEventSink.WaitForAsync<RecoveryClaimRaised>(e => e.CustomerClaimId == month9.ClaimId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(setup.Units[1], raised.ChildUnitId);
        Assert.DoesNotContain(PublishedEventSink.Received.OfType<RecoveryClaimRaised>(), e => e.CustomerClaimId == month15.ClaimId);
    }

    private async Task<SubmitClaimResult> SubmitAsync(TenantId tenant, Guid unitId, DateOnly claimDate, DateTime at, string remedy) =>
        await InvokeAsync<SubmitClaimResult>(tenant, new SubmitClaim(Guid.NewGuid(), unitId, null, claimDate, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "DE", "EUR",
            [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m)], [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "DEFECT-PHOTO")], remedy, "CHOICE-RECORDED", at, "service.desk", "INVOICE-1"));

    private async Task<ClaimSetup> SetUpAsync(string prefix, int count)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
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

        return new(tenant, units, cohorts, obligor, draft.PolicyVersionId);
    }

    private async Task AddChildrenAndSupplierCoverAsync(ClaimSetup setup, Guid firstRoot, Guid firstChild, Guid secondRoot, Guid secondChild)
    {
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        foreach (var (root, child) in new[] { (firstRoot, firstChild), (secondRoot, secondChild) })
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

    private sealed record ClaimSetup(TenantId Tenant, IReadOnlyList<Guid> Units, IReadOnlyList<Guid?> Cohorts, Guid Obligor, Guid PolicyVersionId);
}
