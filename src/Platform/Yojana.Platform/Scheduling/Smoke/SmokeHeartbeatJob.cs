using Quartz;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Scheduling.Smoke;

/// <summary>P0 smoke job: writes one row per run for its tenant, proving the SQL Server job store and tenant scoping work end to end.</summary>
[DisallowConcurrentExecution]
public sealed class SmokeHeartbeatJob(ITenantContextSetter setter, ITenantContext tenant, PlatformDbContext db, IClock clock) : TenantJob(setter)
{
    public const string JobName = "platform.smoke_heartbeat";

    /// <summary>Every five minutes; cheap enough to leave on in development.</summary>
    public const string Cron = "0 0/5 * * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        db.SmokeLogs.Add(new SmokeLog
        {
            TenantId = tenant.TenantId,
            Id = TimeOrderedId.New(clock.UtcNow),
            Source = "job",
            Note = $"heartbeat fired at {context.FireTimeUtc:O}",
            RecordedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
