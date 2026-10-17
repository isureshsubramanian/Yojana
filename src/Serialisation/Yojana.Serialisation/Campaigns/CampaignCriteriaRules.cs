using Yojana.Platform.Contracts.Identifiers;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Identity;

namespace Yojana.Serialisation.Campaigns;

/// <summary>Pure criterion rules shared by command validation and unit tests.</summary>
public static class CampaignCriteriaRules
{
    public static IReadOnlyList<CampaignCriterion> Prepare(
        Guid campaignId,
        IReadOnlyList<CampaignCriterionInput> inputs,
        ISerialSchemeSource schemes,
        IIdentifierPolicySource identifierPolicies,
        DateTime at)
    {
        var rows = new List<CampaignCriterion>(inputs.Count);
        foreach (var input in inputs)
        {
            if (!CampaignCriterionKind.All.Contains(input.Kind, StringComparer.Ordinal))
                throw new CommandRefusedException("campaign_criterion_kind_unknown", $"Campaign criterion kind '{input.Kind}' is not known.");

            var row = new CampaignCriterion
            {
                CriterionId = TimeOrderedId.New(at),
                CampaignId = campaignId,
                Kind = input.Kind,
            };
            switch (input.Kind)
            {
                case CampaignCriterionKind.IdentifierRange:
                {
                    var scheme = schemes.Find(Required(input.Scheme, "scheme"))
                        ?? throw new CommandRefusedException("scheme_unknown", $"Serial scheme '{input.Scheme}' is not defined for this tenant.", "define the scheme in settings");
                    var start = scheme.TryParse(Required(input.RangeStart, "range_start"))
                        ?? throw new CommandRefusedException("range_invalid", $"'{input.RangeStart}' is not a serial of scheme {scheme.Code}.");
                    var end = scheme.TryParse(Required(input.RangeEnd, "range_end"))
                        ?? throw new CommandRefusedException("range_invalid", $"'{input.RangeEnd}' is not a serial of scheme {scheme.Code}.");
                    if (end.Ordinal < start.Ordinal) throw new CommandRefusedException("range_invalid", "The identifier range end is before its start.");
                    var policy = identifierPolicies.For(scheme.IdentifierType);
                    row.Scheme = scheme.Code;
                    row.IdentifierType = scheme.IdentifierType;
                    row.RangeStart = IdentifierNormaliser.Normalise(scheme.FormatOrdinal(start.Ordinal), policy).Value;
                    row.RangeEnd = IdentifierNormaliser.Normalise(scheme.FormatOrdinal(end.Ordinal), policy).Value;
                    row.RangeStartOrdinal = start.Ordinal;
                    row.RangeEndOrdinal = end.Ordinal;
                    break;
                }

                case CampaignCriterionKind.Lot:
                    row.LotId = input.LotId ?? throw Missing("lot_id");
                    break;
                case CampaignCriterionKind.Child:
                    if ((input.ChildUnitId is null) == (input.ChildItemId is null))
                        throw new CommandRefusedException("campaign_criterion_invalid", "A child criterion names either child_unit_id or child_item_id, not both.");
                    row.ChildUnitId = input.ChildUnitId;
                    row.ChildItemId = input.ChildItemId;
                    row.ChildLotId = input.ChildLotId;
                    break;
                case CampaignCriterionKind.Firmware:
                    row.FirmwareKey = string.IsNullOrWhiteSpace(input.FirmwareKey) ? "firmware" : input.FirmwareKey.Trim();
                    row.FirmwareValue = Required(input.FirmwareValue, "firmware_value");
                    row.EffectiveAt = input.EffectiveAt;
                    break;
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>Normalised serials use their scheme's fixed-width representation, compared ordinally.</summary>
    public static bool IdentifierInRange(string normalisedValue, string normalisedStart, string normalisedEnd) =>
        StringComparer.Ordinal.Compare(normalisedValue, normalisedStart) >= 0
        && StringComparer.Ordinal.Compare(normalisedValue, normalisedEnd) <= 0;

    /// <summary>All represented kinds must match; a match against any row of one kind satisfies that kind.</summary>
    public static bool Matches<T>(IReadOnlyList<T> criteria, Func<T, string> kind, Func<T, bool> matches) =>
        criteria.GroupBy(kind, StringComparer.Ordinal).All(group => group.Any(matches));

    public static bool CanMove(string current, string next) => current == next || (current, next) switch
    {
        (CampaignStatus.Selected, CampaignStatus.Active) => true,
        (CampaignStatus.Active, CampaignStatus.Closed) => true,
        _ => false,
    };

    private static string Required(string? value, string field) =>
        !string.IsNullOrWhiteSpace(value) ? value.Trim() : throw Missing(field);

    private static CommandRefusedException Missing(string field) =>
        new("campaign_criterion_invalid", $"Campaign criterion field '{field}' is required.");
}
