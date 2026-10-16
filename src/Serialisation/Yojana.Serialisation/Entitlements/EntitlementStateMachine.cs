using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// The entitlement state machine of ADR-0001 as one pure function, and the lazy expiry a reader applies. A closed
/// entitlement is never reopened (MOD-06 invariant 2): every event from closed is refused. Expiry is evaluated at read
/// and never written by a read; the daily job (P2-T13) is what records <c>closed(expired)</c>.
/// </summary>
public static class EntitlementStateMachine
{
    /// <summary>Reasons <see cref="CloseEntitlement"/> may use; transfer and supersession close with their own reasons through their own commands.</summary>
    public static readonly IReadOnlySet<string> CloseReasons = new HashSet<string>
    {
        EntitlementClosedReason.Refunded, EntitlementClosedReason.ReturnedToStock, EntitlementClosedReason.TerminatedTotalLoss, EntitlementClosedReason.IncludedInError,
        EntitlementClosedReason.Expired, EntitlementClosedReason.SourceDocumentCorrected,
    };

    public static readonly IReadOnlySet<string> VoidReasons = new HashSet<string> { EntitlementClosedReason.Voided, EntitlementClosedReason.VoidedShippingError, EntitlementClosedReason.VoidedDocumentCorrection };

    /// <summary>The status after <paramref name="eventType"/>, or null when the machine does not allow it from <paramref name="status"/>.</summary>
    public static string? Next(string status, string eventType) => (status, eventType) switch
    {
        (EntitlementStatus.PendingEvidence, EntitlementEventType.EvidenceAttached) => EntitlementStatus.Active,
        (EntitlementStatus.Active, EntitlementEventType.Rebased) => EntitlementStatus.Active,
        (EntitlementStatus.Active, EntitlementEventType.Amended) => EntitlementStatus.Active,
        (EntitlementStatus.Active, EntitlementEventType.Suspended) => EntitlementStatus.Suspended,
        (EntitlementStatus.Suspended, EntitlementEventType.Resumed) => EntitlementStatus.Active,
        (EntitlementStatus.PendingEvidence or EntitlementStatus.Active or EntitlementStatus.Suspended, EntitlementEventType.Voided) => EntitlementStatus.Closed,
        (EntitlementStatus.PendingEvidence or EntitlementStatus.Active or EntitlementStatus.Suspended, EntitlementEventType.Closed) => EntitlementStatus.Closed,
        _ => null,
    };

    public static bool IsLive(string status) => status != EntitlementStatus.Closed;

    /// <summary>
    /// What a reader should treat the entitlement as on <paramref name="today"/>: an active entitlement past its end (time
    /// basis), out of usage (usage basis), or either (both) is expired. A suspended one is not: its clock may be paused,
    /// and the end only moves when it resumes.
    /// </summary>
    public static string Effective(string status, DateOnly? endDate, string durationBasis, bool usageExhausted, DateOnly today)
    {
        if (status != EntitlementStatus.Active) return status;
        var timeUp = endDate is { } end && today > end;
        var expired = durationBasis switch
        {
            DurationBasis.Usage => usageExhausted,
            DurationBasis.Both => timeUp || usageExhausted,
            _ => timeUp,
        };
        return expired ? EntitlementClosedReason.Expired : status;
    }
}
