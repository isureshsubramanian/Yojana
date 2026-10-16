using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P2-T03: the policy editor's commands through the bus: drafts, validation, publication, versions by contract date, reference data.</summary>
[Collection("serialisation host")]
public sealed class WarrantyPolicyTests(SerialisationHostFixture fixture)
{
    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<TenantId> TenantWithReferenceDataAsync(string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("service_level", "Service level", false, AssuranceOrService.Service));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, "longer of remaining and 90 days"));
        return tenant;
    }

    [DatabaseFact]
    public async Task A_policy_is_a_series_of_versions_by_contract_date_and_a_published_version_is_never_edited()
    {
        var tenant = await TenantWithReferenceDataAsync("wp");
        var v1 = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "VX220-STD-24", "VX-220 Standard 24 months", "base_warranty", EntitlementType.Manufacturer, new DateOnly(2026, 7, 31), PolicyValidatorTestsTerms.Standard24(), "r.okafor"));
        Assert.Equal((1, PolicyVersionStatus.Draft), (v1.Version, v1.Status));
        Assert.True(v1.Validation.CanPublish);
        Assert.Contains(v1.Validation.Issues, i => i.Code == "refurbished_below_floor");

        // one open draft per policy
        var second = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "VX220-STD-24", "again", "base_warranty", EntitlementType.Manufacturer, new DateOnly(2027, 1, 1), null, "r.okafor")));
        Assert.Equal("draft_open", second.Rule);

        var published = await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(v1.PolicyVersionId, "r.okafor"));
        Assert.Equal(PolicyVersionStatus.Published, published.Status);
        var edit = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<PolicyDraftResult>(tenant, new UpdatePolicyDraft(v1.PolicyVersionId, "changed", new DateOnly(2026, 7, 31), PolicyValidatorTestsTerms.Standard24(), "r.okafor")));
        Assert.Equal("version_not_draft", edit.Rule);

        // the next version copies the terms; dated no later than v1 it cannot publish, dated later it can
        var v2 = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "VX220-STD-24", "VX-220 Standard 24 months", "base_warranty", EntitlementType.Manufacturer, new DateOnly(2026, 7, 31), null, "r.okafor"));
        Assert.Equal(2, v2.Version);
        Assert.Contains(v2.Validation.Issues, i => i.Code == "applies_from_not_later" && i.Severity == PolicyIssue.Error);
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(v2.PolicyVersionId, "r.okafor")));
        Assert.Equal("policy_invalid", refused.Rule);
        var capped = PolicyValidatorTestsTerms.Standard24() with { Start = new StartRule(StartTrigger.Deliver, [new StartFallback(StartTrigger.Invoice, 14)], 60) };
        var fixedDraft = await InvokeAsync<PolicyDraftResult>(tenant, new UpdatePolicyDraft(v2.PolicyVersionId, "VX-220 Standard 24 months", new DateOnly(2027, 1, 1), capped, "r.okafor"));
        Assert.True(fixedDraft.Validation.CanPublish);
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(v2.PolicyVersionId, "r.okafor"));

        // a version cannot change the policy's family; a third draft is discarded without trace in the current table
        var family = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "VX220-STD-24", "x", "service_level", EntitlementType.Manufacturer, new DateOnly(2028, 1, 1), null, "r.okafor")));
        Assert.Equal("policy_identity_changed", family.Rule);
        var v3 = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "VX220-STD-24", "draft to drop", "base_warranty", EntitlementType.Manufacturer, new DateOnly(2028, 1, 1), null, "r.okafor"));
        await InvokeAsync<PolicyDraftResult>(tenant, new DiscardPolicyDraft(v3.PolicyVersionId, "r.okafor"));

        using var scope = fixture.ScopeFor(tenant);
        var queries = scope.ServiceProvider.GetRequiredService<PolicyQueries>();
        var versions = await queries.VersionsAsync("VX220-STD-24", CancellationToken.None);
        Assert.Equal([2, 1], versions.Select(v => v.Version));
        Assert.All(versions, v => Assert.Equal(PolicyVersionStatus.Published, v.Status));
        Assert.Equal("v2.d1", versions[0].RuleVersion);
        var detail = await queries.DetailAsync(v2.PolicyVersionId, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal(60, detail.Terms.Start.CapDays);
        Assert.Equal("STD-90", detail.Terms.ReplacementPolicyCode);
        Assert.Equal(PolicyValidatorTestsTerms.Standard24().Refurbished, detail.Terms.Refurbished);
        Assert.Equal(PolicyValidatorTestsTerms.Standard24().Coverage, detail.Terms.Coverage);
        var summary = Assert.Single(await queries.ListAsync(null, CancellationToken.None));
        Assert.Equal((2, 2, (int?)null), (summary.Versions, summary.InForceVersion, summary.DraftVersion));
    }

    [DatabaseFact]
    public async Task Reference_data_in_use_cannot_change_under_a_published_policy_or_a_live_entitlement()
    {
        var tenant = await TenantWithReferenceDataAsync("wr");
        var v1 = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "VX220-STD-24", "VX-220 Standard 24 months", "base_warranty", EntitlementType.Manufacturer, new DateOnly(2026, 7, 31), PolicyValidatorTestsTerms.Standard24(), "r.okafor"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(v1.PolicyVersionId, "r.okafor"));

        var rule = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.CarryRemaining, null, null, null)));
        Assert.Equal("replacement_policy_in_use", rule.Rule);
        // the description alone may change
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, "reworded"));
        Assert.Equal("min_days_required", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("BAD", ReplacementRule.MaxRemainingOrMinDays, null, null, null)))).Rule);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Entitlements.Add(new Entitlement
            {
                TenantId = tenant, EntitlementId = TimeOrderedId.New(DateTime.UtcNow), UnitId = Guid.NewGuid(), Scope = EntitlementScope.Unit, Type = EntitlementType.Manufacturer, CoverageFamily = "service_level", Direction = EntitlementDirection.Outbound,
                ObligorEntityId = Guid.NewGuid(), PolicyVersionId = v1.PolicyVersionId, StartTrigger = StartTrigger.Invoice, StartTimeZone = "UTC", DurationBasis = DurationBasis.Time, Transferable = Transferability.Yes,
                AssuranceOrService = AssuranceOrService.Service, Status = EntitlementStatus.Active, Confidence = "high", Provenance = EntitlementProvenance.Created, CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var inUse = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("service_level", "Service level", false, AssuranceOrService.Service, Active: false)));
        Assert.Equal("family_in_use", inUse.Rule);
        Assert.Equal("family_code_invalid", (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("Accidental Damage", "x", true, AssuranceOrService.Service)))).Rule);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("accidental_damage", "Accidental damage", true, AssuranceOrService.Service));

        using var read = fixture.ScopeFor(tenant);
        var queries = read.ServiceProvider.GetRequiredService<PolicyQueries>();
        Assert.Equal(["accidental_damage", "base_warranty", "service_level"], (await queries.FamiliesAsync(CancellationToken.None)).Select(f => f.FamilyCode));
        Assert.Equal("reworded", Assert.Single(await queries.ReplacementPoliciesAsync(CancellationToken.None)).Description);
        Assert.Equal(1, Assert.Single(await queries.VersionsAsync("VX220-STD-24", CancellationToken.None)).LiveEntitlements);
    }
}

/// <summary>The reference terms shared with the unit tests (the database tests do not reference the unit-test project).</summary>
internal static class PolicyValidatorTestsTerms
{
    public static PolicyTerms Standard24() => new(
        new DurationRule(24, DurationRule.Months), DurationBasis.Time, [],
        new StartRule(StartTrigger.Deliver, [new StartFallback(StartTrigger.Invoice, 14), new StartFallback(StartTrigger.Ship, 21)], 90),
        new CoverageTerms(100, 100, 0, "return_to_base"), ["misuse", "consumables"], ["EU", "UK", "CH"], Transferability.Yes, "full", "STD-90",
        [new RefurbishedTerm(null, "A", "direct", "EU", 12, null), new RefurbishedTerm(null, "C", "portal_consumer", "EU", 6, 12)], 30, AssuranceOrService.Assurance, PolicyTerms.TimeZoneCustomerSite);
}
