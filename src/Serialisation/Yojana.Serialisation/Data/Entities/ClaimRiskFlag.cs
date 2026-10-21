using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>One immutable rule hit beside the claim state machine; only its one-time reviewer decision may be added.</summary>
public sealed class ClaimRiskFlag : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid FlagId { get; set; }
    public Guid ClaimId { get; set; }
    public required string RuleCode { get; set; }
    public required string ThresholdSnapshot { get; set; }
    public required string Evidence { get; set; }
    public DateTime RaisedAt { get; set; }
    public required string RaisedByEvaluation { get; set; }
    public required string Status { get; set; }
    public DateTime? DecidedAt { get; set; }
    public string? DecidedBy { get; set; }
    public string? DecisionReason { get; set; }
    public byte[] RowVersion { get; set; } = [];
}
