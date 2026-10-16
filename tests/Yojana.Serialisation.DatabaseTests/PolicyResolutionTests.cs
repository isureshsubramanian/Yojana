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
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P2-T04: the policy stack stops at the first match, chooses versions by contract date (case 114), breaks contract ties by priority then start, and reports a sale outside the region.</summary>
[Collection("serialisation host")]
public sealed class PolicyResolutionTests(SerialisationHostFixture fixture)
{
    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<Guid> PublishAsync(TenantId tenant, string code, DateOnly from, PolicyTerms? terms = null)
    {
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, "base_warranty", EntitlementType.Manufacturer, from, terms ?? PolicyValidatorTestsTerms.Standard24(), "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        return draft.PolicyVersionId;
    }

    private async Task<PolicyResolution> ResolveAsync(TenantId tenant, PolicyQuestion question)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<PolicyResolver>().ResolveAsync(question, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task The_stack_runs_unit_then_parent_then_contract_then_product_and_versions_go_by_contract_date()
    {
        var tenant = await fixture.NewTenantAsync("pr");
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var code = "PR-" + Guid.NewGuid().ToString("N")[..6];
        var v1 = await PublishAsync(tenant, code, new DateOnly(2025, 1, 1));
        var draft2 = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, "base_warranty", EntitlementType.Manufacturer, new DateOnly(2026, 7, 31), null, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft2.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        var owner = Guid.NewGuid();
        var unit = Guid.NewGuid();
        Ports.Register(new ItemWarrantyPolicy(item, "base_warranty", code, true));

        // level 4, by contract date: the day before v2 applies is still v1; an unknown family resolves to none
        var before = await ResolveAsync(tenant, new PolicyQuestion(unit, item, "base_warranty", new DateOnly(2026, 7, 30), owner, Country: "DE"));
        Assert.Equal((PolicyLevel.ProductPolicy, (Guid?)v1, (int?)1, false), (before.Level, before.PolicyVersionId, before.Version, before.OutOfRegion));
        var after = await ResolveAsync(tenant, new PolicyQuestion(unit, item, "base_warranty", new DateOnly(2026, 7, 31), owner, Country: "DE"));
        Assert.Equal((PolicyLevel.ProductPolicy, (Guid?)draft2.PolicyVersionId, (int?)2), (after.Level, after.PolicyVersionId, after.Version));
        var tooEarly = await ResolveAsync(tenant, new PolicyQuestion(unit, item, "base_warranty", new DateOnly(2024, 6, 1), owner));
        Assert.Equal(PolicyLevel.None, tooEarly.Level);
        Assert.Contains(tooEarly.Trail, t => t.Contains("no published version", StringComparison.Ordinal));
        Assert.Equal(PolicyLevel.None, (await ResolveAsync(tenant, new PolicyQuestion(unit, item, "service_level", new DateOnly(2026, 9, 2), owner))).Level);

        // a sale outside the policy's regions is reported, not skipped
        var abroad = await ResolveAsync(tenant, new PolicyQuestion(unit, item, "base_warranty", new DateOnly(2026, 9, 2), owner, Country: "US"));
        Assert.True(abroad.OutOfRegion);

        // level 3: two contracts; the higher priority wins, then the later start
        var contractCode = "CT-" + Guid.NewGuid().ToString("N")[..6];
        var contractVersion = await PublishAsync(tenant, contractCode, new DateOnly(2026, 1, 1));
        Ports.Register(item, new CustomerContractTerm("FRAME-2026-A", owner, "base_warranty", code, 1, new DateOnly(2026, 1, 1), null));
        Ports.Register(item, new CustomerContractTerm("FRAME-2026-B", owner, "base_warranty", contractCode, 5, new DateOnly(2026, 3, 1), null));
        var contract = await ResolveAsync(tenant, new PolicyQuestion(unit, item, "base_warranty", new DateOnly(2026, 9, 2), owner));
        Assert.Equal((PolicyLevel.CustomerContract, "FRAME-2026-B", (Guid?)contractVersion), (contract.Level, contract.ContractRef, contract.PolicyVersionId));
        Assert.Equal(PolicyLevel.ProductPolicy, (await ResolveAsync(tenant, new PolicyQuestion(unit, item, "base_warranty", new DateOnly(2026, 9, 2), Guid.NewGuid()))).Level);

        // level 2: a kit entitlement on the parent covers the installed child; a unit-scoped one does not
        var parent = Guid.NewGuid();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.AssemblyLinks.Add(new AssemblyLink { TenantId = tenant, LinkId = TimeOrderedId.New(DateTime.UtcNow), ParentUnitId = parent, ChildUnitId = unit, Relation = AssemblyRelation.Component, InstalledAt = DateTime.UtcNow, InstallingEventId = Guid.NewGuid() });
            db.Entitlements.Add(Entitlement(tenant, parent, v1, EntitlementScope.Unit));
            await db.SaveChangesAsync();
        }

        Assert.Equal(PolicyLevel.CustomerContract, (await ResolveAsync(tenant, new PolicyQuestion(unit, item, "base_warranty", new DateOnly(2026, 9, 2), owner))).Level);
        Guid kit;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var unitScoped = await db.Entitlements.SingleAsync(e => e.UnitId == parent);
            unitScoped.Status = EntitlementStatus.Closed;
            unitScoped.ClosedReason = EntitlementClosedReason.Superseded;
            await db.SaveChangesAsync();
            var kitScoped = Entitlement(tenant, parent, v1, EntitlementScope.Kit);
            kit = kitScoped.EntitlementId;
            db.Entitlements.Add(kitScoped);
            await db.SaveChangesAsync();
        }

