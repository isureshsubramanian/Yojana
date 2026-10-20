using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

/// <summary>Disposable read projection. Operational tables remain authoritative.</summary>
public sealed class ConnectionHealth : ITenantScoped
{
    public bool RelayConnected { get; set; }
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public string Status { get; set; } = "degraded";
    public bool IsLive { get; set; }
    public DateTime? LastInboundAt { get; set; }
    public DateTime? CheckedUpTo { get; set; }
    public string NotificationState { get; set; } = "not_subscribed";
    public long ReceivedToday { get; set; }
    public long ReceivedLast30Days { get; set; }
    public DateTime? InboundCountDateUtc { get; set; }
    public string CompletedInboundDaysJson { get; set; } = "[]";
    public int OutboxDepth { get; set; }
    public DateTime? OldestWaitingAt { get; set; }
    public int MaxOutboxAgeSeconds { get; set; }
    public int OpenDeadLetters { get; set; }
    public int OpenManualTasks { get; set; }
    public DateTime? OldestDeadLetterAt { get; set; }
    public string? LastDailyCheckResult { get; set; }
    public DateTime? LastDailyCheckAt { get; set; }
    public int DailyCheckDifferences { get; set; }
    public int CallsUsed { get; set; }
    public int? CallsCap { get; set; }
    public int CallAlertPercent { get; set; }
    public DateTime? CredentialReplaceBy { get; set; }
    public int CredentialWarnDays { get; set; }
    public string? LastErrorCode { get; set; }
    public string? LastErrorMessage { get; set; }
    public string RecordTypesJson { get; set; } = "[]";
    public DateTime UpdatedAt { get; set; }
}
