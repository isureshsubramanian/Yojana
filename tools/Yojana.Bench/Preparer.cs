using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Serialisation.Data;

namespace Yojana.Bench;

/// <summary>Database settings for a bulk load, the tenant rows, and the partition function extended to the requested count.</summary>
public sealed class Preparer(IServiceProvider services, BenchOptions options, BenchReport report)
{
    public async Task RunAsync()
    {
        report.Log("prepare: tenants");
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            for (var i = 1; i <= options.Tenants; i++)
            {
                var id = new TenantId(BenchOptions.TenantId(i));
                if (!await db.Tenants.AnyAsync(t => t.Id == id))
                {
                    db.Tenants.Add(new Tenant { Id = id, Code = $"BENCH{i}", Name = $"Benchmark tenant {i}", CreatedAt = DateTime.UtcNow });
                }
            }

            await db.SaveChangesAsync();
        }

        await using var connection = await Sql.OpenAsync(options.ConnectionString);
        // A bulk load does not need point-in-time recovery; SIMPLE keeps the log from growing by the size of the data.
        await Sql.ExecAsync(connection, $"IF (SELECT recovery_model FROM sys.databases WHERE name = DB_NAME()) <> 3 ALTER DATABASE [{options.Database}] SET RECOVERY SIMPLE;");
        await Sql.ExecAsync(connection, $"IF (SELECT is_query_store_on FROM sys.databases WHERE name = DB_NAME()) = 0 ALTER DATABASE [{options.Database}] SET QUERY_STORE = ON (OPERATION_MODE = READ_WRITE, INTERVAL_LENGTH_MINUTES = 1, QUERY_CAPTURE_MODE = ALL);");
        report.Fact("Database", "Recovery model", await Sql.ScalarAsync<string>(connection, "SELECT recovery_model_desc FROM sys.databases WHERE name = DB_NAME()"));
        report.Fact("Database", "READ_COMMITTED_SNAPSHOT", await Sql.ScalarAsync<bool>(connection, "SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()"));

        // Partition function: the Initial migration creates 2024-01 .. 2027-12; split older months until the requested count exists.
        var existing = (await Sql.RowsAsync(connection, $"SELECT CAST(r.value AS date) AS v FROM sys.partition_functions f JOIN sys.partition_range_values r ON r.function_id = f.function_id WHERE f.name = '{SerialisationDbContext.PartitionFunction}' ORDER BY r.boundary_id"))
            .Select(r => DateOnly.FromDateTime((DateTime)r["v"]!)).ToList();
        var lowest = existing.Min();
        var added = 0;
        while (existing.Count + added < options.Partitions)
        {
            lowest = lowest.AddMonths(-1);
            await Sql.ExecAsync(connection, $"ALTER PARTITION SCHEME [{SerialisationDbContext.PartitionScheme}] NEXT USED [PRIMARY]; ALTER PARTITION FUNCTION [{SerialisationDbContext.PartitionFunction}]() SPLIT RANGE ('{lowest:yyyy-MM-dd}');");
            added++;
        }

        var count = await Sql.ScalarAsync<int>(connection, $"SELECT COUNT(*) FROM sys.partition_functions f JOIN sys.partition_range_values r ON r.function_id = f.function_id WHERE f.name = '{SerialisationDbContext.PartitionFunction}'");
        report.Fact("Database", "Partition boundaries", $"{count} (partitions {count + 1}); first boundary {lowest:yyyy-MM-dd}; {added} added for this run");
        report.Log($"prepare: partition boundaries {count}");
    }
}
