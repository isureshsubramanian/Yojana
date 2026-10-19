using Yojana.Integration.Reconciliation;

namespace Yojana.Integration.Tests;

public sealed class ReconciliationSettingsTests
{
    [Fact]
    public async Task Window_defaults_to_seven_days_and_allows_a_connection_override()
    {
        var settings = new TestTenantSettings();
        var policy = new ReconciliationSettings(settings);
        var connection = Guid.NewGuid();
        Assert.Equal(7, await policy.WindowDaysAsync(connection, CancellationToken.None));
        await settings.SetAsync(ReconciliationSettings.TenantKey, "14", CancellationToken.None);
        Assert.Equal(14, await policy.WindowDaysAsync(connection, CancellationToken.None));
        await settings.SetAsync(ReconciliationSettings.ConnectionKey(connection), "3", CancellationToken.None);
        Assert.Equal(3, await policy.WindowDaysAsync(connection, CancellationToken.None));
    }
}
