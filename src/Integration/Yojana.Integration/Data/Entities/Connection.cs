using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

/// <summary>Stable connection identity and lifecycle; health is a separate disposable projection.</summary>
public sealed class Connection : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ConnectorCode { get; set; }
    public required string DisplayLabel { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? RegistrationRequestId { get; set; }
    public bool RegistrationCompleted { get; set; }
    public DateTime? LastSmokeReadAt { get; set; }
    public bool? LastSmokeReadSucceeded { get; set; }
    public string? LastSmokeReadFailureCode { get; set; }
    public string? LastSmokeReadFailureDetail { get; set; }
    public DateTime? SyncStoppedAt { get; set; }
    public string? SyncStoppedBy { get; set; }
    public string? SyncStopReason { get; set; }
    public DateTime? SyncResumedAt { get; set; }
    public string NotificationControlState { get; set; } = NotificationControlStates.NotSupported;
    public string? NotificationControlFailureCode { get; set; }
    public string? NotificationControlAction { get; set; }
    public DateTime? NotificationControlNextAttemptAt { get; set; }
    public int NotificationControlAttemptCount { get; set; }
    public long NotificationControlRevision { get; set; }
    public DateTime? ArchivedAt { get; set; }
}

public static class ConnectionStatus
{
    public const string Registering = "registering";
    public const string Paused = "paused";
    public const string Active = "active";
    public const string Archived = "archived";
}

public static class NotificationControlStates
{
    public const string Pending = "pending";
    public const string Held = "held";
    public const string NotHeld = "not_held";
    public const string NotSupported = "not_supported";
}

public static class NotificationControlActions
{
    public const string Suspend = "suspend";
    public const string Resume = "resume";
}
