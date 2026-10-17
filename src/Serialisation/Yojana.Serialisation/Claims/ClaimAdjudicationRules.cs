using System.Text.Json;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims;

public static class ClaimAdjudicationRules
{
    private static readonly string[] BlockingFlags = [OverlayFlag.Lost, OverlayFlag.Stolen, OverlayFlag.WrittenOff, OverlayFlag.CloneSeen, OverlayFlag.Recalled, OverlayFlag.AuthenticityFailed];

    public static string? BlockingFlag(IReadOnlyList<string> flags) => BlockingFlags.FirstOrDefault(flags.Contains);

    public static void RequireLowConfidenceReviewer(ClaimReviewer? reviewer, string actorRef, string requiredRole)
    {
        if (!HoldsRole(reviewer, actorRef, requiredRole))
        {
            throw new CommandRefusedException("low_confidence_reviewer_required",
                $"A claim based on low-confidence cover is adjudicated by a named reviewer holding the tenant's '{requiredRole}' role.",
                $"route the claim to a {requiredRole} reviewer");
        }
    }

    public static void RequireStandardAdjudicator(ClaimReviewer reviewer, string actorRef)
    {
        if (!HoldsRole(reviewer, actorRef, YojanaRole.ServiceManager))
        {
            throw new CommandRefusedException("claim_adjudicator_required",
                $"Claim adjudication is performed by a named reviewer holding the '{YojanaRole.ServiceManager}' role.",
                $"route the claim to a {YojanaRole.ServiceManager}");
        }
    }

    private static bool HoldsRole(ClaimReviewer? reviewer, string actorRef, string requiredRole) =>
        reviewer is not null && reviewer.UserRef == actorRef && reviewer.Roles.Contains(requiredRole, StringComparer.Ordinal);

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

/// <summary>The tenant's reviewer role for low-confidence cover; missing data defaults to the service manager.</summary>
public sealed class ClaimReviewSettings(ITenantSettings settings)
{
    public const string Key = "claims.low_confidence_review";
    public static readonly ClaimReviewPolicy DefaultPolicy = new(YojanaRole.ServiceManager);

    // Coupled to the factory registration in SerialisationModule: ITenantSettings owns PlatformDbContext, which must
    // stay outside AdjudicateClaimHandler's SerialisationDbContext graph or Wolverine refuses to start the host.
    public async Task<ClaimReviewPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<ClaimReviewPolicy>(text, LedgerJson.Options) ?? DefaultPolicy
            : DefaultPolicy;

    public async Task SetAsync(ClaimReviewPolicy policy, CancellationToken cancellationToken)
    {
        if (!YojanaRole.IsKnown(policy.LowConfidenceReviewerRole))
        {
            throw new CommandRefusedException("claim_review_policy_invalid", $"'{policy.LowConfidenceReviewerRole}' is not a tenant role.");
        }

        if (policy.AdjudicationTargetWorkingDays is < 1 or > 60)
        {
            throw new CommandRefusedException("claim_review_policy_invalid", "The adjudication target must be between 1 and 60 working days.");
        }

        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }
}
