using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// The common family seam for live and migrated cover: families are tenant data, add-ons must opt into stacking, and
/// a unit can carry only one live row in a family. The database unique index remains the final concurrency guard.
/// </summary>
public static class EntitlementStackingRules
{
    public static string? FamilyRefusal(CoverageFamily? family, bool addOn)
    {
        if (family is null) return "family_unknown";
        if (!family.Active) return "family_inactive";
        if (addOn && !family.Stacking) return "family_not_add_on";
        return null;
    }

    public static bool FamilyOccupied(IEnumerable<(string Family, string Status)> rows, string family) =>
        rows.Any(row => row.Family == family && row.Status != Contracts.EntitlementStatus.Closed);
}
