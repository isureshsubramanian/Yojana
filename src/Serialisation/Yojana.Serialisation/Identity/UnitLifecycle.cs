using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Identity;

/// <summary>
/// The PhysicalUnit state machine (ADR-0001 "Core model: PhysicalUnit", lifecycle table) as one pure function, so
/// every command asks the same table and the test for it is a table too. Guards that need data (identifier validity,
/// BuildRecord completeness, open links) are checked by the command before it asks for the transition.
/// </summary>
public static class UnitLifecycle
{
    /// <summary>What happens to a unit's status on an event; null when the table has no row for (from, event).</summary>
    public static string? Next(string? from, string eventType, string origin) => (from, eventType) switch
    {
        (null, UnitEventType.Commissioned) => origin == UnitOrigin.Manufactured ? LifecycleStatus.PreRelease : LifecycleStatus.Released,
        (null, UnitEventType.CreatedPendingIdentifier) => LifecycleStatus.PendingIdentifier,
        (LifecycleStatus.PendingIdentifier, UnitEventType.IdentifierAssigned) => origin == UnitOrigin.Manufactured ? LifecycleStatus.PreRelease : LifecycleStatus.Released,
        (LifecycleStatus.PreRelease, UnitEventType.Released) => LifecycleStatus.Released,
        (LifecycleStatus.PreRelease, UnitEventType.Scrapped) => LifecycleStatus.ScrappedPreRelease,
        (LifecycleStatus.PendingIdentifier or LifecycleStatus.PreRelease, UnitEventType.Voided) => LifecycleStatus.Void,
        (LifecycleStatus.Released, UnitEventType.Disassembled) => LifecycleStatus.Dormant,
        (LifecycleStatus.Dormant, UnitEventType.Reassembled) => LifecycleStatus.Released,
        (LifecycleStatus.Released, UnitEventType.Scrapped) => LifecycleStatus.Scrapped,
        (LifecycleStatus.Released or LifecycleStatus.Dormant, UnitEventType.Decommissioned) => LifecycleStatus.Decommissioned,
        _ => null,
    };

    /// <summary>The transition or a refusal naming the rule.</summary>
    public static string Require(string? from, string eventType, string origin, Guid unitId)
    {
        var next = Next(from, eventType, origin);
        if (next is null)
        {
            throw new CommandRefusedException("lifecycle_transition", $"Unit {unitId}: {eventType} is not allowed from status '{from ?? "(none)"}'.",
                from is null ? "commission the unit first" : $"a unit in '{from}' cannot take {eventType}; see the lifecycle table");
        }

        return next;
    }

    /// <summary>Statuses in which a unit is considered to exist for identifier uniqueness (Identity rule 6: all of them).</summary>
    public static bool Exists(string status) => status is not LifecycleStatus.Merged;

    /// <summary>A scrapped unit whose item allows scrap-and-reuse may lend its serial to a new unit (case 13).</summary>
    public static bool IsScrapped(string status) => status is LifecycleStatus.Scrapped or LifecycleStatus.ScrappedPreRelease;

    /// <summary>CBV disposition that accompanies a status in <c>unit_current</c>.</summary>
    public static string DispositionFor(string status) => status switch
    {
        LifecycleStatus.PendingIdentifier or LifecycleStatus.PreRelease => UnitDisposition.InProgress,
        LifecycleStatus.Released => UnitDisposition.Active,
        LifecycleStatus.Dormant => UnitDisposition.Inactive,
        LifecycleStatus.Scrapped or LifecycleStatus.ScrappedPreRelease => UnitDisposition.Destroyed,
        LifecycleStatus.Decommissioned or LifecycleStatus.Void => UnitDisposition.Inactive,
        _ => UnitDisposition.Inactive,
    };
}
