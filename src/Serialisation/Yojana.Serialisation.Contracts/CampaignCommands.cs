namespace Yojana.Serialisation.Contracts;

/// <summary>One campaign criterion. Kinds are ANDed; rows of the same kind are ORed.</summary>
public sealed record CampaignCriterionInput(
    string Kind,
    string? Scheme = null,
    string? RangeStart = null,
    string? RangeEnd = null,
    Guid? LotId = null,
    Guid? ChildUnitId = null,
    Guid? ChildItemId = null,
    string? FirmwareValue = null,
    DateTime? EffectiveAt = null);

public static class CampaignCriterionKind
{
    public const string IdentifierRange = "identifier_range";
    public const string Lot = "lot";
    public const string Child = "child";
    public const string Firmware = "firmware";

    public static readonly IReadOnlyList<string> All = [IdentifierRange, Lot, Child, Firmware];
}

public static class CampaignCoverageSource
{
    public const string Recall = "recall";
    public const string Goodwill = "goodwill";

    public static readonly IReadOnlyList<string> All = [Recall, Goodwill];
}

public static class CampaignStatus
{
    public const string Draft = "draft";
    public const string Selected = "selected";
    public const string Active = "active";
    public const string Closed = "closed";

    public static readonly IReadOnlyList<string> All = [Draft, Selected, Active, Closed];
}

public sealed record CreateCampaign(
    Guid CommandId,
    string Code,
    string Name,
    string CoverageSource,
    string ReserveTag,
    IReadOnlyList<CampaignCriterionInput> Criteria,
    DateTime EventTime,
    string ActorRef);

public sealed record UpdateCampaign(
    Guid CommandId,
    Guid CampaignId,
    string Code,
    string Name,
    string CoverageSource,
    string ReserveTag,
    string Status,
    IReadOnlyList<CampaignCriterionInput> Criteria,
    DateTime EventTime,
    string ActorRef);

public sealed record PreviewCampaign(Guid CampaignId, int Skip = 0, int Take = 50);

public sealed record SelectCampaignUnits(Guid CommandId, Guid CampaignId, DateTime EventTime, Guid LegalEntityId, string ActorRef);

public sealed record GetCampaignUnits(Guid CampaignId, int Skip = 0, int Take = 50);

public sealed record CampaignCriterionRow(
    Guid CriterionId,
    string Kind,
    string? Scheme,
    string? RangeStart,
    string? RangeEnd,
    Guid? LotId,
    Guid? ChildUnitId,
    Guid? ChildItemId,
    string? FirmwareValue,
    DateTime? EffectiveAt);

public sealed record CampaignRow(
    Guid CampaignId,
    string Code,
    string Name,
    string CoverageSource,
    string ReserveTag,
    string Status,
    IReadOnlyList<CampaignCriterionRow> Criteria,
    DateTime CreatedAt,
    string CreatedBy,
    DateTime UpdatedAt,
    string UpdatedBy,
    DateTime? SelectedAt);

/// <summary>Recipients are snapshotted when the unit is selected; callers de-duplicate equal owner and custodian refs.</summary>
public sealed record CampaignUnitRow(
    Guid UnitId,
    string? Serial,
    Guid? OwnerPartyRef,
    string? OwnerPartyType,
    Guid? CustodianPartyRef,
    string? CustodianPartyType,
    DateTime SelectedAt,
    string SelectedBy);

public sealed record CampaignMatchRow(
    Guid UnitId,
    string? Serial,
    Guid? OwnerPartyRef,
    string? OwnerPartyType,
    Guid? CustodianPartyRef,
    string? CustodianPartyType);

public sealed record CampaignResult(CampaignRow Campaign, bool Replayed = false);

public sealed record CampaignPreview(Guid CampaignId, Page<CampaignMatchRow> Page);

public sealed record SelectCampaignUnitsResult(
    Guid CampaignId,
    int Matched,
    int Added,
    int Selected,
    int EventBatches,
    bool Replayed);

