using Quartz;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Ordering;

[DisallowConcurrentExecution]
public sealed class ResequencerJob(ITenantContextSetter setter, BoundedWaitResequencer resequencer) : TenantJob(setter)
{
    public const string JobName = "integration.resequencer";
    public const string Cron = "0 * * * * ?";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        resequencer.ReleaseExpiredAsync(cancellationToken);
}
