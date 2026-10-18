using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Quartz;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Scheduling;
using Yojana.Platform.Tenancy;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ledger;

namespace Yojana.Bench;

/// <summary>Logical-read comparison for the P6 ledger predicates that must retain a seekable leading key.</summary>
public sealed class ReadShapeMeasurer(BenchOptions options, IServiceProvider services)
{
    private static readonly Regex LogicalReads = new(
        @"logical reads (?<reads>\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var tenant = BenchOptions.TenantId(1);
        await using var connection = await Sql.OpenAsync(options.ConnectionString, tenant);
        var facts = await Sql.RowsAsync(connection, """
            SELECT TOP (100) unit.unit_id, unit.created_event_id, unit.created_event_time
            FROM serialisation.unit AS unit
            WHERE unit.tenant_id = @tenant
            ORDER BY unit.unit_id;
            """, 0, ("@tenant", tenant));
        if (facts.Count == 0) throw new InvalidOperationException("The read-shape benchmark needs loaded units.");

        var eventIds = JsonSerializer.Serialize(facts.Select(row => (Guid)row["created_event_id"]!).ToList());
        var unitIds = JsonSerializer.Serialize(facts.Select(row => (Guid)row["unit_id"]!).ToList());
        var eventTimes = JsonSerializer.Serialize(facts.Select(row => (DateTime)row["created_event_time"]!).ToList());
        var eventOnly = """
            SELECT event.event_id, event.event_time
            FROM serialisation.unit_event AS event
            WHERE event.tenant_id = @tenant
              AND event.event_id IN (SELECT value FROM OPENJSON(@event_ids) WITH (value uniqueidentifier '$'));
            """;
        var unitCreationFacts = """
            SELECT unit.unit_id, unit.created_event_time
            FROM serialisation.unit AS unit
            WHERE unit.tenant_id = @tenant
              AND unit.unit_id IN (SELECT value FROM OPENJSON(@unit_ids) WITH (value uniqueidentifier '$'));
            """;

        await CompareAsync(connection, "certificate creation-time batch (100)", eventOnly, unitCreationFacts, cancellationToken,
            ("@tenant", tenant), ("@event_ids", eventIds), ("@unit_ids", unitIds), ("@event_times", eventTimes));

        var closingEventIds = JsonSerializer.Serialize(facts.Take(30).Select(row => (Guid)row["created_event_id"]!).ToList());
        var closingEventTimes = JsonSerializer.Serialize(facts.Take(30).Select(row => (DateTime)row["created_event_time"]!).ToList());
        var eventByTimeAndId = """
            SELECT event.event_id, event.event_time
            FROM serialisation.unit_event AS event
            WHERE event.tenant_id = @tenant
              AND event.event_time IN (SELECT value FROM OPENJSON(@event_times) WITH (value datetime2(3) '$'))
              AND event.event_id IN (SELECT value FROM OPENJSON(@event_ids) WITH (value uniqueidentifier '$'));
            """;
        await CompareAsync(connection, "release issue-point closing-event batch (30)", eventOnly, eventByTimeAndId,
            cancellationToken, ("@tenant", tenant), ("@event_ids", closingEventIds),
            ("@unit_ids", unitIds), ("@event_times", closingEventTimes));

        var baseDocument = (await Sql.RowsAsync(connection, """
            SELECT TOP (1) base_document_type, base_document_id, base_document_line
            FROM serialisation.ledger_base_order_ref
            WHERE tenant_id = @tenant
            ORDER BY event_time;
            """, 0, ("@tenant", tenant))).SingleOrDefault()
            ?? throw new InvalidOperationException("The read-shape benchmark needs loaded base-order facts.");
        var documentType = (string)baseDocument["base_document_type"]!;
        var documentId = (Guid)baseDocument["base_document_id"]!;
        var documentLine = (int)baseDocument["base_document_line"]!;
        var beforeBaseDocument = """
            SELECT TOP (100001) event.unit_id, event.attributes
            FROM serialisation.unit_event AS event
            WHERE event.tenant_id = @tenant
              AND event.attributes LIKE '%' + CONVERT(varchar(36), @document_id) + '%'
            ORDER BY event.seq;
            """;
        var afterBaseDocument = """
            SELECT TOP (100001) reference.unit_id
            FROM serialisation.ledger_base_order_ref AS reference
            WHERE reference.tenant_id = @tenant AND reference.base_document_type = @document_type
              AND reference.base_document_id = @document_id AND reference.base_document_line = @document_line
            ORDER BY reference.unit_id;
            """;
        await CompareAsync(connection, "selection base-order lookup", beforeBaseDocument, afterBaseDocument,
            cancellationToken, ("@tenant", tenant), ("@document_type", documentType),
            ("@document_id", documentId), ("@document_line", documentLine));
    }

