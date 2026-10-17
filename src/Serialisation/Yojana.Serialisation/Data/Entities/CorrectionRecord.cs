using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// Indexed projection of correction ledger facts for the correction-volume KPI. The ledger remains the fact; one row
/// represents one correction even when that correction writes events for many units.
/// </summary>
public sealed class CorrectionRecord : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid CorrectionId { get; set; }
    public required string Command { get; set; }
    public required string ReasonCode { get; set; }
    public int RootCauseCategory { get; set; }
    public required string InitiatedVia { get; set; }
    public required string InitiatingRole { get; set; }
    public required string ActorRef { get; set; }
    public DateTime EventTime { get; set; }
    public DateTime RecordedTime { get; set; }
    public Guid? CorrectsCorrectionId { get; set; }
    public int UnitCount { get; set; }
    public string? Note { get; set; }
    public string EvidenceRefs { get; set; } = "[]";
    public string Approvers { get; set; } = "[]";
}
