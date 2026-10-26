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
    DateTime? EffectiveAt = null,
    Guid? ChildLotId = null,
    string? FirmwareKey = null);

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

public static class CampaignReason
{
    public const string Safety = "safety";
    public const string Reliability = "reliability";
    public const string Firmware = "firmware";
    public const string Regulatory = "regulatory";
    public const string Goodwill = "goodwill";

    public static readonly IReadOnlyList<string> All = [Safety, Reliability, Firmware, Regulatory, Goodwill];
}

public sealed record CreateCampaign(
    Guid CommandId,
    string Code,
    string Name,
    string CoverageSource,
    string ReserveTag,
    IReadOnlyList<CampaignCriterionInput> Criteria,
    DateTime EventTime,
    string ActorRef,
    string ReasonCode,
    string? ReasonNote,
    DateOnly? RunsUntil,
    decimal PartsPct = 100m,
    decimal LabourPct = 100m,
    decimal TravelPct = 100m);

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
    string ActorRef,
    string ReasonCode,
    string? ReasonNote,
    DateOnly? RunsUntil,
    decimal PartsPct = 100m,
    decimal LabourPct = 100m,
    decimal TravelPct = 100m,
    string? ExpectedVersion = null,
    Guid LegalEntityId = default) : IExpectedDocumentVersion;

public sealed record PreviewCampaign(Guid CampaignId, int Skip = 0, int Take = 50, string? Sort = null, string? Direction = null);

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
    DateTime? EffectiveAt,
    Guid? ChildLotId,
    string? FirmwareKey);

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
    DateTime? SelectedAt,
    string ReasonCode,
    string? ReasonNote,
    DateOnly? RunsUntil,
    decimal PartsPct,
    decimal LabourPct,
    decimal TravelPct,
    string Version = "");

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
    Guid ItemId,
    Guid? OwnerPartyRef,
    string? OwnerPartyType,
    Guid? CustodianPartyRef,
    string? CustodianPartyType,
    string? CustodyKind,
    string? MatchedChildSerial,
    string? FirmwareKey,
    string? FirmwareValue,
    DateTime? FirmwareSince);

public sealed record CampaignListRow(
    Guid CampaignId,
    string Code,
    string Name,
    string CoverageSource,
    string Status,
    int SelectedUnits,
    DateTime? SelectedAt,
    string ReserveTag,
    string ReasonCode,
    DateOnly? RunsUntil,
    decimal PartsPct,
    decimal LabourPct,
    decimal TravelPct,
    DateTime UpdatedAt);

public sealed record CampaignFunnelStep(string Kind, int Count);

public sealed record CampaignMatchEvidence(string Kind, string Label, string Value);

public sealed record CampaignUnitDetailRow(
    Guid UnitId,
    string? Serial,
    Guid? OwnerPartyRef,
    string? OwnerPartyType,
    DateTime? OwnerSince,
    Guid? CustodianPartyRef,
    string? CustodianPartyType,
    string? CustodyKind,
    string? CustodyAtSelection,
    DateTime? CustodySince,
    Guid? InvoicePartyRef,
    DateTime SelectedAt,
    string SelectedBy,
    Guid? ClaimId,
    string? ClaimNumber,
    string? ClaimStatus);

public sealed record CampaignUnitsSummary(
    int UnitsSelected,
    DateTime? SelectedAt,
    int AddedSinceFirstSelection,
    int Recipients,
    int Owners,
    int CustodiansNotOwner,
    int OwnerDiffersFromInvoice,
    int Repaired,
    int InOurHands);

public static class CampaignRecipientCsv
{
    public const string Header = "serial,owner,custodian,selected_at\r\n";

    public static string Row(CampaignUnitDetailRow row, string? owner, string? custodian) => string.Join(',',
        Csv(row.Serial), Csv(owner), Csv(custodian), Csv(row.SelectedAt.ToString("O"))) + "\r\n";

    private static string Csv(string? value) => $"\"{(value ?? "").Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}

public sealed record CampaignResult(CampaignRow Campaign, bool Replayed = false);

public sealed record CampaignPreview(Guid CampaignId, Page<CampaignMatchRow> Page);

public sealed record SelectCampaignUnitsResult(
    Guid CampaignId,
    int Matched,
    int Added,
    int Selected,
    int EventBatches,
    bool Replayed);

