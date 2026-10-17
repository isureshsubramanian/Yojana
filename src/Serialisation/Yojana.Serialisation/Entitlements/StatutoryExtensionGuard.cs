using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

public sealed record StatutoryExtensionEligibility(bool Eligible, bool AlreadyApplied, Guid RootEntitlementId);

/// <summary>The P2 one-per-root guard, shared by replacement arithmetic and P5 repair extensions.</summary>
public static class StatutoryExtensionGuard
{
    private static readonly DateOnly EuEffectiveDate = new(2026, 7, 31);

    public static async Task<StatutoryExtensionEligibility> EvaluateAsync(SerialisationDbContext db, TenantId tenantId,
        Entitlement source, DateOnly? contractDate, string? country, bool consumerContract, string remedy,
        CancellationToken cancellationToken)
    {
        var rootId = source.RootEntitlementId ?? source.EntitlementId;
        var scope = JsonSerializer.Deserialize<List<string>>(source.RegionScope, LedgerJson.Options) ?? [];
        var eligible = source.Type == EntitlementType.Statutory && remedy == ClaimRemedy.Repair && consumerContract
            && contractDate is { } dated && dated >= EuEffectiveDate && country is { } countryCode
            && Regions.Contains("EU", countryCode) && Regions.InScope(scope, countryCode);
        var alreadyApplied = eligible && await db.Entitlements.AnyAsync(e => e.TenantId == tenantId
            && (e.EntitlementId == rootId || e.RootEntitlementId == rootId) && e.StatutoryExtensionApplied, cancellationToken);
        return new(eligible, alreadyApplied, rootId);
    }
}
