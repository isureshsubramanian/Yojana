using System.Text.Json;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Rmas;

/// <summary>Tenant configuration for case 55; item overrides win, otherwise fitted parts receive at least 90 days.</summary>
public sealed class ServicePartEntitlementSettings(ITenantSettings settings)
{
    public const string Key = "entitlements.service_parts";
    // Coupled to the factory registration in SerialisationModule: exposing ITenantSettings directly to the Wolverine
    // consumer adds PlatformDbContext beside SerialisationDbContext and prevents transaction discovery at startup.
    public static readonly ServicePartEntitlementPolicy Default = new();

    public async Task<ServicePartEntitlementPolicy> GetAsync(CancellationToken cancellationToken) =>
        await settings.GetAsync(Key, cancellationToken) is { } text
            ? JsonSerializer.Deserialize<ServicePartEntitlementPolicy>(text, LedgerJson.Options) ?? Default
            : Default;

    public async Task SetAsync(ServicePartEntitlementPolicy policy, CancellationToken cancellationToken)
    {
        if (policy.DefaultMinimumDays <= 0 || policy.ItemMinimumDays?.Values.Any(days => days <= 0) == true)
            throw new CommandRefusedException("service_part_entitlement_policy_invalid", "Service-part entitlement days must be positive.");
        await settings.SetAsync(Key, JsonSerializer.Serialize(policy, LedgerJson.Options), cancellationToken);
    }

    public static int MinimumDays(ServicePartEntitlementPolicy policy, Guid itemId) =>
        policy.ItemMinimumDays?.GetValueOrDefault(itemId) ?? policy.DefaultMinimumDays;
}
