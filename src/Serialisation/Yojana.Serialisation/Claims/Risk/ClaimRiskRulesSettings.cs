using System.Text.Json;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims.Risk;

/// <summary>
/// Coupled to the factory registration in SerialisationModule: ITenantSettings owns PlatformDbContext, which must
/// stay outside claim handlers' SerialisationDbContext graphs or Wolverine refuses to start the host.
/// </summary>
public sealed class ClaimRiskRulesSettings(ITenantSettings settings)
{
    public const string Key = "claims.risk_rules";

    public static readonly ClaimRiskRulesPolicy DefaultPolicy = new(
        new(true, 60),
        new(true, 2, 90),
        new(true, 180),
        new(true, 365, 3m, 5, 20),
        new(true),
        new(true, 30),
        new(true),
        new(true),
        YojanaRole.ServiceManager,
        true);

    public async Task<ClaimRiskRulesPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<ClaimRiskRulesPolicy>(text, LedgerJson.Options) ?? DefaultPolicy
            : DefaultPolicy;

    public async Task<ClaimRiskRulesPolicy> SetAsync(ClaimRiskRulesPolicy policy, CancellationToken cancellationToken)
    {
        Validate(policy);
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
        return policy;
    }

    public static void Validate(ClaimRiskRulesPolicy policy)
    {
        Days(policy.RecentCoverPurchase.Days, "recent cover purchase");
        Days(policy.RepeatUnitClaims.Days, "repeat unit claims");
        Days(policy.RepeatFault.Days, "repeat fault");
        Days(policy.ClaimantRate.Days, "claimant rate");
        Days(policy.RecentOwnershipChange.Days, "recent ownership change");
        if (policy.RepeatUnitClaims.MinimumEarlierClaims is < 1 or > 100)
            Invalid("The repeat-unit minimum must be between 1 and 100 earlier claims.");
        if (policy.ClaimantRate.RateMultiplier is <= 1 or > 100)
            Invalid("The claimant-rate multiplier must be greater than 1 and at most 100.");
        if (policy.ClaimantRate.MinimumClaims is < 1 or > 10000)
            Invalid("The claimant-rate minimum claim count must be between 1 and 10,000.");
        if (policy.ClaimantRate.MinimumUnits is < 1 or > 1000000)
            Invalid("The claimant-rate minimum installed base must be between 1 and 1,000,000 units.");
        if (!YojanaRole.IsKnown(policy.ReviewerRole))
            Invalid($"'{policy.ReviewerRole}' is not a tenant role.");
    }

    private static void Days(int days, string name)
    {
        if (days is < 1 or > 3650) Invalid($"The {name} window must be between 1 and 3,650 days.");
    }

    private static void Invalid(string message) =>
        throw new CommandRefusedException("claim_risk_rules_invalid", message);
}
