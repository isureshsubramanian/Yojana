using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class ResequenceItem : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ResequenceItemId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid InboundMessageId { get; set; }
    public required string DependencyKey { get; set; }
    public DateTime EventTime { get; set; }
    public DateTime HeldAt { get; set; }
    public DateTime HoldUntil { get; set; }
    public required string Status { get; set; }
    public DateTime? ReleasedAt { get; set; }
    public bool Late { get; set; }
}

public static class ResequenceStatus
{
    public const string Held = "held";
    public const string Released = "released";
}
