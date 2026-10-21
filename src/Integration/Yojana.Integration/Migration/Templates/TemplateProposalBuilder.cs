using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Templates;

internal sealed record ProposedTemplateBlock(
    PolicyCatalogueFamily Family,
    string Resolution,
    PolicyCataloguePolicy? Policy,
    TemplateSourceTerms Terms);

internal sealed record TemplateProposalDraft(
    TemplateSourceTerms Terms,
    IReadOnlyList<TemplateWordingToReview> Wording,
    IReadOnlyList<ProposedTemplateBlock> Blocks,
    bool NeedsPerson,
    string? ReviewReason,
    string PatternKey,
    string PatternLabel,
    string DecompositionFingerprint);

internal static class TemplateProposalBuilder
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public static TemplateProposalDraft Build(ContractTemplateStagingRow row, PolicyCatalogue catalogue,
        IReadOnlyList<TemplateKeywordRule> keywordRules)
    {
        var reasons = new List<string>();
        var terms = Terms(row, reasons);
        var families = catalogue.Families.Where(family => family.Active)
            .ToDictionary(family => family.FamilyCode, StringComparer.Ordinal);
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var baseFamily = BaseFamily(families.Values);
        if (baseFamily is null)
            reasons.Add("No unambiguous active base coverage family is configured.");
        else
            selected.Add(baseFamily.FamilyCode);

        var description = string.Join(' ', new[] { row.Description, row.Remark }
            .Where(value => !string.IsNullOrWhiteSpace(value))).ToLowerInvariant();
        foreach (var rule in keywordRules.Where(rule => description.Contains(rule.Keyword,
                     StringComparison.OrdinalIgnoreCase)))
        {
            if (families.ContainsKey(rule.FamilyCode)) selected.Add(rule.FamilyCode);
            else reasons.Add($"Keyword '{rule.Keyword}' names unavailable family '{rule.FamilyCode}'.");
        }

        if (terms.ResponseHours is not null)
        {
            if (families.ContainsKey("service_level")) selected.Add("service_level");
            else if (selected.Count == (baseFamily is null ? 0 : 1))
                reasons.Add("Response terms exist but no service-level coverage family could be identified.");
        }
        if (selected.Contains("extended_term"))
            reasons.Add("The source has one total duration and cannot separate the base duration from the extended term.");

        var blocks = selected.Select(code => BuildBlock(families[code], terms, catalogue.Policies, reasons))
            .OrderBy(block => block.Family.Stacking).ThenBy(block => block.Family.FamilyCode,
                StringComparer.Ordinal).ToList();
        if (blocks.Count == 0) reasons.Add("No coverage block could be identified from the template.");
        if (blocks.Count(block => !block.Family.Stacking) != 1)
            reasons.Add("A decomposition must contain exactly one non-stacking base family.");

        var wording = new List<TemplateWordingToReview>();
        if (!string.IsNullOrWhiteSpace(row.Description))
            wording.Add(new("description", row.Description.Trim()));
        // The verified source exposes Remark but no exclusion-specific column. Carry it without claiming its meaning.
        if (!string.IsNullOrWhiteSpace(row.Remark))
            wording.Add(new("staged_remark", row.Remark.Trim()));

        var decomposition = Canonical(blocks, reasons);
        var fingerprint = Hash(decomposition);
        var label = reasons.Count == 0 ? Label(blocks) : $"Needs a person · {reasons[0]}";
        return new(terms, wording, blocks, reasons.Count != 0,
            reasons.Count == 0 ? null : string.Join(' ', reasons.Distinct(StringComparer.Ordinal)),
            Hash(label + "\n" + decomposition), label, fingerprint);
    }

    public static string SourceFingerprint(ContractTemplateStagingRow row) => Hash(JsonSerializer.Serialize(new
    {
        row.TemplateName,
        row.Description,
        row.Duration,
        row.ResponseValue,
        row.ResponseUnit,
        row.IncludesParts,
        row.IncludesLabour,
        row.IncludesTravel,
        row.ContractType,
        row.Renewal,
        row.Deleted,
        row.Remark,
        row.MondayEnabled, row.MondayStart, row.MondayEnd,
        row.TuesdayEnabled, row.TuesdayStart, row.TuesdayEnd,
        row.WednesdayEnabled, row.WednesdayStart, row.WednesdayEnd,
        row.ThursdayEnabled, row.ThursdayStart, row.ThursdayEnd,
        row.FridayEnabled, row.FridayStart, row.FridayEnd,
        row.SaturdayEnabled, row.SaturdayStart, row.SaturdayEnd,
        row.SundayEnabled, row.SundayStart, row.SundayEnd,
    }, Json));

    public static string TemplateKey(string sourceSystem, string sourceKey) =>
        Hash(sourceSystem + "\0" + sourceKey);

    public static string JsonValue<T>(T value) => JsonSerializer.Serialize(value, Json);

    public static T ReadJson<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidOperationException("Stored template decomposition JSON was empty.");

    public static (string PatternKey, string PatternLabel, string Fingerprint) Pattern(
        IReadOnlyList<ProposedTemplateBlock> blocks, bool needsPerson, string? reason)
    {
        var reasons = needsPerson && reason is not null ? new List<string> { reason } : [];
        var canonical = Canonical(blocks, reasons);
        var label = needsPerson ? $"Needs a person · {reason}" : Label(blocks);
        return (Hash(label + "\n" + canonical), label, Hash(canonical));
    }

    private static TemplateSourceTerms Terms(ContractTemplateStagingRow row, List<string> reasons)
    {
        int? duration = row.Duration is > 0 ? row.Duration : null;
        if (duration is null) reasons.Add("Duration is missing or not positive; months cannot be assumed from a value.");
        var parts = Percentage(row.IncludesParts, "parts", reasons);
        var labour = Percentage(row.IncludesLabour, "labour", reasons);
        var travel = Percentage(row.IncludesTravel, "travel", reasons);
        var response = ResponseHours(row.ResponseValue, row.ResponseUnit, reasons);
        var hours = CoverageHours(row, reasons);
        return new(duration, DurationRule.Months, false, parts, labour, travel, response,
            row.ResponseValue is null ? null : $"{row.ResponseValue} {row.ResponseUnit}".Trim(), hours,
            string.IsNullOrWhiteSpace(row.ContractType) ? null : row.ContractType.Trim());
    }

    private static ProposedTemplateBlock BuildBlock(PolicyCatalogueFamily family, TemplateSourceTerms terms,
        IReadOnlyList<PolicyCataloguePolicy> policies, List<string> reasons)
    {
        var matches = policies.Where(policy => policy.FamilyCode == family.FamilyCode)
            .Where(policy => Matches(family, terms, policy.Terms))
            .OrderByDescending(policy => policy.Version).ThenBy(policy => policy.PolicyCode,
                StringComparer.Ordinal).ToList();
        if (family.FamilyCode == "extended_term") matches.Clear();
        var match = matches.Count == 1 ? matches[0] : null;
        if (matches.Count > 1)
            reasons.Add($"Several published policies match family '{family.FamilyName}'; choose one explicitly.");
        return new(family, match is null ? TemplateBlockResolution.PolicyToCreate
            : TemplateBlockResolution.ExistingPolicy, match, terms);
    }

    private static bool Matches(PolicyCatalogueFamily family, TemplateSourceTerms source,
        PolicyCatalogueTerms policy)
    {
        if (source.DurationValue != policy.DurationValue || policy.DurationUnit != DurationRule.Months)
            return false;
        if (family.FamilyCode == "service_level")
            return source.ResponseHours == policy.ResponseHours
                && Same(source.CoverageHours, policy.CoverageHours);
        return source.PartsPct == policy.PartsPct && source.LabourPct == policy.LabourPct
            && source.TravelPct == policy.TravelPct && Same(source.CoverageHours, policy.CoverageHours);
    }

    private static PolicyCatalogueFamily? BaseFamily(IEnumerable<PolicyCatalogueFamily> families)
    {
        var list = families.Where(family => !family.Stacking).ToList();
        return list.FirstOrDefault(family => family.FamilyCode == "base")
            ?? list.FirstOrDefault(family => family.FamilyCode == "base_warranty");
    }

    private static decimal? Percentage(string? value, string label, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            reasons.Add($"The {label} inclusion flag is missing.");
            return null;
        }
        var normalised = value.Trim().ToUpperInvariant();
        if (normalised is "Y" or "YES" or "TYES" or "1" or "TRUE") return 100m;
        if (normalised is "N" or "NO" or "TNO" or "0" or "FALSE") return 0m;
        reasons.Add($"The {label} inclusion flag '{value}' is not recognised.");
        return null;
    }

    private static int? ResponseHours(int? value, string? unit, List<string> reasons)
    {
        if (value is null && string.IsNullOrWhiteSpace(unit)) return null;
        if (value is not > 0 || string.IsNullOrWhiteSpace(unit))
        {
            reasons.Add("Response time has a value or unit missing.");
            return null;
        }
        var token = unit.Trim().ToUpperInvariant();
        if (token is "H" or "HR" or "HRS" or "HOUR" or "HOURS") return value;
        if (token is "D" or "DAY" or "DAYS") return checked(value.Value * 24);
        reasons.Add($"Response unit '{unit}' is not recognised as hours or days.");
        return null;
    }

    private static string? CoverageHours(ContractTemplateStagingRow row, List<string> reasons)
    {
        var days = new[]
        {
            ("mon", row.MondayEnabled, row.MondayStart, row.MondayEnd),
            ("tue", row.TuesdayEnabled, row.TuesdayStart, row.TuesdayEnd),
            ("wed", row.WednesdayEnabled, row.WednesdayStart, row.WednesdayEnd),
            ("thu", row.ThursdayEnabled, row.ThursdayStart, row.ThursdayEnd),
            ("fri", row.FridayEnabled, row.FridayStart, row.FridayEnd),
            ("sat", row.SaturdayEnabled, row.SaturdayStart, row.SaturdayEnd),
            ("sun", row.SundayEnabled, row.SundayStart, row.SundayEnd),
        };
        var enabled = new List<string>();
        foreach (var (day, flag, start, end) in days)
        {
            if (!Enabled(flag)) continue;
            if (start is null || end is null || !ValidClock(start.Value) || !ValidClock(end.Value))
            {
                reasons.Add($"Coverage hours for {day} are incomplete or invalid.");
                continue;
            }
            enabled.Add($"{day}={start.Value:0000}-{end.Value:0000}");
        }
        return enabled.Count == 0 ? null : string.Join(';', enabled);
    }

    private static bool Enabled(string? value) => value?.Trim().ToUpperInvariant()
        is "Y" or "YES" or "TYES" or "1" or "TRUE";

    private static bool ValidClock(int value) => value is >= 0 and <= 2359 && value % 100 < 60;

    private static bool Same(string? left, string? right) => string.Equals(left?.Trim(), right?.Trim(),
        StringComparison.OrdinalIgnoreCase);

    private static string Canonical(IReadOnlyList<ProposedTemplateBlock> blocks, IReadOnlyList<string> reasons) =>
        JsonSerializer.Serialize(new
        {
            blocks = blocks.OrderBy(block => block.Family.FamilyCode, StringComparer.Ordinal).Select(block => new
            {
                block.Family.FamilyCode,
                block.Resolution,
                policy_id = block.Policy?.PolicyId,
                policy_version = block.Policy?.Version,
                block.Terms,
            }),
            reasons = reasons.OrderBy(reason => reason, StringComparer.Ordinal),
        }, Json);

    private static string Label(IReadOnlyList<ProposedTemplateBlock> blocks) => string.Join(" + ",
        blocks.Select(block => block.Policy?.PolicyName ??
            $"Create {block.Family.FamilyName} ({block.Terms.DurationValue?.ToString(CultureInfo.InvariantCulture) ?? "?"} months)"));

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
