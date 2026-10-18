using Quartz;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Platform.Scheduling;

/// <summary>A recurring job every tenant gets. Registered by modules; scheduled per tenant by <see cref="RecurringTenantJobScheduler"/>.</summary>
public sealed record RecurringTenantJob(string JobName, Type JobType, string CronExpression)
{
    public static RecurringTenantJob For<TJob>(string jobName, string cronExpression) where TJob : TenantJob =>
        new(jobName, typeof(TJob), cronExpression);
}

/// <summary>The changes made by one recurring-job reconciliation.</summary>
public sealed record RecurringJobReconciliationResult(
    int TenantCount,
    int ExpectedJobCount,
    int CreatedJobCount,
    int UpdatedJobCount,
    int UnchangedJobCount)
{
    public int ScheduledJobCount => CreatedJobCount + UpdatedJobCount;
}

/// <summary>Ensures each registered recurring job exists for each tenant, writing only missing or changed schedules.</summary>
public sealed class RecurringTenantJobScheduler
{
    private readonly IReadOnlyList<RecurringTenantJob> jobs;

    public RecurringTenantJobScheduler(IEnumerable<RecurringTenantJob> jobs)
    {
        this.jobs = jobs.ToList();
        var duplicate = this.jobs.GroupBy(job => job.JobName, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Recurring job name '{duplicate.Key}' is registered more than once.");
        }
    }

    public async Task<RecurringJobReconciliationResult> EnsureScheduledAsync(
        IScheduler scheduler,
        IEnumerable<TenantId> tenants,
        CancellationToken cancellationToken)
    {
        var tenantList = tenants.Distinct().ToList();
        var expected = (from tenant in tenantList
                        from job in jobs
                        let key = TenantJob.KeyFor(job.JobName, tenant)
                        select new ExpectedSchedule(job, key, BuildDetail(job, key, tenant), BuildTrigger(job, key)))
            .ToList();

        if (expected.Count == 0)
        {
            return new RecurringJobReconciliationResult(tenantList.Count, 0, 0, 0, 0);
        }

        // Quartz 4.5 exposes batch reads for details and triggers. These are deliberately one bulk read each:
        // querying per tenant made host work grow by tenant count even when every stored schedule was unchanged.
        var storedJobs = (await scheduler.GetJobDetails(expected.Select(item => item.Key).ToList(), cancellationToken))
            .ToDictionary(detail => detail.Key);
        var storedTriggers = (await scheduler.GetTriggers(expected.Select(item => item.Trigger.Key).ToList(), cancellationToken))
            .ToDictionary(trigger => trigger.Key);

        var missing = new Dictionary<IJobDetail, IReadOnlyCollection<ITrigger>>();
        var changed = new Dictionary<IJobDetail, IReadOnlyCollection<ITrigger>>();
        var unchanged = 0;
        foreach (var item in expected)
        {
            if (!storedJobs.TryGetValue(item.Key, out var storedJob))
            {
                missing.Add(item.Detail, [item.Trigger]);
                continue;
            }

            var triggerExists = storedTriggers.TryGetValue(item.Trigger.Key, out var storedTrigger);
            var typeChanged = !StringComparer.Ordinal.Equals(storedJob.JobType.FullName, item.Detail.JobType.FullName);
            var cronChanged = storedTrigger is not ICronTrigger cron ||
                !StringComparer.Ordinal.Equals(cron.CronExpressionString, item.Job.CronExpression);
            if (typeChanged || !triggerExists || cronChanged)
            {
                changed.Add(item.Detail, [item.Trigger]);
            }
            else
            {
                unchanged++;
            }
        }

        if (missing.Count > 0)
        {
            await scheduler.ScheduleJobs(missing, default, cancellationToken);
        }

        if (changed.Count > 0)
        {
            await scheduler.ScheduleJobs(changed, ScheduleJobOptions.Replacing, cancellationToken);
        }

        return new RecurringJobReconciliationResult(
            tenantList.Count,
            expected.Count,
            missing.Count,
            changed.Count,
            unchanged);
    }

    private static IJobDetail BuildDetail(RecurringTenantJob job, JobKey key, TenantId tenant) =>
        JobBuilder.Create()
            .OfType(job.JobType)
            .WithIdentity(key)
            .UsingJobData(TenantJob.DataFor(tenant))
            .StoreDurably(true)
            .Build();

    private static ITrigger BuildTrigger(RecurringTenantJob job, JobKey key) =>
        TriggerBuilder.Create(TimeProvider.System)
            .WithIdentity(key.Name, key.Group)
            .ForJob(key)
            .WithSchedule(CronScheduleBuilder.Create(job.CronExpression).WithMisfireInstruction(CronTriggerMisfireInstruction.DoNothing))
            .Build();

    private sealed record ExpectedSchedule(RecurringTenantJob Job, JobKey Key, IJobDetail Detail, ITrigger Trigger);
}

/// <summary>Narrow provisioning seam that gives a newly committed tenant its registered recurring jobs.</summary>
public sealed class RecurringTenantJobProvisioner(
    ISchedulerFactory schedulerFactory,
    RecurringTenantJobScheduler scheduler,
    RecurringJobReconciliationLock reconciliationLock)
{
    public async Task<RecurringJobReconciliationResult> EnsureScheduledAsync(TenantId tenant, CancellationToken cancellationToken)
    {
        await using var lease = await reconciliationLock.AcquireAsync(cancellationToken);
        var quartz = await schedulerFactory.GetScheduler(cancellationToken);
        return await scheduler.EnsureScheduledAsync(quartz, [tenant], cancellationToken);
    }
}
