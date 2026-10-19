using Yojana.Integration.Connections;
using Yojana.Integration.Connectors.CX01;

namespace Yojana.Integration.Tests;

public sealed class ConnectionHealthTests
{
    private static readonly DateTime Now = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
    private static ConnectionHealthFacts Healthy => new("active", false, Now, true, "clean", Now,
        0, 0, null, TimeSpan.FromDays(7), "active", false, false, false);

    [Theory]
    [InlineData("active", false, "healthy")]
    [InlineData("active", true, "down")]
    [InlineData("paused", true, "paused")]
    [InlineData("archived", true, "archived")]
    [InlineData("registering", false, "paused")]
    public void Lifecycle_precedence_is_explicit(string lifecycle, bool smokeFailed, string expected) =>
        Assert.Equal(expected, ConnectionHealthRules.Derive(Healthy with
            { LifecycleStatus = lifecycle, SmokeFailed = smokeFailed }, new(), Now));

    [Fact]
    public void No_healthy_sweep_means_not_healthy_even_with_a_successful_smoke_read()
    {
        Assert.Equal("degraded", Derive(Healthy with { DailyCheckAt = null, DailyCheckResult = null }));
        Assert.Equal("degraded", Derive(Healthy with { DailyCheckAt = Now.AddHours(-36) }));
        Assert.Equal("degraded", Derive(Healthy with { DailyCheckResult = "mismatch" }));
        Assert.Equal("degraded", Derive(Healthy with { DailyCheckResult = "failed" }));
    }

    [Fact]
    public void Queue_work_and_warning_conditions_degrade_without_declaring_the_remote_down()
    {
        Assert.Equal("degraded", Derive(Healthy with { DeadLetters = 1 }));
        Assert.Equal("degraded", Derive(Healthy with { ManualTasks = 1 }));
        Assert.Equal("degraded", Derive(Healthy with { OldestWaitingAt = Now.AddDays(-7) }));
        Assert.Equal("healthy", Derive(Healthy with { OldestWaitingAt = Now.AddHours(-1) }));
        Assert.Equal("degraded", Derive(Healthy with { CredentialDue = true }));
        Assert.Equal("degraded", Derive(Healthy with { CallBudgetWarning = true }));
        Assert.Equal("degraded", Derive(Healthy with { NotificationState = "stopped" }));
        Assert.Equal("degraded", Derive(Healthy with { PollFailed = true }));
    }

    [Fact]
    public void Poll_age_uses_configured_thresholds_and_handles_missing_evidence()
    {
        Assert.Equal("degraded", Derive(Healthy with { CheckedUpTo = null }));
        Assert.Equal("degraded", Derive(Healthy with { CheckedUpTo = Now.AddMinutes(-10) }));
        Assert.Equal("down", Derive(Healthy with { CheckedUpTo = Now.AddMinutes(-60) }));
        Assert.Equal("healthy", ConnectionHealthRules.Derive(Healthy with { CheckedUpTo = Now.AddMinutes(-10) },
            new(20, 120), Now));
    }

    [Fact]
    public async Task Connection_thresholds_override_tenant_values_and_defaults()
    {
        var settings = new TestTenantSettings();
        var id = Guid.NewGuid();
        await settings.SetAsync(ConnectionHealthSettings.Key("degraded_after_minutes"), "15", CancellationToken.None);
        await settings.SetAsync(ConnectionHealthSettings.Key("down_after_minutes", id), "90", CancellationToken.None);
        var result = await new ConnectionHealthSettings(settings).GetAsync(id, CancellationToken.None);
        Assert.Equal(15, result.DegradedAfterMinutes);
        Assert.Equal(90, result.DownAfterMinutes);
        Assert.Equal(14, result.CredentialWarnDays);
        await settings.SetAsync(ConnectionHealthSettings.Key("down_after_minutes", id), "1", CancellationToken.None);
        await Assert.ThrowsAsync<ConnectionRuleException>(() => new ConnectionHealthSettings(settings).GetAsync(id, CancellationToken.None));
    }

    [Fact]
    public void Allowlist_absence_is_unknown_and_explicit_owner_answers_keep_their_source()
    {
        var settings = new Cx01ConnectionSettings(new Uri("https://fictional.invalid"), "FICTIONAL", "test");
        Assert.All(Cx01TrackingPermissions.Declared(settings).Values, value =>
        {
            Assert.Equal("unknown", value.State);
            Assert.Equal("unknown", value.Source);
        });
        var tables = Cx01TrackingPermissions.Tables;
        var declared = Cx01TrackingPermissions.Declared(settings with
        {
            AllowlistedTrackingTables = [tables[0]],
            TrackingTableDeclarations = new Dictionary<string, bool> { [tables[1]] = false },
        });
        Assert.Equal("allowed", declared[tables[0]].State);
        Assert.Equal("not_allowed", declared[tables[1]].State);
        Assert.Equal("owner_declared", declared[tables[1]].Source);
        Assert.Equal("unknown", declared[tables[2]].State);
        Assert.Empty(Cx01TrackingPermissions.DeniedTables("Unspecified permission failure"));
    }

    private static string Derive(ConnectionHealthFacts facts) => ConnectionHealthRules.Derive(facts, new(), Now);
}
