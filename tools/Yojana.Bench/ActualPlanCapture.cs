using System.Globalization;
using System.Xml.Linq;
using Microsoft.Data.SqlClient;

namespace Yojana.Bench;

/// <summary>Captures only this rehearsal process's slow post-execution plans through a short-lived server session.</summary>
internal sealed class ActualPlanCapture(string connectionString, string sessionName, string outputDirectory) : IAsyncDisposable
{
    private bool started;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync($$"""
                CREATE EVENT SESSION [{{sessionName}}] ON SERVER
                ADD EVENT sqlserver.query_post_execution_showplan
                (
                    ACTION(sqlserver.client_app_name, sqlserver.database_name, sqlserver.sql_text)
                    WHERE ([duration] > 50000)
                )
                ADD TARGET package0.ring_buffer(SET max_memory = 10240)
                WITH (MAX_MEMORY = 16 MB, EVENT_RETENTION_MODE = ALLOW_SINGLE_EVENT_LOSS,
                      MAX_DISPATCH_LATENCY = 5 SECONDS, TRACK_CAUSALITY = OFF, STARTUP_STATE = OFF);
                ALTER EVENT SESSION [{{sessionName}}] ON SERVER STATE = START;
                """, cancellationToken);
            started = true;
        }
        catch (SqlException exception)
        {
            Console.Error.WriteLine($"actual-plan capture unavailable: {exception.Message.Split('\n')[0]}");
        }
    }

    public async Task<IReadOnlyList<string>> StopAndWriteAsync(Guid tenantId,
        CancellationToken cancellationToken)
    {
        if (!started) return [];
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(6), cancellationToken);
            await using var connection = new SqlConnection(MasterConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT CONVERT(nvarchar(max), target.target_data)
                  FROM sys.dm_xe_session_targets AS target
                  JOIN sys.dm_xe_sessions AS session ON session.address = target.event_session_address
                 WHERE session.name = @session AND target.target_name = N'ring_buffer';
                """;
            command.Parameters.AddWithValue("@session", sessionName);
            var xml = (string?)await command.ExecuteScalarAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(xml)) return [];

            var captured = XDocument.Parse(xml).Descendants("event").ToList();
            var events = captured.Select(item => new { Plan = PlanText(item) })
                .Where(item => !string.IsNullOrWhiteSpace(item.Plan) &&
                (item.Plan.Contains("[migration]", StringComparison.OrdinalIgnoreCase) ||
                 item.Plan.Contains("[serialisation]", StringComparison.OrdinalIgnoreCase)))
                .Select(item => new { Duration = PlanElapsedMilliseconds(item.Plan!), item.Plan })
                .OrderByDescending(item => item.Duration).Take(2).ToList();
            Console.WriteLine($"  actual plans                     {events.Count:N0} captured from {captured.Count:N0} slow statement event(s)");
            Directory.CreateDirectory(outputDirectory);
            var paths = new List<string>();
            for (var index = 0; index < events.Count; index++)
            {
                var path = Path.Combine(outputDirectory,
                    $"migration-rehearsal-{tenantId:N}-slow-{index + 1}.sqlplan");
                await File.WriteAllTextAsync(path, events[index].Plan!, cancellationToken);
                paths.Add(path);
            }
            return paths;
        }
        catch (Exception exception) when (exception is SqlException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"actual-plan read unavailable: {exception.Message.Split('\n')[0]}");
            return [];
        }
        finally
        {
            await DropAsync(CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (started) await DropAsync(CancellationToken.None);
    }

    private static string? PlanText(XElement item)
    {
        var value = item.Elements("data").FirstOrDefault(data =>
            (string?)data.Attribute("name") == "showplan_xml")?.Element("value");
        if (value is null) return null;
        return value.Elements().FirstOrDefault()?.ToString(SaveOptions.DisableFormatting) ?? value.Value;
    }

    private static long PlanElapsedMilliseconds(string plan)
    {
        var document = XDocument.Parse(plan);
        XNamespace showPlan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
        return document.Descendants(showPlan + "QueryTimeStats")
            .Select(item => long.TryParse((string?)item.Attribute("ElapsedTime"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var elapsed) ? elapsed : 0)
            .DefaultIfEmpty().Max();
    }

    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(MasterConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DropAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync($$"""
                IF EXISTS (SELECT 1 FROM sys.dm_xe_sessions WHERE name = N'{{sessionName}}')
                    ALTER EVENT SESSION [{{sessionName}}] ON SERVER STATE = STOP;
                IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'{{sessionName}}')
                    DROP EVENT SESSION [{{sessionName}}] ON SERVER;
                """, cancellationToken);
        }
        catch (SqlException exception)
        {
            Console.Error.WriteLine($"actual-plan session cleanup failed: {exception.Message.Split('\n')[0]}");
        }
        started = false;
    }

    private string MasterConnectionString() => new SqlConnectionStringBuilder(connectionString)
    {
        InitialCatalog = "master",
        ApplicationIntent = ApplicationIntent.ReadWrite,
    }.ConnectionString;
}
