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
    public DateTime? ArchivedAt { get; set; }
}

public static class ConnectionStatus
{
    public const string Registering = "registering";
    public const string Paused = "paused";
    public const string Active = "active";
    public const string Archived = "archived";
}
