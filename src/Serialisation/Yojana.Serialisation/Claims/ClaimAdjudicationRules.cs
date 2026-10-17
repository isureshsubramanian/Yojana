using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Claims;

public static class ClaimAdjudicationRules
{
    private static readonly string[] BlockingFlags = [OverlayFlag.Lost, OverlayFlag.Stolen, OverlayFlag.WrittenOff, OverlayFlag.CloneSeen, OverlayFlag.Recalled, OverlayFlag.AuthenticityFailed];

    public static string? BlockingFlag(IReadOnlyList<string> flags) => BlockingFlags.FirstOrDefault(flags.Contains);
}
