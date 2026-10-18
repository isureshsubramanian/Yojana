using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Serialisation.Imports;

public sealed class ImportQueries(SerialisationDbContext db, ImportService imports, ITenantContext tenant)
{
    public async Task<ImportJobView?> GetAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var job = await db.ImportJobs.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId && row.JobId == jobId, cancellationToken);
        return job is null ? null : await imports.MetadataViewAsync(job, cancellationToken);
    }

    public async Task<ImportJobPage> PageAsync(string? cursor, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 200);
        var query = db.ImportJobs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId);
        if (Parse(cursor) is { } after)
            query = query.Where(row => row.RecordedTime < after.Time || row.RecordedTime == after.Time && row.JobId.CompareTo(after.Id) < 0);
        var rows = await query.OrderByDescending(row => row.RecordedTime).ThenByDescending(row => row.JobId).Take(take + 1)
            .Select(row => new ImportJobSummary(row.JobId, row.Source, row.FileName, row.Format, row.Mode, row.Status,
                row.AppliedCount, row.SkippedCount, row.RejectedCount, row.RecordedTime)).ToListAsync(cancellationToken);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        var last = more ? rows[^1] : null;
        return new(rows, last is null ? null : $"{last.RecordedTime.Ticks.ToString(CultureInfo.InvariantCulture)}.{last.JobId:N}");
    }

    public async Task<ImportRowPage?> RowsAsync(Guid jobId, string? cursor, int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 500);
        if (!await db.ImportJobs.AsNoTracking().AnyAsync(row => row.TenantId == tenant.TenantId && row.JobId == jobId, cancellationToken))
            return null;
        var query = db.ImportRowResults.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.JobId == jobId);
        if (ParseRow(cursor) is { } after)
            query = query.Where(row => row.RowNumber > after.Number
                || row.RowNumber == after.Number && row.RowResultId.CompareTo(after.Id) > 0);
        var rows = await query.OrderBy(row => row.RowNumber).ThenBy(row => row.RowResultId).Take(take + 1).ToListAsync(cancellationToken);
        var more = rows.Count > take;
        if (more) rows.RemoveAt(rows.Count - 1);
        var labels = await imports.LabelsAsync(rows, cancellationToken);
        var views = rows.Select(row => ImportService.RowView(row, labels.Parties, labels.Units)).ToList();
        var last = more ? rows[^1] : null;
        return new(views, last is null ? null : $"{last.RowNumber.ToString(CultureInfo.InvariantCulture)}.{last.RowResultId:N}");
    }

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportRowsAsync(Guid jobId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await RowsAsync(jobId, cursor, 500, cancellationToken)
                ?? throw new CommandRefusedException("import_job_unknown", $"Import job {jobId} was not found.");
            foreach (var row in page.Items)
            {
                yield return [row.RowNumber.ToString(CultureInfo.InvariantCulture), row.Sheet, row.RawValue,
                    row.NormalisedValue, row.Outcome, row.Rule, row.OrderNumber, row.Lot, row.CustomerAssetTag,
                    row.ComparisonPartition, row.ProposedAction, row.ReviewedAction, row.CollisionUnitName, row.CollisionOwnerName];
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    private static (DateTime Time, Guid Id)? Parse(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        var parts = cursor.Split('.', 2);
        if (parts.Length != 2 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParseExact(parts[1], "N", out var id))
            throw new CommandRefusedException("cursor_invalid", "The import cursor is invalid.");
        return (new DateTime(ticks, DateTimeKind.Utc), id);
    }

    private static (int Number, Guid Id)? ParseRow(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        var parts = cursor.Split('.', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var number)
            || !Guid.TryParseExact(parts[1], "N", out var id))
            throw new CommandRefusedException("cursor_invalid", "The import-row cursor is invalid.");
        return (number, id);
    }
}
