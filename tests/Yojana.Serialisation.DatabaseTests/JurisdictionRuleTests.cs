using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Migrations;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Jurisdictions;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Replacements;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P6-T15: jurisdiction values are tenant data; these tests exercise the resolver and starter import boundary.</summary>
[Collection("serialisation host")]
public sealed class JurisdictionRuleTests(SerialisationHostFixture fixture)
{
    private static readonly DateOnly September = new(2026, 9, 1);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<IReadOnlyList<JurisdictionRuleResolution>> ResolveAsync(TenantId tenant, string kind,
        JurisdictionRuleContext context)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IJurisdictionRuleResolver>()
            .ResolveAsync(kind, context, CancellationToken.None);
    }

    private Task<JurisdictionRuleSaved> SaveAsync(TenantId tenant, string kind, string scope, JsonElement value,
        string buyer = JurisdictionBuyerClass.Any, string? country = null, string? subdivision = null,
        Guid? group = null, decimal? minimum = null, decimal? maximum = null, string? currency = null,
        string dateTested = JurisdictionDateTested.Contract, DateOnly? from = null, DateOnly? to = null,
        string? itemClass = null) => InvokeAsync<JurisdictionRuleSaved>(tenant, new SaveJurisdictionRule(
            Guid.NewGuid(), null, kind, scope, country, subdivision, group, buyer, itemClass, minimum, maximum,
            currency, dateTested, from ?? new DateOnly(2026, 1, 1), to, value, "test statute", null, true,
            new DateOnly(2026, 8, 1), ActorRef: "legal.admin"));

    [DatabaseFact]
    public async Task Resolution_obeys_scope_buyer_item_price_and_effective_date_precedence()
    {
        var tenant = await fixture.NewTenantAsync("juris-priority", importJurisdictionStarter: false);
        var group = await InvokeAsync<RegionGroupSaved>(tenant, new SaveRegionGroup(Guid.NewGuid(), null, "MARKET-A",
            "Market A", ["DE", "FR"], ActorRef: "legal.admin"));
        await SaveAsync(tenant, JurisdictionRuleKind.ShortTermRejectWindow, JurisdictionScope.RegionGroup,
            Json("10"), group: group.RegionGroupId);
        await SaveAsync(tenant, JurisdictionRuleKind.ShortTermRejectWindow, JurisdictionScope.Country,
            Json("20"), country: "DE");
        await SaveAsync(tenant, JurisdictionRuleKind.ShortTermRejectWindow, JurisdictionScope.Subdivision,
            Json("30"), subdivision: "DE-BY");
        await SaveAsync(tenant, JurisdictionRuleKind.RefurbishedReplacementRequiresConsent, JurisdictionScope.Country,
            Json("\"explicit_consumer_request\""), JurisdictionBuyerClass.Consumer, "DE", minimum: 100m, maximum: 500m, currency: "EUR",
            itemClass: "handheld");

        var context = Context("DE", "DE-BY", JurisdictionBuyerClass.Consumer, "handheld", 300m, "EUR", September);
        var scoped = Assert.Single(await ResolveAsync(tenant, JurisdictionRuleKind.ShortTermRejectWindow, context));
        Assert.Equal((30, 3, JurisdictionScope.Subdivision), (scoped.Value.GetInt32(), scoped.Precedence, scoped.Scope));
        Assert.Single(await ResolveAsync(tenant, JurisdictionRuleKind.RefurbishedReplacementRequiresConsent, context));
        Assert.Empty(await ResolveAsync(tenant, JurisdictionRuleKind.RefurbishedReplacementRequiresConsent,
            context with { BuyerClass = JurisdictionBuyerClass.Business }));
        Assert.Empty(await ResolveAsync(tenant, JurisdictionRuleKind.RefurbishedReplacementRequiresConsent,
            context with { Price = 501m }));
        Assert.Empty(await ResolveAsync(tenant, JurisdictionRuleKind.RefurbishedReplacementRequiresConsent,
            context with { ItemClass = "systems" }));
        Assert.Empty(await ResolveAsync(tenant, JurisdictionRuleKind.ShortTermRejectWindow,
            context with { ContractDate = new DateOnly(2025, 12, 31) }));
    }

    [DatabaseFact]
    public async Task Each_rule_tests_its_declared_business_date()
    {
        var tenant = await fixture.NewTenantAsync("juris-dates", importJurisdictionStarter: false);
        await SaveAsync(tenant, JurisdictionRuleKind.PresumptionPeriod, JurisdictionScope.Country, Json("12"),
            country: "DE", dateTested: JurisdictionDateTested.Delivery, from: new DateOnly(2026, 8, 1));
        var context = Context("DE", null, JurisdictionBuyerClass.Consumer, null, null, null,
            new DateOnly(2026, 1, 1)) with { DeliveryDate = September };
        var rule = Assert.Single(await ResolveAsync(tenant, JurisdictionRuleKind.PresumptionPeriod, context));
        Assert.Equal((JurisdictionDateTested.Delivery, September), (rule.DateTested, rule.TestedDate));
        Assert.Empty(await ResolveAsync(tenant, JurisdictionRuleKind.PresumptionPeriod,
            context with { DeliveryDate = new DateOnly(2026, 7, 31) }));
    }

    [DatabaseFact]
    public async Task Saving_an_ambiguous_overlap_is_refused_before_any_version_is_applied()
    {
        var tenant = await fixture.NewTenantAsync("juris-overlap", importJurisdictionStarter: false);
        await SaveAsync(tenant, JurisdictionRuleKind.RepairExtensionMonths, JurisdictionScope.Country, Json("12"),
            JurisdictionBuyerClass.Consumer, "DE", from: new DateOnly(2026, 7, 31));
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => SaveAsync(tenant,
            JurisdictionRuleKind.RepairExtensionMonths, JurisdictionScope.Country, Json("6"),
            JurisdictionBuyerClass.Consumer, "DE", from: September));
        Assert.Equal("jurisdiction_rule_overlap", refusal.Rule);
    }

    [DatabaseFact]
    public async Task Eu_starter_reproduces_extension_and_consent_while_an_empty_tenant_is_neutral()
    {
        var configured = await fixture.NewTenantAsync("juris-starter", importJurisdictionStarter: false);
        Guid packId;
        using (var scope = fixture.ScopeFor(configured))
        {
            var service = scope.ServiceProvider.GetRequiredService<JurisdictionCatalogueService>();
            var pack = Assert.Single((await service.CatalogueAsync(null, null, CancellationToken.None)).RulePacks);
            Assert.Contains("confirm with your legal adviser", pack.Name, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(4, pack.RuleCount);
            packId = pack.RulePackId;
        }
        var imported = await InvokeAsync<RulePackImported>(configured, new ImportRulePack(Guid.NewGuid(), packId,
            "tenant.admin"));
        Assert.Equal((4, 4), (imported.RegionGroupsAdded, imported.RulesAdded));
        using (var scope = fixture.ScopeFor(configured))
        {
            var catalogue = await scope.ServiceProvider.GetRequiredService<JurisdictionCatalogueService>()
                .CatalogueAsync(null, null, CancellationToken.None);
            Assert.Equal(4, Assert.Single(catalogue.RulePacks).RuleCount);
            Assert.Equal(4, catalogue.Rules.Count);
        }
        var context = Context("DE", null, JurisdictionBuyerClass.Consumer, null, null, null, September);
        Assert.Equal(12, Assert.Single(await ResolveAsync(configured,
            JurisdictionRuleKind.RepairExtensionMonths, context)).Value.GetInt32());
        Assert.Equal(1, Assert.Single(await ResolveAsync(configured,
            JurisdictionRuleKind.RepairExtensionCount, context)).Value.GetInt32());
        using (var scope = fixture.ScopeFor(configured))
        {
            Assert.True(await WarrantyReplacementRules.RequiresConsentAsync(
                scope.ServiceProvider.GetRequiredService<IJurisdictionRuleResolver>(), "DE", September,
                CancellationToken.None));
        }

        var neutral = await fixture.NewTenantAsync("juris-neutral", importJurisdictionStarter: false);
        Assert.Empty(await ResolveAsync(neutral, JurisdictionRuleKind.RepairExtensionMonths, context));
        using var neutralScope = fixture.ScopeFor(neutral);
        Assert.False(await WarrantyReplacementRules.RequiresConsentAsync(
            neutralScope.ServiceProvider.GetRequiredService<IJurisdictionRuleResolver>(), "DE", September,
            CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Starter_groups_preserve_legacy_scope_tokens_and_world_is_universal()
    {
        var tenant = await fixture.NewTenantAsync("juris-groups");
        using var scope = fixture.ScopeFor(tenant);
        var catalogue = await scope.ServiceProvider.GetRequiredService<JurisdictionCatalogueService>()
            .CatalogueAsync(null, null, CancellationToken.None);
        Assert.Equal(["EEA", "EFTA", "EU", "UK"], catalogue.RegionGroups.Select(group => group.Code).Order());
        Assert.All(catalogue.RegionGroups, group => Assert.Contains("starter — confirm", group.Name,
            StringComparison.OrdinalIgnoreCase));

        var regions = scope.ServiceProvider.GetRequiredService<IJurisdictionRegionPort>();
        Assert.True(await regions.MatchesAsync("EEA", "NO", null, CancellationToken.None));
        Assert.True(await regions.MatchesAsync("EFTA", "CH", null, CancellationToken.None));
        Assert.True(await regions.MatchesAsync("UK", "GB", null, CancellationToken.None));
        Assert.True(await regions.MatchesAsync("WORLD", "US", null, CancellationToken.None));
        Assert.False(await regions.MatchesAsync("UNKNOWN-GROUP", "NO", null, CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Policy_save_refuses_unknown_scope_tokens_with_the_configuration_step()
    {
        var tenant = await fixture.NewTenantAsync("juris-policy", importJurisdictionStarter: false);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base,
            "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90",
            ReplacementRule.MaxRemainingOrMinDays, 90, null, "Minimum 90 days"));
        var standard = PolicyValidatorTestsTerms.Standard24() with { RegionScope = ["WORLD"], Refurbished = [] };

        var scopeRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "UNKNOWN-SCOPE",
                "Unknown scope", CoverageFamilyCode.Base, EntitlementType.Manufacturer, September,
                standard with { RegionScope = ["UNKNOWN-GROUP"] }, "tenant.admin")));
        Assert.Equal("policy_region_unknown", scopeRefusal.Rule);
        Assert.Contains("UNKNOWN-GROUP", scopeRefusal.Message, StringComparison.Ordinal);
        Assert.Contains("Settings → Jurisdictions", scopeRefusal.UnblockingStep, StringComparison.Ordinal);

        var rowRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "UNKNOWN-ROW",
                "Unknown refurbished row", CoverageFamilyCode.Base, EntitlementType.Manufacturer, September,
                standard with { Refurbished = [new RefurbishedTerm(null, "B", "direct", "UNKNOWN-GROUP", 6, null)] },
                "tenant.admin")));
        Assert.Equal("policy_region_unknown", rowRefusal.Rule);
        Assert.Contains("UNKNOWN-GROUP", rowRefusal.Message, StringComparison.Ordinal);

        using var settingsScope = fixture.ScopeFor(tenant);
        var settings = settingsScope.ServiceProvider.GetRequiredService<RefurbishedGoodsSettings>();
        var floorRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
            new RefurbishedGoodsPolicy([new RefurbishedJurisdictionFloor("UNKNOWN-GROUP",
                RefurbishedBuyerKind.Consumer, 12)]), CancellationToken.None));
        Assert.Equal("refurbished_region_unknown", floorRefusal.Rule);
    }

    [DatabaseFact]
    public async Task Migration_backfill_keeps_eea_and_uk_policies_covering_norway_and_great_britain()
    {
        var tenant = await fixture.NewTenantAsync("juris-migrate", importJurisdictionStarter: false);
        await InvokeAsync<RegionGroupSaved>(tenant, new SaveRegionGroup(Guid.NewGuid(), null, "EEA", "EEA before migration",
            ["NO"], ActorRef: "tenant.admin"));
        await InvokeAsync<RegionGroupSaved>(tenant, new SaveRegionGroup(Guid.NewGuid(), null, "UK", "UK before migration",
            ["GB"], ActorRef: "tenant.admin"));
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base,
            "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90",
            ReplacementRule.MaxRemainingOrMinDays, 90, null, "Minimum 90 days"));
        var code = "BACKFILL-" + Guid.NewGuid().ToString("N")[..6];
        var terms = PolicyValidatorTestsTerms.Standard24() with { RegionScope = ["EEA", "UK"], Refurbished = [] };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2026, 1, 1), terms, "tenant.admin"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "tenant.admin"));

        using (var removeScope = fixture.ScopeFor(tenant))
        {
            var db = removeScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.RegionGroups.RemoveRange(await db.RegionGroups.ToListAsync());
            await db.SaveChangesAsync();
        }

        var migration = new TestableP6Rules();
        var builder = new MigrationBuilder("Microsoft.EntityFrameworkCore.SqlServer");
        migration.BuildUp(builder);
        var statements = builder.Operations.OfType<SqlOperation>().ToList();
        var insert = statements.FindIndex(operation => operation.Sql.Contains("migration:legacy-region-scopes",
            StringComparison.Ordinal));
        Assert.True(insert > 0);
        using (var migrationScope = fixture.ScopeFor(null))
        {
            var db = migrationScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            try
            {
                foreach (var operation in statements.Skip(insert - 1).Take(3))
                    await db.Database.ExecuteSqlRawAsync(operation.Sql);
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER SECURITY POLICY [sec].[tenant_policy_serialisation] WITH (STATE = ON);");
            }
        }

        using (var verifyScope = fixture.ScopeFor(tenant))
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(["EEA", "EFTA", "EU", "UK"], await db.RegionGroups.OrderBy(group => group.Code)
                .Select(group => group.Code).ToListAsync());
        }

        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryWarrantyPorts>()
            .Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, code, true));
        using var resolveScope = fixture.ScopeFor(tenant);
        var resolver = resolveScope.ServiceProvider.GetRequiredService<PolicyResolver>();
        var norway = await resolver.ResolveAsync(new PolicyQuestion(Guid.NewGuid(), item, CoverageFamilyCode.Base,
            September, Country: "NO"), CancellationToken.None);
        var greatBritain = await resolver.ResolveAsync(new PolicyQuestion(Guid.NewGuid(), item, CoverageFamilyCode.Base,
            September, Country: "GB"), CancellationToken.None);
        Assert.False(norway.OutOfRegion);
        Assert.False(greatBritain.OutOfRegion);
    }

    private static JurisdictionRuleContext Context(string country, string? subdivision, string buyer, string? itemClass,
        decimal? price, string? currency, DateOnly contractDate) => new(country, subdivision, buyer, itemClass, price,
            currency, contractDate, contractDate, contractDate, contractDate);

    private static JsonElement Json(string value) => JsonDocument.Parse(value).RootElement.Clone();

    private sealed class TestableP6Rules : P6Rules
    {
        public void BuildUp(MigrationBuilder builder) => base.Up(builder);
    }
}
