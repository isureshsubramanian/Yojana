using Microsoft.Data.SqlClient;

namespace Yojana.Bench;

public sealed class ConcurrencySqlMonitor(string connectionString, Guid tenantId)
{
    private long initialLockWait;
    private int initialDeadlocks;
    private long sampledNumberSeriesWait;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        initialLockWait = await LockWaitAsync(connection, cancellationToken);
        initialDeadlocks = await DeadlocksAsync(connection, cancellationToken);
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
    }

    public async Task<(long LockWaitDelta, int Deadlocks, long NumberSeriesWait)> FinishAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return (Math.Max(0, await LockWaitAsync(connection, cancellationToken) - initialLockWait),
            Math.Max(0, await DeadlocksAsync(connection, cancellationToken) - initialDeadlocks), sampledNumberSeriesWait);
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        return await Sql.OpenAsync(connectionString, tenantId);
    }

    private async Task<long> LockWaitAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT COALESCE(SUM(wait_time_ms), 0) FROM sys.dm_os_wait_stats
            WHERE wait_type LIKE 'LCK[_]M[_]%'
              AND EXISTS (SELECT 1 FROM serialisation.number_series_setting WHERE tenant_id = @tenant);
            """, connection);
        command.Parameters.AddWithValue("@tenant", tenantId);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
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
}
