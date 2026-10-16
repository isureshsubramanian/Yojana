using System.Text.Json;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>What an amendment needs before it may be committed: the role, and the references that must accompany it.</summary>
public sealed record AmendmentRequirement(string Role, bool SystemOnly, bool SourceDocument, bool FinanceReview, bool CustomerNotification);

/// <summary>
/// The approval rules of <c>AmendEntitlement</c> (ADR-0001 correction table, cases 129 and 140) as pure functions over the
/// tenant's <see cref="AmendmentApprovalPolicy"/>. The default is the ADR's proposed table; OD-06 is where the owner fixes
/// the values, and a tenant overrides them as data (<see cref="AmendmentApprovalSettings"/>).
/// </summary>
public static class AmendmentRules
{
    /// <summary>The ladder a higher role satisfies a lower one on: a service manager may commit what the desk may.</summary>
    public static readonly IReadOnlyList<string> Ladder = [YojanaRole.ServiceDesk, YojanaRole.ServiceManager];

    /// <summary>
    /// The ADR's proposed values. The finance-review bound for goodwill and dispute settlement is not given there ("a
    /// configurable value"); 90 days is a placeholder until OD-06 is decided.
    /// </summary>
    public static readonly AmendmentApprovalPolicy DefaultPolicy = new(
    [
        new(AmendmentReason.Purchase, SourceDocumentRequired: true, SystemOnly: false, [new(null, YojanaRole.ServiceDesk)]),
        new(AmendmentReason.Correction, false, false, [new(30, YojanaRole.ServiceDesk), new(null, YojanaRole.ServiceManager)]),
        new(AmendmentReason.SellerError, false, false, [new(30, YojanaRole.ServiceDesk), new(null, YojanaRole.ServiceManager)]),
        new(AmendmentReason.Goodwill, false, false, [new(90, YojanaRole.ServiceManager), new(null, YojanaRole.ServiceManager, FinanceReview: true)]),
        new(AmendmentReason.DisputeSettlement, false, false, [new(90, YojanaRole.ServiceManager), new(null, YojanaRole.ServiceManager, FinanceReview: true)]),
        new(AmendmentReason.StatutoryExtension, false, SystemOnly: true, [new(null, YojanaRole.ServiceManager)]),
    ], YojanaRole.ServiceManager);

    /// <summary>The requirement for <paramref name="reason"/> moving the end by <paramref name="daysChanged"/> (either way), raised for a reduction.</summary>
    public static AmendmentRequirement Required(AmendmentApprovalPolicy policy, string reason, int daysChanged, bool reduction, bool certificateIssued)
    {
        var rule = policy.Reasons.FirstOrDefault(r => r.ReasonCode == reason)
            ?? throw new CommandRefusedException("reason_unknown", $"'{reason}' is not an amendment reason of this tenant ({string.Join(", ", policy.Reasons.Select(r => r.ReasonCode))}).");
        var days = Math.Abs(daysChanged);
        var tier = rule.Tiers.FirstOrDefault(t => t.UpToDays is not { } bound || days <= bound) ?? rule.Tiers[^1];
        var role = tier.Role;
        if (reduction && Rank(policy.ReductionRole) > Rank(role)) role = policy.ReductionRole;
        return new(role, rule.SystemOnly, rule.SourceDocumentRequired, tier.FinanceReview, reduction && certificateIssued);
    }

    /// <summary>Whether someone holding <paramref name="held"/> may commit at <paramref name="required"/>: the role itself, or a higher one on the ladder.</summary>
    public static bool Satisfies(IReadOnlyList<string> held, string required) =>
        held.Contains(required, StringComparer.Ordinal) || (Rank(required) >= 0 && held.Any(h => Rank(h) > Rank(required)));

    private static int Rank(string role) => Ladder.ToList().IndexOf(role);

    /// <summary>
    /// A coverage change takes something from the customer when a percentage falls, a response commitment lengthens or
    /// disappears, or the coverage hours change at all (they have no order, so any change counts as a reduction).
    /// </summary>
    public static bool CoverageReduced(CoverageTerms before, CoverageTerms after) =>
        after.PartsPct < before.PartsPct || after.LabourPct < before.LabourPct || after.TravelPct < before.TravelPct
        || (before.ResponseHours is { } was && (after.ResponseHours is not { } now || now > was))
        || !string.Equals(before.CoverageHours, after.CoverageHours, StringComparison.Ordinal);

    /// <summary>The policy's problems, empty when it may be stored: every reason known and present once, tiers ascending and ending unbounded, roles known.</summary>
    public static IReadOnlyList<string> Validate(AmendmentApprovalPolicy policy)
    {
        var problems = new List<string>();
        if (!YojanaRole.IsKnown(policy.ReductionRole)) problems.Add($"reduction role '{policy.ReductionRole}' is not a role");
        foreach (var missing in AmendmentReason.All.Except(policy.Reasons.Select(r => r.ReasonCode))) problems.Add($"reason '{missing}' has no rule");
        foreach (var rule in policy.Reasons)
        {
            if (!AmendmentReason.All.Contains(rule.ReasonCode)) problems.Add($"'{rule.ReasonCode}' is not an amendment reason");
            if (policy.Reasons.Count(r => r.ReasonCode == rule.ReasonCode) > 1) problems.Add($"reason '{rule.ReasonCode}' has more than one rule");
            if (rule.Tiers.Count == 0) { problems.Add($"reason '{rule.ReasonCode}' has no tiers"); continue; }
            if (rule.Tiers[^1].UpToDays is not null) problems.Add($"reason '{rule.ReasonCode}': the last tier must have no day bound");
            var bounds = rule.Tiers.Take(rule.Tiers.Count - 1).Select(t => t.UpToDays).ToList();
            if (bounds.Any(b => b is null or < 0) || bounds.Zip(bounds.Skip(1)).Any(p => p.First >= p.Second)) problems.Add($"reason '{rule.ReasonCode}': day bounds must be ascending and not negative");
            foreach (var tier in rule.Tiers.Where(t => !YojanaRole.IsKnown(t.Role))) problems.Add($"reason '{rule.ReasonCode}': '{tier.Role}' is not a role");
        }

        return problems.Distinct().ToList();
    }
}

/// <summary>The tenant's amendment approval policy, stored as tenant data under <see cref="Key"/>; the default when none is set.</summary>
public sealed class AmendmentApprovalSettings(ITenantSettings settings)
{
    public const string Key = "entitlements.amendment_approval";

    public async Task<AmendmentApprovalPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text ? JsonSerializer.Deserialize<AmendmentApprovalPolicy>(text, LedgerJson.Options) ?? AmendmentRules.DefaultPolicy : AmendmentRules.DefaultPolicy;

    public async Task SetAsync(AmendmentApprovalPolicy policy, CancellationToken cancellationToken)
    {
        if (AmendmentRules.Validate(policy) is { Count: > 0 } problems) throw new CommandRefusedException("approval_policy_invalid", string.Join("; ", problems));
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }
}
