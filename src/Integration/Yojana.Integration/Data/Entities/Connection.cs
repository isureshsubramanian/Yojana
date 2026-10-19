using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

/// <summary>The stable connection identity needed by the inbox. Credentials and operational administration arrive later.</summary>
public sealed class Connection : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string ConnectorCode { get; set; }
    public required string DisplayLabel { get; set; }
    public required string Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ArchivedAt { get; set; }
}

public static class ConnectionStatus
{
    public const string Active = "active";
    public const string Archived = "archived";
}
