using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Quartz;
using Yojana.Platform.Contracts.Time;

namespace Yojana.Serialisation.Ledger;

/// <summary>
/// Daily, database-wide run of <see cref="PartitionLifecycleService"/>. Not a <c>TenantJob</c>: partitions belong to
/// the database, not to a tenant, and the job reads no tenant rows.
/// </summary>
[DisallowConcurrentExecution]
public sealed class PartitionLifecycleJob(PartitionLifecycleService service, IClock clock) : IJob
{
    public const string JobName = "serialisation.partition_lifecycle";

    public static readonly JobKey Key = new(JobName, "database");

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        await service.RunAsync(DateOnly.FromDateTime(clock.UtcNow), cancellationToken);
    }
}

/// <summary>
/// Schedules the lifecycle job once (durable, replaced on every start so a cron change takes effect) after the
/// platform's migrator and scheduler have started: hosted services start in registration order and the module is
/// registered after the platform.
/// </summary>
public sealed class PartitionLifecycleRegistrar(ISchedulerFactory schedulerFactory, PartitionLifecycleOptions options, ILogger<PartitionLifecycleRegistrar> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var scheduler = await schedulerFactory.GetScheduler(cancellationToken);
        var detail = JobBuilder.Create<PartitionLifecycleJob>().WithIdentity(PartitionLifecycleJob.Key).StoreDurably(true).Build();
        var trigger = TriggerBuilder.Create(TimeProvider.System)
            .WithIdentity(PartitionLifecycleJob.Key.Name, PartitionLifecycleJob.Key.Group)
            .ForJob(PartitionLifecycleJob.Key)
            .WithSchedule(CronScheduleBuilder.Create(options.Cron).WithMisfireInstruction(CronTriggerMisfireInstruction.FireAndProceed))
            .Build();
        await scheduler.ScheduleJob(detail, trigger, ScheduleJobOptions.Replacing, cancellationToken);
        logger.LogInformation("Partition lifecycle job scheduled ({Cron}); {Ahead} months ahead, archive after {Archive} months, merge after {Merge} months",
            options.Cron, options.MonthsAhead, options.ArchiveAfterMonths, options.MergeAfterMonths);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
