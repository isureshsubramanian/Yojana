using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Corrections;

/// <summary>Pure decisions shared by MergeUnits preview, execution and generated-history tests.</summary>
public static class MergeUnitsRules
{
    public static Guid RecommendedSurvivor(IReadOnlyList<(Guid UnitId, DateTime CommissionedAt)> units) =>
        units.OrderBy(u => u.CommissionedAt).ThenBy(u => u.UnitId).First().UnitId;

    public static CommandRefusal? PairProblem(Guid survivor, Guid absorbed, string survivorStatus, string absorbedStatus)
    {
        if (survivor == absorbed) return new("cannot_merge_unit_into_itself", "A unit cannot be merged into itself.", "select two different unit records");
        if (survivorStatus == LifecycleStatus.Merged || absorbedStatus == LifecycleStatus.Merged)
            return new("unit_already_merged", "A merged alias cannot take part in another merge.", "open the surviving unit, or reverse the earlier merge first");
        return null;
    }

    public static bool DifferentShippedParties(IReadOnlyCollection<Guid> first, IReadOnlyCollection<Guid> second) =>
        first.Count > 0 && second.Count > 0 && !first.Intersect(second).Any();
}
