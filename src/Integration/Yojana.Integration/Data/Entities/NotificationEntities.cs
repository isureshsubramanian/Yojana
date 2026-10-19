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
    public string? Watermark { get; set; }
    public int OverlapSeconds { get; set; }
    public int IntervalSeconds { get; set; }
    public DateTime NextPollAt { get; set; }
    public DateTime? LastPolledAt { get; set; }
    public string? LastError { get; set; }
    public long Revision { get; set; }
}
