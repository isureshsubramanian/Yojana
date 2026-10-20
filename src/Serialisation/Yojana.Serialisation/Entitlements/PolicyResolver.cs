using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// The policy stack (P2-T04): (1) a live entitlement on the unit in the family, (2) a live entitlement on a parent unit
/// whose scope covers this child, (3) a customer contract of the owner, (4) the item's product policy, each policy
/// chosen by <b>contract date</b> (case 114). The first match wins; ties inside level 3 go to the administrator's
/// priority, then the later start. Levels 1 and 2 cannot tie: one live entitlement per family per unit is a database rule.
/// </summary>
public sealed class PolicyResolver(SerialisationDbContext db, ITenantContext tenant, IItemPolicyPort itemPolicies,
    ICustomerContractPort contracts, IJurisdictionRegionPort regions)
{
    private const int MaxParentDepth = 5;

    public async Task<PolicyResolution> ResolveAsync(PolicyQuestion question, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var trail = new List<string>();

        var own = await ApplicableAsync(question.UnitId, question, cancellationToken);
        if (own is not null)
        {
            trail.Add($"unit: live {own.CoverageFamily} entitlement {own.EntitlementId}");
            return await FromEntitlementAsync(PolicyLevel.UnitEntitlement, own, question, trail, cancellationToken);
        }

        trail.Add($"unit: no live {question.CoverageFamily} entitlement");
        var child = question.UnitId;
        for (var depth = 0; depth < MaxParentDepth; depth++)
        {
            var links = db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ChildUnitId == child);
            if (question.CoverageDate is { } coverageDate)
            {
                var from = coverageDate.ToDateTime(TimeOnly.MinValue);
                var until = from.AddDays(1);
                links = links.Where(l => l.InstalledAt < until && (l.RemovedAt == null || l.RemovedAt >= from));
            }
            else links = links.Where(l => l.RemovedAt == null);
            var parent = await links.Select(l => (Guid?)l.ParentUnitId).FirstOrDefaultAsync(cancellationToken);
            if (parent is null) break;
            var covering = await ApplicableAsync(parent.Value, question, cancellationToken);
            if (covering is not null && covering.Scope is EntitlementScope.Kit or EntitlementScope.Component && (covering.ScopeChildUnitId is null || covering.ScopeChildUnitId == question.UnitId))
            {
                trail.Add($"parent {parent}: {covering.Scope} entitlement {covering.EntitlementId} covers this child");
                return await FromEntitlementAsync(PolicyLevel.ParentEntitlement, covering, question, trail, cancellationToken);
            }

            trail.Add(covering is null ? $"parent {parent}: no live {question.CoverageFamily} entitlement" : $"parent {parent}: entitlement {covering.EntitlementId} is {covering.Scope}-scoped and does not cover children");
            child = parent.Value;
        }

        if (question.OwnerPartyRef is { } owner)
        {
            var terms = (await contracts.ContractsForAsync(owner, question.ItemId, question.ContractDate, cancellationToken)).Where(c => c.CoverageFamily == question.CoverageFamily)
                .OrderByDescending(c => c.Priority).ThenByDescending(c => c.ValidFrom).ToList();
            if (terms.Count > 1) trail.Add($"customer contract: {terms.Count} match; {terms[0].ContractRef} wins on priority {terms[0].Priority} then start {terms[0].ValidFrom:yyyy-MM-dd}");
            foreach (var term in terms)
            {
                var version = await VersionAtAsync(term.PolicyCode, question.ContractDate, cancellationToken);
                if (version is null) { trail.Add($"customer contract {term.ContractRef}: policy {term.PolicyCode} has no version for contracts on {question.ContractDate:yyyy-MM-dd}"); continue; }
                trail.Add($"customer contract {term.ContractRef}: {term.PolicyCode} v{version.Version}");
                return await FromVersionAsync(PolicyLevel.CustomerContract, version, term.ContractRef, question, trail, cancellationToken);
            }

            if (terms.Count == 0) trail.Add("customer contract: none for the owner, item and date");
        }
        else
        {
            trail.Add("customer contract: no owner given");
        }

        var product = (await itemPolicies.PoliciesForAsync(question.ItemId, cancellationToken)).FirstOrDefault(p => p.CoverageFamily == question.CoverageFamily);
        if (product is null)
        {
            trail.Add($"product policy: item has no {question.CoverageFamily} policy");
            return new PolicyResolution(PolicyLevel.None, null, null, null, null, null, false, trail);
        }

        var productVersion = await VersionAtAsync(product.PolicyCode, question.ContractDate, cancellationToken);
        if (productVersion is null)
        {
            trail.Add($"product policy {product.PolicyCode}: no published version for contracts on {question.ContractDate:yyyy-MM-dd}");
            return new PolicyResolution(PolicyLevel.None, null, null, product.PolicyCode, null, null, false, trail);
        }

        trail.Add($"product policy {product.PolicyCode} v{productVersion.Version} for contracts from {productVersion.AppliesToContractsFrom:yyyy-MM-dd}");
        return await FromVersionAsync(PolicyLevel.ProductPolicy, productVersion, null, question, trail, cancellationToken);
    }

    /// <summary>The published version of a policy in force for a contract date: the latest that applies from on or before it.</summary>
    public Task<WarrantyPolicyVersion?> VersionAtAsync(string policyCode, DateOnly contractDate, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return db.PolicyVersions.AsNoTracking()
            .Where(v => v.TenantId == tenantId && v.PolicyCode == policyCode && v.Status != PolicyVersionStatus.Draft && v.AppliesToContractsFrom <= contractDate)
            .OrderByDescending(v => v.AppliesToContractsFrom).ThenByDescending(v => v.Version).FirstOrDefaultAsync(cancellationToken);
    }

    private Task<Entitlement?> ApplicableAsync(Guid unitId, PolicyQuestion question, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.UnitId == unitId && e.CoverageFamily == question.CoverageFamily);
        if (question.Direction is { } direction) rows = rows.Where(e => e.Direction == direction);
        if (question.CoverageDate is { } on)
        {
            rows = rows.Where(e => e.StartDate != null && e.StartDate <= on && (e.EndDate == null || e.EndDate >= on)
                && (e.Status != EntitlementStatus.Closed || e.ClosedEffective == null || e.ClosedEffective >= on));
        }
        else rows = rows.Where(e => e.Status != EntitlementStatus.Closed);
        return rows.OrderByDescending(e => e.StartDate).ThenByDescending(e => e.CreatedAt).FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<PolicyResolution> FromEntitlementAsync(string level, Entitlement entitlement, PolicyQuestion question, List<string> trail, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var version = await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenantId && v.PolicyVersionId == entitlement.PolicyVersionId, cancellationToken);
        var outOfRegion = question.Country is { } country && !await InScopeAsync(
            System.Text.Json.JsonSerializer.Deserialize<List<string>>(entitlement.RegionScope) ?? [], country, cancellationToken);
        if (outOfRegion) trail.Add($"region: {question.Country} is outside the entitlement's scope");
        var coverage = JsonSerializer.Deserialize<CoverageTerms>(entitlement.Coverage, LedgerJson.Options);
        return new PolicyResolution(level, entitlement.EntitlementId, entitlement.PolicyVersionId, version?.PolicyCode, version?.Version, null, outOfRegion, trail,
            entitlement.Type, entitlement.Status, entitlement.Direction, coverage, version?.SettlementFormula, entitlement.AccrualCohortId,
            entitlement.ObligorEntityId, entitlement.PayerPartyRef, entitlement.StartDate, entitlement.EndDate, await ChainLengthAsync(entitlement, cancellationToken),
            entitlement.PurchaseDate, version?.NoClaimWithinDaysOfPurchase, entitlement.Provenance);
    }

    private async Task<PolicyResolution> FromVersionAsync(string level, WarrantyPolicyVersion version, string? contractRef, PolicyQuestion question, List<string> trail, CancellationToken cancellationToken)
    {
        var scope = System.Text.Json.JsonSerializer.Deserialize<List<string>>(version.RegionScope) ?? [];
        var outOfRegion = question.Country is { } country && !await InScopeAsync(scope, country, cancellationToken);
        if (outOfRegion) trail.Add($"region: {question.Country} is outside {version.PolicyCode} v{version.Version} ({string.Join(", ", scope)})");
        var coverage = JsonSerializer.Deserialize<CoverageTerms>(version.Coverage, LedgerJson.Options);
        return new PolicyResolution(level, null, version.PolicyVersionId, version.PolicyCode, version.Version, contractRef, outOfRegion, trail,
            version.EntitlementType, EntitlementStatus.Active, EntitlementDirection.Outbound, coverage, version.SettlementFormula,
            NoClaimWithinDaysOfPurchase: version.NoClaimWithinDaysOfPurchase);
    }

    private async Task<bool> InScopeAsync(IReadOnlyList<string> scope, string country, CancellationToken cancellationToken)
    {
        var applicable = await regions.ApplicableScopesAsync(country, null, cancellationToken);
        return scope.Any(applicable.Contains);
    }

    private async Task<int> ChainLengthAsync(Entitlement entitlement, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var length = 0;
        var parent = entitlement.ParentEntitlementId;
        while (parent is { } parentId && length < 100)
        {
            length++;
            parent = await db.Entitlements.AsNoTracking().Where(e => e.TenantId == tenantId && e.EntitlementId == parentId)
                .Select(e => e.ParentEntitlementId).FirstOrDefaultAsync(cancellationToken);
        }

        return length;
    }
}
