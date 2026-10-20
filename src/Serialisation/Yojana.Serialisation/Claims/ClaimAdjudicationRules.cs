using System.Text.Json;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims;

public static class ClaimAdjudicationRules
{
    private static readonly string[] BlockingFlags = [OverlayFlag.Lost, OverlayFlag.Stolen, OverlayFlag.WrittenOff, OverlayFlag.CloneSeen, OverlayFlag.Recalled, OverlayFlag.AuthenticityFailed];

    public static string? BlockingFlag(IReadOnlyList<string> flags, bool recalledIsCovered = false) =>
        BlockingFlags.FirstOrDefault(flag => (!recalledIsCovered || flag != OverlayFlag.Recalled) && flags.Contains(flag));

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

/// <summary>
/// Tenant defaults copied into policy versions at publication. The accidental-damage proposal is identified only by
/// the exact tenant catalogue code; names are customer wording and are never guessed.
/// </summary>
public sealed class ClaimWaitingPeriodSettings(ITenantSettings settings)
{
    public const string Key = "claims.waiting_period_defaults";
    private const string ProposedAccidentalDamageFamily = "accidental_damage";

    public async Task<ClaimWaitingPeriodDefaults> GetAsync(IReadOnlyCollection<string> coverageFamilies,
        CancellationToken cancellationToken)
    {
        if (await settings.GetAsync(Key, cancellationToken) is { } text)
        {
            var stored = JsonSerializer.Deserialize<ClaimWaitingPeriodDefaults>(text, LedgerJson.Options);
            if (stored is not null)
            {
                var proposed = Defaults(coverageFamilies).DaysByCoverageFamily;
                return new ClaimWaitingPeriodDefaults(coverageFamilies.Order(StringComparer.Ordinal).ToDictionary(code => code,
                    code => stored.DaysByCoverageFamily.TryGetValue(code, out var days) ? days : proposed.GetValueOrDefault(code), StringComparer.Ordinal));
            }
        }

        return Defaults(coverageFamilies);
    }

    public async Task<ClaimWaitingPeriodDefaults> SetAsync(ClaimWaitingPeriodDefaults policy,
        IReadOnlyCollection<string> coverageFamilies, CancellationToken cancellationToken)
    {
        var known = coverageFamilies.ToHashSet(StringComparer.Ordinal);
        var unknown = policy.DaysByCoverageFamily.Keys.Where(code => !known.Contains(code)).Order().ToList();
        if (unknown.Count > 0)
            throw new CommandRefusedException("claim_waiting_period_defaults_invalid",
                $"Unknown coverage families: {string.Join(", ", unknown)}.");
        if (policy.DaysByCoverageFamily.Any(pair => pair.Value is < 0 or > 365))
            throw new CommandRefusedException("claim_waiting_period_defaults_invalid",
                "Every claim waiting-period default must be between 0 and 365 whole days.");

        var complete = new ClaimWaitingPeriodDefaults(coverageFamilies.Order(StringComparer.Ordinal)
            .ToDictionary(code => code, code => policy.DaysByCoverageFamily.GetValueOrDefault(code), StringComparer.Ordinal));
        await settings.SetAsync(Key, JsonSerializer.Serialize(complete, LedgerJson.Options), cancellationToken);
        return complete;
    }

    private static ClaimWaitingPeriodDefaults Defaults(IEnumerable<string> coverageFamilies) =>
        new(coverageFamilies.Order(StringComparer.Ordinal).ToDictionary(code => code,
            code => code == ProposedAccidentalDamageFamily ? 30 : 0, StringComparer.Ordinal));
}
