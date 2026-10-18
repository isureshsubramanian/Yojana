using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace Yojana.Bench;

/// <summary>Side-by-side SQL Server logical-read measurements for the concurrency run's repaired queries.</summary>
public sealed class ConcurrencyHotPathVerifier(BenchOptions options)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await Sql.OpenAsync(options.ConnectionString, options.ConcurrencyTenantId);
        var pairRows = await Sql.RowsAsync(connection, """
            SELECT TOP (25) idempotency_key, event_time
            FROM serialisation.unit_event
            WHERE tenant_id = @tenant
            ORDER BY event_time DESC;
            """, 0, ("@tenant", options.ConcurrencyTenantId));
        if (pairRows.Count == 0) throw new InvalidOperationException("The benchmark tenant has no ledger events.");
        var keys = JsonSerializer.Serialize(pairRows.Select(row => (string)row["idempotency_key"]!).ToList());
        var times = JsonSerializer.Serialize(pairRows.Select(row => (DateTime)row["event_time"]!).ToList());
        var pairs = JsonSerializer.Serialize(pairRows.Select(row => new
        {
            key = (string)row["idempotency_key"]!,
            at = (DateTime)row["event_time"]!,
        }).ToList());

        var measurements = new[]
        {
            await MeasureAsync(connection, "ledger independent IN lists", """
                SELECT e.idempotency_key, e.event_time
                FROM serialisation.unit_event AS e
                WHERE e.tenant_id = @tenant
                  AND e.idempotency_key IN (SELECT value FROM OPENJSON(@keys) WITH (value varchar(64) '$'))
                  AND e.event_time IN (SELECT value FROM OPENJSON(@times) WITH (value datetime2(3) '$'));
                """, cancellationToken, ("@tenant", options.ConcurrencyTenantId), ("@keys", keys), ("@times", times)),
            await MeasureAsync(connection, "ledger exact JSON pairs", """
                SELECT e.idempotency_key, e.event_time
                FROM OPENJSON(@pairs) WITH ([key] varchar(64) '$.key', [at] datetime2(3) '$.at') AS requested
                INNER LOOP JOIN serialisation.unit_event AS e
                  WITH (FORCESEEK, INDEX(ux_unit_event_tenant_id_idempotency_key_event_time))
                  ON e.tenant_id = @tenant AND e.idempotency_key = requested.[key] AND e.event_time = requested.[at];
                """, cancellationToken, ("@tenant", options.ConcurrencyTenantId), ("@pairs", pairs)),
            await MeasureAsync(connection, "claim CASE sort", """
                SELECT c.claim_id FROM serialisation.claim AS c
                WHERE c.tenant_id = @tenant
                ORDER BY CASE WHEN c.status IN (N'settled', N'closed') THEN 1 ELSE 0 END, c.submitted_at, c.claim_id
                OFFSET 0 ROWS FETCH NEXT 25 ROWS ONLY;
                """, cancellationToken, ("@tenant", options.ConcurrencyTenantId)),
            await MeasureAsync(connection, "claim indexed sort", """
                SELECT c.claim_id FROM serialisation.claim AS c
                WHERE c.tenant_id = @tenant
                ORDER BY c.list_order_bucket, c.submitted_at, c.claim_id
                OFFSET 0 ROWS FETCH NEXT 25 ROWS ONLY;
                """, cancellationToken, ("@tenant", options.ConcurrencyTenantId)),
        };

        Console.WriteLine("query\trows\tlogical_reads\telapsed_ms");
        foreach (var measurement in measurements)
            Console.WriteLine($"{measurement.Name}\t{measurement.Rows}\t{measurement.LogicalReads}\t{measurement.Elapsed.TotalMilliseconds:0.0}");
    }

    private static async Task<Measurement> MeasureAsync(SqlConnection connection, string name, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        try
        {
            await using (var enable = new SqlCommand("SET STATISTICS XML ON;", connection))
                await enable.ExecuteNonQueryAsync(cancellationToken);
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
            foreach (var (parameterName, value) in parameters) command.Parameters.AddWithValue(parameterName, value);
            var rows = 0;
            string? showPlan = null;
            var watch = Stopwatch.StartNew();
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken)) rows++;
                while (await reader.NextResultAsync(cancellationToken))
                {
                    if (reader.FieldCount != 1 || !await reader.ReadAsync(cancellationToken)) continue;
                    if (reader.GetValue(0) is string candidate && candidate.Contains("ShowPlanXML", StringComparison.Ordinal))
                        showPlan = candidate;
                }
            }
            watch.Stop();
            var reads = showPlan is null ? 0 : LogicalReads(showPlan);
            return new(name, rows, reads, watch.Elapsed);
        }
        finally
        {
            await using var disable = new SqlCommand("SET STATISTICS XML OFF;", connection);
            await disable.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static long LogicalReads(string showPlan)
    {
        var document = XDocument.Parse(showPlan);
        return document.Descendants().Where(element => element.Name.LocalName == "RelOp"
                && ((string?)element.Attribute("PhysicalOp") is "Index Seek" or "Clustered Index Seek" or "Index Scan" or "Clustered Index Scan" or "Table Scan"))
            .SelectMany(operation => operation.Descendants().Where(element => element.Name.LocalName == "RunTimeCountersPerThread"))
            .Sum(counters => long.TryParse((string?)counters.Attribute("ActualLogicalReads"), out var value) ? value : 0);
    }

    private sealed record Measurement(string Name, int Rows, long LogicalReads, TimeSpan Elapsed);
}
