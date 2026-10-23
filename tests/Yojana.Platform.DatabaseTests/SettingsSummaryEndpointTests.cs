using System.Diagnostics;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Settings;
using Yojana.Web.Settings;
using Xunit;

namespace Yojana.Platform.DatabaseTests;

public sealed class SettingsSummaryEndpointTests
{
    [Fact]
    public async Task Role_filtering_happens_before_a_contributor_runs()
    {
        var hidden = new StubContributor("hidden", [YojanaRole.TenantAdministrator]);
        var visible = new StubContributor("visible", [YojanaRole.Technician]);

        var response = await SettingsSummaryEndpoints.ReadAsync(User(YojanaRole.Technician), [hidden, visible],
            NullLogger<SettingsSummaryResponse>.Instance, default);

        Assert.Equal(0, hidden.Reads);
        Assert.Equal(1, visible.Reads);
        Assert.Equal("visible", Assert.Single(response.Lines).Key);
    }

    [Fact]
    public async Task Failed_and_timed_out_contributors_are_omitted_without_stopping_later_keys()
    {
        var failed = new StubContributor("failed", [YojanaRole.TenantAdministrator],
            (_, _) => throw new InvalidOperationException("sensitive failure detail"));
        var timedOut = new StubContributor("timed_out", [YojanaRole.TenantAdministrator],
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            });
        var succeeded = new StubContributor("succeeded", [YojanaRole.TenantAdministrator]);
        var stopwatch = Stopwatch.StartNew();

        var response = await SettingsSummaryEndpoints.ReadAsync(User(YojanaRole.TenantAdministrator),
            [failed, timedOut, succeeded], NullLogger<SettingsSummaryResponse>.Instance, default);

        stopwatch.Stop();
        Assert.Equal("succeeded", Assert.Single(response.Lines).Key);
        Assert.Equal((1, 1, 1), (failed.Reads, timedOut.Reads, succeeded.Reads));
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(3));
    }

    private static ClaimsPrincipal User(params string[] roles) => new(new ClaimsIdentity(
        roles.Select(role => new Claim(YojanaClaim.Role, role)), "test", ClaimTypes.Name, YojanaClaim.Role));

    private sealed class StubContributor(
        string key,
        IReadOnlyList<string> roles,
        Func<IReadOnlySet<string>, CancellationToken, Task<IReadOnlyList<SettingsSummaryLine>>>? read = null)
        : ISettingsSummaryContributor
    {
        public int Reads { get; private set; }
        public IReadOnlyList<SettingsSummaryKey> Keys { get; } = [new(key, roles)];

        public async Task<IReadOnlyList<SettingsSummaryLine>> ReadAsync(
            IReadOnlySet<string> keys,
            CancellationToken cancellationToken)
        {
            Reads++;
            if (read is not null) return await read(keys, cancellationToken);
            return [new SettingsSummaryLine(key, new Dictionary<string, long> { ["count"] = 1 }, null)];
        }
    }
}
