using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Templates;

public sealed class TemplateKeywordRules(ITenantSettings settings, IPolicyCatalogueQuery catalogue)
{
    public const string SettingKey = "migration.templates.keyword_rules";

    public static readonly IReadOnlyList<TemplateKeywordRule> Defaults =
    [
        new("accidental damage", "accidental_damage"),
        new("media retention", "media_retention"),
        new("next business day", "service_level"),
        new("next-business-day", "service_level"),
        new("extended", "extended_term"),
    ];

    public async Task<TemplateKeywordRulesView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        var rules = stored is null ? Defaults : Read(stored.Value);
        return new(rules, stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<TemplateKeywordRulesView> UpdateAsync(UpdateTemplateKeywordRulesRequest request,
        CancellationToken cancellationToken)
    {
        var rules = Normalise(request.Rules);
        var knownFamilies = (await catalogue.ReadAsync(cancellationToken)).Families
            .Where(family => family.Active).Select(family => family.FamilyCode)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = rules.Select(rule => rule.FamilyCode).FirstOrDefault(code => !knownFamilies.Contains(code));
        if (unknown is not null)
            throw new TemplateMappingRuleException("template_keyword_family_unknown",
                $"Coverage family '{unknown}' is not active for this tenant.");
        var saved = await settings.SetAsync(SettingKey, JsonSerializer.Serialize(rules), request.ExpectedVersion,
            cancellationToken);
        return new(rules, saved.Version);
    }

    private static List<TemplateKeywordRule> Read(string json)
    {
        try
        {
            return Normalise(JsonSerializer.Deserialize<List<TemplateKeywordRule>>(json) ?? []);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Tenant setting '{SettingKey}' is not valid JSON.", ex);
        }
    }

    private static List<TemplateKeywordRule> Normalise(IEnumerable<TemplateKeywordRule> input)
    {
        var rules = input.Select(rule => new TemplateKeywordRule(rule.Keyword.Trim().ToLowerInvariant(),
                rule.FamilyCode.Trim().ToLowerInvariant()))
            .Where(rule => rule.Keyword.Length != 0 && rule.FamilyCode.Length != 0)
            .ToList();
        if (rules.Count == 0)
            throw new TemplateMappingRuleException("template_keyword_rules_empty",
                "At least one keyword rule is required.");
        if (rules.Any(rule => rule.Keyword.Length > 120 || rule.FamilyCode.Length > 40))
            throw new TemplateMappingRuleException("template_keyword_rule_too_long",
                "Keywords are limited to 120 characters and family codes to 40 characters.");
        if (rules.GroupBy(rule => rule.Keyword, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new TemplateMappingRuleException("template_keyword_duplicate",
                "Each keyword may appear only once.");
        return rules.OrderBy(rule => rule.Keyword, StringComparer.Ordinal).ToList();
    }
}

public sealed class TemplateMappingRuleException(string rule, string message) : Exception(message)
{
    public string Rule { get; } = rule;
}
