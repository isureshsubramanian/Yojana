using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Profiling;

public sealed class MigrationProfileOutcomeSettings(ITenantSettings settings)
{
    public const string SettingKey = "migration.profiling.outcomes";

    public async Task<MigrationProfileOutcomeSettingsView> GetAsync(CancellationToken cancellationToken)
    {
        var stored = await settings.GetVersionedAsync(SettingKey, cancellationToken);
        var values = Resolve(stored?.Value);
        return new(MigrationProfileClasses.All.Select(item =>
                new MigrationProfileOutcomeChoice(item.Code, values[item.Code])).ToList(),
            stored?.Version ?? ITenantSettings.MissingVersion);
    }

    public async Task<MigrationProfileOutcomeSettingsView> SetAsync(UpdateMigrationProfileOutcomeSettings update,
        CancellationToken cancellationToken)
    {
        var known = MigrationProfileClasses.All.ToDictionary(item => item.Code, StringComparer.Ordinal);
        if (update.Classes.Count != known.Count || update.Classes.Select(item => item.ClassCode)
                .Distinct(StringComparer.Ordinal).Count() != known.Count ||
            update.Classes.Any(item => !known.ContainsKey(item.ClassCode)))
            throw new MigrationRuleException("migration_profile_outcomes_incomplete",
                "Provide exactly one outcome for every profiling class.");
        var values = update.Classes.ToDictionary(item => item.ClassCode, item => item.Outcome,
            StringComparer.Ordinal);
        Validate(values, known);
        var saved = await settings.SetAsync(SettingKey, JsonSerializer.Serialize(values), update.ExpectedVersion,
            cancellationToken);
        return new(MigrationProfileClasses.All.Select(item =>
            new MigrationProfileOutcomeChoice(item.Code, values[item.Code])).ToList(), saved.Version);
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(CancellationToken cancellationToken) =>
        Resolve(await settings.GetAsync(SettingKey, cancellationToken));

    private static Dictionary<string, string> Resolve(string? json)
    {
        var known = MigrationProfileClasses.All.ToDictionary(item => item.Code, StringComparer.Ordinal);
        var values = json is null
            ? known.ToDictionary(item => item.Key, item => item.Value.DefaultOutcome, StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? throw new InvalidOperationException($"Tenant setting '{SettingKey}' is invalid.");
        Validate(values, known);
        foreach (var item in known.Values) values.TryAdd(item.Code, item.DefaultOutcome);
        return values;
    }

    private static void Validate(Dictionary<string, string> values,
        IReadOnlyDictionary<string, MigrationProfileClass> known)
    {
        var allowed = new HashSet<string>(
        [
            MigrationProfileOutcome.StopLoad,
            MigrationProfileOutcome.NeedsPerson,
            MigrationProfileOutcome.FixedByRule,
            MigrationProfileOutcome.LoadedWithNote,
        ], StringComparer.Ordinal);
        if (values.Any(item => !known.ContainsKey(item.Key) || !allowed.Contains(item.Value)))
            throw new MigrationRuleException("migration_profile_outcome_invalid",
                "Every profiling outcome must name a known class and supported outcome.");
        if (known.Values.Where(item => item.HardStop).Any(item =>
                values.TryGetValue(item.Code, out var outcome) && outcome != MigrationProfileOutcome.StopLoad))
            throw new MigrationRuleException("migration_profile_hard_stop_required",
                "Serials in multiple warehouses and negative quantities must stop the load.");
    }
}
