using System.Globalization;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Outbox;

/// <summary>Tenant setting with an optional connection override; a missing value applies the shipped seven-day default.</summary>
public sealed class OutboxRelaySettings(ITenantSettings settings)
{
    public const string TenantKey = "integration.outbox.max_age_days";
    public const int DefaultMaxAgeDays = 7;

    public static string ConnectionKey(Guid connectionId) => $"{TenantKey}.{connectionId:N}";

    public async Task<TimeSpan> MaxAgeAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(ConnectionKey(connectionId), cancellationToken)
            ?? await settings.GetAsync(TenantKey, cancellationToken);
        if (raw is null) return TimeSpan.FromDays(DefaultMaxAgeDays);
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || days is < 1 or > 365)
            throw new InvalidOperationException($"Tenant setting '{TenantKey}' must be a whole number from 1 to 365.");
        return TimeSpan.FromDays(days);
    }
}
