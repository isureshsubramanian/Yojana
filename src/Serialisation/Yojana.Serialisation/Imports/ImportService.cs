using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wolverine;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Corrections;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Imports;

public sealed class ImportService(
    SerialisationDbContext db,
    IEnumerable<ISpreadsheetReader> spreadsheetReaders,
    IImportFileStore files,
    IDocumentNumberPort documents,
    IPartyNamePort partyNames,
    IItemNamePort itemNames,
    IdentifierService identifiers,
    AssignIdentifiersService assignments,
    IMessageBus bus,
    ITenantContext tenant,
    IClock clock)
{
    public const int MaximumFileBytes = 25 * 1024 * 1024;
    public const int MaximumRows = 100_000;
    private const int WriteBatchSize = 1_000;

    public async Task<RunImportResult> RunAsync(RunImport command, CancellationToken cancellationToken)
    {
        Validate(command);
        var commandJobs = await CommandJobsAsync(command.CommandId, cancellationToken);
        if (commandJobs.Count > 0)
            return new(await ViewsAsync(commandJobs.Select(row => row.JobId).ToList(), cancellationToken), true);

        var hash = Hash(command.File.Content);
        var parsed = await ParseAsync(command.File, cancellationToken);
        if (parsed.Rows.Count > MaximumRows)
            throw new CommandRefusedException("import_row_limit_exceeded", $"An import may contain at most {MaximumRows:N0} data rows.");

        var mapping = await MappingAsync(command, parsed.Headers, cancellationToken);
        var fileTarget = command.Target ?? await MatchFileNameAsync(command.File.FileName, cancellationToken);
        var prepared = new List<PreparedRow>(parsed.Rows.Count);
        foreach (var row in parsed.Rows)
        {
            var value = Value(row, mapping.Serial);
            var order = Value(row, mapping.OrderNumber);
            var target = command.Target ?? await MatchOrderAsync(order, cancellationToken) ?? fileTarget;
            var numeric = row.Cells.TryGetValue(mapping.Serial, out var serialCell) && serialCell.IsNumeric;
            if (numeric)
            {
                prepared.Add(new(row, value, null, order, Value(row, mapping.Lot), Value(row, mapping.CustomerAssetTag), target, "spreadsheet_numeric_identifier"));
                continue;
            }
            if (string.IsNullOrWhiteSpace(value))
            {
                prepared.Add(new(row, value, null, order, Value(row, mapping.Lot), Value(row, mapping.CustomerAssetTag), target, "serial_required"));
                continue;
            }
            string normalised;
            try
            {
                var identifier = identifiers.Prepare(new IdentifierInput(IdentifierType.VendorSerial, value), null, null);
                normalised = identifier.Normalised;
                if (identifier.Blocks)
                {
                    prepared.Add(new(row, value, normalised, order, Value(row, mapping.Lot), Value(row, mapping.CustomerAssetTag), target, "identifier_invalid"));
                    continue;
                }
            }
            catch (ArgumentException)
            {
                prepared.Add(new(row, value, null, order, Value(row, mapping.Lot), Value(row, mapping.CustomerAssetTag), target, "identifier_type_unknown"));
                continue;
            }
            prepared.Add(new(row, value, normalised, order, Value(row, mapping.Lot), Value(row, mapping.CustomerAssetTag), target,
                target is null && command.Mode == ImportMode.Apply ? "target_not_resolved" : null));
        }

        var groups = prepared.GroupBy(row => row.Target, DocumentRefComparer.Instance).ToList();
        var targets = groups.Select(group => ReplayTarget.From(group.Key)).ToHashSet();
        var replayJobs = await ReplayJobsAsync(command.Source, hash, command.Mode, targets, cancellationToken);
        if (replayJobs.Count == groups.Count)
            return new(await ViewsAsync(replayJobs.Select(row => row.JobId).ToList(), cancellationToken), true);

        var jobsByTarget = replayJobs.ToDictionary(row => ReplayTarget.From(row), row => row);
        var jobIds = replayJobs.Select(row => row.JobId).ToList();
        var replayed = replayJobs.Count > 0;
        var ownsTransaction = db.Database.CurrentTransaction is null;
        IDbContextTransaction? transaction = ownsTransaction ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var mappingPersisted = false;
            for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
            {
                var group = groups[groupIndex];
                if (jobsByTarget.ContainsKey(ReplayTarget.From(group.Key))) continue;

                var jobId = groupIndex == 0 ? command.CommandId : TimeOrderedId.New(command.EventTime.AddMilliseconds(groupIndex));
                var job = NewJob(command, jobId, hash, parsed.ConvertedHash, group.Key);
                db.ImportJobs.Add(job);
                jobIds.Add(jobId);

                // Claim the replay key before assignments, archive writes or row persistence. The unique index is the
                // cross-process arbiter; keeping this transaction open means a loser only sees a fully committed winner.
                await db.SaveChangesAsync(cancellationToken);

                if (!mappingPersisted)
                {
                    await PersistMappingAsync(command, cancellationToken);
                    mappingPersisted = true;
                }

                var sourceRows = group.ToList();
                var resultRows = sourceRows.Select(row => NewResult(jobId, row, clock.UtcNow)).ToList();
                foreach (var batch in resultRows.Chunk(WriteBatchSize))
                {
                    db.ImportRowResults.AddRange(batch);
                    await db.SaveChangesAsync(cancellationToken);
                }

                if (command.Mode == ImportMode.Apply && group.Key is not null)
                    await ApplyAsync(command, job, sourceRows, resultRows, cancellationToken);
                else if (command.Mode == ImportMode.Compare)
                {
                    job.Status = ImportJobStatus.Intake;
                    job.SkippedCount = resultRows.Count(row => row.Outcome == ImportOutcome.Skipped);
                    job.RejectedCount = resultRows.Count(row => row.Outcome == ImportOutcome.Rejected);
                }
                else
                {
                    job.Status = ImportJobStatus.Rejected;
                    job.RejectedCount = resultRows.Count;
                }

                await StoreFileAsync(job, command.File, parsed.ConvertedContent, cancellationToken);
                job.CompletedTime = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                if (job.Status != ImportJobStatus.Intake) await PublishCompletedAsync(job, command, cancellationToken);
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                await transaction.DisposeAsync();
                transaction = null;
            }
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                await transaction.DisposeAsync();
                transaction = null;
            }

            db.ChangeTracker.Clear();
            var winners = await ReplayJobsAsync(command.Source, hash, command.Mode, targets, cancellationToken);
            if (winners.Count == groups.Count)
                return new(await ViewsAsync(winners.Select(row => row.JobId).ToList(), cancellationToken), true);
            commandJobs = await CommandJobsAsync(command.CommandId, cancellationToken);
            if (commandJobs.Count > 0)
                return new(await ViewsAsync(commandJobs.Select(row => row.JobId).ToList(), cancellationToken), true);
            throw;
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            if (transaction is not null) await transaction.DisposeAsync();
        }

        return new(await ViewsAsync(jobIds, cancellationToken), replayed);
    }

    private async Task ApplyAsync(RunImport command, ImportJob job, IReadOnlyList<PreparedRow> sourceRows,
        List<ImportRowResult> resultRows, CancellationToken cancellationToken)
    {
        var accepted = sourceRows.Select((row, index) => (row, index)).Where(pair => pair.row.Rule is null).ToList();
        if (accepted.Count > 0)
        {
            var assign = new AssignIdentifiers(
                TimeOrderedId.New(command.EventTime.AddTicks(1)), job.Target(), IdentifierAssignmentMode.AllocationOrder,
                accepted.Select(pair => new IdentifierAssignmentInput(pair.row.RawValue!)).ToList(), AssignIdentifiersReason.VendorList,
                command.EventTime, command.ActorRef, command.LegalEntityId,
                CommittedBy: command.CommittedBy is null ? null : new CorrectionApprover(command.CommittedBy.UserRef, command.CommittedBy.Roles),
                Correction: new CorrectionMetadata(job.JobId, AssignIdentifiersReason.VendorList, null, [], CorrectionInitiatedVia.Integration,
                    null, CorrectionInitiatingRole.ProductionSupervisor, []));
            try
            {
                var applied = await assignments.ExecuteAsync(assign, cancellationToken);
                for (var i = 0; i < applied.Lines.Count; i++)
                {
                    var result = resultRows[accepted[i].index];
                    var line = applied.Lines[i];
                    result.NormalisedValue = line.NormalisedValue;
                    result.UnitId = line.UnitId;
                    result.Rule = line.Reason;
                    result.Outcome = line.Outcome == IdentifierAssignmentOutcome.Assigned ? ImportOutcome.Applied
                        : line.Reason is "duplicate_value_in_list" or "value_matches_provisional" ? ImportOutcome.Skipped
                        : ImportOutcome.Rejected;
                }
            }
            catch (CommandRefusedException ex) when (ex.Rule == "no_assignments")
            {
                foreach (var pair in accepted)
                {
                    resultRows[pair.index].Outcome = ImportOutcome.Rejected;
                    resultRows[pair.index].Rule = ex.Rule;
                }
            }
        }
        job.AppliedCount = resultRows.Count(row => row.Outcome == ImportOutcome.Applied);
        job.SkippedCount = resultRows.Count(row => row.Outcome == ImportOutcome.Skipped);
        job.RejectedCount = resultRows.Count(row => row.Outcome == ImportOutcome.Rejected);
        job.Status = job.RejectedCount == 0 ? ImportJobStatus.Applied
            : job.AppliedCount + job.SkippedCount > 0 ? ImportJobStatus.Partial : ImportJobStatus.Rejected;
    }

    private async Task StoreFileAsync(ImportJob job, ImportFile file, byte[]? converted, CancellationToken cancellationToken)
    {
        if (job.RejectedCount > 0)
        {
            job.InboxReference = await files.RetainInboxAsync(job.JobId, file.FileName, file.Content, cancellationToken);
            if (converted is not null) job.ConvertedContent = converted;
            return;
        }
        job.OriginalArchiveReference = await files.ArchiveAsync(job.JobId, file.FileName, file.Content, "original", cancellationToken);
        if (converted is not null)
            job.ConvertedArchiveReference = await files.ArchiveAsync(job.JobId, Path.ChangeExtension(file.FileName, ".xlsx"), converted, "converted", cancellationToken);
    }

    private async Task PublishCompletedAsync(ImportJob job, RunImport command, CancellationToken cancellationToken)
    {
        var recorded = clock.UtcNow;
        var header = new PublishedSummaryHeader(ImportCompleted.Schema, ImportCompleted.Version, TimeOrderedId.New(recorded),
            command.EventTime, recorded, command.LegalEntityId, command.ActorRef, job.TargetId is { } id && job.TargetType is { } type
                ? new DocumentRef(type, id, job.TargetLine) : null);
        await bus.PublishAsync(new ImportCompleted(header, job.JobId, job.Source, job.OriginalHash, job.ConvertedHash,
                job.Status, job.AppliedCount, job.SkippedCount, job.RejectedCount),
            new DeliveryOptions { TenantId = tenant.TenantId.ToString() });
    }

    private async Task<ParsedFile> ParseAsync(ImportFile file, CancellationToken cancellationToken)
    {
        if (file.Format == ImportFormat.Csv)
        {
            using var stream = new MemoryStream(file.Content, writable: false);
            var delimiter = DelimitedText.Sniff(stream);
            using var rows = DelimitedText.ReadRows(stream, delimiter).GetEnumerator();
            if (!rows.MoveNext()) throw new CommandRefusedException("import_empty", "The import file has no header row.");
            var headers = UniqueHeaders(rows.Current);
            var parsed = new List<SourceRow>();
            var number = 1;
            while (rows.MoveNext())
            {
                cancellationToken.ThrowIfCancellationRequested();
                number++;
                if (rows.Current.All(string.IsNullOrEmpty)) continue;
                parsed.Add(new("CSV", number, headers.Select((header, index) => new KeyValuePair<string, SpreadsheetCell>(header,
                    new(index < rows.Current.Count ? rows.Current[index] : null))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)));
                if (parsed.Count > MaximumRows) break;
            }
            return new(headers, parsed, null, null);
        }

        var reader = spreadsheetReaders.FirstOrDefault(adapter => adapter.CanRead(file.Format))
            ?? throw new CommandRefusedException("spreadsheet_reader_not_configured", $"No spreadsheet reader is configured for {file.Format}.");
        using var input = new MemoryStream(file.Content, writable: false);
        var result = await reader.ReadAsync(input, cancellationToken);
        var rowsBySheet = result.Rows.GroupBy(row => row.Sheet);
        var source = new List<SourceRow>();
        List<string>? firstHeaders = null;
        foreach (var sheet in rowsBySheet)
        {
            var first = sheet.FirstOrDefault();
            if (first is null) continue;
            var headers = UniqueHeaders(first.Cells.Select(cell => cell.Text ?? "").ToList());
            firstHeaders ??= headers;
            foreach (var row in sheet.Skip(1))
            {
                source.Add(new(row.Sheet, row.RowNumber, headers.Select((header, index) => new KeyValuePair<string, SpreadsheetCell>(header,
                    index < row.Cells.Count ? row.Cells[index] : new(null))).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)));
                if (source.Count > MaximumRows) break;
            }
            if (source.Count > MaximumRows) break;
        }
        if (firstHeaders is null) throw new CommandRefusedException("import_empty", "The spreadsheet has no visible non-empty sheet.");
        return new(firstHeaders, source, result.ConvertedXlsx, result.ConvertedXlsx is null ? null : Hash(result.ConvertedXlsx));
    }

    private async Task<ImportColumnMapping> MappingAsync(RunImport command, IReadOnlyList<string> headers, CancellationToken cancellationToken)
    {
        if (command.Mapping is { } explicitMapping)
        {
            RequireHeader(headers, explicitMapping.Serial, "serial");
            return explicitMapping;
        }
        var saved = await db.ColumnMappings.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && row.Source == command.Source)
            .OrderByDescending(row => row.IsDefault).ThenByDescending(row => row.RecordedTime).FirstOrDefaultAsync(cancellationToken);
        if (saved is not null)
        {
            var value = JsonSerializer.Deserialize<ImportColumnMapping>(saved.MappingJson, LedgerJson.Options)!;
            RequireHeader(headers, value.Serial, "serial");
            return value;
        }
        string? Find(params string[] names) => headers.FirstOrDefault(header => names.Contains(Canonical(header), StringComparer.Ordinal));
        var serial = Find("serial", "serialnumber", "serialno", "vendorserial", "manufacturerserial")
            ?? throw new CommandRefusedException("column_mapping_required", "No serial column was recognised; supply and save a column mapping.");
        return new(serial, Find("ordernumber", "order", "productionorder", "workorder", "workordernumber"),
            Find("unitid"), Find("lot", "lotnumber"), Find("customerassettag", "assettag"), Find("item", "itemcode"));
    }

    private async Task PersistMappingAsync(RunImport command, CancellationToken cancellationToken)
    {
        if (command.Mapping is not { } mapping) return;
        var existing = await db.ColumnMappings.FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.Source == command.Source && row.Name == command.MappingName, cancellationToken);
        if (existing is null)
        {
            db.ColumnMappings.Add(new ColumnMapping
            {
                TenantId = tenant.TenantId,
                MappingId = TimeOrderedId.New(clock.UtcNow),
                Source = command.Source,
                Name = command.MappingName,
                MappingJson = JsonSerializer.Serialize(mapping, LedgerJson.Options),
                IsDefault = true,
                ActorRef = command.ActorRef,
                RecordedTime = clock.UtcNow,
            });
            return;
        }

        existing.MappingJson = JsonSerializer.Serialize(mapping, LedgerJson.Options);
        existing.ActorRef = command.ActorRef;
        existing.RecordedTime = clock.UtcNow;
    }

    private async Task<DocumentRef?> MatchOrderAsync(string? number, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(number)) return null;
        var found = await documents.FindAsync("production_order", number.Trim(), 3, cancellationToken);
        var exact = found.Where(row => string.Equals(row.Number, number.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        return exact.Count == 1 ? exact[0].Document : null;
    }

    private async Task<DocumentRef?> MatchFileNameAsync(string fileName, CancellationToken cancellationToken)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var boundaries = new HashSet<int> { 0, stem.Length };
        for (var index = 0; index < stem.Length; index++)
        {
            if (stem[index] is '-' or '_' or ' ' or '(' or ')' or '[' or ']')
            {
                boundaries.Add(index);
                boundaries.Add(index + 1);
            }
        }
        var points = boundaries.Order().ToList();
        var candidates = (from start in points
                          from end in points
                          where end > start
                          let value = stem[start..end].Trim('-', '_', ' ', '(', ')', '[', ']')
                          where value.Length >= 3 && value.Any(char.IsDigit)
                          orderby value.Length descending
                          select value).Distinct(StringComparer.OrdinalIgnoreCase).Take(64);
        foreach (var candidate in candidates)
            if (await MatchOrderAsync(candidate, cancellationToken) is { } document) return document;
        return null;
    }

    public async Task<IReadOnlyList<ImportJobView>> ViewsAsync(IReadOnlyList<Guid> jobIds, CancellationToken cancellationToken)
    {
        var jobs = await db.ImportJobs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && jobIds.Contains(row.JobId))
            .OrderByDescending(row => row.RecordedTime).ThenBy(row => row.JobId).ToListAsync(cancellationToken);
        var rows = await db.ImportRowResults.AsNoTracking().Where(row => row.TenantId == tenant.TenantId && jobIds.Contains(row.JobId))
            .OrderBy(row => row.JobId).ThenBy(row => row.RowNumber).ThenBy(row => row.RowResultId).ToListAsync(cancellationToken);
        var labels = await LabelsAsync(rows, cancellationToken);
        var targets = jobs.Where(job => job.TargetId != null && job.TargetType != null)
            .Select(job => new DocumentRef(job.TargetType!, job.TargetId!.Value, job.TargetLine)).ToList();
        var targetNames = targets.Count == 0 ? new Dictionary<DocumentKey, DocumentLabel>()
            : await documents.NumbersAsync(targets, cancellationToken);
        return jobs.Select(job => View(job, rows.Where(row => row.JobId == job.JobId).ToList(), labels.Parties, labels.Units,
            job.TargetId is { } id && job.TargetType is { } type
                && targetNames.TryGetValue(new(type, id), out var label) ? label.Number : null)).ToList();
    }

    internal async Task<ImportJobView> MetadataViewAsync(ImportJob job, CancellationToken cancellationToken)
    {
        if (job.TargetId is not { } id || job.TargetType is not { } type) return View(job, []);
        var target = new DocumentRef(type, id, job.TargetLine);
        var labels = await documents.NumbersAsync([target], cancellationToken);
        return View(job, [], targetName: labels.GetValueOrDefault(DocumentKey.From(target))?.Number);
    }

    internal async Task<(IReadOnlyDictionary<Guid, PartyLabel> Parties, IReadOnlyDictionary<Guid, string> Units)> LabelsAsync(
        IReadOnlyList<ImportRowResult> rows, CancellationToken cancellationToken)
    {
        var owners = rows.Where(row => row.CollisionOwnerPartyRef != null).Select(row => row.CollisionOwnerPartyRef!.Value).Distinct().ToList();
        var parties = owners.Count == 0 ? new Dictionary<Guid, PartyLabel>() : await partyNames.NamesAsync(owners, cancellationToken);
        var collisionIds = rows.Where(row => row.CollisionUnitId != null).Select(row => row.CollisionUnitId!.Value).Distinct().ToList();
        var facts = collisionIds.Count == 0 ? [] : await (from assignment in db.ItemAssignments.AsNoTracking()
                    join identifier in db.Identifiers.AsNoTracking().Where(row => row.ValidTo == null)
                        on new { assignment.TenantId, assignment.UnitId } equals new { identifier.TenantId, identifier.UnitId }
                    where assignment.TenantId == tenant.TenantId && assignment.ValidTo == null && collisionIds.Contains(assignment.UnitId)
                    orderby identifier.Type == IdentifierType.InternalSerial ? 0 : 1, identifier.Type
                    select new { assignment.UnitId, assignment.ItemId, identifier.RawValue }).ToListAsync(cancellationToken);
        var itemIds = facts.Select(row => row.ItemId).Distinct().ToList();
        var names = itemIds.Count == 0 ? new Dictionary<Guid, ItemLabel>() : await itemNames.NamesAsync(itemIds, cancellationToken);
        var unitNames = facts.GroupBy(row => row.UnitId).ToDictionary(group => group.Key, group =>
        {
            var fact = group.First();
            if (!names.TryGetValue(fact.ItemId, out var item)) return fact.RawValue;
            var itemName = string.IsNullOrWhiteSpace(item.Name) ? item.Code : $"{item.Name} ({item.Code})";
            return $"{itemName} — {fact.RawValue}";
        });
        return (parties, unitNames);
    }

    internal static ImportJobView View(ImportJob job, IReadOnlyList<ImportRowResult> rows,
        IReadOnlyDictionary<Guid, PartyLabel>? parties = null, IReadOnlyDictionary<Guid, string>? unitNames = null,
        string? targetName = null) =>
        new(job.JobId, job.Source, job.FileName, job.OriginalHash, job.ConvertedHash, job.Format, job.Mode,
            job.TargetId is { } id && job.TargetType is { } type ? new(type, id, job.TargetLine) : null, targetName, job.Status,
            job.AppliedCount, job.SkippedCount, job.RejectedCount, job.OriginalArchiveReference, job.ConvertedArchiveReference,
            job.InboxReference, job.RecordedTime, rows.Select(row => new ImportRowView(row.RowResultId, row.RowNumber, row.Sheet,
                row.RawValue, row.NormalisedValue, row.Outcome, row.Rule, row.OrderNumber, row.Lot, row.CustomerAssetTag,
                row.UnitId, row.ComparisonPartition, row.ProposedAction, row.ReviewedAction,
                row.CollisionUnitId is { } collision && unitNames?.TryGetValue(collision, out var unitName) == true ? unitName : null,
                row.CollisionOwnerPartyRef is { } owner && parties?.TryGetValue(owner, out var party) == true
                    ? party.Name ?? party.Code : null)).ToList());

    internal static ImportRowView RowView(ImportRowResult row, IReadOnlyDictionary<Guid, PartyLabel> parties,
        IReadOnlyDictionary<Guid, string> unitNames) =>
        new(row.RowResultId, row.RowNumber, row.Sheet, row.RawValue, row.NormalisedValue, row.Outcome, row.Rule,
            row.OrderNumber, row.Lot, row.CustomerAssetTag, row.UnitId, row.ComparisonPartition, row.ProposedAction,
            row.ReviewedAction,
            row.CollisionUnitId is { } collision && unitNames.TryGetValue(collision, out var unitName) ? unitName : null,
            row.CollisionOwnerPartyRef is { } owner && parties.TryGetValue(owner, out var party) ? party.Name ?? party.Code : null);

    private async Task<List<ImportJob>> ReplayJobsAsync(string source, string hash, string mode,
        IReadOnlySet<ReplayTarget> targets, CancellationToken cancellationToken)
    {
        var candidates = await db.ImportJobs.AsNoTracking()
            .Where(row => row.TenantId == tenant.TenantId && row.Source == source && row.OriginalHash == hash && row.Mode == mode)
            .OrderBy(row => row.RecordedTime).ThenBy(row => row.JobId).ToListAsync(cancellationToken);
        return candidates.Where(row => targets.Contains(ReplayTarget.From(row)))
            .DistinctBy(row => ReplayTarget.From(row)).ToList();
    }

    private async Task<List<ImportJob>> CommandJobsAsync(Guid commandId, CancellationToken cancellationToken)
    {
        var root = await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(row => row.TenantId == tenant.TenantId
            && row.CommandId == commandId, cancellationToken);
        if (root is null) return [];
        // ParentJobId also records comparison provenance. Split children are only the rows that retain the root
        // command's file identity; a later comparison has its own derived hash and must not join this replay.
        return await db.ImportJobs.AsNoTracking().Where(row => row.TenantId == tenant.TenantId
                && (row.JobId == root.JobId || (row.ParentJobId == commandId && row.Source == root.Source
                    && row.OriginalHash == root.OriginalHash && row.Mode == root.Mode && row.FileName == root.FileName)))
            .OrderBy(row => row.RecordedTime).ThenBy(row => row.JobId).ToListAsync(cancellationToken);
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is SqlException { Number: 2601 or 2627 };

    private ImportJob NewJob(RunImport command, Guid id, string hash, string? convertedHash, DocumentRef? target) => new()
    {
        TenantId = tenant.TenantId,
        JobId = id,
        CommandId = id,
        // ParentJobId groups jobs split from one command. Comparison jobs also use it for provenance, so
        // CommandJobsAsync must retain its matching file-identity guard or command replay returns comparisons too.
        ParentJobId = command.CommandId == id ? null : command.CommandId,
        Source = command.Source,
        FileName = command.File.FileName,
        OriginalHash = hash,
        ConvertedHash = convertedHash,
        Format = command.File.Format,
        Mode = command.Mode,
        TargetType = target?.Type,
        TargetId = target?.Id,
        TargetLine = target?.Line,
        Status = ImportJobStatus.Intake,
        ActorRef = command.ActorRef,
        RecordedTime = command.EventTime,
    };

    private ImportRowResult NewResult(Guid jobId, PreparedRow row, DateTime now) => new()
    {
        TenantId = tenant.TenantId,
        RowResultId = TimeOrderedId.New(now),
        JobId = jobId,
        RowNumber = row.Source.RowNumber,
        Sheet = row.Source.Sheet,
        RawValue = row.RawValue,
        NormalisedValue = row.NormalisedValue,
        Outcome = row.Rule is null ? ImportOutcome.Skipped : ImportOutcome.Rejected,
        Rule = row.Rule,
        OrderNumber = row.OrderNumber,
        Lot = row.Lot,
        CustomerAssetTag = row.CustomerAssetTag,
        RecordedTime = now,
    };

    private static string? Value(SourceRow row, string? header) => header is not null && row.Cells.TryGetValue(header, out var cell) ? cell.Text : null;
    private static string Canonical(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static void RequireHeader(IReadOnlyList<string> headers, string header, string field)
    {
        if (!headers.Contains(header, StringComparer.OrdinalIgnoreCase)) throw new CommandRefusedException("column_mapping_invalid", $"The mapped {field} column '{header}' is not present.");
    }
    private static List<string> UniqueHeaders(IReadOnlyList<string> headers)
    {
        var result = new List<string>(headers.Count);
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var given in headers)
        {
            var value = given.Trim().TrimStart('\uFEFF');
            if (value.Length == 0) value = "column_" + (result.Count + 1);
            if (used.TryGetValue(value, out var count)) { used[value] = ++count; value += "_" + count; }
            else used[value] = 1;
            result.Add(value);
        }
        return result;
    }
    private static string Hash(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
    private static void Validate(RunImport command)
    {
        if (!ImportSource.All.Contains(command.Source)) throw new CommandRefusedException("import_source_unknown", $"Import source '{command.Source}' is not supported.");
        if (!ImportFormat.All.Contains(command.File.Format)) throw new CommandRefusedException("spreadsheet_reader_not_configured", $"Format '{command.File.Format}' is not supported.");
        if (command.File.Content.Length == 0) throw new CommandRefusedException("import_empty", "The import file is empty.");
        if (command.File.Content.Length > MaximumFileBytes) throw new CommandRefusedException("import_file_too_large", $"An import file may not exceed {MaximumFileBytes / 1024 / 1024} MiB.");
        if (command.Mode is not ImportMode.Apply and not ImportMode.Compare) throw new CommandRefusedException("import_mode_unknown", $"Import mode '{command.Mode}' is not supported.");
    }

    private sealed record SourceRow(string Sheet, int RowNumber, IReadOnlyDictionary<string, SpreadsheetCell> Cells);
    private sealed record PreparedRow(SourceRow Source, string? RawValue, string? NormalisedValue, string? OrderNumber,
        string? Lot, string? CustomerAssetTag, DocumentRef? Target, string? Rule);
    private sealed record ParsedFile(IReadOnlyList<string> Headers, IReadOnlyList<SourceRow> Rows, byte[]? ConvertedContent, string? ConvertedHash);
    private readonly record struct ReplayTarget(string? Type, Guid? Id, int? Line)
    {
        public static ReplayTarget From(DocumentRef? target) => new(target?.Type, target?.Id, target?.Line);
        public static ReplayTarget From(ImportJob job) => new(job.TargetType, job.TargetId, job.TargetLine);
    }

    private sealed class DocumentRefComparer : IEqualityComparer<DocumentRef?>
    {
        public static readonly DocumentRefComparer Instance = new();
        public bool Equals(DocumentRef? x, DocumentRef? y) => x?.Type == y?.Type && x?.Id == y?.Id && x?.Line == y?.Line;
        public int GetHashCode(DocumentRef? obj) => obj is null ? 0 : HashCode.Combine(obj.Type, obj.Id, obj.Line);
    }
}

internal static class ImportJobTarget
{
    public static DocumentRef Target(this ImportJob job) => job.TargetId is { } id && job.TargetType is { } type
        ? new(type, id, job.TargetLine) : throw new InvalidOperationException("Import job has no target.");
}

public static class RunImportHandler
{
    public static Task<RunImportResult> Handle(RunImport command, ImportService service, CancellationToken cancellationToken) =>
        service.RunAsync(command, cancellationToken);
}
