using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Claims;

public static class ClaimStateMachine
{
    public static string? Next(string status, string eventType) => (status, eventType) switch
    {
        (ClaimStatus.Submitted, ClaimEventType.Adjudicating) => ClaimStatus.Adjudicating,
        (ClaimStatus.Adjudicating, ClaimStatus.Approved) => ClaimStatus.Approved,
        (ClaimStatus.Adjudicating, ClaimStatus.PartiallyApproved) => ClaimStatus.PartiallyApproved,
        (ClaimStatus.Adjudicating, ClaimStatus.Denied) => ClaimStatus.Denied,
        (ClaimStatus.Approved or ClaimStatus.PartiallyApproved or ClaimStatus.Denied, ClaimEventType.Settled) => ClaimStatus.Settled,
        (ClaimStatus.Settled, ClaimEventType.Closed) => ClaimStatus.Closed,
        _ => null,
    };
}

