using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

public sealed class RegionGroup : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RegionGroupId { get; set; }
    public Guid LastCommandId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string Members { get; set; } = "[]";
    public DateTime CreatedAt { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime LastModifiedAt { get; set; }
    public required string LastModifiedBy { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class JurisdictionRule : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RuleId { get; set; }
    public Guid RootRuleId { get; set; }
    public Guid? SupersedesRuleId { get; set; }
    public Guid CommandId { get; set; }
    public int Version { get; set; }
    public required string Kind { get; set; }
    public required string Scope { get; set; }
    public string? CountryCode { get; set; }
    public string? SubdivisionCode { get; set; }
    public Guid? RegionGroupId { get; set; }
    public required string BuyerClass { get; set; }
    public string? ItemClass { get; set; }
    public decimal? PriceMin { get; set; }
    public decimal? PriceMax { get; set; }
    public string? Currency { get; set; }
    public required string DateTested { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public required string Value { get; set; }
    public string? SourceCitation { get; set; }
    public string? SourceNote { get; set; }
    public bool ConfirmedByCounsel { get; set; }
    public DateOnly? CounselConfirmedOn { get; set; }
    public required string CreatedBy { get; set; }
    public required string ApprovedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? ClosedCommandId { get; set; }
    public string? ClosedBy { get; set; }
    public DateTime? ClosedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

public sealed class RulePack : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RulePackId { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Warning { get; set; }
    public bool BuiltIn { get; set; }
    public Guid? ImportCommandId { get; set; }
    public DateTime? ImportedAt { get; set; }
    public string? ImportedBy { get; set; }
}

public sealed class RulePackRule : ITenantScoped
{
    public TenantId TenantId { get; set; }
    public Guid RulePackRuleId { get; set; }
    public Guid RulePackId { get; set; }
    public int Sequence { get; set; }
    public required string Template { get; set; }
}
