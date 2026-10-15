using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.Scheduling;

/// <summary>
/// Base class for every scheduled job that works for one tenant. The tenant id is carried in the job
/// data map under <see cref="TenantIdKey"/>; the job's scope is set to that tenant before
/// <see cref="ExecuteForTenant"/> runs. Quartz creates one DI scope per execution.
/// </summary>
public abstract class TenantJob(ITenantContextSetter setter) : IJob
{
    public const string TenantIdKey = "tenant_id";

    public async ValueTask Execute(IJobExecutionContext context, CancellationToken cancellationToken)
    {
        var raw = context.MergedJobDataMap.GetString(TenantIdKey);
        if (!TenantId.TryParse(raw, out var tenantId))
        {
            throw new JobExecutionException($"Job {context.JobDetail.Key} has no valid '{TenantIdKey}' in its job data.") { UnscheduleAllTriggers = false };
        }

        setter.Set(tenantId);
        await ExecuteForTenant(context, cancellationToken);
    }

    protected abstract Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken);

    /// <summary>Job key convention: one job per (job name, tenant) so tenants schedule independently.</summary>
    public static JobKey KeyFor(string jobName, TenantId tenantId) => new(jobName, "tenant:" + tenantId);

    public static JobDataMap DataFor(TenantId tenantId) => new() { { TenantIdKey, tenantId.ToString() } };
}
