using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Relays;

public sealed class SiteRelay : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RelayId { get; set; }
    public Guid ConnectionId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Status { get; set; } = "active";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? AgentVersion { get; set; }
    public string? ErpVersion { get; set; }
    public string? ProbedErpVersion { get; set; }
    public string? HostName { get; set; }
    public string SigningKeyEnvelope { get; set; } = "";
    public string? PreviousSigningKeyEnvelope { get; set; }
    public DateTime? PreviousSecretExpiresAt { get; set; }
}

public sealed class RelayRequestRow : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RequestId { get; set; }
    public Guid ConnectionId { get; set; }
    public Guid? RelayId { get; set; }
    public Guid? LeaseId { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? LeasedUntil { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ResultJson { get; set; }
}

public sealed class RelayNonce : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RelayId { get; set; }
    public string Nonce { get; set; } = "";
    public DateTime SeenAt { get; set; }
    public DateTime ExpiresAt { get; set; }
}
