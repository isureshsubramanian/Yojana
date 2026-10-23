using System.Text.Json;

namespace Yojana.Integration.Verification;

public sealed record ConnectorLiveVerification(bool Verified, string? Release = null,
    DateOnly? Date = null, int? ScenarioCount = null);

public sealed record ConnectorVerificationFact(string Id, string Fact, string ValueUsed, string Kind,
    string SourceLabel, DateOnly DateRead, string UsedAt, bool AcceptanceItem);

public sealed record ConnectorVerificationManifest(string ConnectorCode,
    ConnectorLiveVerification LiveVerification, IReadOnlyList<ConnectorVerificationFact> Facts);

public sealed record ConnectorEdgeScenarioDefinition(string Id, string Description,
    IReadOnlyList<string> RequiredCapabilities);

public sealed record VerificationFactView(string Id, string Fact, string ValueUsed, string Kind,
    string SourceLabel, DateOnly DateRead, string UsedAt, bool AcceptanceItem);
public sealed record AcceptanceRunSummary(Guid RunId, string SandboxLabel, string Status,
    string StartedBy, DateTime StartedAt, string? CompletedBy, DateTime? CompletedAt,
    int Passed, int Failed, int NotApplicable, int Pending);
public sealed record ConnectorVerificationView(Guid ConnectionId, string ConnectionName,
    string ConnectorCode, ConnectorLiveVerification LiveVerification, bool AcceptancePassed,
    int DocumentedFacts, int InferredFacts, int AssumedFacts, int EdgeScenarios,
    int EdgeScenariosApplicable, IReadOnlyList<VerificationFactView> Facts,
    IReadOnlyList<AcceptanceRunSummary> Runs);

public sealed record StartAcceptanceRunRequest(string SandboxLabel);
public sealed record MarkAcceptanceItemRequest(string Result, string? Note, string? Evidence);
public sealed record AcceptanceItemView(Guid ItemId, string Code, string Kind, string Label,
    string Result, string? Note, string? Evidence, string? MarkedBy, DateTime? MarkedAt);
public sealed record AcceptanceRunView(Guid RunId, Guid ConnectionId, string ConnectionName,
    string ConnectorCode, string SandboxLabel, string Status, string StartedBy, DateTime StartedAt,
    string? CompletedBy, DateTime? CompletedAt, IReadOnlyList<AcceptanceItemView> Items);

public sealed record EdgeResultFile(string Connector, DateTime GeneratedAt,
    IReadOnlyList<EdgeScenarioResult> Results, string ContentHash);
public sealed record EdgeScenarioResult(string ScenarioId, string Result, string? SkipReason,
    DateTime StartedAt, DateTime CompletedAt);
public sealed record ImportEdgeResultsRequest(JsonElement ResultFile);
public sealed record ImportEdgeResultsResult(Guid RunId, int Imported, string ContentHash);

public sealed record AcceptanceRequirement(string ConnectorCode, bool Required);
public sealed record AcceptanceGateSettingsView(IReadOnlyList<AcceptanceRequirement> Connectors,
    string SettingsVersion);
public sealed record UpdateAcceptanceGateSettings(IReadOnlyList<AcceptanceRequirement> Connectors,
    string ExpectedVersion);

public static class ConnectorVerificationKind
{
    public const string Documented = "documented";
    public const string Inferred = "inferred";
    public const string Assumed = "assumed";
}
