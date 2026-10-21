using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Reconstruction;

namespace Yojana.Integration.LegacyMigration;

[DisallowConcurrentExecution]
public sealed class MigrationExtractionJob(ITenantContextSetter setter, MigrationBatchService batches,
    MigrationProfileService profiles, CanonicalisationService canonicalisation,
    ReconstructionService reconstruction)
    : TenantJob(setter)
{
    public const string JobName = "integration.migration-extraction";
    public const string Cron = "0/30 * * * * ?";

    protected override async Task ExecuteForTenant(IJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        await batches.RunPendingAsync(cancellationToken);
        await profiles.RunPendingAsync(cancellationToken);
        await canonicalisation.EnqueueEligibleAsync(cancellationToken);
        await canonicalisation.RunPendingAsync(cancellationToken);
        await reconstruction.EnqueueEligibleAsync(cancellationToken);
        await reconstruction.RunPendingAsync(cancellationToken);
    }
}
