using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

public sealed record StatutoryExtensionEligibility(bool Eligible, bool AlreadyApplied, Guid RootEntitlementId,
    int ExtensionMonths, int MaximumApplications, int AppliedApplications);

/// <summary>The P2 one-per-root guard, shared by replacement arithmetic and P5 repair extensions.</summary>
public static class StatutoryExtensionGuard
{
    public static async Task<StatutoryExtensionEligibility> EvaluateAsync(SerialisationDbContext db, TenantId tenantId,
        Entitlement source, DateOnly? contractDate, string? country, bool consumerContract, string remedy,
        IJurisdictionRuleResolver rules, IJurisdictionRegionPort regions, CancellationToken cancellationToken)
    {
        var rootId = source.RootEntitlementId ?? source.EntitlementId;
        var scope = JsonSerializer.Deserialize<List<string>>(source.RegionScope, LedgerJson.Options) ?? [];
        if (source.Type != EntitlementType.Statutory || remedy != ClaimRemedy.Repair || !consumerContract
            || contractDate is not { } dated || country is not { } countryCode)
            return new(false, false, rootId, 0, 0, 0);
        var applicable = await regions.ApplicableScopesAsync(countryCode, null, cancellationToken);
        if (!scope.Any(applicable.Contains)) return new(false, false, rootId, 0, 0, 0);

        var context = new JurisdictionRuleContext(countryCode, null, JurisdictionBuyerClass.Consumer, null, null, null,
            dated, dated, null, dated);
        var monthRules = await rules.ResolveAsync(JurisdictionRuleKind.RepairExtensionMonths, context, cancellationToken);
        var countRules = await rules.ResolveAsync(JurisdictionRuleKind.RepairExtensionCount, context, cancellationToken);
        var monthsRule = monthRules.Count == 0 ? null : monthRules[0];
        var countRule = countRules.Count == 0 ? null : countRules[0];
        var months = monthsRule is null ? null : JurisdictionEntitlementRules.Months(monthsRule.Value);
        var maximum = countRule is null ? null : JurisdictionEntitlementRules.Integer(countRule.Value);
        if (months is not > 0 || maximum is not > 0) return new(false, false, rootId, 0, 0, 0);

        var chainIds = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId
            && (e.EntitlementId == rootId || e.RootEntitlementId == rootId)).Select(e => e.EntitlementId).ToListAsync(cancellationToken);
        var amendmentApplications = await db.EntitlementAmendments.AsNoTracking().Where(a => a.TenantId == tenantId
            && chainIds.Contains(a.EntitlementId) && a.ReasonCode == AmendmentReason.StatutoryExtension)
            .Select(a => new { a.EntitlementId, a.EventId }).Distinct().ToListAsync(cancellationToken);
        var amendedIds = amendmentApplications.Select(row => row.EntitlementId).ToHashSet();
        var transferredApplications = await db.Entitlements.AsNoTracking().CountAsync(e => e.TenantId == tenantId
            && chainIds.Contains(e.EntitlementId) && e.StatutoryExtensionApplied && !amendedIds.Contains(e.EntitlementId), cancellationToken);
        var applied = amendmentApplications.Select(row => row.EventId).Distinct().Count() + transferredApplications;
        return new(true, applied >= maximum, rootId, months.Value, maximum.Value, applied);
    }
}
