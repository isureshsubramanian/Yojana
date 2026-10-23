using Xunit;

namespace Yojana.Integration.DatabaseTests.ConnectorEdges;

[AttributeUsage(AttributeTargets.Method)]
public sealed class ConnectorEdgeFactAttribute : FactAttribute
{
    public ConnectorEdgeFactAttribute(Type harnessType, string scenarioId, bool live = false)
    {
        if (IntegrationHostFixture.ConnectionString is null)
        {
            Skip = "YOJANA_TEST_SQL is required.";
            return;
        }

        var manifest = harnessType.GetProperty("Manifest",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null)
            as ConnectorEdgeHarnessManifest
            ?? throw new InvalidOperationException($"{harnessType.Name} must expose a public static Manifest property.");
        var scenario = ConnectorEdgeScenarios.Get(scenarioId);
        Skip = manifest.NotApplicable(scenario);
        if (Skip is null && live && !LiveEnvironmentPresent(manifest.ConnectorCode))
            Skip = $"The {manifest.ConnectorCode} live sandbox environment is not configured.";
        if (live && Skip is not null && ConnectorEdgeResultWriter.Requested)
            ConnectorEdgeResultWriter.Record(manifest.ConnectorCode, scenario.Id, "skipped", Skip,
                DateTime.UtcNow, DateTime.UtcNow);
    }

    private static bool LiveEnvironmentPresent(string connectorCode) => connectorCode switch
    {
        "CX01" => Required("YOJANA_CX01_URL") && Required("YOJANA_CX01_USER")
            && Required("YOJANA_CX01_PASSWORD") && Required("YOJANA_CX01_EDGE_SCENARIO"),
        "CX02" => Required("YOJANA_CX02_URL") && Required("YOJANA_CX02_CLIENT_ID")
            && Required("YOJANA_CX02_CLIENT_SECRET") && Required("YOJANA_CX02_EDGE_SCENARIOS"),
        "CX04" => Required("YOJANA_CX04_URL") && Required("YOJANA_CX04_CLIENT_ID")
            && Required("YOJANA_CX04_REFRESH_TOKEN") && Required("YOJANA_CX04_EDGE_SCENARIO"),
        _ => false,
    };

    private static bool Required(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
}
