using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// Immutable, effective-dated tracking-mode fact owned by Serialisation. The item master stays behind IItemQueryPort;
/// consumers learn about this local correction through Item.TrackingModeChanged rather than reading this table.
/// </summary>
public sealed class ItemTrackingModeChange : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid ChangeId { get; set; }
    public Guid ItemId { get; set; }
    public required string PreviousTrackingMode { get; set; }
    public required string TrackingMode { get; set; }
    public DateTime EffectiveAt { get; set; }
    public DateTime RecordedAt { get; set; }
    public Guid CorrectionId { get; set; }
    public Guid? CorrectsCorrectionId { get; set; }
    public required string ActorRef { get; set; }
    public int CommissionedCount { get; set; }
    public int ExemptedCount { get; set; }
    public int KitsCaptured { get; set; }
    public string StockCoverage { get; set; } = "[]";
}
