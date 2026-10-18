using Microsoft.EntityFrameworkCore;
using Quartz;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Presence;

[DisallowConcurrentExecution]
public sealed class PresenceCleanupJob(ITenantContextSetter setter, PlatformDbContext db, IClock clock) : TenantJob(setter)
{
    public const string JobName = "platform.presence_cleanup";
    public const string Cron = "0 * * * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var cutoff = clock.UtcNow - PresenceService.Lifetime;
        await db.DocumentPresence.Where(p => p.LastHeartbeat < cutoff).ExecuteDeleteAsync(cancellationToken);
    }
}
