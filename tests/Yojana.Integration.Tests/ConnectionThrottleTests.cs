using System.Net;
using System.Net.Http.Headers;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class ConnectionThrottleTests
{
    private static readonly DateTime Start = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly TenantId Tenant = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly ConnectionWorkerKey Key = new(Tenant,
        Guid.Parse("20000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task Connector_declaration_seeds_an_editable_per_connection_profile()
    {
        var clock = new TestClock(Start);
        var profiles = new ConnectionThrottleProfiles(new InMemoryConnectionThrottleProfileStore(), clock);
        var declared = Profile(perSecond: 4, perDay: 100);

        var onboarded = await profiles.OnboardAsync(Key,
            new ConnectorThrottleDeclaration("CX99", declared), CancellationToken.None);
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        var changed = await profiles.UpdateAsync(Key, declared with { RequestsPerDay = 75 },
            CancellationToken.None);
        var repeatedOnboarding = await profiles.OnboardAsync(Key,
            new ConnectorThrottleDeclaration("CX99", declared), CancellationToken.None);

        Assert.Equal(100, onboarded.Profile.RequestsPerDay);
        Assert.Equal(75, changed.Profile.RequestsPerDay);
        Assert.Equal(changed, repeatedOnboarding);
    }

    [Fact]
    public async Task Burst_is_available_immediately_then_refills_at_the_declared_rate()
    {
        var clock = new TestClock(Start);
        var delay = new AdvancingThrottleDelay(clock);
        using var isolation = new ConnectionWorkerIsolation();
        var worker = Registry(isolation, new InMemoryConnectionDailyBudgetStore(),
            new CollectingBudgetAlerts(), clock, delay).For(Key);
        var profile = Profile(perSecond: 1, burst: 2);

        await AcquireAndRelease(worker, profile);
        await AcquireAndRelease(worker, profile);
        Assert.Empty(delay.Waits);
        await AcquireAndRelease(worker, profile);

        Assert.Single(delay.Waits);
        Assert.InRange(delay.Waits[0], TimeSpan.FromMilliseconds(999), TimeSpan.FromMilliseconds(1001));
    }

    [Fact]
    public async Task Daily_cap_and_both_alerts_are_raised_once_per_utc_day()
    {
        var clock = new TestClock(Start);
        var alerts = new CollectingBudgetAlerts();
        using var isolation = new ConnectionWorkerIsolation();
        var worker = Registry(isolation, new InMemoryConnectionDailyBudgetStore(), alerts, clock,
            new AdvancingThrottleDelay(clock)).For(Key);
        var profile = Profile(perDay: 4, threshold: 50);

        for (var call = 0; call < 4; call++) await AcquireAndRelease(worker, profile);
        await Assert.ThrowsAsync<ConnectionDailyCapExceededException>(async () =>
            await AcquireAndRelease(worker, profile));
        await Assert.ThrowsAsync<ConnectionDailyCapExceededException>(async () =>
            await AcquireAndRelease(worker, profile));

        Assert.Collection(alerts.Published,
            alert => Assert.Equal(ConnectionCallBudgetAlertKind.ApproachingDailyCap, alert.Kind),
            alert => Assert.Equal(ConnectionCallBudgetAlertKind.DailyCapReached, alert.Kind));

        clock.UtcNow = new DateTime(2026, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        for (var call = 0; call < 4; call++) await AcquireAndRelease(worker, profile);
        Assert.Equal(4, alerts.Published.Count);
    }

    [Fact]
    public async Task A_new_worker_resumes_the_persisted_daily_budget_after_restart()
    {
        var clock = new TestClock(Start);
        var store = new InMemoryConnectionDailyBudgetStore();
        var alerts = new CollectingBudgetAlerts();
        var profile = Profile(perDay: 3);
        using var beforeRestart = new ConnectionWorkerIsolation();
        var first = Registry(beforeRestart, store, alerts, clock,
            new AdvancingThrottleDelay(clock)).For(Key);
        await AcquireAndRelease(first, profile);
        await AcquireAndRelease(first, profile);

        using var afterRestart = new ConnectionWorkerIsolation();
        var resumed = Registry(afterRestart, store, alerts, clock,
            new AdvancingThrottleDelay(clock)).For(Key);
        await AcquireAndRelease(resumed, profile);

        await Assert.ThrowsAsync<ConnectionDailyCapExceededException>(async () =>
            await AcquireAndRelease(resumed, profile));
        Assert.Single(alerts.Published,
            x => x.Kind == ConnectionCallBudgetAlertKind.DailyCapReached);
    }

    [Fact]
    public async Task A_paused_tenant_connection_does_not_reduce_another_tenants_throughput()
    {
        var clock = new TestClock(Start);
        var delay = new BlockingThrottleDelay();
        using var isolation = new ConnectionWorkerIsolation();
        var registry = Registry(isolation, new InMemoryConnectionDailyBudgetStore(),
            new CollectingBudgetAlerts(), clock, delay);
        var saturated = registry.For(Key);
        saturated.PauseUntil(Start.AddMinutes(1));
        using var cancellation = new CancellationTokenSource();
        var blocked = saturated.AcquireAsync(Profile(), cancellation.Token).AsTask();
        await delay.WaitStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var otherKey = new ConnectionWorkerKey(
            new TenantId(Guid.Parse("10000000-0000-0000-0000-000000000002")), Key.ConnectionId);

        await AcquireAndRelease(registry.For(otherKey), Profile());

        Assert.False(blocked.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }

    [Theory]
    [InlineData(120, 30, 30)]
    [InlineData(5, 30, 5)]
    public void Retry_after_seconds_are_parsed_and_capped(int seconds, int ceilingSeconds, int expectedSeconds)
    {
        var retryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));

        var result = ConnectionRateLimitedHandler.RetryDelay(retryAfter, Start,
            TimeSpan.FromSeconds(ceilingSeconds));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), result);
    }

    [Fact]
    public void Retry_after_http_date_is_relative_to_the_clock_and_past_dates_are_immediate()
    {
        var future = new RetryConditionHeaderValue(new DateTimeOffset(Start.AddSeconds(12)));
        var past = new RetryConditionHeaderValue(new DateTimeOffset(Start.AddSeconds(-1)));

        Assert.Equal(TimeSpan.FromSeconds(12), ConnectionRateLimitedHandler.RetryDelay(future, Start,
            TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.Zero, ConnectionRateLimitedHandler.RetryDelay(past, Start,
            TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Handler_retries_marked_requests_but_not_unmarked_non_idempotent_requests()
    {
        var retryingTransport = new ResponseSequenceHandler(HttpStatusCode.TooManyRequests, HttpStatusCode.OK);
        using var retrying = await HandlerAsync(retryingTransport);
        var marked = new HttpRequestMessage(HttpMethod.Post, "https://connector.invalid/items");
        marked.Options.Set(ConnectionRateLimitedHandler.IdempotentRequest, true);

        using var successful = await retrying.SendAsync(marked);

        Assert.Equal(HttpStatusCode.OK, successful.StatusCode);
        Assert.Equal(2, retryingTransport.Calls);

        var singleTransport = new ResponseSequenceHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        using var single = await HandlerAsync(singleTransport);
        using var rejected = await single.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            "https://connector.invalid/items"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
        Assert.Equal(1, singleTransport.Calls);
    }

    private static ConnectionThrottleProfile Profile(int? perSecond = null, int? perDay = null,
        int burst = 1, int threshold = 80) =>
        new(perSecond, null, perDay, burst, 1, TimeSpan.FromSeconds(30), threshold);

    private static ConnectionThrottleWorkerRegistry Registry(IConnectionWorkerIsolation isolation,
        IConnectionDailyBudgetStore store, IConnectionCallBudgetAlertPublisher alerts,
        TestClock clock, IConnectionThrottleDelay delay) =>
        new(isolation, store, alerts, clock, delay);

    private static async Task AcquireAndRelease(IConnectionThrottleWorker worker,
        ConnectionThrottleProfile profile)
    {
        await using var lease = await worker.AcquireAsync(profile, CancellationToken.None);
    }

    private static async Task<HttpClient> HandlerAsync(HttpMessageHandler transport)
    {
        var clock = new TestClock(Start);
        var delay = new AdvancingThrottleDelay(clock);
        var profileStore = new InMemoryConnectionThrottleProfileStore();
        var profiles = new ConnectionThrottleProfiles(profileStore, clock);
        await profiles.OnboardAsync(Key, new ConnectorThrottleDeclaration("CX99", Profile()),
            CancellationToken.None);
        var isolation = new ConnectionWorkerIsolation();
        var registry = Registry(isolation, new InMemoryConnectionDailyBudgetStore(),
            new CollectingBudgetAlerts(), clock, delay);
        var handler = new ConnectionRateLimitedHandler(Key, profiles, registry, clock, delay,
            new ZeroJitter()) { InnerHandler = transport };
        return new HttpClient(handler, true);
    }

    private sealed class BlockingThrottleDelay : IConnectionThrottleDelay
    {
        public TaskCompletionSource WaitStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            WaitStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ZeroJitter : IConnectionRetryJitter
    {
        public TimeSpan Backoff(int retryNumber, TimeSpan ceiling) => TimeSpan.Zero;
    }

    private sealed class ResponseSequenceHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> statuses = new(statuses);
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new HttpResponseMessage(statuses.Dequeue()));
        }
    }
}
