using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Claims;

/// <summary>The vendor terms one approved line recovers from, or why it recovers from none.</summary>
public sealed record RecoveryDecision(IReadOnlyList<InboundRecoveryCoverage> Covers, string? NotRaisedReason);

/// <summary>
/// Applies the tenant's <see cref="RecoveryPolicy"/> to the live vendor terms found for one approved line. Approval and the
/// adjudication preview both call it, so the screen shows exactly the recovery claims approve will raise.
/// </summary>
public static class RecoverySelection
{
    public static RecoveryDecision Select(RecoveryPolicy policy, Guid rootUnitId, ClaimLine line, IReadOnlyList<InboundRecoveryCoverage> found)
    {
        if (found.Count == 0) return new([], null);
        if (!policy.LineKinds.Contains(line.Kind)) return new([], RecoveryNotRaisedReason.LineKindNotRecovered);

        // One recovery per line: a part's (or the unit's) earliest-listed live vendor term carries the whole line amount.
        static IReadOnlyList<InboundRecoveryCoverage> First(IEnumerable<InboundRecoveryCoverage> covers) => covers.Take(1).ToList();
        if (line.UnitId != rootUnitId) return new(First(found), null);

        var own = found.Where(cover => cover.UnitId == rootUnitId).ToList();
        if (own.Count > 0) return new(First(own), null);
        if (policy.WholeUnitLines == RecoveryWholeUnitRule.NamedPartOnly) return new([], RecoveryNotRaisedReason.PartNotNamed);

        var parts = found.GroupBy(cover => cover.UnitId).ToList();
        return parts.Count == 1 ? new(First(parts[0]), null) : new([], RecoveryNotRaisedReason.SeveralCoveredParts);
    }
}

/// <summary>The tenant's supplier-recovery policy over <see cref="ITenantSettings"/>.</summary>
public sealed class RecoverySettings(ITenantSettings settings)
{
    public const string Key = "claims.recovery_policy";

    // Coupled to the factory registration in SerialisationModule: ITenantSettings owns PlatformDbContext, which must
    // stay outside AdjudicateClaimHandler's SerialisationDbContext graph or Wolverine refuses to start the host.
    public async Task<RecoveryPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<RecoveryPolicy>(text, LedgerJson.Options) ?? RecoveryPolicy.Default
            : RecoveryPolicy.Default;

    public async Task<RecoveryPolicy> SetAsync(RecoveryPolicy policy, CancellationToken cancellationToken)
    {
        if (!RecoveryWholeUnitRule.All.Contains(policy.WholeUnitLines))
            throw new CommandRefusedException("recovery_policy_invalid", $"'{policy.WholeUnitLines}' is not a whole-unit recovery rule.", "choose single_covered_part or named_part_only");
        var kinds = (policy.LineKinds ?? []).Distinct(StringComparer.Ordinal).ToList();
        if (kinds.Count == 0)
            throw new CommandRefusedException("recovery_policy_invalid", "Choose at least one claim line kind for supplier recovery.");
        if (kinds.Any(kind => !ClaimLineKind.All.Contains(kind)))
            throw new CommandRefusedException("recovery_policy_invalid", "Line kinds must be parts, labour or travel.");
        if (policy.VendorAnswerDueDays is < 1 or > 90)
            throw new CommandRefusedException("recovery_policy_invalid", "The vendor answer is due within 1 to 90 days.");

        var normalised = policy with { LineKinds = ClaimLineKind.All.Where(kinds.Contains).Order(StringComparer.Ordinal).ToList() };
        await settings.SetAsync(Key, JsonSerializer.Serialize(normalised, LedgerJson.Options), cancellationToken);
        return normalised;
    }
}
