using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Yojana.Integration.Data;

namespace Yojana.Integration.LegacyMigration.Staging;

public sealed record StagingLoadResult(
    string SourceTable,
    long RowsRead,
    long Inserted,
    long Updated,
    long Unchanged,
    long Absent,
    long DurationMilliseconds);

/// <summary>One extraction: the source server's local clock when it began, and the per-table counts.</summary>
public sealed record StagingExtraction(DateTime SourceClockAtStart, IReadOnlyList<StagingLoadResult> Tables);

/// <summary>
/// Read-only adapter for the incumbent ERP. Production users are expected to be database principals restricted to
/// SELECT on the named extraction objects. Full-table query construction is isolated here so P9-T10 can add a delta
/// predicate without changing batch orchestration or the staging upsert contract.
/// </summary>
public sealed class SourceExtractor(
    IntegrationDbContext db,
    MigrationSourceSecret secrets)
{
    private const int ChunkSize = 10_000;

    public async Task<IReadOnlyList<MigrationSourceTestTable>> CountAsync(MigrationSource source,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenSourceAsync(source, cancellationToken);
            var results = new List<MigrationSourceTestTable>(SourceProtocol.Tables.Count);
            foreach (var table in SourceProtocol.Tables)
            {
                try
                {
                    await using var command = new SqlCommand(
                        $"SELECT COUNT_BIG(1) FROM ({SelectBody(source, table)}) AS [source_rows];", connection)
                    {
                        CommandTimeout = source.CommandTimeoutSeconds,
                    };
                    var count = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken),
                        CultureInfo.InvariantCulture);
                    results.Add(new(table.SourceTable, count, null));
                }
                catch (Exception ex) when (ex is SqlException or InvalidOperationException)
                {
                    results.Add(new(table.SourceTable, null, "migration_source_read_failed"));
                }
            }
            return results;
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException or ArgumentException)
        {
            return SourceProtocol.Tables.Select(table => new MigrationSourceTestTable(table.SourceTable, null,
                "migration_source_connection_failed")).ToList();
        }
    }

    public async Task<StagingExtraction> ExtractFullAsync(MigrationSource source, Guid batchId,
        DateTime extractedAt, CancellationToken cancellationToken)
    {
        if (source.Runtime != MigrationSourceRuntime.SqlServer)
            throw new MigrationRuleException("migration_source_runtime_unsupported",
                "Only sql_server migration sources are extractable in this build.");
        await using var sourceConnection = await OpenSourceAsync(source, cancellationToken);
        var wasOpen = db.Database.GetDbConnection().State == ConnectionState.Open;
        if (!wasOpen) await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var stagingConnection = (SqlConnection)db.Database.GetDbConnection();
            var sourceClock = await SourceClockAsync(sourceConnection, cancellationToken);
            var results = new List<StagingLoadResult>(SourceProtocol.Tables.Count);
            foreach (var table in SourceProtocol.Tables)
                results.Add(await ExtractTableAsync(source, sourceConnection, stagingConnection, table, batchId,
                    extractedAt, cancellationToken));
            return new(sourceClock, results);
        }
        finally
        {
            if (!wasOpen) await db.Database.CloseConnectionAsync();
        }
    }

    private async Task<StagingLoadResult> ExtractTableAsync(MigrationSource source, SqlConnection sourceConnection,
        SqlConnection stagingConnection, SourceTableProtocol table, Guid batchId, DateTime extractedAt,
        CancellationToken cancellationToken)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        await using var transaction = (SqlTransaction)await stagingConnection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(stagingConnection, transaction, CreateLoadTables(table), source.CommandTimeoutSeconds,
            cancellationToken);
        long rowsRead = 0, inserted = 0, updated = 0, unchanged = 0;
        try
        {
            await using var sourceCommand = new SqlCommand(SelectSql(source, table), sourceConnection)
            {
                CommandTimeout = source.CommandTimeoutSeconds,
            };
            await using var reader = await sourceCommand.ExecuteReaderAsync(CommandBehavior.SequentialAccess,
                cancellationToken);
            var chunk = CreateBuffer(table);
            while (await reader.ReadAsync(cancellationToken))
            {
                AddRow(chunk, table, reader, source, batchId, extractedAt);
                rowsRead++;
                if (chunk.Rows.Count < ChunkSize) continue;
                var counts = await LoadChunkAsync(stagingConnection, transaction, table, chunk,
                    source.CommandTimeoutSeconds, cancellationToken);
                inserted += counts.Inserted;
                updated += counts.Updated;
                unchanged += counts.Unchanged;
                chunk.Clear();
            }
            if (chunk.Rows.Count != 0)
            {
                var counts = await LoadChunkAsync(stagingConnection, transaction, table, chunk,
                    source.CommandTimeoutSeconds, cancellationToken);
                inserted += counts.Inserted;
                updated += counts.Updated;
                unchanged += counts.Unchanged;
            }
            var absent = await MarkAbsentAsync(stagingConnection, transaction, table, source, batchId,
                source.CommandTimeoutSeconds, cancellationToken);
            await ExecuteAsync(stagingConnection, transaction,
                "DROP TABLE #migration_load; DROP TABLE #migration_seen;", source.CommandTimeoutSeconds,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            started.Stop();
            return new(table.SourceTable, rowsRead, inserted, updated, unchanged, absent,
                (long)started.Elapsed.TotalMilliseconds);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<ChunkCounts> LoadChunkAsync(SqlConnection connection, SqlTransaction transaction,
        SourceTableProtocol table, DataTable chunk, int timeout, CancellationToken cancellationToken)
    {
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, transaction)
        {
            DestinationTableName = "#migration_load",
            BatchSize = ChunkSize,
            BulkCopyTimeout = timeout,
        })
        {
            foreach (DataColumn column in chunk.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(chunk, cancellationToken);
        }
        await using var command = new SqlCommand(UpsertSql(table), connection, transaction)
        {
            CommandTimeout = timeout,
        };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var result = new ChunkCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        await reader.CloseAsync();
        return result;
    }

    private static async Task<long> MarkAbsentAsync(SqlConnection connection, SqlTransaction transaction,
        SourceTableProtocol table, MigrationSource source, Guid batchId, int timeout,
        CancellationToken cancellationToken)
    {
        var sql = $"""
            UPDATE target
               SET absent_since_batch_id = @batch_id
              FROM [{MigrationModel.Schema}].[{table.StagingTable}] AS target
             WHERE target.tenant_id = @tenant_id
               AND target.source_system = @source_system
               AND target.source_table = @source_table
               AND target.absent_since_batch_id IS NULL
               AND NOT EXISTS (SELECT 1 FROM #migration_seen AS seen WHERE seen.source_pk = target.source_pk);
            SELECT CAST(@@ROWCOUNT AS bigint);
            """;
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = timeout };
        command.Parameters.Add(new("@tenant_id", SqlDbType.UniqueIdentifier) { Value = source.TenantId.Value });
        command.Parameters.Add(new("@source_system", SqlDbType.NVarChar, 40) { Value = source.SourceSystem });
        command.Parameters.Add(new("@source_table", SqlDbType.NVarChar, 100) { Value = table.SourceTable });
        command.Parameters.Add(new("@batch_id", SqlDbType.UniqueIdentifier) { Value = batchId });
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    // HIDDEN COUPLING (OD-60, connector hand-over after the final load)
    // WHAT:  this is the source server's own local clock, read before the first table. The final load turns it into
    //        the connector's starting watermark (IConnectorPoller.StartingWatermark), whose modified-since filter compares
    //        against the incumbent's document update stamps, which that server writes in the same local time.
    // WHY:   the incumbent stores no time zone with its stamps, so the product's UTC clock cannot be compared with them.
    // WHAT BREAKS IF SEPARATED: reading the clock after the tables, or from another server, opens a window of
    //        documents that are in neither the migrated history nor the connector's first poll.
    private static async Task<DateTime> SourceClockAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT SYSDATETIME();";
        return DateTime.SpecifyKind((DateTime)(await command.ExecuteScalarAsync(cancellationToken))!,
            DateTimeKind.Unspecified);
    }

    private async Task<SqlConnection> OpenSourceAsync(MigrationSource source, CancellationToken cancellationToken)
    {
        var raw = source.Route == MigrationSourceRoute.Separate
            ? await secrets.GetAsync(source.ConnectionId, cancellationToken)
                ?? throw new MigrationRuleException("migration_source_connection_string_required",
                    "The separate source has no connection string.")
            : db.Database.GetConnectionString()
                ?? throw new InvalidOperationException("The product database connection string is not configured.");
        var builder = new SqlConnectionStringBuilder(raw)
        {
            ApplicationIntent = ApplicationIntent.ReadOnly,
        };
        if (source.Route == MigrationSourceRoute.Separate) builder.InitialCatalog = source.DatabaseName;
        var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string SelectSql(MigrationSource source, SourceTableProtocol table) =>
        SelectBody(source, table) + " ORDER BY " +
        string.Join(", ", table.Keys.Select(key => $"[{key.TargetName}]")) + ";";

    private static string SelectBody(MigrationSource source, SourceTableProtocol table) =>
        string.Join(" UNION ALL ", table.Reads.Select(read =>
            "SELECT " + string.Join(", ", table.Columns.Select(column =>
                Projection(read, column) + $" AS [{column.TargetName}]")) +
            $" FROM {QualifiedName(source, read.SourceTable)}"));

    private static string Projection(SourceRead read, RawColumn column)
    {
        if (read.TextConstants?.TryGetValue(column.PropertyName, out var constant) == true)
            return "N'" + constant.Replace("'", "''", StringComparison.Ordinal) + "'";
        if (read.NullProperties?.Contains(column.PropertyName) == true)
            return $"CAST(NULL AS {SqlDeclaration(column)})";
        var sourceName = read.SourceColumnOverrides?.GetValueOrDefault(column.PropertyName) ?? column.SourceName
            ?? throw new InvalidOperationException($"No source projection was declared for {column.PropertyName}.");
        return $"[{Escape(sourceName)}]";
    }

    internal static string QualifiedName(MigrationSource source, string table)
    {
        var schema = $"[{Escape(source.SchemaName)}]";
        var name = $"[{Escape(table)}]";
        return source.Route switch
        {
            MigrationSourceRoute.CrossDatabase => $"[{Escape(source.DatabaseName)}].{schema}.{name}",
            MigrationSourceRoute.LinkedServer =>
                $"[{Escape(source.LinkedServerName!)}].[{Escape(source.DatabaseName)}].{schema}.{name}",
            _ => $"{schema}.{name}",
        };
    }

    private static string Escape(string value) => value.Replace("]", "]]", StringComparison.Ordinal);

    private static DataTable CreateBuffer(SourceTableProtocol table)
    {
        var data = new DataTable { Locale = CultureInfo.InvariantCulture };
        data.Columns.Add("tenant_id", typeof(Guid));
        data.Columns.Add("batch_id", typeof(Guid));
        data.Columns.Add("source_system", typeof(string));
        data.Columns.Add("source_table", typeof(string));
        data.Columns.Add("source_pk", typeof(string));
        data.Columns.Add("extracted_at", typeof(DateTime));
        data.Columns.Add("row_hash", typeof(byte[]));
        data.Columns.Add("first_batch_id", typeof(Guid));
        data.Columns.Add("absent_since_batch_id", typeof(Guid));
        foreach (var column in table.Columns) data.Columns.Add(column.TargetName, column.ClrType);
        return data;
    }

    private static void AddRow(DataTable data, SourceTableProtocol table, SqlDataReader reader,
        MigrationSource source, Guid batchId, DateTime extractedAt)
    {
        var values = new object?[table.Columns.Count];
        for (var index = 0; index < table.Columns.Count; index++)
            values[index] = reader.IsDBNull(index) ? null : ConvertValue(reader.GetValue(index), table.Columns[index]);
        var sourcePk = string.Join('|', table.Columns.Select((column, index) => (column, index))
            .Where(item => item.column.Key).Select(item =>
        {
            var value = values[item.index];
            return value is null ? throw new InvalidOperationException(
                    $"A source primary-key value was null for {table.SourceTable}.{item.column.PropertyName}.")
                : Canonical(value);
        }));
        var row = data.NewRow();
        row["tenant_id"] = source.TenantId.Value;
        row["batch_id"] = batchId;
        row["source_system"] = source.SourceSystem;
        row["source_table"] = table.SourceTable;
        row["source_pk"] = sourcePk;
        row["extracted_at"] = extractedAt;
        row["row_hash"] = Hash(values);
        row["first_batch_id"] = batchId;
        row["absent_since_batch_id"] = DBNull.Value;
        for (var index = 0; index < table.Columns.Count; index++)
            row[table.Columns[index].TargetName] = values[index] ?? DBNull.Value;
        data.Rows.Add(row);
    }

    private static object ConvertValue(object value, RawColumn column) => column.Kind switch
    {
        RawColumnKind.Int32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
        RawColumnKind.Int64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        RawColumnKind.Decimal => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        RawColumnKind.DateTime => Convert.ToDateTime(value, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    private static byte[] Hash(IReadOnlyList<object?> values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var value in values)
            {
                switch (value)
                {
                    case null: writer.WriteNullValue(); break;
                    case string text: writer.WriteStringValue(text); break;
                    case int number: writer.WriteNumberValue(number); break;
                    case long number: writer.WriteNumberValue(number); break;
                    case decimal number: writer.WriteNumberValue(number); break;
                    case DateTime date: writer.WriteStringValue(date.ToString("O", CultureInfo.InvariantCulture)); break;
                    default: throw new InvalidOperationException($"Unsupported canonical value type {value.GetType().Name}.");
                }
            }
            writer.WriteEndArray();
        }
        return SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
    }

    private static string Canonical(object value) => value switch
    {
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formatted => formatted.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()!,
    };

    private static string CreateLoadTables(SourceTableProtocol table)
    {
        var rawColumns = string.Join(",\n", table.Columns.Select(column =>
            $"[{column.TargetName}] {SqlDeclaration(column)} NULL"));
        return $"""
            CREATE TABLE #migration_load
            (
                tenant_id uniqueidentifier NOT NULL,
                batch_id uniqueidentifier NOT NULL,
                source_system nvarchar(40) NOT NULL,
                source_table nvarchar(100) NOT NULL,
                source_pk nvarchar(1000) NOT NULL,
                extracted_at datetime2(3) NOT NULL,
                row_hash binary(32) NOT NULL,
                first_batch_id uniqueidentifier NOT NULL,
                absent_since_batch_id uniqueidentifier NULL,
                {rawColumns}
            );
            CREATE TABLE #migration_seen (source_pk nvarchar(1000) NOT NULL PRIMARY KEY);
            """;
    }

    private static string SqlDeclaration(RawColumn column) => column.Kind switch
    {
        RawColumnKind.Int32 => "int",
        RawColumnKind.Int64 => "bigint",
        RawColumnKind.Decimal => "decimal(28,8)",
        RawColumnKind.DateTime => "datetime2(3)",
        _ => $"nvarchar({column.MaxLength})",
    };

    private static string UpsertSql(SourceTableProtocol table)
    {
        var target = $"[{MigrationModel.Schema}].[{table.StagingTable}]";
        var join = "target.tenant_id = load_row.tenant_id AND target.source_system = load_row.source_system " +
                   "AND target.source_table = load_row.source_table AND target.source_pk = load_row.source_pk";
        // TIGHT COUPLING (stored serial match keys): these assignments and migration.canonical_serial's
        // trim-and-uppercase behaviour are one storage contract. The staging-key migrations use the same function;
        // changing its false/false/false behaviour without rebuilding these columns makes old and new rows differ.
        var matchKeys = MatchKeys(table.RowType);
        var rawUpdates = string.Join(",\n                   ", table.Columns.Select(column =>
                $"target.[{column.TargetName}] = load_row.[{column.TargetName}]")
            .Concat(matchKeys.Select(key =>
                $"target.[{key.Target}] = {MatchKeySql($"load_row.[{key.Source}]")}")));
        var allColumns = new[] { "tenant_id", "batch_id", "source_system", "source_table", "source_pk",
            "extracted_at", "row_hash", "first_batch_id", "absent_since_batch_id" }
            .Concat(table.Columns.Select(x => x.TargetName))
            .Concat(matchKeys.Select(key => key.Target)).ToList();
        var insertColumns = string.Join(", ", allColumns.Select(x => $"[{x}]"));
        var matchKeyTargets = matchKeys.ToDictionary(key => key.Target, StringComparer.Ordinal);
        var insertValues = string.Join(", ", allColumns.Select(column =>
            matchKeyTargets.TryGetValue(column, out var key)
                ? MatchKeySql($"load_row.[{key.Source}]") : $"load_row.[{column}]"));
        return $"""
            IF EXISTS (SELECT source_pk FROM #migration_load GROUP BY source_pk HAVING COUNT_BIG(1) > 1)
                THROW 51000, 'migration_source_duplicate_key', 1;
            IF EXISTS (SELECT 1 FROM #migration_load AS load_row JOIN #migration_seen AS seen ON seen.source_pk = load_row.source_pk)
                THROW 51000, 'migration_source_duplicate_key', 1;

            DECLARE @inserted bigint = 0, @updated bigint = 0, @unchanged bigint = 0;
            SELECT @inserted = COALESCE(SUM(CASE WHEN target.source_pk IS NULL THEN 1 ELSE 0 END), 0),
                   @updated = COALESCE(SUM(CASE WHEN target.source_pk IS NOT NULL AND
                       (target.row_hash <> load_row.row_hash OR target.absent_since_batch_id IS NOT NULL) THEN 1 ELSE 0 END), 0),
                   @unchanged = COALESCE(SUM(CASE WHEN target.source_pk IS NOT NULL AND
                       target.row_hash = load_row.row_hash AND target.absent_since_batch_id IS NULL THEN 1 ELSE 0 END), 0)
              FROM #migration_load AS load_row
              LEFT JOIN {target} AS target ON {join};

            UPDATE target
               SET target.batch_id = load_row.batch_id,
                   target.extracted_at = load_row.extracted_at
              FROM {target} AS target
              JOIN #migration_load AS load_row ON {join}
             WHERE target.row_hash = load_row.row_hash
               AND target.absent_since_batch_id IS NULL;

            UPDATE target
               SET target.batch_id = load_row.batch_id,
                   target.extracted_at = load_row.extracted_at,
                   target.row_hash = load_row.row_hash,
                   target.absent_since_batch_id = NULL,
                   {rawUpdates}
              FROM {target} AS target
              JOIN #migration_load AS load_row ON {join}
             WHERE target.row_hash <> load_row.row_hash
                OR target.absent_since_batch_id IS NOT NULL;

            INSERT {target} ({insertColumns})
            SELECT {insertValues}
              FROM #migration_load AS load_row
             WHERE NOT EXISTS (SELECT 1 FROM {target} AS target WHERE {join});

            INSERT #migration_seen (source_pk) SELECT DISTINCT source_pk FROM #migration_load;
            TRUNCATE TABLE #migration_load;
            SELECT @inserted, @updated, @unchanged;
            """;
    }

    private static IReadOnlyList<MatchKeyColumn> MatchKeys(Type rowType) =>
        rowType == typeof(SerialMasterStagingRow)
            ? [new("normalised_serial_number", "serial_number"),
                new("normalised_manufacturer_serial", "manufacturer_serial")]
            : rowType == typeof(EquipmentCardStagingRow)
                ? [new("normalised_internal_serial", "internal_serial"),
                    new("normalised_manufacturer_serial", "manufacturer_serial")]
                : rowType == typeof(ContractLineStagingRow)
                    ? [new("normalised_internal_serial", "internal_serial")]
                : [];

    private static string MatchKeySql(string value) =>
        $"[{MigrationModel.Schema}].[canonical_serial]({value}, 0, 0, 0)";

    private static async Task ExecuteAsync(SqlConnection connection, SqlTransaction transaction, string sql,
        int timeout, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = timeout };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record ChunkCounts(long Inserted, long Updated, long Unchanged);
    private sealed record MatchKeyColumn(string Target, string Source);
}