    public async Task BackfillAsync(CancellationToken cancellationToken)
    {
        var tenant = new TenantId(BenchOptions.TenantId(1));
        await using var connection = await Sql.OpenAsync(options.ConnectionString, tenant.Value);
        var scheduler = await services.GetRequiredService<ISchedulerFactory>().GetScheduler(cancellationToken);
        await scheduler.DeleteJob(TenantJob.KeyFor(LedgerBaseOrderBackfillJob.JobName, tenant), cancellationToken);
        var events = await Sql.ScalarAsync<long>(connection,
            "SELECT (SELECT COUNT_BIG(*) FROM serialisation.unit_event WHERE tenant_id = @tenant) + (SELECT COUNT_BIG(*) FROM serialisation.unit_event_archive WHERE tenant_id = @tenant)",
            0, ("@tenant", tenant.Value));
        using (var reset = services.CreateScope())
        {
            reset.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
            var db = reset.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            await db.LedgerBaseOrderReferences.ExecuteDeleteAsync(cancellationToken);
            await db.LedgerBaseOrderBackfillCheckpoints.ExecuteDeleteAsync(cancellationToken);
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        LedgerBaseOrderBackfillProgress progress;
        do
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(tenant);
            progress = await scope.ServiceProvider.GetRequiredService<LedgerBaseOrderBackfill>()
                .RunAsync(cancellationToken);
        } while (progress.Status != LedgerBaseOrderBackfillStatus.Complete);

        stopwatch.Stop();
        var seconds = Math.Max(stopwatch.Elapsed.TotalSeconds, 0.001);
        var estimateSeconds = 152_000_000d / Math.Max(1, events / seconds);
        Console.WriteLine($"ledger base-order background backfill: scanned {progress.RowsDone:N0}/{events:N0} events, wrote {progress.RowsWritten:N0} refs in {seconds:N3} s ({events / seconds:N0} events/s); 152 M estimate {TimeSpan.FromSeconds(estimateSeconds):g}");
    }

    private static async Task CompareAsync(SqlConnection connection, string label, string before, string after,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        var beforeReads = await ReadsAsync(connection, before, cancellationToken, parameters);
        var afterReads = await ReadsAsync(connection, after, cancellationToken, parameters);
        Console.WriteLine($"{label}: before {beforeReads:N0} logical reads; after {afterReads:N0} logical reads");
    }

    private static async Task<int> ReadsAsync(SqlConnection connection, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        var messages = new List<string>();
        SqlInfoMessageEventHandler handler = (_, args) => messages.AddRange(args.Errors.Cast<SqlError>().Select(error => error.Message));
        connection.InfoMessage += handler;
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SET STATISTICS IO ON;\n" + sql + "\nSET STATISTICS IO OFF;";
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            do
            {
                while (await reader.ReadAsync(cancellationToken)) { }
            } while (await reader.NextResultAsync(cancellationToken));
        }
        finally
        {
            connection.InfoMessage -= handler;
        }

        return messages.SelectMany(message => LogicalReads.Matches(message).Cast<Match>())
            .Sum(match => int.Parse(match.Groups["reads"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }
}
