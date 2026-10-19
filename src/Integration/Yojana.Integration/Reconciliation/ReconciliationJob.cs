using Quartz;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.Reconciliation;

[DisallowConcurrentExecution]
public sealed class ReconciliationJob(ITenantContextSetter setter, ReconciliationService service)
    : TenantJob(setter)
{
    public const string JobName = "integration-reconciliation";
    public const string Cron = "0 0 1 * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context,
        CancellationToken cancellationToken) => await service.RunScheduledAsync(cancellationToken);
}
