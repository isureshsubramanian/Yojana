using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Yojana.Developer.Data;
using Yojana.Developer.Data.Entities;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Mail;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Security;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Developer.Delivery;

public static class WebhookRetrySchedule
{
    public static TimeSpan After(int failedAttempt, double jitter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempt, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(jitter);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(jitter, 1);
        var ceiling = Math.Min(3600d, 30d * Math.Pow(2, Math.Min(failedAttempt - 1, 20)));
        return TimeSpan.FromSeconds(ceiling * jitter);
    }
}

[DisallowConcurrentExecution]
public sealed class WebhookDeliveryJob(ITenantContextSetter setter, ITenantContext tenant,
    DeveloperDbContext db, WebhookDeliveryCoordinator coordinator)
    : TenantJob(setter)
{
    public const string JobName = "developer.webhook-delivery";
    public const string Cron = "0/1 * * * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var ids = await db.Subscriptions.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && row.Status == WebhookSubscriptionStatus.Active
                && db.Deliveries.Any(delivery => delivery.TenantId == row.TenantId
                    && delivery.SubscriptionId == row.SubscriptionId
                    && delivery.Status == WebhookDeliveryStatus.Pending))
            .OrderBy(row => row.SubscriptionId).Select(row => row.SubscriptionId).ToListAsync(cancellationToken);
        foreach (var id in ids) coordinator.TryStart(tenant.TenantId, id);
    }
}

/// <summary>
/// Keeps one independent dispatch task per subscription on this host. A slow request therefore outlives the Quartz
/// tick that discovered it without holding the next tick or any other subscription. Host shutdown cancels and joins
/// every tracked task; the database lock in <see cref="WebhookSubscriptionDispatchLock"/> supplies cross-host safety.
/// </summary>
public sealed class WebhookDeliveryCoordinator(IServiceScopeFactory scopes,
    Microsoft.Extensions.Options.IOptions<DeveloperWebhookOptions> options,
    ILogger<WebhookDeliveryCoordinator> logger) : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<(TenantId Tenant, Guid Subscription), Task> active = new();
    private readonly CancellationTokenSource stopping = new();
    // Bounds the unpooled lock connections and in-flight requests on this host. A subscription waiting here is still
    // tracked in `active`, so a later tick cannot start it twice.
    private readonly SemaphoreSlim sending = new(options.Value.SubscriptionParallelism);

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public bool TryStart(TenantId tenant, Guid subscriptionId)
    {
        if (stopping.IsCancellationRequested) return false;
        var key = (tenant, subscriptionId);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!active.TryAdd(key, completed.Task)) return false;
        _ = RunAsync(key, completed);
        return true;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        var tasks = active.Values.ToArray();
        if (tasks.Length > 0) await Task.WhenAll(tasks).WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        stopping.Dispose();
        sending.Dispose();
    }

    private async Task RunAsync((TenantId Tenant, Guid Subscription) key,
        TaskCompletionSource completed)
    {
        var entered = false;
        try
        {
            await sending.WaitAsync(stopping.Token);
            entered = true;
            await using var scope = scopes.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(key.Tenant);
            await scope.ServiceProvider.GetRequiredService<WebhookDeliveryDispatcher>()
                .DispatchSubscriptionAsync(key.Subscription, stopping.Token);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception error)
        {
            logger.LogError(error, "Webhook subscription dispatch failed before an attempt could be recorded.");
        }
        finally
        {
            if (entered) sending.Release();
            active.TryRemove(key, out _);
            completed.TrySetResult();
        }
    }
}

/// <summary>
/// TIGHT COUPLING: this session-owned SQL Server lock must use the same database as the Developer delivery tables.
/// It is the cross-host half of the coordinator's one-task-per-subscription rule; moving it elsewhere would allow two
/// hosts to send the same subscription concurrently and break strict ordering.
/// </summary>
public sealed class WebhookSubscriptionDispatchLock(DeveloperDbContext db, ITenantContext tenant)
{
    public async Task<IAsyncDisposable?> TryAcquireAsync(Guid subscriptionId,
        CancellationToken cancellationToken)
    {
        // The session lock intentionally lives for the HTTP attempt. Keep it out of the EF connection pool so slow
        // endpoints cannot consume the pool that independent subscriptions need for their own delivery queries.
        var connectionString = new SqlConnectionStringBuilder(db.Database.GetConnectionString()) { Pooling = false };
        var connection = new SqlConnection(connectionString.ConnectionString);
        var resource = $"yojana:developer:webhook:{tenant.TenantId.Value:N}:{subscriptionId:N}";
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0; SELECT @result;";
            command.Parameters.AddWithValue("@resource", resource);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (result < 0)
            {
                await connection.DisposeAsync();
                return null;
            }
            return new Lease(connection, resource);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(SqlConnection connection, string resource) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            var released = false;
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session'; SELECT @result;";
                command.Parameters.AddWithValue("@resource", resource);
                var result = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None),
                    System.Globalization.CultureInfo.InvariantCulture);
                released = result >= 0;
            }
            finally
            {
                if (!released) SqlConnection.ClearPool(connection);
                await connection.DisposeAsync();
            }
        }

    }
}

