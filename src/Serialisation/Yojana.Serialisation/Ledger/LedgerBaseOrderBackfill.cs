using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Ledger;

public sealed class LedgerBaseOrderBackfillOptions
{
    public const string SectionName = "Serialisation:LedgerBaseOrderBackfill";

    /// <summary>Rows read in one transaction from one monthly ledger partition.</summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>Yield between transactions so interactive ledger work gets the connection and I/O budget first.</summary>
    public int PauseMilliseconds { get; set; } = 100;

    /// <summary>Bounds each recurring execution; a later trigger resumes from the durable cursor.</summary>
    public int MaximumBatchesPerRun { get; set; } = 60;

    public int CommandTimeoutSeconds { get; set; } = 120;

    public string Cron { get; set; } = "0 0/1 * * * ?";
}

public static class LedgerBaseOrderBackfillStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Complete = "complete";
}

public sealed record LedgerBaseOrderBackfillProgress(
    string Status,
    long RowsDone,
    long RowsWritten,
    long? EstimatedRows,
    DateTime? CheckpointMonth,
    int MonthsCompleted,
    int MonthsTotal,
    DateTime? StartedAt,
    DateTime? UpdatedAt,
    DateTime? CompletedAt);

/// <summary>
/// Resumable historical projection for <c>attributes.base_order</c>. A transaction reads one bounded descending
/// window from one calendar month across live and archive, inserts missing sparse references, and advances the
/// checkpoint. Reading both ledger tables in the same statement keeps partition switches invisible to the cursor.
/// </summary>
public sealed class LedgerBaseOrderBackfill(
    SerialisationDbContext db,
    ITenantContext tenant,
    IClock clock,
    LedgerBaseOrderBackfillOptions options,
    ILogger<LedgerBaseOrderBackfill> logger)
{
    private sealed record BatchResult(int Scanned, int Written, DateTime? CursorEventTime, Guid? CursorEventId);

    public async Task<LedgerBaseOrderBackfillProgress> RunAsync(
        CancellationToken cancellationToken,
        int? maximumBatches = null)
    {
        ValidateOptions();
        var limit = maximumBatches ?? options.MaximumBatchesPerRun;
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        LedgerBaseOrderBackfillProgress progress = await GetProgressAsync(cancellationToken);
        for (var batch = 0; batch < limit && progress.Status != LedgerBaseOrderBackfillStatus.Complete; batch++)
        {
            progress = await RunOneBatchAsync(cancellationToken);
            if (progress.Status != LedgerBaseOrderBackfillStatus.Complete
                && batch + 1 < limit && options.PauseMilliseconds > 0)
            {
                await Task.Delay(options.PauseMilliseconds, cancellationToken);
            }
        }

        return progress;
    }

    public async Task<LedgerBaseOrderBackfillProgress> GetProgressAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await db.LedgerBaseOrderBackfillCheckpoints.AsNoTracking()
            .SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId, cancellationToken);
        return checkpoint is null
            ? new LedgerBaseOrderBackfillProgress(LedgerBaseOrderBackfillStatus.Pending, 0, 0, null, null, 0, 0, null, null, null)
            : View(checkpoint);
    }

    private async Task<LedgerBaseOrderBackfillProgress> RunOneBatchAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var checkpoint = await db.LedgerBaseOrderBackfillCheckpoints
            .SingleOrDefaultAsync(row => row.TenantId == tenant.TenantId, cancellationToken);
        var created = checkpoint is null;
        if (checkpoint is null)
        {
            checkpoint = await CreateCheckpointAsync(cancellationToken);
            db.LedgerBaseOrderBackfillCheckpoints.Add(checkpoint);
        }

        if (checkpoint.Status == LedgerBaseOrderBackfillStatus.Complete)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (created)
            {
                logger.LogInformation("Ledger base-order backfill completed for tenant {Tenant}: no historical ledger rows", tenant.TenantId);
            }
            return View(checkpoint);
        }

        if (checkpoint.CheckpointMonth is not { } month)
        {
            Complete(checkpoint);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Ledger base-order backfill completed for tenant {Tenant}: {RowsScanned} ledger rows scanned, {RowsWritten} references written",
                tenant.TenantId, checkpoint.RowsScanned, checkpoint.RowsWritten);
            return View(checkpoint);
        }

        var batch = await ExecuteBatchAsync(month, checkpoint.CursorEventTime, checkpoint.CursorEventId, cancellationToken);
        if (batch.Scanned > 0)
        {
            checkpoint.CursorEventTime = batch.CursorEventTime;
            checkpoint.CursorEventId = batch.CursorEventId;
            checkpoint.RowsScanned += batch.Scanned;
            checkpoint.RowsWritten += batch.Written;
        }

        if (batch.Scanned < options.BatchSize)
        {
            checkpoint.MonthsCompleted = MonthsBetween(checkpoint.NewestMonth!.Value, month) + 1;
            checkpoint.EstimatedRows = checkpoint.MonthsCompleted == checkpoint.MonthsTotal
                ? checkpoint.RowsScanned
                : Math.Max(checkpoint.RowsScanned,
                    (long)Math.Ceiling(checkpoint.RowsScanned * (double)checkpoint.MonthsTotal / checkpoint.MonthsCompleted));
            checkpoint.CheckpointMonth = await LatestMonthBeforeAsync(month, cancellationToken);
            checkpoint.CursorEventTime = null;
            checkpoint.CursorEventId = null;
            if (checkpoint.CheckpointMonth is null)
            {
                Complete(checkpoint);
            }
        }

        checkpoint.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (checkpoint.Status == LedgerBaseOrderBackfillStatus.Complete)
        {
            logger.LogInformation("Ledger base-order backfill completed for tenant {Tenant}: {RowsScanned} ledger rows scanned, {RowsWritten} references written",
                tenant.TenantId, checkpoint.RowsScanned, checkpoint.RowsWritten);
        }

        return View(checkpoint);
    }

    private async Task<LedgerBaseOrderBackfillCheckpoint> CreateCheckpointAsync(CancellationToken cancellationToken)
    {
        var liveNewest = await EdgeAsync(db.Events, newest: true, before: null, cancellationToken);
        var archiveNewest = await EdgeAsync(db.ArchivedEvents, newest: true, before: null, cancellationToken);
        var liveOldest = await EdgeAsync(db.Events, newest: false, before: null, cancellationToken);
        var archiveOldest = await EdgeAsync(db.ArchivedEvents, newest: false, before: null, cancellationToken);
        var newest = Max(liveNewest, archiveNewest);
        var oldest = Min(liveOldest, archiveOldest);
        var now = clock.UtcNow;
        if (newest is null || oldest is null)
        {
            return new LedgerBaseOrderBackfillCheckpoint
            {
                TenantId = tenant.TenantId,
                Status = LedgerBaseOrderBackfillStatus.Complete,
                RowsScanned = 0,
                RowsWritten = 0,
                EstimatedRows = 0,
                MonthsCompleted = 0,
                MonthsTotal = 0,
                StartedAt = now,
                UpdatedAt = now,
                CompletedAt = now,
            };
        }

        var newestMonth = StartOfMonth(newest.Value);
        var oldestMonth = StartOfMonth(oldest.Value);
        return new LedgerBaseOrderBackfillCheckpoint
        {
            TenantId = tenant.TenantId,
            Status = LedgerBaseOrderBackfillStatus.Running,
            CheckpointMonth = newestMonth,
            NewestMonth = newestMonth,
            OldestMonth = oldestMonth,
            RowsScanned = 0,
            RowsWritten = 0,
            MonthsCompleted = 0,
            MonthsTotal = MonthsBetween(newestMonth, oldestMonth) + 1,
            StartedAt = now,
            UpdatedAt = now,
        };
    }

    private async Task<DateTime?> LatestMonthBeforeAsync(DateTime month, CancellationToken cancellationToken)
    {
        var live = await EdgeAsync(db.Events, newest: true, before: month, cancellationToken);
        var archive = await EdgeAsync(db.ArchivedEvents, newest: true, before: month, cancellationToken);
        var latest = Max(live, archive);
        return latest is null ? null : StartOfMonth(latest.Value);
    }

    private async Task<BatchResult> ExecuteBatchAsync(
        DateTime month,
        DateTime? cursorTime,
        Guid? cursorEvent,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandTimeout = options.CommandTimeoutSeconds;
        // HIDDEN COUPLING (base-order ledger projection): this is the bounded historical form of the append observer
        // and BulkIngestion statement. All three read the same attributes.base_order object and write the same sparse
        // contract. Keep their field paths together; a mismatch makes only one ingestion era selectable by order.
        command.CommandText = """
            DECLARE @rows int, @written int, @last_time datetime2(3), @last_id uniqueidentifier;
            DECLARE @source TABLE
            (
                event_time datetime2(3) NOT NULL,
                event_id uniqueidentifier NOT NULL,
                unit_id uniqueidentifier NOT NULL,
                base_document_type nvarchar(4000) NULL,
                base_document_id uniqueidentifier NULL,
                base_document_line int NULL,
                PRIMARY KEY (event_time, event_id)
            );

            INSERT INTO @source
                (event_time, event_id, unit_id, base_document_type, base_document_id, base_document_line)
            SELECT TOP (@batch) source.event_time, source.event_id, source.unit_id,
                   JSON_VALUE(source.attributes, '$.base_order.type'),
                   TRY_CONVERT(uniqueidentifier, JSON_VALUE(source.attributes, '$.base_order.id')),
                   TRY_CONVERT(int, JSON_VALUE(source.attributes, '$.base_order.line'))
            FROM
            (
                SELECT event.event_time, event.event_id, event.unit_id, event.attributes
                FROM serialisation.unit_event AS event
                WHERE event.tenant_id = @tenant AND event.event_time >= @month AND event.event_time < @next
                  AND (@cursor_time IS NULL OR event.event_time < @cursor_time
                       OR (event.event_time = @cursor_time AND event.event_id < @cursor_event))
                UNION ALL
                SELECT event.event_time, event.event_id, event.unit_id, event.attributes
                FROM serialisation.unit_event_archive AS event
                WHERE event.tenant_id = @tenant AND event.event_time >= @month AND event.event_time < @next
                  AND (@cursor_time IS NULL OR event.event_time < @cursor_time
                       OR (event.event_time = @cursor_time AND event.event_id < @cursor_event))
            ) AS source
            ORDER BY source.event_time DESC, source.event_id DESC;
            SET @rows = @@ROWCOUNT;

            INSERT INTO serialisation.ledger_base_order_ref
                (tenant_id, base_document_type, base_document_id, base_document_line, unit_id, event_time, event_id)
            SELECT @tenant, CONVERT(nvarchar(40), source.base_document_type), source.base_document_id,
                   source.base_document_line, source.unit_id, source.event_time, source.event_id
            FROM @source AS source
            WHERE NULLIF(source.base_document_type, N'') IS NOT NULL AND source.base_document_id IS NOT NULL
              AND NOT EXISTS
                  (SELECT 1 FROM serialisation.ledger_base_order_ref AS existing
                   WHERE existing.tenant_id = @tenant AND existing.event_time = source.event_time
                     AND existing.event_id = source.event_id);
            SET @written = @@ROWCOUNT;

            SELECT TOP (1) @last_time = event_time, @last_id = event_id
            FROM @source ORDER BY event_time ASC, event_id ASC;
            SELECT @rows AS scanned, @written AS written, @last_time AS cursor_event_time,
                   @last_id AS cursor_event_id;
            """;
        Add(command, "@batch", DbType.Int32, options.BatchSize);
        Add(command, "@tenant", DbType.Guid, tenant.TenantId.Value);
        Add(command, "@month", DbType.DateTime2, month);
        Add(command, "@next", DbType.DateTime2, month.AddMonths(1));
        Add(command, "@cursor_time", DbType.DateTime2, cursorTime);
        Add(command, "@cursor_event", DbType.Guid, cursorEvent);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The ledger base-order backfill batch returned no progress row.");
        return new BatchResult(reader.GetInt32(0), reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2), reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    private async Task<DateTime?> EdgeAsync<TEvent>(
        DbSet<TEvent> source,
        bool newest,
        DateTime? before,
        CancellationToken cancellationToken)
        where TEvent : UnitEventRow
    {
        var query = source.AsNoTracking().Where(row => row.TenantId == tenant.TenantId);
        if (before is { } boundary) query = query.Where(row => row.EventTime < boundary);
        return newest
            ? await query.OrderByDescending(row => row.EventTime).Select(row => (DateTime?)row.EventTime).FirstOrDefaultAsync(cancellationToken)
            : await query.OrderBy(row => row.EventTime).Select(row => (DateTime?)row.EventTime).FirstOrDefaultAsync(cancellationToken);
    }

    private void Complete(LedgerBaseOrderBackfillCheckpoint checkpoint)
    {
        checkpoint.Status = LedgerBaseOrderBackfillStatus.Complete;
        checkpoint.CheckpointMonth = null;
        checkpoint.CursorEventTime = null;
        checkpoint.CursorEventId = null;
        checkpoint.EstimatedRows = checkpoint.RowsScanned;
        checkpoint.MonthsCompleted = checkpoint.MonthsTotal;
        checkpoint.CompletedAt = clock.UtcNow;
        checkpoint.UpdatedAt = clock.UtcNow;
    }

    private void ValidateOptions()
    {
        if (options.BatchSize is < 1 or > 100_000)
            throw new InvalidOperationException($"{LedgerBaseOrderBackfillOptions.SectionName}:BatchSize must be from 1 to 100000.");
        if (options.PauseMilliseconds is < 0 or > 60_000)
            throw new InvalidOperationException($"{LedgerBaseOrderBackfillOptions.SectionName}:PauseMilliseconds must be from 0 to 60000.");
        if (options.MaximumBatchesPerRun is < 1 or > 10_000)
            throw new InvalidOperationException($"{LedgerBaseOrderBackfillOptions.SectionName}:MaximumBatchesPerRun must be from 1 to 10000.");
        if (options.CommandTimeoutSeconds is < 1 or > 3600)
            throw new InvalidOperationException($"{LedgerBaseOrderBackfillOptions.SectionName}:CommandTimeoutSeconds must be from 1 to 3600.");
    }

    private static void Add(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static LedgerBaseOrderBackfillProgress View(LedgerBaseOrderBackfillCheckpoint row) => new(
        row.Status, row.RowsScanned, row.RowsWritten, row.EstimatedRows, row.CheckpointMonth,
        row.MonthsCompleted, row.MonthsTotal, row.StartedAt, row.UpdatedAt, row.CompletedAt);

    private static DateTime StartOfMonth(DateTime value) => new(value.Year, value.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    private static int MonthsBetween(DateTime newest, DateTime oldest) =>
        (newest.Year - oldest.Year) * 12 + newest.Month - oldest.Month;

    private static DateTime? Max(DateTime? first, DateTime? second) =>
        first is null ? second : second is null || first >= second ? first : second;

    private static DateTime? Min(DateTime? first, DateTime? second) =>
        first is null ? second : second is null || first <= second ? first : second;
}

/// <summary>Tenant-local, throttled continuation of the one-time historical projection.</summary>
[DisallowConcurrentExecution]
public sealed class LedgerBaseOrderBackfillJob(ITenantContextSetter setter, LedgerBaseOrderBackfill backfill)
    : TenantJob(setter)
{
    public const string JobName = "serialisation.ledger_base_order_backfill";

    protected override Task ExecuteForTenant(IJobExecutionContext context, CancellationToken cancellationToken) =>
        backfill.RunAsync(cancellationToken);
}
