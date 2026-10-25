using Yojana.Developer.Authentication;

namespace Yojana.Developer.DatabaseTests;

public sealed class ApiAuthenticationRefusalLimiterTests
{
    private static readonly DateTime Start = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Concurrent_valid_requests_from_one_address_are_never_throttled()
    {
        var limiter = new ApiAuthenticationRefusalLimiter(new MutableDeveloperClock(Start),
            new ApiAuthenticationOptions { RefusalLimitPerMinute = 20 });
        var inFlight = Enumerable.Range(0, 200).Select(_ => limiter.Acquire("203.0.113.24")).ToList();
        Assert.All(inFlight, attempt => Assert.True(attempt.Allowed));
        foreach (var attempt in inFlight) attempt.Succeeded();
        Assert.True(limiter.Acquire("203.0.113.24").Allowed);
    }

    [Fact]
    public void Refusals_over_the_limit_are_throttled_per_address_until_the_window_ends()
    {
        var clock = new MutableDeveloperClock(Start);
        var limiter = new ApiAuthenticationRefusalLimiter(clock, new ApiAuthenticationOptions { RefusalLimitPerMinute = 20 });
        for (var i = 0; i < 20; i++) limiter.Acquire("198.51.100.7").Refused();
        var throttled = limiter.Acquire("198.51.100.7");
        Assert.False(throttled.Allowed);
        Assert.True(throttled.RetryAfter > TimeSpan.Zero);
        Assert.True(limiter.Acquire("198.51.100.8").Allowed);
        clock.UtcNow = Start.AddMinutes(1);
        Assert.True(limiter.Acquire("198.51.100.7").Allowed);
    }
}
