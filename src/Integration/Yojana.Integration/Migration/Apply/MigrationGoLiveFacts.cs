using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Coexistence;
using Yojana.Integration.Data;
using Yojana.Integration.LegacyMigration.Validation;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Apply;

/// <summary>
/// The cutover facts the go-live record shows (P9-T11), read from this connection's migration loads and sign-off
/// records: the final sign-off, the load that brought the module up to date at cutover (the latest delta, else the
/// final load) and the latest rehearsed rollback. Loads keep the console's labels (R1, F1, D1).
/// </summary>
public sealed class MigrationGoLiveFactsReader(IntegrationDbContext db, ITenantContext tenant, ApplyService loads)
    : IMigrationGoLiveFacts
{
    public async Task<MigrationGoLiveFacts> ReadAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var summaries = (await loads.LoadsAsync(connectionId, null, 100, cancellationToken)).Items;

        var cutover = summaries.Where(row => row.Status == ApplyRunStatus.Completed && row.Kind != ApplyService.RehearsalKind
                && row.StartedAt != null && row.FinishedAt != null)
            .OrderByDescending(row => row.FinishedAt).ThenByDescending(row => row.LoadId).FirstOrDefault();
        var final = summaries.Where(row => row.Status == ApplyRunStatus.Completed && row.Kind == ApplyService.FinalKind)
            .OrderByDescending(row => row.FinishedAt).FirstOrDefault();
        var rollback = summaries.Where(row => row.Kind == ApplyService.RehearsalKind && row.RollbackStartedAt != null
                && row.RolledBackAt != null)
            .OrderByDescending(row => row.RolledBackAt).ThenByDescending(row => row.LoadId).FirstOrDefault();

        var signoff = await (from record in db.MigrationSignoffRecords.AsNoTracking()
                join validation in db.MigrationValidationRuns.AsNoTracking()
                    on new { record.TenantId, record.RunId } equals new { validation.TenantId, validation.RunId }
                join map in db.MigrationMapRuns.AsNoTracking()
                    on new { validation.TenantId, RunId = validation.MapRunId } equals new { map.TenantId, map.RunId }
                join batch in db.MigrationBatches.AsNoTracking()
                    on new { map.TenantId, map.BatchId } equals new { batch.TenantId, batch.BatchId }
                where record.TenantId == tenant.TenantId && !record.Rehearsal && batch.ConnectionId == connectionId
                orderby record.CreatedAt descending, record.RecordId descending
                select new { record.RecordId, record.CreatedAt })
            .FirstOrDefaultAsync(cancellationToken);
        FinalSignoffFact? signed = null;
        if (signoff is not null)
        {
            var results = await db.MigrationSignoffLines.AsNoTracking().Where(line => line.TenantId == tenant.TenantId
                && line.RecordId == signoff.RecordId).Select(line => line.Result).ToListAsync(cancellationToken);
            signed = new(signoff.CreatedAt, results.Count, results.Count(result =>
                result is ValidationResult.Pass or ValidationResult.Explained or ValidationResult.Report));
        }

        return new(signed,
            cutover is null ? null : new(cutover.Label, cutover.Kind, cutover.FinishedAt!.Value,
                cutover.FinishedAt.Value - cutover.StartedAt!.Value),
            rollback is null ? null : new(rollback.Label, rollback.Kind, rollback.RolledBackAt!.Value,
                rollback.RolledBackAt.Value - rollback.RollbackStartedAt!.Value),
            final?.HandOver?.From ?? final?.FinishedAt);
    }
}
