using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

/// <summary>Timestamped, read-only discovery evidence for one connection.</summary>
public sealed class Cx01SurveyRecord : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public DateTime ObservedAt { get; set; }
    public required string ResultJson { get; set; }
}
