using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Validation;

/// <summary>What the apply step asks before a final load (P9-T10): is this load plan signed off for real?</summary>
public interface IMigrationSignoffStatus
{
    /// <summary>True when a non-rehearsal sign-off record exists for a validation run of exactly this map run.</summary>
    Task<bool> HasFinalSignoffAsync(Guid mapRunId, CancellationToken cancellationToken);
}

public sealed class MigrationSignoffStatus(IntegrationDbContext db, ITenantContext tenant) : IMigrationSignoffStatus
{
    public Task<bool> HasFinalSignoffAsync(Guid mapRunId, CancellationToken cancellationToken) =>
        db.Set<MigrationSignoffRecord>().AsNoTracking().AnyAsync(record => record.TenantId == tenant.TenantId &&
            !record.Rehearsal && db.Set<MigrationValidationRun>().Any(run => run.TenantId == record.TenantId &&
                run.RunId == record.RunId && run.MapRunId == mapRunId && !run.Rehearsal), cancellationToken);
}
