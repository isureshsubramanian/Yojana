using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Yojana.Bench;

public sealed record MigrationPhaseMeasurement(
    string Phase,
    double WallSeconds,
    long RowsIn,
    long RowsOut,
    int SqlRoundTrips,
    decimal TargetDatabaseMb,
    decimal SourceDatabaseMb,
    string? Notes,
    IReadOnlyList<string> SlowestStatements);

public sealed class MigrationRehearsalReport(string outputDirectory, string targetDatabase, string sourceDatabase,
    Guid tenantId, int sourceSerials)
{
    private readonly List<MigrationPhaseMeasurement> phases = [];
    private readonly List<string> plans = [];
    private readonly DateTime startedAt = DateTime.UtcNow;
    private string status = "running";
    private string? failure;

    public IReadOnlyList<MigrationPhaseMeasurement> Phases => phases;
    public void Add(MigrationPhaseMeasurement phase) => phases.Add(phase);
    public void AddPlan(string path) => plans.Add(path);
    public void Complete() => status = "completed";
    public void Fail(Exception exception)
    {
        status = "failed";
        failure = exception.GetType().Name + ": " + exception.Message;
    }

    public async Task<(string Markdown, string Json)> WriteAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(outputDirectory);
        var stamp = startedAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var prefix = Path.Combine(outputDirectory, $"migration-rehearsal-{sourceSerials}-{stamp}");
        var markdown = prefix + ".md";
        var json = prefix + ".json";
        var text = new StringBuilder()
            .AppendLine($"# Migration rehearsal · {sourceSerials:N0} serials")
            .AppendLine()
            .AppendLine($"Started {startedAt:yyyy-MM-dd HH:mm:ss} UTC · status **{status}** · tenant `{tenantId:D}` · target `{targetDatabase}` · source `{sourceDatabase}`.")
            .AppendLine()
            .AppendLine("| Phase | Wall time (s) | Rows in | Rows out | SQL round trips | Target MB | Source MB | Notes |")
            .AppendLine("|---|---:|---:|---:|---:|---:|---:|---|");
        foreach (var phase in phases)
            text.AppendLine($"| {Escape(phase.Phase)} | {phase.WallSeconds:0.000} | {phase.RowsIn:N0} | {phase.RowsOut:N0} | {phase.SqlRoundTrips:N0} | {phase.TargetDatabaseMb:0.0} | {phase.SourceDatabaseMb:0.0} | {Escape(phase.Notes)} |");
        text.AppendLine();
        text.AppendLine("SQL round trips are client-driver command completions in the measured async flow. Bulk-copy streams and transaction commits are not commands, so this is a deliberately cheap lower-bound count.");
        text.AppendLine();
        if (phases.Count != 0)
        {
            text.AppendLine("## Slowest statements by phase").AppendLine();
            foreach (var phase in phases.Where(item => item.SlowestStatements.Count != 0))
            {
                text.AppendLine($"### {phase.Phase}").AppendLine();
                foreach (var statement in phase.SlowestStatements) text.AppendLine($"- `{Escape(statement)}`");
                text.AppendLine();
            }
        }
        if (plans.Count != 0)
        {
            text.AppendLine("## Captured actual plans").AppendLine();
            foreach (var plan in plans) text.AppendLine($"- `{Path.GetFileName(plan)}`");
            text.AppendLine();
        }
        if (failure is not null) text.AppendLine("## Failure").AppendLine().AppendLine(failure).AppendLine();
        await File.WriteAllTextAsync(markdown, text.ToString(), cancellationToken);
        await File.WriteAllTextAsync(json, JsonSerializer.Serialize(new
        {
            startedAt,
            finishedAt = DateTime.UtcNow,
            status,
            failure,
            tenantId,
            targetDatabase,
            sourceDatabase,
            sourceSerials,
            phases,
            plans = plans.Select(Path.GetFileName),
        }, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        return (markdown, json);
    }

    private static string Escape(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value
        .Replace("|", "\\|", StringComparison.Ordinal).Replace("`", "'", StringComparison.Ordinal)
        .Replace('\n', ' ').Replace('\r', ' ');
}
