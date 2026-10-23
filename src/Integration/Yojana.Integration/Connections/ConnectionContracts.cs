using System.Text.Json;
using Yojana.Integration.Gateway;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.Connections;

public sealed record RegisterConnection(Guid RequestId, string ConnectorCode, string DisplayLabel,
    Guid LegalEntityId, JsonElement ConnectorSettings, IReadOnlyList<RegisterCredential> Credentials,
    ConnectionThrottleProfile? Throttle = null);
public sealed record RegisterCredential(string Purpose, string Secret, DateTime? ExpiresAt = null,
    DateTime? ReplaceBy = null);
public sealed record ArchiveConnection(bool Force = false, string? Reason = null);
public sealed record ArchivePreconditions(int WaitingOutbox, int OpenDeadLetters, int RunningBackfills,
    int OpenManualTasks)
{
    public bool Blocked => WaitingOutbox + OpenDeadLetters + RunningBackfills + OpenManualTasks > 0;
}
public sealed record ArchiveConnectionResult(Guid ConnectionId, bool Archived, ArchivePreconditions Preconditions,
    string? CleanupErrorCode = null);
public sealed record OnboardingCheck(string Code, string Label, string Result, string Detail);
public sealed record RegistrationResult(Guid ConnectionId, string HealthStatus, IReadOnlyList<OnboardingCheck> Checklist,
    JsonElement? SurveySummary);
public sealed record ConnectionKpis(int Connections, int Live, int Paused, long ReceivedToday,
    long ReceivedLast30Days, int WaitingToSend, DateTime? OldestWaitingAt, int OpenDeadLetters,
    int OpenManualTasks, int ConnectionsNeedingAttention, int RelayConnected = 0);
public sealed record ConnectionPage(IReadOnlyList<ConnectionSummary> Items, string? NextCursor, ConnectionKpis Kpis);
public sealed record ConnectionSummary(Guid ConnectionId, string ConnectionName, string ConnectorCode,
    string ConnectorName, string Status, bool IsLive, string OutboundMode, bool ReadOnly,
    ConnectionHealthView Health, bool AcceptancePassed);
public sealed record ConnectionHealthView(string Status, bool IsLive, DateTime? LastInboundAt,
    DateTime? CheckedUpTo, string NotificationState, long ReceivedToday, long ReceivedLast30Days,
    int WaitingToSend, DateTime? OldestWaitingAt, double? OldestWaitingAgeSeconds, int MaxOutboxAgeSeconds,
    int OpenDeadLetters, int OpenManualTasks, DateTime? OldestDeadLetterAt, string? LastDailyCheckResult,
    DateTime? LastDailyCheckAt, int DailyCheckDifferences, int CallsUsed, int? CallsCap, int CallAlertPercent,
    DateTime? CredentialReplaceBy, int CredentialWarnDays, string? LastErrorCode, string? LastErrorMessage,
    DateTime UpdatedAt, bool RelayConnected = false);
public sealed record RecordTypeHealth(string RecordType, string NotificationState, string? Watermark,
    DateTime? CheckedUpTo, double? BehindSeconds, int IntervalSeconds, int OverlapSeconds,
    long ReceivedToday, int NeedAttention, string? SourceEntity = null);
public sealed record ConnectionHealthDetail(Guid ConnectionId, string ConnectionName, ConnectionHealthView Health,
    IReadOnlyList<RecordTypeHealth> RecordTypes, ConnectionHandOverNote? HandOver = null);

/// <summary>
/// A stopped connection waiting for a person to resume it after the final migration load handed it a starting point
/// (OD-60, P9-T34): shown on Connection health until the sync runs again.
/// </summary>
public sealed record ConnectionHandOverNote(DateTime From, string Text);

/// <summary>Port the migration apply step answers; connections never read migration tables themselves.</summary>
public interface IConnectionHandOverNotes
{
    Task<ConnectionHandOverNote?> ForAsync(Guid connectionId, CancellationToken cancellationToken);
}
public sealed record CredentialState(string Purpose, bool IsSet, DateTime? CreatedAt, DateTime? ExpiresAt,
    DateTime? ReplaceBy, DateTime? RetiredAt);
public sealed record CapabilityView(string CanonicalMessage, string SupportLevel);
public sealed record ConnectionSettingsView(string OutboundMode, bool ReadOnly, string NativeWarrantyObjectMode,
    string WarrantyContractsMaintainedIn,
    int OrderingWaitMinutes, ConnectionHealthThresholds Health, ConnectionThrottleProfile? Throttle,
    JsonElement? ConnectorSettings, IReadOnlyList<DegradationRuleSetting> DegradationRules);
public sealed record ConnectionDetail(ConnectionSummary Connection, DateTime CreatedAt, DateTime? ArchivedAt,
    IReadOnlyList<CapabilityView> Capabilities, ConnectionSettingsView Settings,
    IReadOnlyList<CredentialState> Credentials, ConnectorOnboardingChecklist Checklist,
    string LifecycleStatus, int ReconciliationWindowDays, DateOnly ReconciliationToday,
    Yojana.Integration.Connectors.ConnectionAuthorisationView? Authorisation = null,
    Yojana.Integration.Connectors.ConnectionFeatures? Features = null);
public sealed record ConnectionEventView(long Sequence, string Kind, DateTime OccurredAt, string ActorName, string Summary);
public sealed record ConnectionEventPage(IReadOnlyList<ConnectionEventView> Items, string? NextCursor);
public sealed record TenantConnectionStatus(Guid ConnectionId, string ConnectionName, string Status, bool IsLive,
    DateTime? CheckedUpTo, int WaitingToSend, int MessagesNeedingAttention, int OpenManualTasks,
    int ConnectionAttention, DateTime UpdatedAt);
public sealed record TenantStatusPage(IReadOnlyList<TenantConnectionStatus> Items, string? NextCursor, ConnectionKpis Kpis);

public sealed record RegistrationConnector(string Code, string DisplayName,
    Yojana.Integration.Connectors.ConnectionRegistrationForm Form);
public sealed record RegistrationConnectorPage(IReadOnlyList<RegistrationConnector> Items, string? NextCursor);
