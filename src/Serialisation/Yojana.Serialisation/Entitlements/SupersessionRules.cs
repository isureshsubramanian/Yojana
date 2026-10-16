using Yojana.Platform.Contracts.Security;
using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Entitlements;

public sealed record SupersessionRequirement(string Role, bool SourceDocument, bool CustomerNotification);

/// <summary>Pure approval and reduction rules for a coverage-family supersession (ADR-0001 case 123).</summary>
public static class SupersessionRules
{
    public static readonly IReadOnlySet<string> Reasons = new HashSet<string>
    {
        AmendmentReason.Purchase, AmendmentReason.Correction, AmendmentReason.SellerError, AmendmentReason.Goodwill,
    };

    public static SupersessionRequirement Required(string reason, bool reduction, bool certificateIssued)
    {
        if (!Reasons.Contains(reason))
            throw new CommandRefusedException("reason_unknown", $"'{reason}' is not a supersession reason ({string.Join(", ", Reasons)}).");
        // A purchase is backed by its source document (an upgrade sold on an invoice): the desk commits it, as a purchase
        // amendment; every other reason, and any supersession that reduces cover, needs the service manager.
        var role = reason == AmendmentReason.Purchase && !reduction ? YojanaRole.ServiceDesk : YojanaRole.ServiceManager;
        return new(role, reason == AmendmentReason.Purchase, reduction && certificateIssued);
    }

    public static bool CoverageReduced(DateOnly oldEnd, CoverageTerms oldCoverage, DateOnly newEnd, CoverageTerms newCoverage) =>
        newEnd < oldEnd || AmendmentRules.CoverageReduced(oldCoverage, newCoverage);
}
