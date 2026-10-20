using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;

namespace Yojana.Platform.Security;

public sealed record SecurityMaintenanceResult(int ThrottleRows, int EventRows, int InvitationRows, int Batches);

public sealed class SecurityMaintenance(PlatformDbContext db, IClock clock, SignInOptions options)
{
    public async Task<SecurityMaintenanceResult> PurgeAsync(CancellationToken cancellationToken)
    {
        var throttleRows = 0;
        var eventRows = 0;
        var invitationRows = 0;
        var batches = 0;
        int deleted;
        do
        {
            deleted = await db.SignInThrottles.Where(row => row.ExpiresAt <= clock.UtcNow)
                .OrderBy(row => row.ExpiresAt).Take(options.MaintenanceBatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            throttleRows += deleted;
            if (deleted > 0) batches++;
        } while (deleted == options.MaintenanceBatchSize);

        var cutoff = clock.UtcNow.AddDays(-options.SecurityEventRetentionDays);
        do
        {
            deleted = await db.SecurityEvents.Where(row => row.OccurredAt < cutoff)
                .OrderBy(row => row.OccurredAt).ThenBy(row => row.Id).Take(options.MaintenanceBatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            eventRows += deleted;
            if (deleted > 0) batches++;
        } while (deleted == options.MaintenanceBatchSize);

        var invitationCutoff = clock.UtcNow.AddDays(-90);
        do
        {
            deleted = await db.UserInvitations
                .Where(row => row.ExpiresAt < invitationCutoff || row.AcceptedAt < invitationCutoff ||
                              row.RevokedAt < invitationCutoff)
                .OrderBy(row => row.ExpiresAt).ThenBy(row => row.Id).Take(options.MaintenanceBatchSize)
                .ExecuteDeleteAsync(cancellationToken);
            invitationRows += deleted;
            if (deleted > 0) batches++;
        } while (deleted == options.MaintenanceBatchSize);

        return new SecurityMaintenanceResult(throttleRows, eventRows, invitationRows, batches);
    }
}

[DisallowConcurrentExecution]
public sealed class SecurityMaintenanceJob(SecurityMaintenance maintenance) : IJob
{
    public const string JobName = "platform.security_maintenance";
    public const string Cron = "0 23 * * * ?";
    public static readonly JobKey Key = new(JobName, "database");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken) =>
        await maintenance.PurgeAsync(cancellationToken);
}

/// <summary>Schedules one database-wide cleanup job; startup never scans either security table.</summary>
public sealed class SecurityMaintenanceRegistrar(
    ISchedulerFactory schedulerFactory,
    ILogger<SecurityMaintenanceRegistrar> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        var detail = JobBuilder.Create<SecurityMaintenanceJob>().WithIdentity(SecurityMaintenanceJob.Key)
            .StoreDurably(true).Build();
        var trigger = TriggerBuilder.Create(TimeProvider.System)
            .WithIdentity(SecurityMaintenanceJob.Key.Name, SecurityMaintenanceJob.Key.Group)
            .ForJob(SecurityMaintenanceJob.Key)
            .WithSchedule(CronScheduleBuilder.Create(SecurityMaintenanceJob.Cron)
                .WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
            .Build();
        await scheduler.ScheduleJob(detail, trigger, ScheduleJobOptions.Replacing, cancellationToken);
        logger.LogInformation("Security throttle and event retention job scheduled ({Cron})", SecurityMaintenanceJob.Cron);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
