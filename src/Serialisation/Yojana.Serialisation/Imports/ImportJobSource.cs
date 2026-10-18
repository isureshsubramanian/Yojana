using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Imports;

public sealed class ImportJobSource(SerialisationDbContext db, ITenantContext tenant) : IImportJobSource
{
    public async Task<IReadOnlyList<ImportJobSourceRow>> ReadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.ImportJobs.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.JobId == jobId, cancellationToken)
            ?? throw new CommandRefusedException("import_job_unknown", $"Import job {jobId} was not found.");
        var target = job.TargetId is { } id && job.TargetType is { } type ? new DocumentRef(type, id, job.TargetLine) : null;
        var rows = await db.ImportRowResults.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.JobId == jobId)
            .OrderBy(row => row.RowNumber).ThenBy(row => row.RowResultId)
            .Select(row => new { row.UnitId, row.RawValue, row.NormalisedValue, row.Outcome, row.Rule, row.ReviewedAction, row.ProposedAction })
            .ToListAsync(cancellationToken);
        return rows.Where(row => row.Outcome != ImportOutcome.Rejected
                && (job.Mode != ImportMode.Compare || (row.ReviewedAction ?? row.ProposedAction) == ImportReviewAction.Assign))
            .Select(row => new ImportJobSourceRow(row.UnitId, row.RawValue ?? "", row.NormalisedValue, target, row.Outcome, row.Rule)).ToList();
    }
}
