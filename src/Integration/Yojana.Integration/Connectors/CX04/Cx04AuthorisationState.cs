using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Connectors.CX04;

public sealed class Cx04AuthorisationState : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public string Status { get; set; } = Cx04AuthorisationStatus.ConsentRequired;
    public byte[]? PendingStateHash { get; set; }
    public DateTime? PendingStateExpiresAt { get; set; }
    public DateTime? RefreshChainExpiresAt { get; set; }
    public string RefreshExpiryMode { get; set; } = Cx04RefreshExpiryModes.Sliding;
    public int RefreshLifetimeDays { get; set; } = 90;
    public bool Reminder14DaysSent { get; set; }
    public bool Reminder3DaysSent { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public static class Cx04AuthorisationStatus
{
    public const string ConsentRequired = "reconsent_required";
    public const string ConsentPending = "consent_pending";
    public const string Active = "active";
    public const string Disabled = "disabled";
}

public static class Cx04RefreshExpiryModes
{
    public const string Absolute = "absolute";
    public const string Sliding = "sliding";
}
