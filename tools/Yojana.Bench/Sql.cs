using System.Data;
using Microsoft.Data.SqlClient;

namespace Yojana.Bench;

/// <summary>Plain connections for the load and the server-side loops; every tenant-scoped connection carries the session context row-level security reads.</summary>
public static class Sql
{
    public static async Task<SqlConnection> OpenAsync(string connectionString, Guid? tenant = null)
    {
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        if (tenant is { } t)
        {
            await using var command = new SqlCommand("EXEC sp_set_session_context @key = N'tenant_id', @value = @tenant, @read_only = 1;", connection);
            command.Parameters.AddWithValue("@tenant", t);
            await command.ExecuteNonQueryAsync();
        }

        return connection;
    }

    public static async Task<int> ExecAsync(SqlConnection connection, string sql, int timeoutSeconds = 0, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<T?> ScalarAsync<T>(SqlConnection connection, string sql, int timeoutSeconds = 0, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        var result = await command.ExecuteScalarAsync();
        return result is null || result is DBNull ? default : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    public static async Task<List<Dictionary<string, object?>>> RowsAsync(SqlConnection connection, string sql, int timeoutSeconds = 0, params (string Name, object Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = timeoutSeconds };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return rows;
    }

    public static async Task BulkAsync(SqlConnection connection, string table, DataTable rows, int batchSize = 100_000)
    {
        using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock | SqlBulkCopyOptions.KeepNulls | SqlBulkCopyOptions.UseInternalTransaction | SqlBulkCopyOptions.CheckConstraints, null)
        {
            DestinationTableName = table, BatchSize = batchSize, BulkCopyTimeout = 0, EnableStreaming = true,
        };
        foreach (DataColumn column in rows.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        await bulk.WriteToServerAsync(rows);
    }

    public static DataTable Table(params (string Name, Type Type)[] columns)
    {
        var table = new DataTable();
        foreach (var (name, type) in columns) table.Columns.Add(name, type);
        return table;
    }
}
