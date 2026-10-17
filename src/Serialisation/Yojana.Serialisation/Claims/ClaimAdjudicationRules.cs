using Yojana.Serialisation.Contracts;

namespace Yojana.Serialisation.Claims;

public static class ClaimAdjudicationRules
{
    private static readonly string[] BlockingFlags = [OverlayFlag.Lost, OverlayFlag.Stolen, OverlayFlag.WrittenOff, OverlayFlag.CloneSeen, OverlayFlag.Recalled, OverlayFlag.AuthenticityFailed];

    public static string? BlockingFlag(IReadOnlyList<string> flags) => BlockingFlags.FirstOrDefault(flags.Contains);

    public static string? ItemDenialReason(ItemSerialisationProfile? item, DateOnly claimDate, string lineKind)
    {
        if (lineKind is not (ClaimLineKind.Parts or ClaimLineKind.Labour)) return null;
        if (item?.ServiceBlocked == true) return ClaimDenialReason.ItemServiceBlocked;
        return item?.LastServiceDate is { } lastServiceDate && claimDate > lastServiceDate
            ? ClaimDenialReason.AfterLastServiceDate
            : null;
    }

    public static bool IsDiscontinued(ItemSerialisationProfile? item, DateOnly claimDate) =>
        item is not null && (item.SalesBlocked || item.ServiceBlocked || item.LastServiceDate is { } lastServiceDate && claimDate > lastServiceDate);
}
