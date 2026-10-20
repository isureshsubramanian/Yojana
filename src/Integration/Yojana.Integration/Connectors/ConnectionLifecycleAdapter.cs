using System.Text.Json;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.Connectors;

/// <summary>Local setup and read-only probing are separate from capture and outbound execution.</summary>
public interface IConnectionLifecycleAdapter
{
    ConnectionRegistrationForm? RegistrationForm => null;
    void ValidateSettings(JsonElement value);
    IReadOnlyList<string> CredentialPurposes { get; }
    IReadOnlyList<string> RequiredCredentialPurposes(JsonElement value) => CredentialPurposes;
    Task<ConnectionDeclaration> ConfigureAsync(Guid connectionId, JsonElement value, CancellationToken ct);
    Task<ConnectionProbe> SmokeReadAsync(Guid connectionId, CancellationToken ct);
    Task<JsonElement> ReadSettingsAsync(Guid connectionId, CancellationToken ct);
    Task<ConnectionProbe?> LatestProbeAsync(Guid connectionId, CancellationToken ct);
    Task UnsubscribeAsync(Guid connectionId, CancellationToken ct);
}

public sealed record ConnectionDeclaration(IReadOnlyDictionary<string, string> Capabilities,
    ConnectorThrottleDeclaration Throttle, IReadOnlyDictionary<string, string> PollEntities,
    IReadOnlyList<string> NotificationSources);
public sealed record ConnectionProbe(JsonElement? SurveySummary, bool? NotificationsSupported = null);

public sealed record ConnectionRegistrationField(string Code, string Label, string Type, string? DefaultValue = null);
public sealed record ConnectionRegistrationForm(IReadOnlyList<ConnectionRegistrationField> Settings,
    string CredentialPurpose, IReadOnlyList<ConnectionRegistrationField> SecretFields);
