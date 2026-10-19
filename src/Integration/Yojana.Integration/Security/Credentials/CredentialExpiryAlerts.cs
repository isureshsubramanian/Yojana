using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Security.Credentials;

public sealed record CredentialExpiryAlert(
    TenantId TenantId,
    Guid ConnectionId,
    string Purpose,
    DateTime ExpiresAt,
    int DaysBeforeExpiry,
    bool IsExpired);

/// <summary>Reads the tenant's warning horizon; absence means thirty days, not a global product rule.</summary>
public sealed class CredentialExpiryAlertEvaluator(ITenantSettings settings, IClock clock)
{
    public const string SettingKey = "integration.security.credential_expiry_alert_days";
    public const int DefaultDaysBeforeExpiry = 30;

    public async Task<CredentialExpiryAlert?> EvaluateAsync(ConnectionCredentialRecord credential,
        CancellationToken cancellationToken)
    {
        if (credential.ExpiresAt is not { } expiresAt) return null;
        var days = await DaysBeforeExpiryAsync(cancellationToken);
        if (expiresAt > clock.UtcNow.AddDays(days)) return null;
        return new CredentialExpiryAlert(credential.TenantId, credential.ConnectionId, credential.Purpose,
            expiresAt, days, expiresAt <= clock.UtcNow);
    }

    public async Task<int> DaysBeforeExpiryAsync(CancellationToken cancellationToken)
    {
        var value = await settings.GetAsync(SettingKey, cancellationToken);
        if (value is null) return DefaultDaysBeforeExpiry;
        if (!int.TryParse(value, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var days) || days is < 1 or > 365)
            throw new InvalidOperationException($"Tenant setting '{SettingKey}' must be an integer from 1 to 365.");
        return days;
    }
}
