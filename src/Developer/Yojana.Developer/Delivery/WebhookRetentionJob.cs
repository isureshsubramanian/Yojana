using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Developer.Data;
using Yojana.Developer.Webhooks;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Developer.Delivery;

[DisallowConcurrentExecution]
public sealed class WebhookRetentionJob(ITenantContextSetter setter, WebhookRetentionService retention)
    : TenantJob(setter)
{
    public const string JobName = "developer.webhook-retention";
    public const string Cron = "0 23 2 * * ?";
    protected override async Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken)
        => await retention.RunAsync(cancellationToken);
}

public sealed class WebhookRetentionService(ITenantContext tenant, DeveloperDbContext db,
    DeveloperWebhookSettings settings, IClock clock)
{
    private const int BatchSize = 1000;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow.AddDays(-await settings.RetentionDaysAsync(cancellationToken));
        while (true)
        {
            var ids = await db.Deliveries.Where(row => row.TenantId == tenant.TenantId
                    && (row.Status == WebhookDeliveryStatus.Succeeded
                        || row.Status == WebhookDeliveryStatus.Discarded)
                    && row.CreatedAt < cutoff)
                .OrderBy(row => row.CreatedAt).ThenBy(row => row.DeliveryId)
                .Select(row => row.DeliveryId).Take(BatchSize).ToListAsync(cancellationToken);
            if (ids.Count == 0) break;
            await db.DeliveryAttempts.Where(row => row.TenantId == tenant.TenantId
                && ids.Contains(row.DeliveryId)).ExecuteDeleteAsync(cancellationToken);
            await db.Deliveries.Where(row => row.TenantId == tenant.TenantId
                && ids.Contains(row.DeliveryId)).ExecuteDeleteAsync(cancellationToken);
        }
        while (true)
        {
            var ids = await db.PublicEvents.Where(row => row.TenantId == tenant.TenantId
                    && row.RecordedAt < cutoff
                    && !db.Deliveries.Any(delivery => delivery.TenantId == row.TenantId
                        && delivery.EventId == row.EventId))
                .OrderBy(row => row.RecordedAt).ThenBy(row => row.EventId)
                .Select(row => row.EventId).Take(BatchSize).ToListAsync(cancellationToken);
            if (ids.Count == 0) break;
            await db.PublicEvents.Where(row => row.TenantId == tenant.TenantId
                && ids.Contains(row.EventId)).ExecuteDeleteAsync(cancellationToken);
        }
    }
}
