using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Serialisation.Data.Entities;

public sealed class Campaign : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid CampaignId { get; set; }

    public required string Code { get; set; }

    public required string Name { get; set; }

    public required string CoverageSource { get; set; }

    /// <summary>Accounting reserve for this campaign; deliberately independent of warranty accrual cohorts.</summary>
    public required string ReserveTag { get; set; }

    public required string Status { get; set; }

    public DateTime CreatedAt { get; set; }

    public required string CreatedBy { get; set; }

    public DateTime UpdatedAt { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTime? SelectedAt { get; set; }
}

public sealed class CampaignCriterion : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid CriterionId { get; set; }

    public Guid CampaignId { get; set; }

    public required string Kind { get; set; }

    public string? Scheme { get; set; }

    public string? IdentifierType { get; set; }

    public string? RangeStart { get; set; }

    public string? RangeEnd { get; set; }

    public long? RangeStartOrdinal { get; set; }

    public long? RangeEndOrdinal { get; set; }

    public Guid? LotId { get; set; }

    public Guid? ChildUnitId { get; set; }

    public Guid? ChildItemId { get; set; }

    public Guid? ChildLotId { get; set; }

    public string? FirmwareKey { get; set; }

    public string? FirmwareValue { get; set; }

    public DateTime? EffectiveAt { get; set; }
}

/// <summary>One immutable recipient snapshot per unit selected into a campaign.</summary>
public sealed class CampaignUnit : ITenantScoped
{
    public TenantId TenantId { get; set; }

    public Guid CampaignId { get; set; }

    public Guid UnitId { get; set; }

    public Guid? OwnerPartyRef { get; set; }

    public string? OwnerPartyType { get; set; }

    public Guid? CustodianPartyRef { get; set; }

    public string? CustodianPartyType { get; set; }

    public DateTime SelectedAt { get; set; }

    public required string SelectedBy { get; set; }

    public Guid SelectionCommandId { get; set; }
}
