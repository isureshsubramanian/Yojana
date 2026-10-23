using System.Text.Json;
using Yojana.Integration.Contracts;
using Yojana.Integration.Verification;

namespace Yojana.Integration.DatabaseTests.ConnectorEdges;

/// <summary>
/// Writes only scenario metadata. The live harness never puts credentials, remote addresses or payloads in this file.
/// </summary>
public static class ConnectorEdgeResultWriter
{
    public const string OutputEnvironmentVariable = "YOJANA_CONNECTOR_EDGE_RESULT_FILE";
    private static readonly object Sync = new();

    public static bool Requested => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(
        OutputEnvironmentVariable));

    public static void Record(string connectorCode, string scenarioId, string result, string? skipReason,
        DateTime startedAt, DateTime completedAt)
    {
        var path = Environment.GetEnvironmentVariable(OutputEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (result is not ("passed" or "failed" or "skipped"))
            throw new InvalidOperationException("The edge result must be passed, failed or skipped.");
        if (result == "skipped" && string.IsNullOrWhiteSpace(skipReason))
            throw new InvalidOperationException("A skipped edge result needs a reason.");
        lock (Sync)
        {
            EdgeResultFile? current = null;
            if (File.Exists(path))
                current = JsonSerializer.Deserialize<EdgeResultFile>(File.ReadAllText(path),
                    IntegrationContracts.Json);
            if (current is not null && current.Connector != connectorCode)
                throw new InvalidOperationException("One edge result file may contain only one connector.");
            var results = current?.Results.Where(row => row.ScenarioId != scenarioId).ToList() ?? [];
            results.Add(new(scenarioId, result, result == "skipped" ? skipReason : null,
                startedAt, completedAt));
            results = results.OrderBy(row => row.ScenarioId).ToList();
            var generatedAt = DateTime.UtcNow;
            var hash = EdgeResultFileHash.Compute(connectorCode, generatedAt, results);
            var file = new EdgeResultFile(connectorCode, generatedAt, results, hash);
            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var temporary = fullPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(file, IntegrationContracts.Json));
            File.Move(temporary, fullPath, true);
        }
    }
}
