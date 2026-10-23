using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class ConnectorAcceptanceRun : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectorAcceptanceRunId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ConnectorCode { get; set; }
    public required string SandboxLabel { get; set; }
    public required string Status { get; set; }
    public required string StartedBy { get; set; }
    public DateTime StartedAt { get; set; }
    public string? CompletedBy { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public sealed class ConnectorAcceptanceItem : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectorAcceptanceItemId { get; set; }
    public Guid ConnectorAcceptanceRunId { get; set; }
    public required string Code { get; set; }
    public required string Kind { get; set; }
    public required string Label { get; set; }
    public required string Result { get; set; }
    public string? Note { get; set; }
    public string? Evidence { get; set; }
    public string? MarkedBy { get; set; }
    public DateTime? MarkedAt { get; set; }
}

/// <summary>
/// A result-file fingerprint only. Test payloads and credentials never cross this boundary or enter the database.
/// </summary>
public sealed class ConnectorAcceptanceImport : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectorAcceptanceImportId { get; set; }
    public Guid ConnectorAcceptanceRunId { get; set; }
    public required string ContentHash { get; set; }
    public required string ConnectorCode { get; set; }
    public int ScenarioCount { get; set; }
    public required string ImportedBy { get; set; }
    public DateTime ImportedAt { get; set; }
}

public static class ConnectorAcceptanceRunStatus
{
    public const string Open = "open";
    public const string Passed = "passed";
    public const string Failed = "failed";
}

public static class ConnectorAcceptanceItemResult
{
    public const string Pending = "pending";
    public const string Passed = "passed";
    public const string Failed = "failed";
    public const string NotApplicable = "not_applicable";
}

public static class ConnectorAcceptanceItemKind
{
    public const string Fact = "fact";
    public const string EdgeScenario = "edge_scenario";
    public const string OutboundCommand = "outbound_command";
}
