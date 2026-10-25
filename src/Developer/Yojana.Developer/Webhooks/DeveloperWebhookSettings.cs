using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Developer.Webhooks;

public sealed class DeveloperWebhookSettings(ITenantSettings settings)
{
    public const string RetentionDaysKey = "developer.delivery_log_retention_days";
    public const string RatePerSecondKey = "developer.webhook_rate_per_second";
    public const string TimeoutSecondsKey = "developer.webhook_timeout_seconds";
    public const string AllowNonStandardPortsKey = "developer.webhook_allow_non_standard_ports";
    public const string BulkThresholdKey = "developer.bulk_summary_threshold";

    public Task<int> RetentionDaysAsync(CancellationToken ct) => IntAsync(RetentionDaysKey, 30, 1, 3650, ct);
    public Task<int> RatePerSecondAsync(CancellationToken ct) => IntAsync(RatePerSecondKey, 10, 1, 1000, ct);
    public Task<int> TimeoutSecondsAsync(CancellationToken ct) => IntAsync(TimeoutSecondsKey, 10, 1, 60, ct);
    public Task<int> BulkThresholdAsync(CancellationToken ct) => IntAsync(BulkThresholdKey, 500, 1, 100_000, ct);

    public async Task<bool> AllowNonStandardPortsAsync(CancellationToken ct) =>
        bool.TryParse(await settings.GetAsync(AllowNonStandardPortsKey, ct), out var value) && value;

    private async Task<int> IntAsync(string key, int fallback, int minimum, int maximum, CancellationToken ct)
    {
        var stored = await settings.GetAsync(key, ct);
        return int.TryParse(stored, out var value) && value >= minimum && value <= maximum ? value : fallback;
    }
}

public sealed class DeveloperWebhookOptions
{
    public const string SectionName = "Developer:Webhooks";
    public bool AllowLoopbackTargetsForTests { get; set; }
    /// <summary>Subscriptions sending at the same time on one host; each holds one unpooled SQL connection for its lock.</summary>
    public int SubscriptionParallelism { get; set; } = 16;
}
