using System.Globalization;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Reconciliation;

public sealed class ReconciliationSettings(ITenantSettings settings)
{
    public const string TenantKey = "integration.reconciliation.window_days";
    public const int DefaultWindowDays = 7;

    public static string ConnectionKey(Guid connectionId) => $"{TenantKey}.{connectionId:N}";

    public async Task<int> WindowDaysAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var raw = await settings.GetAsync(ConnectionKey(connectionId), cancellationToken)
            ?? await settings.GetAsync(TenantKey, cancellationToken);
        if (raw is null) return DefaultWindowDays;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var days)
            || days is < 1 or > 365)
            throw new InvalidOperationException($"Tenant setting '{TenantKey}' must be a whole number from 1 to 365.");
        return days;
    }
}
