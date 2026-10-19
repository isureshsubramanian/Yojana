using System.Net;
using System.Net.Http.Headers;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Integration.Security.Workers;

public interface IConnectionRetryJitter
{
    TimeSpan Backoff(int retryNumber, TimeSpan ceiling);
}

public sealed class RandomConnectionRetryJitter : IConnectionRetryJitter
{
    public TimeSpan Backoff(int retryNumber, TimeSpan ceiling)
    {
        var maximumMs = Math.Min(ceiling.TotalMilliseconds,
            TimeSpan.FromSeconds(Math.Pow(2, Math.Min(retryNumber - 1, 20))).TotalMilliseconds);
        return TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * maximumMs);
    }
}

public sealed class ConnectionRateLimitedHandler : DelegatingHandler
{
    public static readonly HttpRequestOptionsKey<bool> IdempotentRequest =
        new("Yojana.Integration.IdempotentRequest");

    private readonly ConnectionWorkerKey workerKey;
    private readonly ConnectionThrottleProfiles profiles;
    private readonly IConnectionThrottleWorker worker;
    private readonly IClock clock;
    private readonly IConnectionThrottleDelay delay;
    private readonly IConnectionRetryJitter jitter;
    private readonly int maximumRetries;

    public ConnectionRateLimitedHandler(ConnectionWorkerKey workerKey,
        ConnectionThrottleProfiles profiles,
        IConnectionThrottleWorkerRegistry workers,
        IClock clock,
        IConnectionThrottleDelay delay,
        IConnectionRetryJitter jitter,
        int maximumRetries = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetries);
        this.workerKey = workerKey;
        this.profiles = profiles;
        worker = workers.For(workerKey);
        this.clock = clock;
        this.delay = delay;
        this.jitter = jitter;
        this.maximumRetries = maximumRetries;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var buffered = await BufferedRequest.CreateAsync(request, cancellationToken);
        var mayRetry = IsIdempotent(request);
        for (var attempt = 0; ; attempt++)
        {
            var stored = await profiles.GetAsync(workerKey, cancellationToken);
            HttpResponseMessage response;
            try
            {
                await using (await worker.AcquireAsync(stored.Profile, cancellationToken))
                {
                    using var outbound = buffered.Create();
                    response = await base.SendAsync(outbound, cancellationToken);
                }
            }
            catch (HttpRequestException) when (mayRetry && attempt < maximumRetries)
            {
                await delay.WaitAsync(jitter.Backoff(attempt + 1, stored.Profile.RetryAfterCeiling),
                    cancellationToken);
                continue;
            }

            if (response.StatusCode != HttpStatusCode.RequestTimeout
                && response.StatusCode != HttpStatusCode.TooManyRequests
                && (int)response.StatusCode < 500) return response;

            var retryDelay = RetryDelay(response.Headers.RetryAfter, clock.UtcNow,
                stored.Profile.RetryAfterCeiling)
                ?? jitter.Backoff(attempt + 1, stored.Profile.RetryAfterCeiling);
            worker.PauseUntil(clock.UtcNow + retryDelay);
            if (!mayRetry || attempt >= maximumRetries) return response;

            response.Dispose();
            await delay.WaitAsync(retryDelay, cancellationToken);
        }
    }

    public static TimeSpan? RetryDelay(RetryConditionHeaderValue? retryAfter, DateTime nowUtc,
        TimeSpan ceiling)
    {
        if (retryAfter is null) return null;
        var requested = retryAfter.Delta
            ?? (retryAfter.Date is { } date ? date.UtcDateTime - nowUtc : (TimeSpan?)null);
        if (requested is null) return null;
        if (requested < TimeSpan.Zero) return TimeSpan.Zero;
        return requested > ceiling ? ceiling : requested;
    }

    private static bool IsIdempotent(HttpRequestMessage request)
    {
        if (request.Options.TryGetValue(IdempotentRequest, out var marked) && marked) return true;
        return request.Method == HttpMethod.Get || request.Method == HttpMethod.Head
            || request.Method == HttpMethod.Options || request.Method == HttpMethod.Trace
            || request.Method == HttpMethod.Put || request.Method == HttpMethod.Delete;
    }

    private sealed record BufferedRequest(
        HttpMethod Method,
        Uri? Uri,
        Version Version,
        HttpVersionPolicy VersionPolicy,
        IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> Headers,
        IReadOnlyList<KeyValuePair<string, object?>> Options,
        byte[]? Content,
        IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> ContentHeaders)
    {
        public static async Task<BufferedRequest> CreateAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[]? content = null;
            IReadOnlyList<KeyValuePair<string, IEnumerable<string>>> contentHeaders = [];
            if (request.Content is not null)
            {
                content = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                contentHeaders = request.Content.Headers
                    .Select(header => KeyValuePair.Create(header.Key, header.Value)).ToList();
            }
            return new(request.Method, request.RequestUri, request.Version, request.VersionPolicy,
                request.Headers.Select(header => KeyValuePair.Create(header.Key, header.Value)).ToList(),
                request.Options.Select(option => KeyValuePair.Create(option.Key, option.Value)).ToList(),
                content, contentHeaders);
        }

        public HttpRequestMessage Create()
        {
            var request = new HttpRequestMessage(Method, Uri)
            {
                Version = Version,
                VersionPolicy = VersionPolicy,
            };
            foreach (var header in Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var option in Options)
                request.Options.Set(new HttpRequestOptionsKey<object?>(option.Key), option.Value);
            if (Content is not null)
            {
                request.Content = new ByteArrayContent(Content);
                foreach (var header in ContentHeaders)
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return request;
        }
    }
}