        var viaParent = await ResolveAsync(tenant, new PolicyQuestion(unit, item, "base_warranty", new DateOnly(2026, 9, 2), owner));
        Assert.Equal((PolicyLevel.ParentEntitlement, (Guid?)kit), (viaParent.Level, viaParent.EntitlementId));

        // level 1: the unit's own entitlement wins over everything
        Guid own;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var mine = Entitlement(tenant, unit, draft2.PolicyVersionId, EntitlementScope.Unit);
            own = mine.EntitlementId;
            db.Entitlements.Add(mine);
            await db.SaveChangesAsync();
        }

        var first = await ResolveAsync(tenant, new PolicyQuestion(unit, item, "base_warranty", new DateOnly(2026, 9, 2), owner, Country: "DE"));
        Assert.Equal((PolicyLevel.UnitEntitlement, (Guid?)own, (int?)2, false), (first.Level, first.EntitlementId, first.Version, first.OutOfRegion));
        Assert.Single(first.Trail);
    }

    private static Entitlement Entitlement(TenantId tenant, Guid unit, Guid policyVersion, string scope) => new()
    {
        TenantId = tenant, EntitlementId = TimeOrderedId.New(DateTime.UtcNow), UnitId = unit, Scope = scope, Type = EntitlementType.Manufacturer, CoverageFamily = "base_warranty", Direction = EntitlementDirection.Outbound,
        ObligorEntityId = Guid.NewGuid(), PolicyVersionId = policyVersion, StartTrigger = StartTrigger.Deliver, StartDate = new DateOnly(2026, 9, 2), StartTimeZone = "Europe/Berlin", EndDate = new DateOnly(2028, 9, 1),
        DurationValue = 24, DurationUnit = DurationRule.Months, EndOfMonthRule = DurationRule.Clamp, DurationBasis = DurationBasis.Time, RegionScope = """["EU","UK","CH"]""", Transferable = Transferability.Yes, AssuranceOrService = AssuranceOrService.Assurance, Status = EntitlementStatus.Active, Confidence = "high",
        Provenance = EntitlementProvenance.Created, CreatedAt = DateTime.UtcNow,
    };
}
