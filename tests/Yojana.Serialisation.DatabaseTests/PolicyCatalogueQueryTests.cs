using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class PolicyCatalogueQueryTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Migration_catalogue_returns_families_and_only_published_immutable_versions()
    {
        var tenant = await fixture.NewTenantAsync("policycat", importJurisdictionStarter: false);
        var publishedId = Guid.CreateVersion7();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.CoverageFamilies.AddRange(
            Family(tenant, "base", "Base warranty", false),
            Family(tenant, "service_level", "Service level", true));
        db.PolicyVersions.AddRange(
            Policy(tenant, publishedId, "BASE-36", 1, PolicyVersionStatus.Published),
            Policy(tenant, Guid.CreateVersion7(), "BASE-36", 2, PolicyVersionStatus.Draft));
        await db.SaveChangesAsync();

        var catalogue = await scope.ServiceProvider.GetRequiredService<IPolicyCatalogueQuery>()
            .ReadAsync(default);

        Assert.Equal(["base", "service_level"], catalogue.Families.Select(row => row.FamilyCode));
        var policy = Assert.Single(catalogue.Policies);
        Assert.Equal(publishedId, policy.PolicyId);
        Assert.Equal(36, policy.Terms.DurationValue);
        Assert.Equal((100m, 100m, 0m),
            (policy.Terms.PartsPct, policy.Terms.LabourPct, policy.Terms.TravelPct));
    }

    private static CoverageFamily Family(Yojana.Platform.Contracts.Tenancy.TenantId tenant, string code,
        string name, bool stacking) => new()
    {
        TenantId = tenant, FamilyCode = code, Name = name, Stacking = stacking,
        AccountingTag = stacking ? AssuranceOrService.Service : AssuranceOrService.Assurance,
        CreatedAt = new DateTime(2026, 9, 1), LastModifiedAt = new DateTime(2026, 9, 1),
        LastModifiedBy = "test",
    };

    private static WarrantyPolicyVersion Policy(Yojana.Platform.Contracts.Tenancy.TenantId tenant, Guid id,
        string code, int version, string status) => new()
    {
        TenantId = tenant, PolicyVersionId = id, PolicyCode = code, Version = version,
        Name = "RS-1100 base 36 months", CoverageFamily = "base", EntitlementType = EntitlementType.Manufacturer,
        AppliesToContractsFrom = new DateOnly(2026, 1, 1), Status = status, RuleVersion = $"v{version}.d1",
        DurationValue = 36, DurationUnit = DurationRule.Months, EndOfMonthRule = DurationRule.Clamp,
        DurationBasis = DurationBasis.Time, StartTrigger = StartTrigger.Deliver,
        Coverage = JsonSerializer.Serialize(new CoverageTerms(100, 100, 0, "return_to_base"), LedgerJson.Options),
        Transferable = Transferability.Yes, SettlementFormula = "full",
        AssuranceOrService = AssuranceOrService.Assurance, ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
        CreatedAt = new DateTime(2026, 9, 1), CreatedBy = "test", LastModifiedAt = new DateTime(2026, 9, 1),
        LastModifiedBy = "test", PublishedAt = status == PolicyVersionStatus.Published
            ? new DateTime(2026, 9, 1) : null, PublishedBy = status == PolicyVersionStatus.Published ? "test" : null,
    };
}
