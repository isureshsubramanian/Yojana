using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>An immutable before/after policy decision caused by a changed entitlement on still-open work.</summary>
public sealed class CoverageDecision : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid CoverageDecisionId { get; set; }
    public Guid UnitId { get; set; }
    public required string Reason { get; set; }
    public required string WorkType { get; set; }
    public Guid WorkId { get; set; }
    public Guid? WorkLineId { get; set; }
    /// <summary>Non-null replay key: the work id when the external work item has no separate line id.</summary>
    public Guid WorkLineKey { get; set; }
    public required string BeforeDecision { get; set; }
    public required string AfterDecision { get; set; }
    public Guid SourceEventId { get; set; }
    public Guid EventId { get; set; }
    public DateTime DecidedAt { get; set; }
    public required string ActorRef { get; set; }
}
