using System.Text.Json;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.Connectors;

/// <summary>Local setup and read-only probing are separate from capture and outbound execution.</summary>
public interface IConnectionLifecycleAdapter
{
    ConnectionRegistrationForm? RegistrationForm => null;
    ConnectionFeatures Features => ConnectionFeatures.None;
    string DefaultOutboundMode => "none";
    string InitialConnectionStatus => Data.Entities.ConnectionStatus.Active;
    int PollIntervalSeconds => 120;
    int PollOverlapSeconds => 300;
    void ValidateSettings(JsonElement value);
    IReadOnlyList<string> CredentialPurposes { get; }
    IReadOnlyList<string> RequiredCredentialPurposes(JsonElement value) => CredentialPurposes;
    Task<ConnectionDeclaration> ConfigureAsync(Guid connectionId, JsonElement value, CancellationToken ct);
    Task<ConnectionProbe> SmokeReadAsync(Guid connectionId, CancellationToken ct);
    Task<JsonElement> ReadSettingsAsync(Guid connectionId, CancellationToken ct);
    Task<ConnectionProbe?> LatestProbeAsync(Guid connectionId, CancellationToken ct);
    Task<Uri?> ServiceUriAsync(Guid connectionId, CancellationToken ct) => Task.FromResult<Uri?>(null);
    Task<ConnectionAuthorisationView?> AuthorisationAsync(Guid connectionId, CancellationToken ct) =>
        Task.FromResult<ConnectionAuthorisationView?>(null);
    Task UnsubscribeAsync(Guid connectionId, CancellationToken ct);

    /// <summary>
    /// Holds remote change notifications while a person has stopped the sync, so the remote does not mark the
    /// stream failed. Polling catches up from its watermark after resume, so a connector without the feature may
    /// keep the default.
    /// </summary>
    Task<bool> SupportsNotificationControlAsync(Guid connectionId, CancellationToken ct) => Task.FromResult(false);
    Task SuspendNotificationsAsync(Guid connectionId, CancellationToken ct) => Task.CompletedTask;
    Task ResumeNotificationsAsync(Guid connectionId, CancellationToken ct) => Task.CompletedTask;
}

public sealed record ConnectionFeatures(bool TenantSideExtension, bool InteractiveSignIn)
{
    public static ConnectionFeatures None { get; } = new(false, false);
}

public sealed record ConnectionAuthorisationView(
    string Status,
    DateTime? RefreshChainExpiresAt,
    string RefreshExpiryMode,
    int RefreshLifetimeDays,
    DateTime? LastCallAt,
    IReadOnlyList<int> ReminderDays,
    IReadOnlyList<string> ReminderRecipientRoles,
    IReadOnlyList<int> RemindersSent,
    int? NextReminderDays,
    string? SignedInBy,
    DateTime? SignedInAt,
    string? DisabledBy,
    DateTime? DisabledAt,
    string? DisabledReason,
    int WaitingOutbound,
    string LicenceTier,
    int LicensedRequestsPerMinute,
    int GovernedRequestsPerMinute,
    int RequestHeadroomPerMinute,
    bool CanDisable,
    bool RequiresFreshConsent,
    Cx04SignInHistoryPage History);

public sealed record Cx04SignInHistoryItem(long Sequence, string Kind, string By, DateTime OccurredAt,
    string Result, int Count, string? Reason = null, int? DaysBeforeExpiry = null,
    IReadOnlyList<string>? RecipientRoles = null);

public sealed record Cx04SignInHistoryPage(IReadOnlyList<Cx04SignInHistoryItem> Items, string? NextCursor);

public sealed record ConnectionDeclaration(IReadOnlyDictionary<string, string> Capabilities,
    ConnectorThrottleDeclaration Throttle, IReadOnlyDictionary<string, string> PollEntities,
    IReadOnlyList<string> NotificationSources);
public sealed record ConnectionProbe(JsonElement? SurveySummary, bool? NotificationsSupported = null);

public sealed record ConnectionRegistrationField(string Code, string Label, string Type, string? DefaultValue = null);
public sealed record ConnectionRegistrationForm(IReadOnlyList<ConnectionRegistrationField> Settings,
    string CredentialPurpose, IReadOnlyList<ConnectionRegistrationField> SecretFields);
