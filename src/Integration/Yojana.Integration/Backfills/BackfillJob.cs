using Quartz;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Backfills;

[DisallowConcurrentExecution]
public sealed class BackfillJob(ITenantContextSetter setter, BackfillService service) : TenantJob(setter)
{
    public const string JobName = "integration.backfill";
    public const string Cron = "0 * * * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context,
        CancellationToken cancellationToken) => await service.RunPendingAsync(cancellationToken);
}
