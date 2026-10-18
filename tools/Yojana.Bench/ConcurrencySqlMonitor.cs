using Microsoft.Data.SqlClient;

namespace Yojana.Bench;

public sealed record QueryStoreDiagnostic(string Query, long Executions, double AverageMilliseconds, long LogicalReads);

public sealed record SqlMonitorResult(
    long LockWaitDelta,
    int Deadlocks,
    long NumberSeriesWait,
    IReadOnlyDictionary<string, long> WaitMilliseconds,
    int PublishedQueueStart,
    int PublishedQueueMaximum,
    int PublishedQueueEnd,
    IReadOnlyList<QueryStoreDiagnostic> RmaCreateQueries);

public sealed class ConcurrencySqlMonitor(string connectionString, Guid tenantId)
{
    private Dictionary<string, long> initialWaits = new(StringComparer.Ordinal);
    private Dictionary<long, QueryStoreSnapshot> initialQueries = [];
    private int initialDeadlocks;
    private long sampledNumberSeriesWait;
    private int publishedQueueStart;
    private int publishedQueueMaximum;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        initialWaits = await WaitsAsync(connection, cancellationToken);
        initialDeadlocks = await DeadlocksAsync(connection, cancellationToken);
        initialQueries = await QueryStoreAsync(connection, cancellationToken);
        publishedQueueStart = await PublishedQueueDepthAsync(connection, cancellationToken);
        publishedQueueMaximum = publishedQueueStart;
    }

    public async Task SampleAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("""
            SELECT COALESCE(SUM(CONVERT(bigint, r.wait_time)), 0)
            FROM sys.dm_exec_requests r
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
            WHERE r.session_id <> @@SPID AND r.wait_type LIKE 'LCK[_]M[_]%'
              AND (t.text LIKE '%number_series%' OR t.text LIKE @sequence_name)
              AND EXISTS (SELECT 1 FROM serialisation.number_series_setting WHERE tenant_id = @tenant);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId);
        command.Parameters.AddWithValue("@sequence_name", $"%number_{tenantId:N}_%");
        sampledNumberSeriesWait += Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        publishedQueueMaximum = Math.Max(publishedQueueMaximum, await PublishedQueueDepthAsync(connection, cancellationToken));
    }

    public async Task<SqlMonitorResult> FinishAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var finalWaits = await WaitsAsync(connection, cancellationToken);
        var waitDelta = finalWaits.ToDictionary(pair => pair.Key,
            pair => Math.Max(0, pair.Value - initialWaits.GetValueOrDefault(pair.Key)), StringComparer.Ordinal);
        var finalQueries = await QueryStoreAsync(connection, cancellationToken);
        var queryDelta = finalQueries.Select(pair =>
        {
            var before = initialQueries.GetValueOrDefault(pair.Key);
            var executions = Math.Max(0, pair.Value.Executions - (before?.Executions ?? 0));
            var duration = Math.Max(0, pair.Value.DurationMicroseconds - (before?.DurationMicroseconds ?? 0));
            var reads = Math.Max(0, pair.Value.LogicalReads - (before?.LogicalReads ?? 0));
            return new QueryStoreDiagnostic(Short(pair.Value.Text), executions,
                executions == 0 ? 0 : duration / executions / 1000d, reads);
        }).Where(row => row.Executions > 0).OrderByDescending(row => row.AverageMilliseconds * row.Executions)
            .Take(20).ToList();
        var queueEnd = await PublishedQueueDepthAsync(connection, cancellationToken);
        return new(waitDelta.Where(pair => pair.Key.StartsWith("LCK_", StringComparison.Ordinal)).Sum(pair => pair.Value),
            Math.Max(0, await DeadlocksAsync(connection, cancellationToken) - initialDeadlocks), sampledNumberSeriesWait,
            waitDelta, publishedQueueStart, publishedQueueMaximum, queueEnd, queryDelta);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken) =>
        await Sql.OpenAsync(connectionString, tenantId);

    private static async Task<Dictionary<string, long>> WaitsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT wait_type, wait_time_ms FROM sys.dm_os_wait_stats
            WHERE wait_type LIKE 'LCK[_]M[_]%' OR wait_type IN ('THREADPOOL', 'RESOURCE_SEMAPHORE');
            """, connection);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetString(0)] = reader.GetInt64(1);
        return result;
    }

    private static async Task<Dictionary<long, QueryStoreSnapshot>> QueryStoreAsync(SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT q.query_id, MAX(qt.query_sql_text) AS query_sql_text,
                   SUM(CONVERT(bigint, rs.count_executions)) AS executions,
                   SUM(CONVERT(float, rs.avg_duration) * rs.count_executions) AS duration_microseconds,
                   SUM(CONVERT(bigint, rs.avg_logical_io_reads) * rs.count_executions) AS logical_reads
            FROM sys.query_store_query_text qt
            JOIN sys.query_store_query q ON q.query_text_id = qt.query_text_id
            JOIN sys.query_store_plan p ON p.query_id = q.query_id
            JOIN sys.query_store_runtime_stats rs ON rs.plan_id = p.plan_id
            WHERE qt.query_sql_text LIKE '%P6-T22 RMA create:%'
               OR qt.query_sql_text LIKE '%INSERT INTO [[]serialisation].[[]rma]%'
               OR qt.query_sql_text LIKE '%UPDATE [[]serialisation].[[]rma]%'
               OR qt.query_sql_text LIKE '%INSERT INTO [[]serialisation].[[]rma_line]%'
               OR qt.query_sql_text LIKE '%UPDATE [[]serialisation].[[]rma_line]%'
               OR qt.query_sql_text LIKE '%[[]serialisation].[[]number_series]%'
            GROUP BY q.query_id;
            """, connection);
        var result = new Dictionary<long, QueryStoreSnapshot>();
        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result[reader.GetInt64(0)] = new(reader.GetString(1), reader.GetInt64(2), reader.GetDouble(3), reader.GetInt64(4));
        }
        catch (SqlException)
        {
            // Query Store is enabled by benchmark preparation; a deliberately minimal verification database may omit it.
        }
        return result;
    }

    private static async Task<int> PublishedQueueDepthAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT
                (SELECT COUNT(*) FROM wolverine.wolverine_outgoing_envelopes
                 WHERE destination LIKE 'local://serialisation-published-events%')
              + (SELECT COUNT(*) FROM wolverine.wolverine_incoming_envelopes
                 WHERE status <> 'Handled' AND (
                    message_type LIKE 'Yojana.Serialisation.Contracts.UnitReleased%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.UnitAsBuiltCorrected%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.UnitIdentifierCorrected%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.UnitIdentifiersSwapped%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.UnitItemCorrected%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.UnitGraded%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.RefurbishmentReleased%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.UnitMerged%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.UnitIdentifiersAssigned%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.ReplacementShipped%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.ReplacementLinkCorrected%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.CertificateIssueRequested%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.EntitlementCoverageChanged%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.DocumentSerialsCorrected%'
                    OR message_type LIKE 'Yojana.Serialisation.Contracts.OwnershipSourceDocumentCorrected%'));
            """, connection);
        try { return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture); }
        catch (SqlException) { return -1; }
    }

    private async Task<int> DeadlocksAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT COUNT(*)
            FROM (SELECT TRY_CONVERT(xml, target_data) AS data FROM sys.dm_xe_session_targets t
                  JOIN sys.dm_xe_sessions s ON s.address = t.event_session_address
                  WHERE s.name = N'system_health' AND t.target_name = N'ring_buffer') x
            CROSS APPLY x.data.nodes('//RingBufferTarget/event[@name="xml_deadlock_report"]') e(n)
            WHERE EXISTS (SELECT 1 FROM serialisation.number_series_setting WHERE tenant_id = @tenant);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId);
        try { return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture); }
        catch (SqlException) { return 0; }
    }

    private static string Short(string sql)
    {
        var oneLine = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 220 ? oneLine : oneLine[..217] + "...";
    }

    private sealed record QueryStoreSnapshot(string Text, long Executions, double DurationMicroseconds, long LogicalReads);
}
