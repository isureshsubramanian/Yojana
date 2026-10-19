using Yojana.Integration.Security.Credentials;
using Yojana.Integration.Security.Cryptography;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class CredentialExpiryAlertTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Missing_setting_uses_thirty_day_default()
    {
        var evaluator = new CredentialExpiryAlertEvaluator(new TestTenantSettings(), new TestClock(Now));

        var alert = await evaluator.EvaluateAsync(Credential(Now.AddDays(30)),
            CancellationToken.None);

        Assert.NotNull(alert);
        Assert.Equal(30, alert.DaysBeforeExpiry);
        Assert.False(alert.IsExpired);
    }

    [Fact]
    public async Task Tenant_setting_changes_warning_horizon()
    {
        var settings = new TestTenantSettings();
        await settings.SetAsync(CredentialExpiryAlertEvaluator.SettingKey, "7", CancellationToken.None);
        var evaluator = new CredentialExpiryAlertEvaluator(settings, new TestClock(Now));

        Assert.Null(await evaluator.EvaluateAsync(Credential(Now.AddDays(8)),
            CancellationToken.None));
        Assert.NotNull(await evaluator.EvaluateAsync(Credential(Now.AddDays(7)),
            CancellationToken.None));
    }

    private static ConnectionCredentialRecord Credential(DateTime? expiresAt) => new(
        new TenantId(Guid.Parse("10000000-0000-0000-0000-000000000001")),
        Guid.Parse("20000000-0000-0000-0000-000000000001"), "api-token", 1, [], [], [], Now, null,
        expiresAt, Now.AddDays(10));
}
