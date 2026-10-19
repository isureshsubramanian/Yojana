using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Tests;

internal sealed class TestClock(DateTime utcNow) : IClock
{
    public DateTime UtcNow { get; set; } = utcNow;
}

internal sealed class TestTenantSettings : ITenantSettings
{
    private readonly Dictionary<string, string> values = [];

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(values.GetValueOrDefault(key));

    public Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        values[key] = value;
        return Task.CompletedTask;
    }
}
