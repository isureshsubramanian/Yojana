using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.LegacyMigration.Templates;

public static class TemplateMappingState
{
    public const string Draft = "draft";
    public const string Approved = "approved";
    public const string Superseded = "superseded";
    public const string Waiting = "waiting";
    public const string NeedsPerson = "needs_person";
    public const string WaitingForPolicyPublication = "waiting_for_policy_publication";
}

public static class TemplateBlockResolution
{
    public const string ExistingPolicy = "existing_policy";
    public const string PolicyToCreate = "policy_to_create";
}

public sealed class TemplateMapping : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid TemplateMappingId { get; set; }
    public required string TemplateKey { get; set; }
    public required string SourceSystem { get; set; }
    public required string TemplateSourceKey { get; set; }
    public required string TemplateName { get; set; }
    public Guid ExtractionBatchId { get; set; }
    public DateTime ExtractedAt { get; set; }
    public required string SourceFingerprint { get; set; }
    public required string SourceTermsJson { get; set; }
    public required string WordingToReviewJson { get; set; }
    public required string PatternKey { get; set; }
    public required string PatternLabel { get; set; }
    public required string DecompositionFingerprint { get; set; }
    public required string State { get; set; }
    public bool NeedsPerson { get; set; }
    public string? ReviewReason { get; set; }
    public int ContractCount { get; set; }
    public int SerialCount { get; set; }
    public int ActiveContractCount { get; set; }
    public int ActiveSerialCount { get; set; }
    public DateTime ProposedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public Guid? SupersededByMappingId { get; set; }
    public DateTime? SupersededAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class TemplateMappingBlock : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid TemplateMappingBlockId { get; set; }
    public Guid TemplateMappingId { get; set; }
    public int Position { get; set; }
    public required string FamilyCode { get; set; }
    public required string FamilyName { get; set; }
    public bool Stacking { get; set; }
    public required string Resolution { get; set; }
    public Guid? PolicyId { get; set; }
    public string? PolicyCode { get; set; }
    public int? PolicyVersion { get; set; }
    public string? PolicyName { get; set; }
    public required string ProposedTermsJson { get; set; }
}
