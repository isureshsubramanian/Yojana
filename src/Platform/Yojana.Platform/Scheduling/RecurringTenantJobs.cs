using Quartz;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Scheduling;

/// <summary>A recurring job every tenant gets. Registered by modules; scheduled per tenant by <see cref="RecurringTenantJobScheduler"/>.</summary>
public sealed record RecurringTenantJob(string JobName, Type JobType, string CronExpression)
{
    public static RecurringTenantJob For<TJob>(string jobName, string cronExpression) where TJob : TenantJob =>
        new(jobName, typeof(TJob), cronExpression);
}

/// <summary>Ensures each registered recurring job exists for each tenant (idempotent; safe to run at every start).</summary>
public sealed class RecurringTenantJobScheduler(IEnumerable<RecurringTenantJob> jobs)
{
    public async Task EnsureScheduledAsync(IScheduler scheduler, IEnumerable<TenantId> tenants, CancellationToken cancellationToken)
    {
        foreach (var tenant in tenants)
        {
            foreach (var job in jobs)
            {
                var key = TenantJob.KeyFor(job.JobName, tenant);
                var detail = JobBuilder.Create()
                    .OfType(job.JobType)
                    .WithIdentity(key)
                    .UsingJobData(TenantJob.DataFor(tenant))
                    .StoreDurably(true)
                    .Build();
                var trigger = TriggerBuilder.Create(TimeProvider.System)
                    .WithIdentity(key.Name, key.Group)
                    .ForJob(key)
                    .WithSchedule(CronScheduleBuilder.Create(job.CronExpression).WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
                    .Build();
                await scheduler.ScheduleJob(detail, trigger, ScheduleJobOptions.Replacing, cancellationToken);
            }
        }
    }
}
