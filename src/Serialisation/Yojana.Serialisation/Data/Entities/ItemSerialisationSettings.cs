using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// Serialisation-owned additions to an item profile. The item master stays behind <c>IItemQueryPort</c>; this row holds
/// only facts edited by Serialisation and can therefore be removed without changing the source item's storage.
/// </summary>
public sealed class ItemSerialisationSettings : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ItemId { get; set; }
    public string ModelNumbersJson { get; set; } = "[]";
    public DateTime LastModifiedAt { get; set; }
    public required string LastModifiedBy { get; set; }
}
