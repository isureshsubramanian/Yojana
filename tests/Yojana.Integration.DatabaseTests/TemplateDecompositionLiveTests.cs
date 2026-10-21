using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class TemplateDecompositionLiveTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-live-template-reviewer";

    [MigrationLiveFact]
    [Trait("Category", "MigrationLive")]
    public async Task Product_extraction_proposes_structured_template_decompositions()
    {
        var connectionString = Environment.GetEnvironmentVariable("YOJANA_MIGRATION_LIVE_SQL")!;
        var database = new SqlConnectionStringBuilder(connectionString).InitialCatalog;
        var tenant = await fixture.NewTenantAsync("tmpllive");
        using var scope = fixture.ScopeFor(tenant);
        var sources = scope.ServiceProvider.GetRequiredService<MigrationSourceService>();
        var batches = scope.ServiceProvider.GetRequiredService<MigrationBatchService>();
        var source = await sources.CreateAsync(new("Owner demo read-only source", "live-templates",
            MigrationSourceRuntime.SqlServer, MigrationSourceRoute.Separate, database,
            ConnectionString: connectionString), Actor, default);
        await sources.SetLicenceAsync(source.SourceId,
            new(true, "OWNER-DEMO-READ-ONLY-2026-10-21",
                "Owner confirmed read-only extraction from the isolated demo company."), Actor, default);

        var started = await batches.StartAsync(new(source.SourceId), Actor, default);
        Assert.Equal(MigrationBatchStatus.Queued, started.Status);
        Assert.Equal(1, await batches.RunPendingAsync(default));
        var extracted = await batches.DetailAsync(started.BatchId, default);
        Assert.NotNull(extracted);
        Assert.Equal(MigrationBatchStatus.Succeeded, extracted.Status);

        await SeedMinimalCatalogueAsync(tenant);
        var mappings = scope.ServiceProvider.GetRequiredService<TemplateMappingService>();
        var proposed = await mappings.ProposeAsync(new(), default);
        Assert.True(proposed.Requested > 0);
        Assert.Equal(proposed.Requested, proposed.Proposed);

        var patterns = new List<TemplatePatternView>();
        string? cursor = null;
        do
        {
            var page = await mappings.ListAsync(cursor, 100, default);
            patterns.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor is not null);

        Assert.NotEmpty(patterns);
        Assert.Equal(proposed.Requested, patterns.Sum(pattern => pattern.TemplateCount));
        Assert.All(patterns, pattern =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pattern.PatternKey));
            Assert.False(string.IsNullOrWhiteSpace(pattern.PatternLabel));
            Assert.NotEmpty(pattern.Templates);
            Assert.Equal(pattern.TemplateCount, pattern.Templates.Count);
        });

        foreach (var pattern in patterns.OrderBy(item => item.PatternLabel, StringComparer.Ordinal))
        foreach (var summary in pattern.Templates.OrderBy(item => item.TemplateName, StringComparer.Ordinal))
        {
            var detail = await mappings.DetailAsync(summary.TemplateKey, default);
            Assert.NotNull(detail);
            Assert.Equal(pattern.PatternKey, detail.PatternKey);
            Assert.Equal(summary.Usage, detail.Usage);
            Assert.NotEmpty(detail.Blocks);
            Assert.All(detail.Blocks, block =>
            {
                Assert.False(string.IsNullOrWhiteSpace(block.FamilyCode));
                Assert.Contains(block.Resolution,
                    new[] { TemplateBlockResolution.ExistingPolicy, TemplateBlockResolution.PolicyToCreate });
                if (block.Resolution == TemplateBlockResolution.ExistingPolicy)
                {
                    Assert.NotNull(block.PolicyId);
                    Assert.False(string.IsNullOrWhiteSpace(block.PolicyCode));
                }
            });

            Console.WriteLine("TEMPLATE {0}", detail.TemplateName);
            Console.WriteLine("  TERMS {0}", JsonSerializer.Serialize(detail.SourceTerms));
            foreach (var block in detail.Blocks)
                Console.WriteLine("  BLOCK family={0}; resolution={1}; policy={2}", block.FamilyCode,
                    block.Resolution, block.PolicyCode ?? "create");
            Console.WriteLine("  REVIEW needs_person={0}; reason={1}", detail.NeedsPerson,
                detail.ReviewReason ?? "none");
            Console.WriteLine("  PATTERN key={0}; label={1}; grouped_templates={2}",
                detail.PatternKey, detail.PatternLabel, pattern.TemplateCount);
            Console.WriteLine("  USAGE contracts={0}; serials={1}; active_contracts={2}; active_serials={3}",
                detail.Usage.Contracts, detail.Usage.Serials, detail.Usage.ActiveContracts,
                detail.Usage.ActiveSerials);
        }
    }

    private async Task SeedMinimalCatalogueAsync(Yojana.Platform.Contracts.Tenancy.TenantId tenant)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.CoverageFamilies.AddRange(
            Family(tenant, "base", "Base warranty", false),
            Family(tenant, "service_level", "Service level", true));
        db.PolicyVersions.AddRange(
            Policy(tenant, "base", "BASE-24", "Base 24 months", "return_to_base", null),
            Policy(tenant, "service_level", "SERVICE-1D-24", "One-day response 24 months", "on_site", 24));
        await db.SaveChangesAsync();
    }

    private CoverageFamily Family(Yojana.Platform.Contracts.Tenancy.TenantId tenant, string code,
        string name, bool stacking) => new()
    {
        TenantId = tenant,
        FamilyCode = code,
        Name = name,
        Stacking = stacking,
        AccountingTag = stacking ? AssuranceOrService.Service : AssuranceOrService.Assurance,
        CreatedAt = fixture.Clock.UtcNow,
        LastModifiedAt = fixture.Clock.UtcNow,
        LastModifiedBy = Actor,
    };

    private WarrantyPolicyVersion Policy(Yojana.Platform.Contracts.Tenancy.TenantId tenant, string family,
        string code, string name, string serviceMode, int? responseHours) => new()
    {
        TenantId = tenant,
        PolicyVersionId = Guid.CreateVersion7(),
        PolicyCode = code,
        Version = 1,
        Name = name,
        CoverageFamily = family,
        EntitlementType = family == "base" ? EntitlementType.Manufacturer : EntitlementType.Extended,
        AppliesToContractsFrom = new DateOnly(2026, 1, 1),
        Status = PolicyVersionStatus.Published,
        RuleVersion = "v1.live",
        DurationValue = 24,
        DurationUnit = DurationRule.Months,
        EndOfMonthRule = DurationRule.Clamp,
        DurationBasis = DurationBasis.Time,
        StartTrigger = StartTrigger.Deliver,
        Coverage = JsonSerializer.Serialize(new CoverageTerms(100, 100, 100, serviceMode, responseHours),
            LedgerJson.Options),
        Transferable = Transferability.Yes,
        SettlementFormula = "full",
        AssuranceOrService = family == "base" ? AssuranceOrService.Assurance : AssuranceOrService.Service,
        ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
        CreatedAt = fixture.Clock.UtcNow,
        CreatedBy = Actor,
        PublishedAt = fixture.Clock.UtcNow,
        PublishedBy = Actor,
        LastModifiedAt = fixture.Clock.UtcNow,
        LastModifiedBy = Actor,
    };
}
