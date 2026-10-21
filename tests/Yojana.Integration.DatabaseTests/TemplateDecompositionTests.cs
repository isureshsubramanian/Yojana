using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class TemplateDecompositionTests(IntegrationHostFixture fixture)
{
    private const string Source = "fixture-source";
    private static readonly DateTime ExtractedAt = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Identical_base_and_next_business_day_templates_group_approve_and_resolve_for_a_line()
    {
        var tenant = await fixture.NewTenantAsync("tmplgroup");
        var policies = await SeedCatalogueAsync(tenant, includeAccidental: false);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        AddTemplate(db, tenant, "RS-1100 36M NBD", "RS-1100 servers with next business day response");
        AddTemplate(db, tenant, "ST-44 36M NBD", "ST-44 storage with next-business-day response");
        AddContract(db, tenant, 101, "RS-1100 36M NBD", active: true, lineCount: 2);
        AddContract(db, tenant, 102, "RS-1100 36M NBD", active: false, lineCount: 1);
        AddContract(db, tenant, 201, "ST-44 36M NBD", active: true, lineCount: 1);
        db.Set<ServiceContractStagingRow>().Local.Single(row => row.ContractId == 201).EndDate = ExtractedAt.Date;
        db.Set<ContractLineStagingRow>().Local.Single(row => row.ContractId == 201).EndDate = ExtractedAt.Date;
        await db.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<TemplateMappingService>();
        var result = await service.ProposeAsync(new(), default);
        Assert.Equal((2, 2, 0), (result.Requested, result.Proposed, result.Superseded));
        var page = await service.ListAsync(null, 25, default);
        var pattern = Assert.Single(page.Items);
        Assert.Equal(2, pattern.TemplateCount);
        Assert.Equal(new TemplateUsageCounts(3, 4, 2, 3), pattern.Usage);
        Assert.All(pattern.Templates, template => Assert.False(template.NeedsPerson));

        var approved = await service.ApprovePatternAsync(pattern.PatternKey, Guid.NewGuid().ToString("D"), default);
        Assert.Equal(2, approved.Approved);
        var detail = await service.DetailAsync(pattern.Templates[0].TemplateKey, default);
        Assert.NotNull(detail);
        Assert.Equal(TemplateMappingState.Approved, detail.State);
        Assert.Equal([policies.Base, policies.Service], detail.Blocks.Select(block => block.PolicyId!.Value));
        Assert.False(detail.SourceTerms.DurationUnitVerified);
        Assert.Contains(detail.WordingToReview, wording => wording.Kind == "description"
            && wording.Text.Contains("RS-1100", StringComparison.Ordinal));
        Assert.Contains(detail.WordingToReview, wording => wording.Kind == "staged_remark"
            && wording.Text.Contains("retained media", StringComparison.Ordinal));

        var output = await scope.ServiceProvider.GetRequiredService<IApprovedTemplateDecompositionQuery>()
            .ForStagedContractLineAsync(Source, "101|0", default);
        Assert.NotNull(output);
        Assert.Equal(["base", "service_level"], output.Blocks.Select(block => block.CoverageFamily));
        Assert.Equal([policies.Base, policies.Service], output.Blocks.Select(block => block.PolicyId));

        var otherTenant = await fixture.NewTenantAsync("tmplother");
        using var otherScope = fixture.ScopeFor(otherTenant);
        var otherDb = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(0, await otherDb.TemplateMappings.CountAsync());
        Assert.Equal(0, await otherDb.TemplateMappingBlocks.CountAsync());
    }

    [DatabaseFact]
    public async Task Policy_to_create_blocks_cannot_be_approved_until_an_existing_policy_is_selected()
    {
        var tenant = await fixture.NewTenantAsync("tmplresolve");
        var policies = await SeedCatalogueAsync(tenant, includeAccidental: false);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        AddTemplate(db, tenant, "RS-1100 damage", "RS-1100 accidental damage protection",
            responseValue: null, responseUnit: null);
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<TemplateMappingService>();
        await service.ProposeAsync(new(), default);
        var summary = Assert.Single(Assert.Single((await service.ListAsync(null, 25, default)).Items).Templates);
        var detail = await service.DetailAsync(summary.TemplateKey, default);
        Assert.NotNull(detail);
        Assert.Contains(detail.Blocks, block => block.FamilyCode == "accidental_damage"
            && block.Resolution == TemplateBlockResolution.PolicyToCreate);
        var refused = await Assert.ThrowsAsync<TemplateMappingRuleException>(() => service.ApproveAsync(
            summary.TemplateKey, Guid.NewGuid().ToString("D"), default));
        Assert.Equal("template_policy_unresolved", refused.Rule);

        var accidentalId = await AddPublishedPolicyAsync(tenant, "accidental_damage", "ADP-36",
            "RS-1100 accidental damage 36 months", responseHours: null);
        detail = await service.UpdateAsync(summary.TemplateKey, new(
        [
            new("base", policies.Base),
            new("accidental_damage", accidentalId),
        ], detail.Version), default);
        Assert.All(detail.Blocks, block => Assert.Equal(TemplateBlockResolution.ExistingPolicy, block.Resolution));
        Assert.Equal(TemplateMappingState.Approved, (await service.ApproveAsync(summary.TemplateKey,
            Guid.NewGuid().ToString("D"), default)).State);
    }

    [DatabaseFact]
    public async Task New_extraction_supersedes_an_approved_mapping_and_returns_it_to_review()
    {
        var tenant = await fixture.NewTenantAsync("tmplchange");
        _ = await SeedCatalogueAsync(tenant, includeAccidental: false);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        AddTemplate(db, tenant, "ST-44 standard", "ST-44 storage base cover",
            responseValue: null, responseUnit: null);
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<TemplateMappingService>();
        await service.ProposeAsync(new(), default);
        var key = Assert.Single(Assert.Single((await service.ListAsync(null, 25, default)).Items).Templates).TemplateKey;
        await service.ApproveAsync(key, Guid.NewGuid().ToString("D"), default);

        var source = await db.Set<ContractTemplateStagingRow>().SingleAsync(row => row.TemplateName == "ST-44 standard");
        source.Duration = 48;
        source.BatchId = Guid.CreateVersion7();
        source.ExtractedAt = ExtractedAt.AddDays(1);
        source.RowHash = [4, 8];
        await db.SaveChangesAsync();
        var reproposed = await service.ProposeAsync(new([key]), default);
        Assert.Equal(1, reproposed.Superseded);
        var current = await service.DetailAsync(key, default);
        Assert.NotNull(current);
        Assert.Equal(TemplateMappingState.Draft, current.State);
        Assert.Equal(48, current.SourceTerms.DurationValue);
        Assert.Equal(2, await db.TemplateMappings.CountAsync(row => row.TemplateKey == key));
        Assert.Equal(1, await db.TemplateMappings.CountAsync(row => row.TemplateKey == key
            && row.State == TemplateMappingState.Superseded));
    }

    [DatabaseFact]
    public async Task Keyword_rule_edits_change_drafts_and_vague_templates_need_a_person()
    {
        var tenant = await fixture.NewTenantAsync("tmplrules");
        _ = await SeedCatalogueAsync(tenant, includeAccidental: false);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        AddTemplate(db, tenant, "RS-1100 shield", "RS-1100 opaque shield",
            responseValue: null, responseUnit: null);
        var vague = AddTemplate(db, tenant, "ST-44 vague", "Premium storage care",
            responseValue: null, responseUnit: null);
        vague.Duration = null;
        vague.IncludesParts = null;
        vague.IncludesLabour = "sometimes";
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<TemplateMappingService>();
        await service.ProposeAsync(new(), default);
        var before = (await service.ListAsync(null, 25, default)).Items.SelectMany(group => group.Templates).ToList();
        var shieldKey = before.Single(item => item.TemplateName == "RS-1100 shield").TemplateKey;
        Assert.Single((await service.DetailAsync(shieldKey, default))!.Blocks);
        Assert.True(before.Single(item => item.TemplateName == "ST-44 vague").NeedsPerson);

        var rules = scope.ServiceProvider.GetRequiredService<TemplateKeywordRules>();
        var currentRules = await rules.GetAsync(default);
        await rules.UpdateAsync(new([.. currentRules.Rules, new("opaque shield", "accidental_damage")],
            currentRules.Version), default);
        await service.ProposeAsync(new([shieldKey]), default);
        var after = await service.DetailAsync(shieldKey, default);
        Assert.NotNull(after);
        Assert.Equal(["base", "accidental_damage"], after.Blocks.Select(block => block.FamilyCode));
    }

    private async Task<(Guid Base, Guid Service)> SeedCatalogueAsync(
        Yojana.Platform.Contracts.Tenancy.TenantId tenant, bool includeAccidental)
    {
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.CoverageFamilies.AddRange(
                Family(tenant, "base", "Base warranty", false),
                Family(tenant, "service_level", "Service level", true),
                Family(tenant, "accidental_damage", "Accidental damage", true),
                Family(tenant, "media_retention", "Media retention", true),
                Family(tenant, "extended_term", "Extended term", true));
            await db.SaveChangesAsync();
        }
        var baseId = await AddPublishedPolicyAsync(tenant, "base", "BASE-36", "Base 36 months", null);
        var serviceId = await AddPublishedPolicyAsync(tenant, "service_level", "NBD-36",
            "Next business day 36 months", 24);
        if (includeAccidental)
            _ = await AddPublishedPolicyAsync(tenant, "accidental_damage", "ADP-36",
                "Accidental damage 36 months", null);
        return (baseId, serviceId);
    }

    private async Task<Guid> AddPublishedPolicyAsync(Yojana.Platform.Contracts.Tenancy.TenantId tenant,
        string family, string code, string name, int? responseHours)
    {
        var id = Guid.CreateVersion7();
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        db.PolicyVersions.Add(new WarrantyPolicyVersion
        {
            TenantId = tenant, PolicyVersionId = id, PolicyCode = code, Version = 1, Name = name,
            CoverageFamily = family, EntitlementType = family == "base" ? EntitlementType.Manufacturer
                : EntitlementType.Extended,
            AppliesToContractsFrom = new DateOnly(2026, 1, 1), Status = PolicyVersionStatus.Published,
            RuleVersion = "v1.d1", DurationValue = 36, DurationUnit = DurationRule.Months,
            EndOfMonthRule = DurationRule.Clamp, DurationBasis = DurationBasis.Time,
            StartTrigger = StartTrigger.Deliver,
            Coverage = JsonSerializer.Serialize(new CoverageTerms(100, 100, 100,
                family == "service_level" ? "on_site" : "return_to_base", responseHours), LedgerJson.Options),
            Transferable = Transferability.Yes, SettlementFormula = "full",
            AssuranceOrService = family == "base" ? AssuranceOrService.Assurance : AssuranceOrService.Service,
            ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc, CreatedAt = ExtractedAt, CreatedBy = "test",
            PublishedAt = ExtractedAt, PublishedBy = "test", LastModifiedAt = ExtractedAt,
            LastModifiedBy = "test",
        });
        await db.SaveChangesAsync();
        return id;
    }

    private static CoverageFamily Family(Yojana.Platform.Contracts.Tenancy.TenantId tenant, string code,
        string name, bool stacking) => new()
    {
        TenantId = tenant, FamilyCode = code, Name = name, Stacking = stacking,
        AccountingTag = stacking ? AssuranceOrService.Service : AssuranceOrService.Assurance,
        CreatedAt = ExtractedAt, LastModifiedAt = ExtractedAt, LastModifiedBy = "test",
    };

    private static ContractTemplateStagingRow AddTemplate(IntegrationDbContext db,
        Yojana.Platform.Contracts.Tenancy.TenantId tenant, string name, string description,
        int? responseValue = 1, string? responseUnit = "day")
    {
        var batch = Guid.CreateVersion7();
        var row = new ContractTemplateStagingRow
        {
            TenantId = tenant, BatchId = batch, FirstBatchId = batch, SourceSystem = Source,
            SourceTable = "template", SourcePk = name, ExtractedAt = ExtractedAt, RowHash = [1, 2, 3],
            TemplateName = name, Description = description, Duration = 36, ResponseValue = responseValue,
            ResponseUnit = responseUnit, IncludesParts = "Y", IncludesLabour = "Y", IncludesTravel = "Y",
            Remark = "Review exclusions for server drives and retained media.",
        };
        db.Set<ContractTemplateStagingRow>().Add(row);
        return row;
    }

    private static void AddContract(IntegrationDbContext db,
        Yojana.Platform.Contracts.Tenancy.TenantId tenant, int contractId, string template, bool active,
        int lineCount)
    {
        var batch = Guid.CreateVersion7();
        db.Set<ServiceContractStagingRow>().Add(new()
        {
            TenantId = tenant, BatchId = batch, FirstBatchId = batch, SourceSystem = Source,
            SourceTable = "contract", SourcePk = contractId.ToString(), ExtractedAt = ExtractedAt,
            RowHash = [4, 5], ContractId = contractId, CustomerCode = "NORTHSTAR-SYSTEMS",
            TemplateName = template, Status = active ? "A" : "I", StartDate = new DateTime(2026, 1, 1),
            EndDate = active ? new DateTime(2027, 1, 1) : new DateTime(2026, 8, 31),
        });
        for (var line = 0; line < lineCount; line++)
            db.Set<ContractLineStagingRow>().Add(new()
            {
                TenantId = tenant, BatchId = batch, FirstBatchId = batch, SourceSystem = Source,
                SourceTable = "contract_line", SourcePk = $"{contractId}|{line}", ExtractedAt = ExtractedAt,
                RowHash = [6, (byte)line], ContractId = contractId, LineNumber = line,
                ItemCode = line == 0 ? "RS-1100" : "ST-44", InternalSerial = $"FIC-{contractId}-{line}",
                StartDate = new DateTime(2026, 1, 1), EndDate = active ? new DateTime(2027, 1, 1)
                    : new DateTime(2026, 8, 31),
            });
    }
}
