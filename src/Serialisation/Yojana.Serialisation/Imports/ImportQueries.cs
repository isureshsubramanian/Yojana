using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Paging;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Imports;

public sealed class ImportQueries(SerialisationDbContext db, ImportService imports, ITenantContext tenant,
    IUserDirectory users)
{
    private static readonly SortedPage<ImportJob, Guid> HistoryPages = new("uploaded", row => row.JobId,
        new SortColumn<ImportJob, string>("file", row => row.FileName),
        new SortColumn<ImportJob, DateTime>("uploaded", row => row.RecordedTime, SortDirection.Descending),
        new SortColumn<ImportJob, string>("status", row => row.Status));
    private static readonly SortedPage<ThreePlFeedMessage, Guid> FeedPages = new("received", row => row.FeedMessageId,
        new SortColumn<ThreePlFeedMessage, string>("message", row => row.MessageReference),
        new SortColumn<ThreePlFeedMessage, DateTime>("received", row => row.RecordedTime, SortDirection.Descending),
        new SortColumn<ThreePlFeedMessage, string>("status", row => row.Status));
    private static readonly SortedPage<ImportJob, Guid> ComparisonPages = new("ran", row => row.JobId,
        new SortColumn<ImportJob, string>("file", row => row.FileName),
        new SortColumn<ImportJob, DateTime>("ran", row => row.RecordedTime, SortDirection.Descending),
        new SortColumn<ImportJob, string>("status", row => row.Status));

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

    public async Task<ImportFileHistoryPage> HistoryAsync(ImportFileHistoryFilter filter, string? sort, string? direction,
        string? cursor, int take,
        CancellationToken cancellationToken)
    {
        var paging = HistoryPages.Parse(sort, direction, take, cursor);
        // Upload history lists files people uploaded: compare results and feed messages store no file and stay out.
        var query = db.ImportJobs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && (row.OriginalFileReference != null || row.OriginalContent != null));
        if (filter.From is { } from) query = query.Where(row => row.RecordedTime >= from);
        if (filter.To is { } to) query = query.Where(row => row.RecordedTime < to);
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(row => row.Status == filter.Status);
        if (!string.IsNullOrWhiteSpace(filter.Uploader)) query = query.Where(row => row.ActorRef == filter.Uploader);
        if (!string.IsNullOrWhiteSpace(filter.Find))
            query = query.Where(row => row.FileName.Contains(filter.Find)
                || row.OriginalHash.Contains(filter.Find)
                || row.ConvertedHash != null && row.ConvertedHash.Contains(filter.Find));
        if (!string.IsNullOrWhiteSpace(filter.FileName)) query = query.Where(row => row.FileName.Contains(filter.FileName));
        if (!string.IsNullOrWhiteSpace(filter.Hash))
            query = query.Where(row => row.OriginalHash == filter.Hash || row.ConvertedHash == filter.Hash);
        var rows = await paging.Apply(query).Take(paging.Take + 1)
            .Select(row => new HistoryProjection
            {
                JobId = row.JobId, FileName = row.FileName, Format = row.Format,
                Size = row.OriginalLength ?? (row.OriginalContent == null ? 0 : row.OriginalContent.LongLength),
                OriginalHash = row.OriginalHash, ConvertedHash = row.ConvertedHash, ActorRef = row.ActorRef,
                RecordedTime = row.RecordedTime, Source = row.Source, Status = row.Status,
                Applied = row.AppliedCount, Skipped = row.SkippedCount, Rejected = row.RejectedCount,
                Archived = row.OriginalArchived, OriginalAvailable = row.OriginalFileReference != null || row.OriginalContent != null,
                ConvertedAvailable = row.ConvertedFileReference != null || row.ConvertedContent != null,
                OriginalReference = row.OriginalFileReference,
            }).ToListAsync(cancellationToken);
        var more = rows.Count > paging.Take;
        if (more) rows.RemoveAt(rows.Count - 1);
        var names = await users.DisplayNamesAsync(rows.Select(row => row.ActorRef).Distinct().ToList(), cancellationToken);
        var items = rows.Select(row => new ImportFileHistoryRow(row.JobId, row.FileName, row.Format, row.Size,
            row.OriginalHash, row.ConvertedHash, names.GetValueOrDefault(row.ActorRef, "Former user"), row.ActorRef,
            row.RecordedTime, row.Source, row.Status, row.Applied, row.Skipped, row.Rejected,
            row.Archived ? "archived" : "active", row.OriginalAvailable, row.ConvertedAvailable,
            ImportFileStore.ArchiveStoreName(row.OriginalReference))).ToList();
        var last = more ? rows[^1] : null;
        return new(items, last is null ? null : paging.CursorFor(ImportCursorRow(last)));
    }

    public Task<ImportFileHistoryPage> HistoryAsync(ImportFileHistoryFilter filter, string? cursor, int take,
        CancellationToken cancellationToken) => HistoryAsync(filter, null, null, cursor, take, cancellationToken);

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportHistoryAsync(ImportFileHistoryFilter filter, string? sort, string? direction,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await HistoryAsync(filter, sort, direction, cursor, 200, cancellationToken);
            foreach (var row in page.Items)
                yield return [row.FileName, row.Format, row.Size.ToString(CultureInfo.InvariantCulture), row.OriginalHash,
                    row.ConvertedHash, row.UploaderName, row.UploadedAt.ToString("O", CultureInfo.InvariantCulture), row.Source,
                    row.Status, row.Applied.ToString(CultureInfo.InvariantCulture), row.Skipped.ToString(CultureInfo.InvariantCulture),
                    row.Rejected.ToString(CultureInfo.InvariantCulture), row.StorageState];
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    public IAsyncEnumerable<IReadOnlyList<string?>> ExportHistoryAsync(ImportFileHistoryFilter filter,
        CancellationToken cancellationToken) => ExportHistoryAsync(filter, null, null, cancellationToken);

    public async Task<FeedMessageHistoryPage> FeedMessagesAsync(ImportHistoryFilter filter, string? sort, string? direction,
        string? cursor, int take,
        CancellationToken cancellationToken)
    {
        var paging = FeedPages.Parse(sort, direction, take, cursor);
        var query = db.ThreePlFeedMessages.AsNoTracking().Where(row => row.TenantId == tenant.TenantId);
        if (filter.From is { } from) query = query.Where(row => row.RecordedTime >= from);
        if (filter.To is { } to) query = query.Where(row => row.RecordedTime < to);
        if (!string.IsNullOrWhiteSpace(filter.Find))
            query = query.Where(row => row.MessageReference.Contains(filter.Find) || row.PayloadHash.Contains(filter.Find));
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(row => row.Status == filter.Status);
        var rows = await paging.Apply(query).Take(paging.Take + 1).Select(row => new FeedProjection
            {
                FeedMessageId = row.FeedMessageId, MessageReference = row.MessageReference,
                DocumentType = row.DocumentType, DocumentId = row.DocumentId, DocumentLine = row.DocumentLine,
                ImportJobId = row.ImportJobId, PayloadHash = row.PayloadHash, Status = row.Status,
                Shipped = row.ShippedCount, AssetTags = row.AssetTagCount, Rejected = row.RejectedCount,
                ActorRef = row.ActorRef, RecordedTime = row.RecordedTime,
            }).ToListAsync(cancellationToken);
        var more = rows.Count > paging.Take;
        if (more) rows.RemoveAt(rows.Count - 1);
        var names = await users.DisplayNamesAsync(rows.Select(row => row.ActorRef).Distinct().ToList(), cancellationToken);
        var items = rows.Select(row => new FeedMessageHistoryRow(row.FeedMessageId, row.MessageReference,
            row.DocumentType, row.DocumentId, row.DocumentLine, row.ImportJobId, row.PayloadHash, row.Status,
            row.Shipped, row.AssetTags, row.Rejected, names.GetValueOrDefault(row.ActorRef, "Former user"),
            row.RecordedTime)).ToList();
        var last = more ? rows[^1] : null;
        return new(items, last is null ? null : paging.CursorFor(FeedCursorRow(last)));
    }

    public Task<FeedMessageHistoryPage> FeedMessagesAsync(ImportHistoryFilter filter, string? cursor, int take,
        CancellationToken cancellationToken) => FeedMessagesAsync(filter, null, null, cursor, take, cancellationToken);

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportFeedMessagesAsync(ImportHistoryFilter filter, string? sort, string? direction,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await FeedMessagesAsync(filter, sort, direction, cursor, 200, cancellationToken);
            foreach (var row in page.Items)
                yield return [row.MessageReference, row.DocumentType, row.DocumentId.ToString("D"),
                    row.DocumentLine.ToString(CultureInfo.InvariantCulture), row.ReceivedAt.ToString("O", CultureInfo.InvariantCulture),
                    row.CallerName, row.Status, row.Shipped.ToString(CultureInfo.InvariantCulture),
                    row.AssetTags.ToString(CultureInfo.InvariantCulture), row.Rejected.ToString(CultureInfo.InvariantCulture),
                    row.PayloadHash, row.ImportJobId.ToString("D")];
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    public IAsyncEnumerable<IReadOnlyList<string?>> ExportFeedMessagesAsync(ImportHistoryFilter filter,
        CancellationToken cancellationToken) => ExportFeedMessagesAsync(filter, null, null, cancellationToken);

    public async Task<ComparisonHistoryPage> ComparisonsAsync(ImportHistoryFilter filter, string? sort, string? direction,
        string? cursor, int take,
        CancellationToken cancellationToken)
    {
        var paging = ComparisonPages.Parse(sort, direction, take, cursor);
        var query = db.ImportJobs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
            && row.Mode == ImportMode.Compare && row.ParentJobId != null
            && row.OriginalFileReference == null && row.OriginalContent == null
            && !row.FileName.EndsWith(" versus selection"));
        if (filter.From is { } from) query = query.Where(row => row.RecordedTime >= from);
        if (filter.To is { } to) query = query.Where(row => row.RecordedTime < to);
        if (!string.IsNullOrWhiteSpace(filter.Find))
        {
            var find = filter.Find;
            query = query.Where(row => row.FileName.Contains(find) || db.ImportJobs.Any(input =>
                input.TenantId == tenant.TenantId
                && (input.OriginalFileReference != null || input.OriginalContent != null)
                && (input.OriginalHash.Contains(find) || input.ConvertedHash != null && input.ConvertedHash.Contains(find))
                && (input.JobId == row.ParentJobId || row.FileName.EndsWith(" versus " + input.FileName))));
        }
        if (!string.IsNullOrWhiteSpace(filter.Status)) query = query.Where(row => row.Status == filter.Status);
        var rows = await paging.Apply(query).Take(paging.Take + 1).Select(row => new ComparisonProjection
            {
                JobId = row.JobId, ParentJobId = row.ParentJobId!.Value, FileName = row.FileName,
                ComparisonHash = row.OriginalHash, Status = row.Status, Applied = row.AppliedCount,
                Skipped = row.SkippedCount, Rejected = row.RejectedCount, RecordedTime = row.RecordedTime,
            }).ToListAsync(cancellationToken);
        var more = rows.Count > paging.Take;
        if (more) rows.RemoveAt(rows.Count - 1);
        var firstIds = rows.Select(row => row.ParentJobId).Distinct().ToList();
        var firstFiles = await db.ImportJobs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && firstIds.Contains(row.JobId))
            .Select(row => new InputFileProjection { Id = row.JobId, Name = row.FileName, Hash = row.OriginalHash })
            .ToListAsync(cancellationToken);
        var firstById = firstFiles.ToDictionary(row => row.Id);
        // TIGHT COUPLING (file-comparison history provenance)
        // WHAT: this read mirrors ImportComparisonService's exact "A versus B" name and composite-hash convention.
        // WHY: the stored comparison has job A's id but no job B field. WHAT BREAKS IF SEPARATED: this query can no
        // longer prove which second file was used, and must show the file as unavailable rather than attach an unrelated same-named file.
        var secondNames = rows.Select(row => firstById.TryGetValue(row.ParentJobId, out var first)
                && row.FileName.StartsWith(first.Name + " versus ", StringComparison.Ordinal)
                ? row.FileName[(first.Name.Length + " versus ".Length)..] : null)
            .Where(name => name is not null).Distinct().ToList();
        // Any stored upload with the second name is a candidate; the composite hash proves which contents were compared.
        var candidates = await db.ImportJobs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && secondNames.Contains(row.FileName)
                && (row.OriginalFileReference != null || row.OriginalContent != null))
            .Select(row => new InputFileProjection { Id = row.JobId, Name = row.FileName, Hash = row.OriginalHash, RecordedTime = row.RecordedTime })
            .ToListAsync(cancellationToken);
        var items = new List<ComparisonHistoryRow>(rows.Count);
        foreach (var row in rows)
        {
            // One row that cannot be traced shows its file as unavailable; it never fails the whole card.
            var first = firstById.GetValueOrDefault(row.ParentJobId);
            var prefix = first is null ? null : first.Name + " versus ";
            var secondName = prefix is not null && row.FileName.StartsWith(prefix, StringComparison.Ordinal)
                ? row.FileName[prefix.Length..] : null;
            // Uploads with the same name and hash have identical contents, so the latest one before the run stands for them.
            var second = first is null || secondName is null ? null : candidates
                .Where(candidate => candidate.Name == secondName && candidate.RecordedTime <= row.RecordedTime
                    && ComparisonHash(first.Hash, candidate.Hash) == row.ComparisonHash)
                .OrderByDescending(candidate => candidate.RecordedTime).FirstOrDefault();
            items.Add(new(row.JobId,
                first is null ? new(null, UnavailableFile, null) : new(first.Id, first.Name, first.Hash),
                second is null ? new(null, secondName ?? UnavailableFile, null) : new(second.Id, second.Name, second.Hash),
                row.Status, row.Applied, row.Skipped, row.Rejected, row.RecordedTime));
        }
        var last = more ? rows[^1] : null;
        return new(items, last is null ? null : paging.CursorFor(ComparisonCursorRow(last)));
    }

    public Task<ComparisonHistoryPage> ComparisonsAsync(ImportHistoryFilter filter, string? cursor, int take,
        CancellationToken cancellationToken) => ComparisonsAsync(filter, null, null, cursor, take, cancellationToken);

    public async IAsyncEnumerable<IReadOnlyList<string?>> ExportComparisonsAsync(ImportHistoryFilter filter, string? sort, string? direction,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? cursor = null;
        do
        {
            var page = await ComparisonsAsync(filter, sort, direction, cursor, 200, cancellationToken);
            foreach (var row in page.Items)
                yield return [row.FirstFile.Id?.ToString("D"), row.FirstFile.Name, row.FirstFile.Hash,
                    row.SecondFile.Id?.ToString("D"), row.SecondFile.Name, row.SecondFile.Hash, row.Status,
                    row.Applied.ToString(CultureInfo.InvariantCulture), row.Skipped.ToString(CultureInfo.InvariantCulture),
                    row.Rejected.ToString(CultureInfo.InvariantCulture), row.RanAt.ToString("O", CultureInfo.InvariantCulture),
                    row.JobId.ToString("D")];
            cursor = page.NextCursor;
        } while (cursor is not null);
    }

    public IAsyncEnumerable<IReadOnlyList<string?>> ExportComparisonsAsync(ImportHistoryFilter filter,
        CancellationToken cancellationToken) => ExportComparisonsAsync(filter, null, null, cancellationToken);

    private static ImportJob ImportCursorRow(HistoryProjection row) => new()
    {
        JobId = row.JobId,
        FileName = row.FileName,
        RecordedTime = row.RecordedTime,
        Status = row.Status,
        Source = "",
        OriginalHash = "",
        Format = "",
        Mode = "",
        ActorRef = "",
    };

    private static ThreePlFeedMessage FeedCursorRow(FeedProjection row) => new()
    {
        FeedMessageId = row.FeedMessageId,
        MessageReference = row.MessageReference,
        RecordedTime = row.RecordedTime,
        Status = row.Status,
        DocumentType = "",
        PayloadHash = "",
        ActorRef = "",
    };

    private static ImportJob ComparisonCursorRow(ComparisonProjection row) => new()
    {
        JobId = row.JobId,
        FileName = row.FileName,
        RecordedTime = row.RecordedTime,
        Status = row.Status,
        Source = "",
        OriginalHash = "",
        Format = "",
        Mode = "",
        ActorRef = "",
    };

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

    private static string Cursor(DateTime time, Guid id) =>
        $"{time.Ticks.ToString(CultureInfo.InvariantCulture)}.{id:N}";

    private static string ComparisonHash(string first, string second) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(first + ":" + second))).ToLowerInvariant();

    private static (int Number, Guid Id)? ParseRow(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return null;
        var parts = cursor.Split('.', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var number)
            || !Guid.TryParseExact(parts[1], "N", out var id))
            throw new CommandRefusedException("cursor_invalid", "The import-row cursor is invalid.");
        return (number, id);
    }

    private sealed class HistoryProjection
    {
        public Guid JobId { get; init; }
        public required string FileName { get; init; }
        public required string Format { get; init; }
        public long Size { get; init; }
        public required string OriginalHash { get; init; }
        public string? ConvertedHash { get; init; }
        public required string ActorRef { get; init; }
        public DateTime RecordedTime { get; init; }
        public required string Source { get; init; }
        public required string Status { get; init; }
        public int Applied { get; init; }
        public int Skipped { get; init; }
        public int Rejected { get; init; }
        public bool Archived { get; init; }
        public bool OriginalAvailable { get; init; }
        public bool ConvertedAvailable { get; init; }
        public string? OriginalReference { get; init; }
    }

    private sealed class FeedProjection
    {
        public Guid FeedMessageId { get; init; }
        public required string MessageReference { get; init; }
        public required string DocumentType { get; init; }
        public Guid DocumentId { get; init; }
        public int DocumentLine { get; init; }
        public Guid ImportJobId { get; init; }
        public required string PayloadHash { get; init; }
        public required string Status { get; init; }
        public int Shipped { get; init; }
        public int AssetTags { get; init; }
        public int Rejected { get; init; }
        public required string ActorRef { get; init; }
        public DateTime RecordedTime { get; init; }
    }

    private sealed class ComparisonProjection
    {
        public Guid JobId { get; init; }
        public Guid ParentJobId { get; init; }
        public required string FileName { get; init; }
        public required string ComparisonHash { get; init; }
        public required string Status { get; init; }
        public int Applied { get; init; }
        public int Skipped { get; init; }
        public int Rejected { get; init; }
        public DateTime RecordedTime { get; init; }
    }

    private sealed class InputFileProjection
    {
        public Guid Id { get; init; }
        public required string Name { get; init; }
        public required string Hash { get; init; }
        public DateTime RecordedTime { get; init; }
    }

    private const string UnavailableFile = "File no longer available";
}
