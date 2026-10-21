using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;

namespace Yojana.Integration.LegacyMigration;

[DisallowConcurrentExecution]
public sealed class MigrationExtractionJob(ITenantContextSetter setter, MigrationBatchService batches)
    : TenantJob(setter)
{
    public const string JobName = "integration.migration-extraction";
    public const string Cron = "0/30 * * * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context,
        CancellationToken cancellationToken) => await batches.RunPendingAsync(cancellationToken);
}
