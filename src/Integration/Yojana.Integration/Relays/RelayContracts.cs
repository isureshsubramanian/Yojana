using Yojana.Relay.Contracts;

namespace Yojana.Integration.Relays;

public sealed record RelayConnectionOptions(string Transport = "direct", string Credentials = "site",
    string? WebhookUrl = null, int OfflineAfterSeconds = 120, int RequestTimeoutSeconds = 90);
public sealed record RegisterSiteRelay(string DisplayName, Uri ProductUrl);
public sealed record RotateSiteRelay(int OverlapMinutes = 60, bool Force = false, string? Reason = null);
public sealed record SiteRelayRotated(Guid RelayId, string Secret, DateTime PreviousSecretExpiresAt);
public sealed record SiteRelayIssued(Guid RelayId, string Secret, RelayAgentConfiguration Configuration);
public sealed record SiteRelayView(Guid RelayId, string DisplayName, string Status, DateTime CreatedAt,
    DateTime? LastSeenAt, string? AgentVersion, string? HostName, DateTime? PreviousSecretExpiresAt);
public sealed record SiteRelayPage(IReadOnlyList<SiteRelayView> Items, string? NextCursor, int RelayConnected);
public sealed record RelayReceipt(bool Accepted);

public interface IRelayConnectorProfile
{
    Task<RelayConnectionOptions> OptionsAsync(Guid connectionId, CancellationToken ct);
    Task<RelayAgentConfiguration> ConfigurationAsync(Guid connectionId, Guid tenantId, Guid relayId, Uri productUrl, CancellationToken ct);
    Task ProbeAsync(Guid connectionId, string release, CancellationToken ct);
    Task<RelayWebhookReply> WebhookAsync(Guid connectionId, RelayWebhook request, CancellationToken ct);
}
