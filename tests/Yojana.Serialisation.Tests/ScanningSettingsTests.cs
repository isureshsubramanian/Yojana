using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Settings;
using Xunit;

namespace Yojana.Serialisation.Tests;

public sealed class ScanningSettingsTests
{
    [Fact]
    public async Task Missing_and_invalid_values_use_the_accepted_defaults()
    {
        var store = new MemorySettings();
        var settings = new ScanningSettings(store);
        Assert.Equal(new ScanningPolicy(300, 1500), await settings.GetAsync(default));

        await store.SetAsync(ScanningSettings.EndPauseKey, "149", default);
        await store.SetAsync(ScanningSettings.RepeatWindowKey, "not-a-number", default);
        Assert.Equal(new ScanningPolicy(300, 1500), await settings.GetAsync(default));
    }

    [Theory]
    [InlineData(149, 1500, "scan_end_pause_out_of_range")]
    [InlineData(1001, 1500, "scan_end_pause_out_of_range")]
    [InlineData(300, -1, "scan_repeat_window_out_of_range")]
    [InlineData(300, 10001, "scan_repeat_window_out_of_range")]
    public async Task Out_of_range_values_are_refused_with_a_coded_reason(int endPauseMs,
        int repeatWindowMs, string rule)
    {
        var settings = new ScanningSettings(new MemorySettings());
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() =>
            settings.SetAsync(new ScanningPolicy(endPauseMs, repeatWindowMs), default));
        Assert.Equal(rule, refusal.Rule);
    }

    [Fact]
    public async Task Zero_disables_repeat_suppression_and_bounds_are_saved()
    {
        var settings = new ScanningSettings(new MemorySettings());
        Assert.Equal(new ScanningPolicy(150, 0),
            await settings.SetAsync(new ScanningPolicy(150, 0), default));
        Assert.Equal(new ScanningPolicy(1000, 10000),
            await settings.SetAsync(new ScanningPolicy(1000, 10000), default));
    }

    private sealed class MemorySettings : ITenantSettings
    {
        private readonly Dictionary<string, string> values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(values.GetValueOrDefault(key));

        public Task SetAsync(string key, string value, CancellationToken cancellationToken)
        {
            values[key] = value;
            return Task.CompletedTask;
        }
    }
}
