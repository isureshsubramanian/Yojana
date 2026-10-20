using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Security.Audit;
using Wolverine;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ports;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class OnboardingChecklistTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Connector_profile_seeds_the_disclosure_and_the_integration_user_item()
    {
        var tenant = await fixture.NewTenantAsync("onbprof");
        var cx01 = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var generic = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), "CX02");
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>()
            .SeedGenericEntriesAsync(CancellationToken.None);
        var generator = scope.ServiceProvider.GetRequiredService<ConnectorOnboardingChecklistGenerator>();

        var specific = await generator.GenerateAsync(cx01, CancellationToken.None);
        Assert.Contains("indirect-access licence", specific.CustomerCostDisclosure, StringComparison.Ordinal);
        var user = Assert.Single(specific.Items, x => x.Code == OnboardingItemCode.IntegrationUser);
        Assert.Contains("named user licence", user.Detail, StringComparison.Ordinal);
        Assert.Equal("Integration user code as created in the ERP", user.EvidencePrompt);
        Assert.Equal(specific.CustomerCostDisclosure,
            Assert.Single(specific.Items, x => x.Code == OnboardingItemCode.Costs).Detail);
        Assert.All(specific.Items.Where(x => x.ManualConfirmation),
            x => Assert.Equal(OnboardingItemStatus.Pending, x.Status));
        Assert.Equal(OnboardingItemStatus.Pass,
            Assert.Single(specific.Items, x => x.Code == OnboardingItemCode.AddOnWarrantyItems).Status);
        Assert.False(specific.RequiredItemsConfirmed);

        var fallback = await generator.GenerateAsync(generic, CancellationToken.None);
        Assert.Equal(GenericConnectorOnboardingProfile.Instance.CustomerCostDisclosure,
            fallback.CustomerCostDisclosure);
        Assert.Equal(GenericConnectorOnboardingProfile.Instance.IntegrationUser.Label,
            Assert.Single(fallback.Items, x => x.Code == OnboardingItemCode.IntegrationUser).Label);
    }

    [DatabaseFact]
    public async Task Add_on_item_check_passes_and_fails_with_policy_and_item_names()
    {
        var tenant = await fixture.NewTenantAsync("onbaddon");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>()
            .SeedGenericEntriesAsync(CancellationToken.None);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var extended = "FIC-EXT-" + suffix;
        var damage = "FIC-DMG-" + suffix;
        await PublishPolicy(tenant, extended, "Extended server cover", "extended_term", "Extended term");
        await PublishPolicy(tenant, damage, "Accidental server cover", "accidental_damage", "Accidental damage");
        var mappings = fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();
        mappings.Register(new WarrantyPolicyItem(Guid.NewGuid(), "NBD-3Y", "Three-year next-business-day service",
            extended, true, WarrantyTargetRule.ReferencedDocument));
        var damageItem = Guid.NewGuid();
        mappings.Register(new WarrantyPolicyItem(damageItem, "ADP-3Y", "Three-year accidental damage",
            damage, false, null));

        var checklist = await scope.ServiceProvider.GetRequiredService<ConnectorOnboardingChecklistGenerator>()
            .GenerateAsync(connection, default);
        var failed = Assert.Single(checklist.Items, item => item.Code == OnboardingItemCode.AddOnWarrantyItems);
        Assert.Equal(OnboardingItemStatus.Fail, failed.Status);
        Assert.False(failed.ManualConfirmation);
        Assert.Contains("Accidental server cover", failed.Detail);
        Assert.Contains("ADP-3Y · Three-year accidental damage", failed.Detail);
        Assert.DoesNotContain("NBD-3Y", failed.Detail);
        Assert.DoesNotContain(damageItem.ToString(), failed.Detail, StringComparison.OrdinalIgnoreCase);

        mappings.Register(new WarrantyPolicyItem(damageItem, "ADP-3Y-STACK", "Stacked accidental damage",
            damage, true, WarrantyTargetRule.SameOrder));
        checklist = await scope.ServiceProvider.GetRequiredService<ConnectorOnboardingChecklistGenerator>()
            .GenerateAsync(connection, default);
        var passed = Assert.Single(checklist.Items, item => item.Code == OnboardingItemCode.AddOnWarrantyItems);
        Assert.Equal(OnboardingItemStatus.Pass, passed.Status);
        Assert.Contains("2 checked", passed.Detail);

        mappings.Register(new WarrantyPolicyItem(Guid.NewGuid(), "NBD-LEGACY", "Legacy service item",
            extended, false, null));
        checklist = await scope.ServiceProvider.GetRequiredService<ConnectorOnboardingChecklistGenerator>()
            .GenerateAsync(connection, default);
        var mixed = Assert.Single(checklist.Items, item => item.Code == OnboardingItemCode.AddOnWarrantyItems);
        Assert.Equal(OnboardingItemStatus.Pass, mixed.Status);
        Assert.Contains("NBD-LEGACY · Legacy service item (not ready)", mixed.Detail);

        var other = await fixture.NewTenantAsync("onbaddiso");
        var otherConnection = await fixture.AddConnectionAsync(other, Guid.NewGuid());
        using var otherScope = fixture.ScopeFor(other);
        await otherScope.ServiceProvider.GetRequiredService<ConnectorCatalogue>().SeedGenericEntriesAsync(default);
        var isolated = Assert.Single((await otherScope.ServiceProvider
            .GetRequiredService<ConnectorOnboardingChecklistGenerator>().GenerateAsync(otherConnection, default)).Items,
            item => item.Code == OnboardingItemCode.AddOnWarrantyItems);
        Assert.Equal(OnboardingItemStatus.Pass, isolated.Status);
        Assert.DoesNotContain("Extended server cover", isolated.Detail);
    }

    private async Task PublishPolicy(TenantId tenant, string code, string name, string family, string familyName)
    {
        await Invoke<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(family, familyName, true, AssuranceOrService.Service));
        await Invoke<ReplacementPolicyRow>(tenant,
            new DefineReplacementPolicy("RP-" + code, ReplacementRule.MaxRemainingOrMinDays, 30, null, null));
        var terms = new PolicyTerms(new(12, DurationRule.Months), DurationBasis.Time, [],
            new(StartTrigger.PlanPurchase, [], null), new(100, 100, 100, "return_to_base"), [], ["WORLD"],
            Transferability.Yes, "full", "RP-" + code, [], null, AssuranceOrService.Service,
            PolicyTerms.TimeZoneUtc);
        var draft = await Invoke<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, name,
            family, EntitlementType.Extended, new(2026, 1, 1), terms, "fictional-operator"));
        await Invoke<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "fictional-operator"));
    }

    private async Task<T> Invoke<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Confirmation_needs_evidence_is_audited_and_goes_stale_when_the_disclosure_changes()
    {
        var tenant = await fixture.NewTenantAsync("onbconf");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>()
            .SeedGenericEntriesAsync(CancellationToken.None);
        var confirmations = scope.ServiceProvider.GetRequiredService<ConnectorOnboardingConfirmations>();

        var missing = await Assert.ThrowsAsync<OnboardingRuleException>(() => confirmations.ConfirmAsync(
            connection, OnboardingItemCode.Costs, new("  "), "user-1", CancellationToken.None));
        Assert.Equal("onboarding_evidence_required", missing.Rule);
        var unknown = await Assert.ThrowsAsync<OnboardingRuleException>(() => confirmations.ConfirmAsync(
            connection, "no-such-item", new("x"), "user-1", CancellationToken.None));
        Assert.Equal("onboarding_item_not_found", unknown.Rule);

        var checklist = await confirmations.ConfirmAsync(connection, OnboardingItemCode.Costs,
            new(" Purchasing lead, partner quote Q-1042 "), "user-1", CancellationToken.None);
        var costs = Assert.Single(checklist.Items, x => x.Code == OnboardingItemCode.Costs);
        Assert.Equal(OnboardingItemStatus.Confirmed, costs.Status);
        Assert.Equal("Purchasing lead, partner quote Q-1042", costs.Evidence);
        Assert.Equal("user-1", costs.ConfirmedBy);
        Assert.Equal(fixture.Clock.UtcNow, costs.ConfirmedAt!.Value, TimeSpan.FromMilliseconds(1));
        checklist = await confirmations.ConfirmAsync(connection, OnboardingItemCode.Poll, new(null), "user-1",
            CancellationToken.None);
        Assert.Equal(OnboardingItemStatus.Confirmed,
            Assert.Single(checklist.Items, x => x.Code == OnboardingItemCode.Poll).Status);

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var audited = await db.ConnectionEvents.AsNoTracking().Where(x => x.ConnectionId == connection
            && x.EventType == nameof(ConnectionAuditEventType.OnboardingItemConfirmed)).ToListAsync();
        Assert.Equal(2, audited.Count);
        Assert.Contains(audited, x => x.DetailsJson.Contains("Q-1042", StringComparison.Ordinal));

        var entry = await db.ConnectorCatalogue.SingleAsync(x => x.ConnectorCode == "CX01");
        entry.CustomerCostDisclosure += " Marketplace listing fees apply.";
        await db.SaveChangesAsync();
        checklist = await scope.ServiceProvider.GetRequiredService<ConnectorOnboardingChecklistGenerator>()
            .GenerateAsync(connection, CancellationToken.None);
        Assert.Equal(OnboardingItemStatus.Stale,
            Assert.Single(checklist.Items, x => x.Code == OnboardingItemCode.Costs).Status);
        Assert.Equal(OnboardingItemStatus.Confirmed,
            Assert.Single(checklist.Items, x => x.Code == OnboardingItemCode.Poll).Status);
    }

    [DatabaseFact]
    public async Task Required_items_confirmed_only_when_every_required_item_is_confirmed()
    {
        var tenant = await fixture.NewTenantAsync("onball");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>()
            .SeedGenericEntriesAsync(CancellationToken.None);
        var confirmations = scope.ServiceProvider.GetRequiredService<ConnectorOnboardingConfirmations>();
        var checklist = await scope.ServiceProvider.GetRequiredService<ConnectorOnboardingChecklistGenerator>()
            .GenerateAsync(connection, CancellationToken.None);
        foreach (var item in checklist.Items.Where(x => x.Required && x.ManualConfirmation))
        {
            Assert.False(checklist.RequiredItemsConfirmed);
            checklist = await confirmations.ConfirmAsync(connection, item.Code,
                new(item.EvidencePrompt is null ? null : "INTEGRATION01"), "user-2", CancellationToken.None);
        }
        Assert.True(checklist.RequiredItemsConfirmed);
    }

    [DatabaseFact]
    public async Task Archived_connection_cannot_be_confirmed_and_other_tenants_see_nothing()
    {
        var tenant = await fixture.NewTenantAsync("onbarch");
        var other = await fixture.NewTenantAsync("onbother");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using (var scope = fixture.ScopeFor(tenant))
        {
            await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>()
                .SeedGenericEntriesAsync(CancellationToken.None);
            var confirmations = scope.ServiceProvider.GetRequiredService<ConnectorOnboardingConfirmations>();
            await confirmations.ConfirmAsync(connection, OnboardingItemCode.Poll, new(null), "user-3",
                CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var row = await db.Connections.SingleAsync(x => x.ConnectionId == connection);
            row.Status = ConnectionStatus.Archived;
            await db.SaveChangesAsync();
            var archived = await Assert.ThrowsAsync<OnboardingRuleException>(() => confirmations.ConfirmAsync(
                connection, OnboardingItemCode.Credentials, new(null), "user-3", CancellationToken.None));
            Assert.Equal("onboarding_connection_archived", archived.Rule);
        }

        using var otherScope = fixture.ScopeFor(other);
        var otherDb = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Empty(await otherDb.ConnectionOnboardingConfirmations.IgnoreQueryFilters().ToListAsync());
        var notFound = await Assert.ThrowsAsync<OnboardingRuleException>(() => otherScope.ServiceProvider
            .GetRequiredService<ConnectorOnboardingChecklistGenerator>().GenerateAsync(connection, CancellationToken.None));
        Assert.Equal("onboarding_connection_not_found", notFound.Rule);
    }
}
