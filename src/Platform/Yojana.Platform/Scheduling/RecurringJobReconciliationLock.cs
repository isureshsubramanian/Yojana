using Microsoft.Data.SqlClient;

namespace Yojana.Platform.Scheduling;

/// <summary>
/// TIGHT COUPLING: this SQL Server application lock and the clustered Quartz store must use the same database.
/// Quartz coordinates firing but has no cluster-wide application-work lock; separating the databases lets two hosts
/// observe and insert the same missing schedule concurrently. Keep the matching configuration comment in PlatformSetup.
/// </summary>
public sealed class RecurringJobReconciliationLock(PlatformOptions options)
{
    private const string Resource = "yojana:recurring-job-reconciliation";

    public async Task<IAsyncDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 65;
            command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 60000; SELECT @result;";
            command.Parameters.AddWithValue("@resource", Resource);
            var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
            if (result < 0)
            {
                throw new TimeoutException($"Could not acquire the recurring-job reconciliation lock (SQL result {result}).");
            }

            return new Lease(connection);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class Lease(SqlConnection connection) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            var released = false;
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = "DECLARE @result int; EXEC @result = sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session'; SELECT @result;";
                command.Parameters.AddWithValue("@resource", Resource);
                var result = Convert.ToInt32(await command.ExecuteScalarAsync(CancellationToken.None), System.Globalization.CultureInfo.InvariantCulture);
                released = result >= 0;
            }
            finally
            {
                if (!released)
                {
                    SqlConnection.ClearPool(connection);
                }

                await connection.DisposeAsync();
            }
        }
    }
}
