using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Integration.LegacyMigration.Templates;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.Tests;

public sealed class TemplateProposalTests
{
    [Fact]
    public void Terms_select_a_base_policy_and_explicit_response_plus_keyword_selects_service_level()
    {
        var baseId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();
        var draft = TemplateProposalBuilder.Build(Template("RS-1100 36 month NBD", "Next business day"),
            Catalogue(
                Policy("base", baseId, "BASE-36", "Base 36 months", response: null),
                Policy("service_level", serviceId, "NBD-36", "Next business day", response: 24)),
            TemplateKeywordRules.Defaults);

        Assert.False(draft.NeedsPerson);
        Assert.Equal(["base", "service_level"], draft.Blocks.Select(block => block.Family.FamilyCode));
        Assert.Equal([baseId, serviceId], draft.Blocks.Select(block => block.Policy!.PolicyId));
        Assert.All(draft.Blocks, block => Assert.Equal(TemplateBlockResolution.ExistingPolicy, block.Resolution));
        Assert.False(draft.Terms.DurationUnitVerified);
        Assert.Equal(DurationRule.Months, draft.Terms.DurationUnit);
    }

    [Fact]
    public void Description_only_adds_families_through_keyword_rules_and_ambiguous_terms_need_a_person()
    {
        var template = Template("ST-44 protection", "Opaque premium plan");
        template.Duration = null;
        template.IncludesParts = null;
        template.IncludesLabour = "sometimes";
        var withoutRule = TemplateProposalBuilder.Build(template, Catalogue(), TemplateKeywordRules.Defaults);
        Assert.True(withoutRule.NeedsPerson);
        Assert.DoesNotContain(withoutRule.Blocks, block => block.Family.FamilyCode == "accidental_damage");

        var withRule = TemplateProposalBuilder.Build(template, Catalogue(),
            [new("opaque premium", "accidental_damage")]);
        Assert.Contains(withRule.Blocks, block => block.Family.FamilyCode == "accidental_damage");
        Assert.True(withRule.NeedsPerson);
    }

    [Fact]
    public void Several_exact_policies_are_never_resolved_by_sort_order()
    {
        var template = Template("RS-1100 duplicated base", "Next business day");
        var draft = TemplateProposalBuilder.Build(template, Catalogue(
                Policy("base", Guid.NewGuid(), "BASE-A", "Base A", null),
                Policy("base", Guid.NewGuid(), "BASE-B", "Base B", null),
                Policy("service_level", Guid.NewGuid(), "NBD", "Next business day", 24)),
            TemplateKeywordRules.Defaults);

        Assert.True(draft.NeedsPerson);
        Assert.Contains("Several published policies match", draft.ReviewReason);
        Assert.Null(draft.Blocks.Single(block => block.Family.FamilyCode == "base").Policy);
    }

    [Fact]
    public void Service_hours_do_not_prevent_an_otherwise_exact_base_policy_match()
    {
        var template = Template("RS-1100 weekday response", "One day response");
        template.MondayEnabled = "Y";
        template.MondayStart = 800;
        template.MondayEnd = 1700;
        var baseId = Guid.NewGuid();
        var serviceId = Guid.NewGuid();

        var draft = TemplateProposalBuilder.Build(template, Catalogue(
                Policy("base", baseId, "BASE", "Base cover", null),
                Policy("service_level", serviceId, "ONE-DAY", "One-day weekday response", 24,
                    "mon=0800-1700")),
            TemplateKeywordRules.Defaults);

        Assert.False(draft.NeedsPerson);
        Assert.Equal(baseId, draft.Blocks.Single(block => block.Family.FamilyCode == "base").Policy!.PolicyId);
        Assert.Equal(serviceId,
            draft.Blocks.Single(block => block.Family.FamilyCode == "service_level").Policy!.PolicyId);
    }

    [Fact]
    public void A_single_total_duration_never_guesses_the_extended_portion()
    {
        var template = Template("ST-44 extended", "Extended storage warranty");
        var draft = TemplateProposalBuilder.Build(template, Catalogue(
                Policy("base", Guid.NewGuid(), "BASE", "Base cover", null),
                Policy("extended_term", Guid.NewGuid(), "EXT", "Extended cover", 24),
                Policy("service_level", Guid.NewGuid(), "NBD", "Next business day", 24)),
            TemplateKeywordRules.Defaults);

        Assert.True(draft.NeedsPerson);
        Assert.Contains("cannot separate the base duration", draft.ReviewReason);
        Assert.Null(draft.Blocks.Single(block => block.Family.FamilyCode == "extended_term").Policy);
    }

    [Fact]
    public void A_template_with_every_inclusion_excluded_always_needs_a_person()
    {
        var template = Template("RS-1100 response only", "Response only");
        template.IncludesParts = "N";
        template.IncludesLabour = "N";
        template.IncludesTravel = "N";
        var draft = TemplateProposalBuilder.Build(template, Catalogue(
                Policy("base", Guid.NewGuid(), "BASE", "Base cover", null)),
            TemplateKeywordRules.Defaults);

        Assert.True(draft.NeedsPerson);
        Assert.Contains("all excluded", draft.ReviewReason);

        template.IncludesTravel = "Y";
        Assert.DoesNotContain("all excluded", TemplateProposalBuilder.Build(template, Catalogue(),
            TemplateKeywordRules.Defaults).ReviewReason ?? string.Empty);
    }

    private static ContractTemplateStagingRow Template(string name, string description) => new()
    {
        BatchId = Guid.NewGuid(),
        SourceSystem = "source-a",
        SourceTable = "template",
        SourcePk = name,
        ExtractedAt = new DateTime(2026, 9, 1),
        RowHash = [1],
        FirstBatchId = Guid.NewGuid(),
        TemplateName = name,
        Description = description,
        Duration = 36,
        ResponseValue = 1,
        ResponseUnit = "day",
        IncludesParts = "Y",
        IncludesLabour = "Y",
        IncludesTravel = "Y",
    };

    private static PolicyCatalogue Catalogue(params PolicyCataloguePolicy[] policies) => new(
        [
            new("base", "Base warranty", false, true),
            new("service_level", "Service level", true, true),
            new("accidental_damage", "Accidental damage", true, true),
            new("media_retention", "Media retention", true, true),
            new("extended_term", "Extended term", true, true),
        ], policies);

    private static PolicyCataloguePolicy Policy(string family, Guid id, string code, string name, int? response,
        string? coverageHours = null) =>
        new(family, id, code, 1, name, new(36, DurationRule.Months, 100, 100, 100,
            family == "service_level" ? "on_site" : "return_to_base", response, coverageHours));
}
