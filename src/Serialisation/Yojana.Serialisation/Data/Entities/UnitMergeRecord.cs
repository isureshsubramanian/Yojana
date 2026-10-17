using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// TIGHT COUPLING with MergeUnitsHandler: this journal and every row re-point happen in its one SerialisationDbContext
/// transaction. The alias is valid only when all references move atomically; separating the journal would allow a
/// partial merge or make the later split unable to restore uniqueness-conflict state. Rows themselves carry
/// <c>merged_from_unit_id</c> for direct provenance; this record preserves what cannot be inferred safely.
/// </summary>
public sealed class UnitMergeRecord : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid CorrectionId { get; set; }
    public Guid SurvivorUnitId { get; set; }
    public Guid AbsorbedUnitId { get; set; }
    public DateTime MergedAt { get; set; }
    public required string ReversalData { get; set; }
    public Guid? ReversedByCorrectionId { get; set; }
    public DateTime? ReversedAt { get; set; }
}
