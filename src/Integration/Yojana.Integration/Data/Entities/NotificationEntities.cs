using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class ConnectionNotificationSource : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string Source { get; set; }
    public bool Enabled { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class ConnectionPollWatermark : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string EntityType { get; set; }
    public string? CanonicalMessage { get; set; }
    public string? Watermark { get; set; }
    public int OverlapSeconds { get; set; }
    public int IntervalSeconds { get; set; }
    public DateTime NextPollAt { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public string? LastError { get; set; }
    public long Revision { get; set; }
}

public sealed class ConnectionNotificationReceipt : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string EventId { get; set; }
    public DateTime? FirstSeenPendingAt { get; set; }
    public Guid? FirstSeenPendingRunId { get; set; }
    public DateTime LastObservedAt { get; set; }
    public DateTime? ProcessedAt { get; set; }
}

public sealed class ConnectionNotificationWatchdog : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public string StreamState { get; set; } = "not_subscribed";
    public DateOnly? WatermarkDate { get; set; }
    public DateTime UpdatedAt { get; set; }
}
