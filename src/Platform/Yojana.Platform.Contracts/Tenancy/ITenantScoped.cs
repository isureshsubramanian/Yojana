namespace Yojana.Platform.Contracts.Tenancy;

/// <summary>
/// Marker for every entity that belongs to a tenant. The platform applies the EF Core global query
/// filter, the composite-key rule (<see cref="TenantId"/> first) and the row-level-security policy to
/// every table mapped from an entity carrying this interface.
/// </summary>
public interface ITenantScoped
{
    TenantId TenantId { get; }
}
