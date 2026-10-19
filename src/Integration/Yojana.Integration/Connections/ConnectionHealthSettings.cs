using System.Globalization;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connections;

public sealed record ConnectionHealthThresholds(int DegradedAfterMinutes = 10, int DownAfterMinutes = 60,
    int HealthySweepHours = 36, int CredentialWarnDays = 14, int CredentialRotationDays = 90);

public sealed class ConnectionHealthSettings(ITenantSettings settings)
{
    public const string Prefix = "integration.health.";
    public static string Key(string name, Guid? connectionId = null) =>
        Prefix + name + (connectionId is { } id ? $".{id:N}" : "");

    public async Task<ConnectionHealthThresholds> GetAsync(Guid connectionId, CancellationToken ct)
    {
        var result = new ConnectionHealthThresholds(
            await Number("degraded_after_minutes", 10, 1, 10080),
            await Number("down_after_minutes", 60, 1, 43200),
            await Number("healthy_sweep_hours", 36, 1, 8760),
            await Number("credential_warn_days", 14, 0, 365),
            await Number("credential_rotation_days", 90, 1, 3650));
        if (result.DownAfterMinutes < result.DegradedAfterMinutes)
            throw new ConnectionRuleException("health_thresholds_invalid", "The down threshold must follow the degraded threshold.");
        return result;

        async Task<int> Number(string name, int fallback, int minimum, int maximum)
        {
            var raw = await settings.GetAsync(Key(name, connectionId), ct)
                ?? await settings.GetAsync(Key(name), ct);
            if (raw is null) return fallback;
            if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value < minimum || value > maximum)
                throw new ConnectionRuleException("health_thresholds_invalid", $"Setting {Key(name)} is outside its supported range.");
            return value;
        }
    }
}

public sealed record ConnectionHealthFacts(string LifecycleStatus, bool SmokeFailed, DateTime? CheckedUpTo,
    bool PollConfigured, string? DailyCheckResult, DateTime? DailyCheckAt, int DeadLetters, int ManualTasks,
    DateTime? OldestWaitingAt, TimeSpan MaxOutboxAge, string NotificationState, bool CredentialDue,
    bool CallBudgetWarning, bool PollFailed, DateTime? InitialisedAt = null);

public static class ConnectionHealthRules
{
    public static string Derive(ConnectionHealthFacts facts, ConnectionHealthThresholds thresholds, DateTime now)
    {
        if (facts.LifecycleStatus == "archived") return "archived";
        if (facts.LifecycleStatus is "paused" or "registering") return "paused";
        if (facts.SmokeFailed || (facts.PollConfigured && facts.CheckedUpTo is null
                && facts.InitialisedAt <= now.AddMinutes(-thresholds.DownAfterMinutes)) || (facts.PollConfigured && facts.CheckedUpTo is { } checkedAt
                && checkedAt <= now.AddMinutes(-thresholds.DownAfterMinutes))) return "down";
        if (facts.DailyCheckResult != "clean" || facts.DailyCheckAt is null
            || facts.DailyCheckAt <= now.AddHours(-thresholds.HealthySweepHours)
            || (facts.PollConfigured && (facts.CheckedUpTo is null
                || facts.CheckedUpTo <= now.AddMinutes(-thresholds.DegradedAfterMinutes)))
            || facts.DeadLetters > 0 || facts.ManualTasks > 0 || facts.CredentialDue || facts.CallBudgetWarning
            || facts.PollFailed || facts.NotificationState == "stopped"
            || facts.OldestWaitingAt + facts.MaxOutboxAge <= now) return "degraded";
        return "healthy";
    }
}

public sealed class ConnectionRuleException(string rule, string message) : Exception(message)
{
    public string Rule { get; } = rule;
}
