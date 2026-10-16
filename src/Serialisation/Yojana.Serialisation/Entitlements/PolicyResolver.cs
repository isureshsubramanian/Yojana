using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Serialisation.Entitlements;

/// <summary>
/// The policy stack (P2-T04): (1) a live entitlement on the unit in the family, (2) a live entitlement on a parent unit
/// whose scope covers this child, (3) a customer contract of the owner, (4) the item's product policy, each policy
/// chosen by <b>contract date</b> (case 114). The first match wins; ties inside level 3 go to the administrator's
/// priority, then the later start. Levels 1 and 2 cannot tie: one live entitlement per family per unit is a database rule.
/// </summary>
public sealed class PolicyResolver(SerialisationDbContext db, ITenantContext tenant, IItemPolicyPort itemPolicies, ICustomerContractPort contracts)
{
    private const int MaxParentDepth = 5;

    public async Task<PolicyResolution> ResolveAsync(PolicyQuestion question, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var trail = new List<string>();

        var own = await LiveAsync(question.UnitId, question.CoverageFamily, cancellationToken);
        if (own is not null)
        {
            trail.Add($"unit: live {own.CoverageFamily} entitlement {own.EntitlementId}");
            return await FromEntitlementAsync(PolicyLevel.UnitEntitlement, own, question, trail, cancellationToken);
        }

        trail.Add($"unit: no live {question.CoverageFamily} entitlement");
        var child = question.UnitId;
        for (var depth = 0; depth < MaxParentDepth; depth++)
        {
            var parent = await db.AssemblyLinks.AsNoTracking().Where(l => l.TenantId == tenantId && l.ChildUnitId == child && l.RemovedAt == null).Select(l => (Guid?)l.ParentUnitId).FirstOrDefaultAsync(cancellationToken);
            if (parent is null) break;
            var covering = await LiveAsync(parent.Value, question.CoverageFamily, cancellationToken);
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

    private Task<Entitlement?> LiveAsync(Guid unitId, string family, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        return db.Entitlements.AsNoTracking().FirstOrDefaultAsync(e => e.TenantId == tenantId && e.UnitId == unitId && e.CoverageFamily == family && e.Status != EntitlementStatus.Closed, cancellationToken);
    }

    private async Task<PolicyResolution> FromEntitlementAsync(string level, Entitlement entitlement, PolicyQuestion question, List<string> trail, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var version = await db.PolicyVersions.AsNoTracking().FirstOrDefaultAsync(v => v.TenantId == tenantId && v.PolicyVersionId == entitlement.PolicyVersionId, cancellationToken);
        var outOfRegion = question.Country is { } country && !Regions.InScope(System.Text.Json.JsonSerializer.Deserialize<List<string>>(entitlement.RegionScope) ?? [], country);
        if (outOfRegion) trail.Add($"region: {question.Country} is outside the entitlement's scope");
        return new PolicyResolution(level, entitlement.EntitlementId, entitlement.PolicyVersionId, version?.PolicyCode, version?.Version, null, outOfRegion, trail);
    }

    private Task<PolicyResolution> FromVersionAsync(string level, WarrantyPolicyVersion version, string? contractRef, PolicyQuestion question, List<string> trail, CancellationToken cancellationToken)
    {
        var scope = System.Text.Json.JsonSerializer.Deserialize<List<string>>(version.RegionScope) ?? [];
        var outOfRegion = question.Country is { } country && !Regions.InScope(scope, country);
        if (outOfRegion) trail.Add($"region: {question.Country} is outside {version.PolicyCode} v{version.Version} ({string.Join(", ", scope)})");
        return Task.FromResult(new PolicyResolution(level, null, version.PolicyVersionId, version.PolicyCode, version.Version, contractRef, outOfRegion, trail));
    }
}