public sealed class WebhookDeliveryDispatcher(DeveloperDbContext db, ITenantContext tenant, IClock clock,
    WebhookTransport transport, ITenantStaffDirectory staff, IMailOutbox mail,
    SecurityEventWriter security, PlatformDbContext platform, WebhookSubscriptionDispatchLock dispatchLock)
{
    public async Task DispatchSubscriptionAsync(Guid subscriptionId, CancellationToken ct)
    {
        // TIGHT COUPLING counterpart: this lease must span selection, HTTP and persistence. Shortening it would let a
        // second host select the same head while the first request is in flight, breaking per-subscription ordering.
        await using var lease = await dispatchLock.TryAcquireAsync(subscriptionId, ct);
        if (lease is null) return;
        var subscription = await db.Subscriptions.SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.SubscriptionId == subscriptionId && row.Status == WebhookSubscriptionStatus.Active, ct);
        if (subscription is null) return;
        var maximum = Math.Clamp(subscription.RatePerSecond, 1, 1000);
        for (var sent = 0; sent < maximum && subscription.Status == WebhookSubscriptionStatus.Active; sent++)
        {
            var head = await (from candidate in db.Deliveries
                              join publicEvent in db.PublicEvents
                                  on new { candidate.TenantId, candidate.EventId }
                                  equals new { publicEvent.TenantId, publicEvent.EventId }
                              where candidate.TenantId == tenant.TenantId
                                 && candidate.SubscriptionId == subscriptionId
                                 && candidate.Status == WebhookDeliveryStatus.Pending
                              orderby candidate.QueuePosition
                              select new
                              {
                                  Delivery = candidate,
                                  EventBody = publicEvent.Body,
                                  publicEvent.EventId,
                                  publicEvent.Type,
                              }).FirstOrDefaultAsync(ct);
            if (head is null) return;
            var delivery = head.Delivery;
            if (delivery.NextAttemptAt > clock.UtcNow) return;
            var attempt = delivery.AttemptCount + 1;
            WebhookSendResult result;
            try
            {
                result = await transport.SendAsync(subscription, Encoding.UTF8.GetBytes(head.EventBody),
                    head.EventId, head.Type, attempt, ct);
            }
            catch (CommandRefusedException error)
            {
                result = new(false, null, 0, null, error.Rule);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                result = new(false, null, 0, null, "transport_error");
            }
            delivery.AttemptCount = attempt;
            delivery.LastAttemptAt = clock.UtcNow;
            db.DeliveryAttempts.Add(new WebhookDeliveryAttempt
            {
                TenantId = tenant.TenantId, AttemptId = Guid.CreateVersion7(clock.UtcNow),
                DeliveryId = delivery.DeliveryId, AttemptedAt = clock.UtcNow,
                Outcome = result.Success ? "succeeded" : "failed", StatusCode = result.StatusCode,
                DurationMs = result.DurationMs, ResponseExcerpt = result.ResponseExcerpt,
                ErrorCode = result.ErrorCode,
            });
            if (result.Success)
            {
                delivery.Status = WebhookDeliveryStatus.Succeeded;
                delivery.SucceededAt = clock.UtcNow;
                delivery.RetryWindowStartedAt = null;
                subscription.FailingSince = null;
            }
            else
            {
                subscription.FailingSince ??= clock.UtcNow;
                delivery.RetryWindowStartedAt ??= clock.UtcNow;
                if (result.StatusCode == (int)HttpStatusCode.Gone)
                    await PauseAndNotifyAsync(subscription, "endpoint_gone", ct);
                else if (subscription.FailingSince <= clock.UtcNow.AddHours(-72))
                    await PauseAndNotifyAsync(subscription, "continuous_failure", ct);
                if (delivery.RetryWindowStartedAt <= clock.UtcNow.AddHours(-24))
                {
                    delivery.Status = WebhookDeliveryStatus.Parked;
                    delivery.ParkedAt = clock.UtcNow;
                }
                else
                    delivery.NextAttemptAt = clock.UtcNow + WebhookRetrySchedule.After(attempt, Random.Shared.NextDouble());
            }
            await db.SaveChangesAsync(ct);
            await platform.SaveChangesAsync(ct);
            if (!result.Success) return;
        }
    }

    private async Task PauseAndNotifyAsync(WebhookSubscription subscription, string reason, CancellationToken ct)
    {
        subscription.Status = WebhookSubscriptionStatus.Paused;
        subscription.PausedAt = clock.UtcNow;
        subscription.PausedReason = reason;
        subscription.UpdatedAt = clock.UtcNow;
        security.Add(SecurityEventKind.SettingsChanged, null, tenant.TenantId, null,
            new SecurityRequestContext(null, null), "webhook_automatically_paused");
        var recipients = await staff.NotificationRecipientsAsync(null, YojanaRole.TenantAdministrator, ct);
        const string text = "A webhook subscription was paused after repeated delivery failures. Its queued events are retained. Review the endpoint and resume the subscription when it is healthy.";
        foreach (var recipient in recipients)
            await mail.QueueAsync(new("developer.webhook_paused", tenant.TenantId, recipient.UserId,
                new(recipient.Email, recipient.Name), "Webhook subscription paused",
                $"<p>{WebUtility.HtmlEncode(text)}</p>", text), ct);
    }
}
