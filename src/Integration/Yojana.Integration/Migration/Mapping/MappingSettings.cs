using System.Globalization;
using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.LegacyMigration.Mapping;

public sealed class MappingSettings(ITenantSettings settings, IPolicyCatalogueQuery policies)
{
    public const string AllowWaitingReviewsKey = "migration.mapping.allow_waiting_reviews";
    public const string CutoverDateKey = "migration.mapping.cutover_date";
    public const string ConditionMapKey = "migration.mapping.condition_map";
    public const string CardCoverPolicyKey = "migration.mapping.card_cover_policy";
    public const string SerialMasterCoverPolicyKey = "migration.mapping.serial_master_cover_policy";

    private static readonly IReadOnlyList<ConditionMappingRule> Defaults =
    [
        new("defective", null, ConditionGrades.Defective),
        new("scrap", null, ConditionGrades.Scrap),
        new(null, Reconstruction.HistoryMovementKind.CustomerReturned, ConditionGrades.Used),
        new(null, Reconstruction.HistoryMovementKind.IssuedOut, ConditionGrades.Used),
        new(null, Reconstruction.HistoryMovementKind.Shipped, ConditionGrades.New),
        new(null, null, ConditionGrades.New),
    ];

    public async Task<MappingSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var allow = bool.TryParse(await settings.GetAsync(AllowWaitingReviewsKey, cancellationToken), out var value)
            && value;
        var dateText = await settings.GetAsync(CutoverDateKey, cancellationToken);
        DateOnly? cutover = null;
        if (!string.IsNullOrWhiteSpace(dateText) && DateOnly.TryParseExact(dateText, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) cutover = parsed;
        var mapText = await settings.GetAsync(ConditionMapKey, cancellationToken);
        var map = mapText is null ? Defaults : Validate(JsonSerializer.Deserialize<List<ConditionMappingRule>>(mapText)
            ?? throw new InvalidOperationException($"Tenant setting '{ConditionMapKey}' is invalid."));
        var cardPolicy = ParsePolicyId(await settings.GetAsync(CardCoverPolicyKey, cancellationToken),
            CardCoverPolicyKey);
        var serialMasterPolicy = ParsePolicyId(
            await settings.GetAsync(SerialMasterCoverPolicyKey, cancellationToken), SerialMasterCoverPolicyKey);
        return new(allow, cutover, map, cardPolicy, serialMasterPolicy);
    }

    public async Task<MappingSettingsView> SetAsync(UpdateMappingSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var map = Validate(request.ConditionMap);
        var catalogue = await policies.ReadAsync(cancellationToken);
        ValidatePolicy(request.CardCoverPolicyId, CoverageFamilyCode.Base, catalogue,
            "equipment-card cover");
        ValidatePolicy(request.SerialMasterCoverPolicyId, CoverageFamilyCode.Supplier, catalogue,
            "serial-master cover");
        await settings.SetAsync(AllowWaitingReviewsKey, request.AllowWaitingReviews.ToString(), cancellationToken);
        await settings.SetAsync(CutoverDateKey,
            request.CutoverDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", cancellationToken);
        await settings.SetAsync(ConditionMapKey, JsonSerializer.Serialize(map), cancellationToken);
        await settings.SetAsync(CardCoverPolicyKey, request.CardCoverPolicyId?.ToString("D") ?? "",
            cancellationToken);
        await settings.SetAsync(SerialMasterCoverPolicyKey,
            request.SerialMasterCoverPolicyId?.ToString("D") ?? "", cancellationToken);
        return new(request.AllowWaitingReviews, request.CutoverDate, map, request.CardCoverPolicyId,
            request.SerialMasterCoverPolicyId);
    }

    public static string InferCondition(string? cardStatus, string? lastMovement,
        IReadOnlyList<ConditionMappingRule> map) =>
        map.First(rule => (rule.CardStatus is null || string.Equals(rule.CardStatus, cardStatus,
                StringComparison.OrdinalIgnoreCase)) &&
            (rule.LastMovementKind is null || rule.LastMovementKind == lastMovement)).Grade;

    private static List<ConditionMappingRule> Validate(IReadOnlyList<ConditionMappingRule> rules)
    {
        if (rules.Count is < 1 or > 100 || rules[^1].CardStatus is not null ||
            rules[^1].LastMovementKind is not null)
            throw new MappingRuleException("migration_mapping_condition_map_invalid",
                "Provide between one and 100 condition rules ending with a catch-all rule.");
        var cleaned = rules.Select(rule => rule with
        {
            CardStatus = string.IsNullOrWhiteSpace(rule.CardStatus) ? null : rule.CardStatus.Trim(),
            LastMovementKind = string.IsNullOrWhiteSpace(rule.LastMovementKind)
                ? null : rule.LastMovementKind.Trim(),
            Grade = rule.Grade.Trim().ToLowerInvariant(),
        }).ToList();
        if (cleaned.Any(rule => !ConditionGrades.IsKnown(rule.Grade)))
            throw new MappingRuleException("migration_mapping_condition_grade_invalid",
                "Every condition rule must use a supported condition grade.");
        return cleaned;
    }

    private static Guid? ParsePolicyId(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (Guid.TryParse(value, out var policyId)) return policyId;
        throw new MappingRuleException("migration_mapping_cover_policy_setting_invalid",
            $"Tenant setting '{key}' does not contain a policy id.");
    }

    private static void ValidatePolicy(Guid? policyId, string family, PolicyCatalogue catalogue,
        string settingName)
    {
        if (policyId is null) return;
        var policy = catalogue.Policies.SingleOrDefault(row => row.PolicyId == policyId);
        if (policy is null)
            throw new MappingRuleException("migration_mapping_cover_policy_not_published",
                $"The {settingName} policy does not exist or has no published version.");
        if (policy.FamilyCode != family)
            throw new MappingRuleException("migration_mapping_cover_policy_family_mismatch",
                $"The {settingName} policy must belong to the {family} family.");
    }
}
