using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Privacy;

namespace Yojana.Serialisation.Data.Entities;

/// <summary>
/// A correction workbench request freezes the command's own preview and carries only server-recorded approvals. The
/// command arguments remain opaque JSON here: the workbench owns routing, while each command continues to own meaning.
/// </summary>
[PersonalData]
public sealed class WorkbenchRequest : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RequestId { get; set; }
    public Guid CommandId { get; set; }
    public required string Choice { get; set; }
    public required string Command { get; set; }
    public required string TargetJson { get; set; }
    public required string AnswersJson { get; set; }
    public required string Reason { get; set; }
    public string? Note { get; set; }
    public required string EvidenceJson { get; set; }
    public required string PreviewJson { get; set; }
    public required string PreviewHash { get; set; }
    public required string Status { get; set; }
    public required string InitiatedBy { get; set; }
    public required string InitiatorRolesJson { get; set; }
    public DateTime InitiatedAt { get; set; }
    public required string RequiredApproverRolesJson { get; set; }
    public required string ApprovalsJson { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAt { get; set; }
    public string? DecisionNote { get; set; }
    public string? CommittedBy { get; set; }
    public DateTime? CommittedAt { get; set; }
    public string? ResultJson { get; set; }
    public string? UnclassifiedDescription { get; set; }
    public string? ClassifiedBy { get; set; }
    public DateTime? ClassifiedAt { get; set; }
    public string? EscalatedBy { get; set; }
    public DateTime? EscalatedAt { get; set; }
}
