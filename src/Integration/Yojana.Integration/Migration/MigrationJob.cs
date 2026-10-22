using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Integration.LegacyMigration.Profiling;
using Yojana.Integration.LegacyMigration.Canonicalisation;
using Yojana.Integration.LegacyMigration.Reconstruction;
using Yojana.Integration.LegacyMigration.Mapping;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Integration.LegacyMigration.Apply;

namespace Yojana.Integration.LegacyMigration;

[DisallowConcurrentExecution]
public sealed class MigrationExtractionJob(ITenantContextSetter setter, MigrationBatchService batches,
    MigrationProfileService profiles, CanonicalisationService canonicalisation,
    ReconstructionService reconstruction, MappingService mapping, IMigrationValidationQueue validation,
    ApplyService apply)
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
        await mapping.EnqueueEligibleAsync(cancellationToken);
        await mapping.RunPendingAsync(cancellationToken);
        await validation.EnqueueEligibleAsync(cancellationToken);
        await validation.RunPendingAsync(cancellationToken);
        // Loads are only ever requested by a person (POST .../apply); the job just runs what is queued.
        await apply.RunPendingAsync(cancellationToken);
    }
}
