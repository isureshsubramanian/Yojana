using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Data.Entities;

public sealed class ExternalIdMap : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ExternalIdMapId { get; set; }
    public Guid ConnectionId { get; set; }
    public required string EntityType { get; set; }
    public required string ExternalId { get; set; }
    public Guid ModuleId { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidTo { get; set; }
    public required string NaturalKeyHash { get; set; }
    public required string MappingSource { get; set; }
    public required string MappedBy { get; set; }
    public DateTime MappedAt { get; set; }
}

public static class ExternalIdMappingSource
{
    public const string Auto = "auto";
    public const string Migration = "migration";
    public const string Outbound = "outbound";
    public const string Operator = "operator";
    public const string Provisional = "provisional";
    public const string Manual = "manual";
}
