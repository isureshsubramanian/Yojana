using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

/// <summary>
/// An operator's confirmation of one onboarding checklist item. <see cref="TextHash"/> fingerprints the item text that
/// was confirmed, so a later change to a disclosure makes the confirmation stale instead of silently carrying over.
/// </summary>
public sealed class ConnectionOnboardingConfirmation : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ItemCode { get; set; }
    public required string TextHash { get; set; }
    public required string Evidence { get; set; }
    public required string ConfirmedBy { get; set; }
    public DateTime ConfirmedAt { get; set; }
}
