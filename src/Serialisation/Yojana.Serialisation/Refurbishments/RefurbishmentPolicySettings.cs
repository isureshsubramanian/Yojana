using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Refurbishments;

/// <summary>OD-09 item-class policy; a missing tenant setting means same unit/new serial and three cycles.</summary>
public sealed class RefurbishmentPolicySettings(ITenantSettings settings)
{
    public const string Key = "refurbishment.policy";
    public static readonly RefurbishmentPolicy Default = new(RefurbishmentIdentity.SameUnitNewSerial, 3, []);

    // Coupled to the factory registration in SerialisationModule for the same Wolverine two-DbContext constraint as
    // the other settings wrappers: exposing ITenantSettings to a handler also exposes PlatformDbContext and prevents
    // Wolverine from selecting SerialisationDbContext as the transactional context.
    public async Task<RefurbishmentPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<RefurbishmentPolicy>(text, LedgerJson.Options) ?? Default
            : Default;

    public async Task SetAsync(RefurbishmentPolicy policy, CancellationToken cancellationToken)
    {
        var problems = Validate(policy);
        if (problems.Count > 0) throw new CommandRefusedException("refurbishment_policy_invalid", string.Join("; ", problems));
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }

    public static ResolvedRefurbishmentPolicy Resolve(RefurbishmentPolicy policy, Guid itemId, string? itemClass)
    {
        var itemClassPolicy = itemClass is null ? null : policy.ItemClasses.FirstOrDefault(row => row.ItemClass == itemClass);
        var itemCycles = policy.Items?.FirstOrDefault(row => row.ItemId == itemId)?.MaxCycles;
        return itemClassPolicy is null
            ? new(policy.DefaultIdentity, itemCycles ?? policy.DefaultMaxCycles, policy.DefaultReplacementPolicyCode)
            : new(itemClassPolicy.Identity, itemCycles ?? itemClassPolicy.MaxCycles,
                itemClassPolicy.ReplacementPolicyCode ?? policy.DefaultReplacementPolicyCode);
    }

    public static IReadOnlyList<string> Validate(RefurbishmentPolicy policy)
    {
        var problems = new List<string>();
        if (!RefurbishmentIdentity.All.Contains(policy.DefaultIdentity)) problems.Add($"default identity '{policy.DefaultIdentity}' is unknown");
        if (policy.DefaultMaxCycles < 1) problems.Add("default max cycles must be at least one");
        if (policy.ItemClasses.Any(row => string.IsNullOrWhiteSpace(row.ItemClass))) problems.Add("an item-class override has no item class");
        if (policy.ItemClasses.GroupBy(row => row.ItemClass, StringComparer.Ordinal).Any(group => group.Count() > 1)) problems.Add("an item class has more than one refurbishment override");
        if (policy.Items?.GroupBy(row => row.ItemId).Any(group => group.Count() > 1) == true) problems.Add("an item has more than one refurbishment cycle override");
        foreach (var row in policy.ItemClasses)
        {
            if (!RefurbishmentIdentity.All.Contains(row.Identity)) problems.Add($"item class '{row.ItemClass}' has unknown identity '{row.Identity}'");
            if (row.MaxCycles < 1) problems.Add($"item class '{row.ItemClass}' max cycles must be at least one");
        }
        if (policy.Items?.Any(row => row.MaxCycles < 1) == true) problems.Add("item max cycles must be at least one");

        return problems;
    }
}
