using System.Net;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Yojana.Relay.Contracts;

namespace Yojana.Relay;

public sealed class RelayWebhookListener(RelayAgentConfiguration configuration, RelayCloudClient cloud, ILogger<RelayWebhookListener>? logger = null) : BackgroundService
{
    private HttpListener? listener;
    private readonly ILogger<RelayWebhookListener> log = logger ?? NullLogger<RelayWebhookListener>.Instance;
    private readonly SemaphoreSlim slots = new(16, 16);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (configuration.WebhookBind is null) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            var active = new HashSet<Task>();
            HttpListener? current = null;
            try
            {
                current = new HttpListener();
                listener = current;
                using var registration = stoppingToken.Register(current.Close);
                ValidateBind(configuration.WebhookBind);
                current.Prefixes.Add(configuration.WebhookBind);
                current.Start();
                while (!stoppingToken.IsCancellationRequested)
                {
                    var context = await current.GetContextAsync();
                    if (!await slots.WaitAsync(0, stoppingToken))
                    { context.Response.StatusCode = 503; context.Response.Close(); continue; }
                    active.RemoveWhere(task => task.IsCompleted);
                    active.Add(HandleAsync(context, stoppingToken));
                }
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            { log.LogWarning("Relay webhook listener unavailable; retrying bind or receive after back-off."); }
            finally
            {
                try { current?.Close(); }
                catch (Exception) { log.LogWarning("Relay webhook listener close failed during recovery."); }
                try { await Task.WhenAll(active); }
                catch (Exception) { log.LogWarning("Relay webhook work failed during listener recovery."); }
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public static void ValidateBind(string bind) => RelayWebhookUrl.Validate(bind);

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(configuration.WebhookTimeoutSeconds, 1, 30)));
        try
        {
            if (context.Request.HttpMethod is not ("GET" or "POST")) { context.Response.StatusCode = 405; return; }
            var bytes = await RelayBounds.ReadAsync(context.Request.InputStream, RelayBounds.BodyLimit, timeout.Token);
            var headers = context.Request.Headers.AllKeys.Where(key => key is not null
                    && configuration.Declaration.WebhookHeaders.Contains(key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(key => key!, key => context.Request.Headers.GetValues(key!)!);
            var reply = await cloud.SendAsync<RelayWebhook, RelayWebhookReply>("/api/relay/v1/webhook",
                new(context.Request.HttpMethod, context.Request.Url!.Query, headers, bytes), timeout.Token);
            context.Response.StatusCode = reply.Status;
            foreach (var pair in reply.Headers.Where(pair => pair.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)))
                context.Response.ContentType = pair.Value.FirstOrDefault();
            await context.Response.OutputStream.WriteAsync(reply.Body, timeout.Token);
        }
        catch (InvalidDataException) { SetStatus(413); }
        catch (Exception)
        { SetStatus(503); }
        finally
        {
            try { context.Response.Close(); }
            catch (Exception) { log.LogWarning("Relay webhook response close failed."); }
            slots.Release();
        }

        void SetStatus(int status)
        {
            try { context.Response.StatusCode = status; }
            catch (Exception) { log.LogWarning("Relay webhook failure response could not be written."); }
        }
    }

    public override void Dispose() { listener?.Close(); slots.Dispose(); base.Dispose(); }
}
