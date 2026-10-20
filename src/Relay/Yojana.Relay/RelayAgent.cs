using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Yojana.Relay.Contracts;

namespace Yojana.Relay;

public sealed class RelayCloudClient(HttpClient client, RelayAgentConfiguration configuration, RelaySecrets secrets, TimeProvider? time = null)
{
    public async Task<TReply> SendAsync<TRequest, TReply>(string path, TRequest payload, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonSerializerOptions.Web);
        if (body.Length > RelayBounds.WireLimit) throw new InvalidDataException("relay_body_too_large");
        using var request = new HttpRequestMessage(HttpMethod.Post, RelayProductUrl.Resolve(configuration.ProductUrl, path))
        { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new("application/json");
        RelaySigning.Stamp(request, configuration, secrets.RelaySecret, body, (time ?? TimeProvider.System).GetUtcNow());
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await RelayBounds.ReadAsync(await response.Content.ReadAsStreamAsync(ct), RelayBounds.WireLimit, ct);
        return JsonSerializer.Deserialize<TReply>(bytes, JsonSerializerOptions.Web)
            ?? throw new InvalidDataException("relay_reply_invalid");
    }
}

/// <summary>Public library entry point used by the service, console and in-process acceptance tests.</summary>
public sealed class RelayAgent(RelayAgentConfiguration configuration, RelayCloudClient cloud,
    RelayLocalClient local, ILogger<RelayAgent> logger, TimeProvider? time = null) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        if (configuration.Concurrency is < 1 or > 32 || configuration.Credentials is not ("site" or "cloud")
            || configuration.ProductUrl.Scheme is not ("https" or "http")
            || configuration.ProductUrl.Scheme == "http" && !configuration.ProductUrl.IsLoopback
            || configuration.ServiceUrl.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(configuration.ServiceUrl.UserInfo)
            || !string.IsNullOrEmpty(configuration.ProductUrl.UserInfo))
            throw new InvalidOperationException("relay_configuration_invalid");
        return base.StartAsync(cancellationToken);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Enumerable.Range(0, configuration.Concurrency).Select(_ => WorkerAsync(stoppingToken))
            .Append(KeepAliveAsync(stoppingToken)));

    private async Task WorkerAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var reply = await cloud.SendAsync<RelayPoll, RelayPollReply>("/api/relay/v1/poll",
                    new(1, typeof(RelayAgent).Assembly.GetName().Version?.ToString() ?? "unknown",
                        Environment.MachineName, local.ErpVersion), ct);
                if (reply.Items is null) throw new InvalidDataException("relay_poll_items_invalid");
                foreach (var work in reply.Items.Take(1))
                {
                    if (work.ExpiresAtUtc <= (time ?? TimeProvider.System).GetUtcNow().UtcDateTime)
                    {
                        logger.LogWarning("Leased relay work skipped because its deadline has passed; check agent clock skew.");
                        continue;
                    }
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(work.ExpiresAtUtc - (time ?? TimeProvider.System).GetUtcNow().UtcDateTime);
                    RelayResult result;
                    try { result = await local.ExecuteAsync(work, deadline.Token); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception) { result = RelayLocalClient.Failure(work); }
                    // A result upload is retried; the local request is never repeated because an upload failed.
                    var uploadFailures = 0;
                    while (!deadline.IsCancellationRequested)
                    {
                        try
                        {
                            _ = await cloud.SendAsync<RelayResult, JsonElement>("/api/relay/v1/results", result, deadline.Token);
                            break;
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch (Exception ex)
                        {
                            logger.LogWarning("Relay result upload failed; retrying without executing local work again.");
                            if (ex is InvalidDataException || result.Body.Length > RelayBounds.BodyLimit) result = RelayLocalClient.Failure(work);
                            var seconds = Math.Min(8, Math.Pow(2, Math.Min(uploadFailures++, 3))) + Random.Shared.NextDouble();
                            await Task.Delay(TimeSpan.FromSeconds(seconds), deadline.Token);
                        }
                    }
                }
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception)
            {
                failures = Math.Min(failures + 1, 6);
                logger.LogWarning("Relay cloud exchange unavailable; retrying (attempt {Attempt}).", failures);
                try { await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, failures) + Random.Shared.NextDouble()), ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            }
        }
    }

    private async Task KeepAliveAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await local.KeepAliveAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception)
            { logger.LogWarning("Relay local session unavailable; authentication will retry after back-off."); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}
