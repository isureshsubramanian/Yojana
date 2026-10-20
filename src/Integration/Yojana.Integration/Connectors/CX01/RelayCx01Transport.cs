using Yojana.Platform.Contracts.Time;
using Yojana.Integration.Relays;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.Connectors.CX01;

public sealed class RelayCx01Transport(Cx01Settings settings, Cx01Transport direct, RelayRequestQueue queue,
    ConnectionThrottleProfiles profiles, IConnectionThrottleWorkerRegistry workers, IClock clock,
    IConnectionThrottleDelay delay, IConnectionRetryJitter jitter, IConnectionBusyObserver busy) : ICx01Transport
{
    public async Task<HttpResponseMessage> SendAsync(ConnectionWorkerKey workerKey, HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var configured = await settings.GetAsync(workerKey.ConnectionId, cancellationToken);
        if (configured.Transport == "direct") return await direct.SendAsync(workerKey, request, cancellationToken);
        if (configured.RelayCredentials == "site" && request.RequestUri!.AbsolutePath is Cx01Protocol.Login or Cx01Protocol.Logout)
            throw new InvalidOperationException("Site sessions are owned by the relay agent.");
        // Reuse the connection's quota, semaphore and remote-busy pause. Queue timeouts are uncertain writes;
        // leave retries to the existing outbox query-before-create path rather than issuing a second lease here.
        using var invoker = new HttpMessageInvoker(new ConnectionRateLimitedHandler(workerKey, profiles, workers,
            clock, delay, jitter, maximumRetries: 0, busy: busy)
        { InnerHandler = new RelayQueueHandler(queue, workerKey.ConnectionId, configured) });
        return await invoker.SendAsync(request, cancellationToken);
    }
    private sealed class RelayQueueHandler(RelayRequestQueue queue, Guid connectionId, Cx01ConnectionSettings configured) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            queue.SendAsync(connectionId, request, configured.RelayRequestTimeoutSeconds, configured.RelayQueueCapacity,
                Cx01RelayProfile.Declaration(configured), cancellationToken);
    }
}
