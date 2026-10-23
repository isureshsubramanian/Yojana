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
            Skip = manifest.ConnectorCode == "CX02"
                ? "YOJANA_CX02_URL, YOJANA_CX02_CLIENT_ID, YOJANA_CX02_CLIENT_SECRET and YOJANA_CX02_EDGE_SCENARIOS are required."
                : "YOJANA_CX01_URL, YOJANA_CX01_USER, YOJANA_CX01_PASSWORD and YOJANA_CX01_EDGE_SCENARIO are required.";
    }

    private static bool LiveEnvironmentPresent(string connectorCode) => connectorCode == "CX02"
        ? Required("YOJANA_CX02_URL") && Required("YOJANA_CX02_CLIENT_ID")
            && Required("YOJANA_CX02_CLIENT_SECRET") && Required("YOJANA_CX02_EDGE_SCENARIOS")
        : Required("YOJANA_CX01_URL") && Required("YOJANA_CX01_USER")
            && Required("YOJANA_CX01_PASSWORD") && Required("YOJANA_CX01_EDGE_SCENARIO");

    private static bool Required(string name) =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name));
}
