using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;

namespace Yojana.Serialisation.Currency;

/// <summary>
/// Serialisation's narrow contribution to the Platform-owned home-currency lock and legacy audit. This list is coupled
/// to the module's money-storing entities: adding another such entity without extending this probe would let a tenant
/// relabel its amounts. CurrencyUsageArchitectureTests documents and checks the same seam from the other side.
/// </summary>
public sealed class SerialisationCurrencyUsage(SerialisationDbContext db, ITenantContext tenant, ITenantSettings settings)
    : ICurrencyUsage, ICurrencyUsageReport
{
    public static readonly IReadOnlySet<string> CoveredEntityNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "Claim", "RecoveryClaim", "ReplacementLink", "WarrantyReplacementLine", "Entitlement", "Rma",
        "RefurbishmentPart", "RefurbishmentLabour", "RefurbishmentCost", "ReturnReason", "JurisdictionRule",
        "WarrantyPolicyVersion", "UnitCoverDeviationChange", "UnitCoverDeviationValue",
    };

    public async Task<IReadOnlyList<CurrencyUsage>> CountAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var result = new List<CurrencyUsage>();
        await AddAsync("claims", db.Claims.CountAsync(row => row.TenantId == tenantId, cancellationToken));
        await AddAsync("recovery claims", db.RecoveryClaims.CountAsync(row => row.TenantId == tenantId, cancellationToken));
        await AddAsync("replacement amounts", db.ReplacementLinks.CountAsync(row => row.TenantId == tenantId, cancellationToken));
        await AddAsync("replacement lines", db.WarrantyReplacementLines.CountAsync(row => row.TenantId == tenantId, cancellationToken));
        await AddAsync("entitlement prices", db.Entitlements.CountAsync(row => row.TenantId == tenantId && row.SalePrice != null, cancellationToken));
        await AddAsync("return requests", db.Rmas.CountAsync(row => row.TenantId == tenantId, cancellationToken));
        await AddAsync("refurbishment parts", db.RefurbishmentParts.CountAsync(row => row.TenantId == tenantId && row.UnitCost != null, cancellationToken));
        await AddAsync("refurbishment labour", db.RefurbishmentLabour.CountAsync(row => row.TenantId == tenantId, cancellationToken));
        await AddAsync("refurbishment costs", db.RefurbishmentCosts.CountAsync(row => row.TenantId == tenantId, cancellationToken));
        await AddAsync("restocking thresholds", db.ReturnReasons.CountAsync(row => row.TenantId == tenantId
            && row.RestockFeeValue != null && row.RestockFeeRule == RestockFeeRule.Fixed, cancellationToken));
        await AddAsync("jurisdiction price thresholds", db.JurisdictionRules.CountAsync(row => row.TenantId == tenantId
            && (row.PriceMin != null || row.PriceMax != null), cancellationToken));
        await AddAsync("policy values", db.PolicyVersions.CountAsync(row => row.TenantId == tenantId
            && row.AmendmentValue != null, cancellationToken));
        await AddAsync("cover history amounts", db.CoverDeviationChanges.CountAsync(row => row.TenantId == tenantId
            && row.Amount != null, cancellationToken));
        await AddAsync("cover history totals", db.CoverDeviationValues.CountAsync(row => row.TenantId == tenantId,
            cancellationToken));
        if (await HasAmendmentThresholdAsync(cancellationToken)) result.Add(new("approval thresholds", 1));
        return result;

        async Task AddAsync(string kind, Task<int> countTask)
        {
            var count = await countTask;
            if (count > 0) result.Add(new(kind, count));
        }
    }

    public async Task<IReadOnlyList<OtherCurrencyUsage>> CountOtherAsync(string homeCurrency,
        CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var rows = new List<OtherCurrencyUsage>();
        rows.AddRange(await GroupsAsync("claims", db.Claims.Where(row => row.TenantId == tenantId && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("recovery claims", db.RecoveryClaims.Where(row => row.TenantId == tenantId && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("replacement amounts", db.ReplacementLinks.Where(row => row.TenantId == tenantId && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("replacement lines", db.WarrantyReplacementLines.Where(row => row.TenantId == tenantId && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("entitlement prices", db.Entitlements.Where(row => row.TenantId == tenantId && row.SaleCurrency != null && row.SaleCurrency != homeCurrency)
            .GroupBy(row => row.SaleCurrency!).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("return requests", db.Rmas.Where(row => row.TenantId == tenantId && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("refurbishment parts", db.RefurbishmentParts.Where(row => row.TenantId == tenantId && row.Currency != null && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency!).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("refurbishment labour", db.RefurbishmentLabour.Where(row => row.TenantId == tenantId && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("refurbishment costs", db.RefurbishmentCosts.Where(row => row.TenantId == tenantId && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("jurisdiction price thresholds", db.JurisdictionRules.Where(row => row.TenantId == tenantId
                && row.Currency != null && row.Currency != homeCurrency && (row.PriceMin != null || row.PriceMax != null))
            .GroupBy(row => row.Currency!).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("cover history amounts", db.CoverDeviationChanges.Where(row => row.TenantId == tenantId
                && row.Amount != null && row.Currency != null && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency!).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));
        rows.AddRange(await GroupsAsync("cover history totals", db.CoverDeviationValues.Where(row => row.TenantId == tenantId
                && row.Currency != homeCurrency)
            .GroupBy(row => row.Currency).Select(group => new CurrencyCount(group.Key, group.LongCount())), cancellationToken));

        var policyValues = await db.PolicyVersions.AsNoTracking().Where(row => row.TenantId == tenantId && row.AmendmentValue != null)
            .Select(row => row.AmendmentValue!).ToListAsync(cancellationToken);
        rows.AddRange(policyValues.Select(value => JsonSerializer.Deserialize<AmendmentValueTerm>(value, LedgerJson.Options))
            .OfType<AmendmentValueTerm>().Where(value => value.Currency != homeCurrency)
            .GroupBy(value => value.Currency).Select(group => new OtherCurrencyUsage("policy values", group.Key, group.LongCount())));
        return rows.OrderBy(row => row.Kind, StringComparer.Ordinal).ThenBy(row => row.Currency, StringComparer.Ordinal).ToList();
    }

    private async Task<bool> HasAmendmentThresholdAsync(CancellationToken cancellationToken)
    {
        var text = await settings.GetAsync(AmendmentApprovalSettings.Key, cancellationToken);
        if (text is null) return false;
        var policy = JsonSerializer.Deserialize<AmendmentApprovalPolicy>(text, LedgerJson.Options);
        return policy?.Reasons.SelectMany(reason => reason.Tiers).Any(tier => tier.FinanceReviewAboveAmount is not null) == true;
    }

    private static async Task<IReadOnlyList<OtherCurrencyUsage>> GroupsAsync(string kind,
        IQueryable<CurrencyCount> query, CancellationToken cancellationToken) =>
        (await query.ToListAsync(cancellationToken)).Select(row => new OtherCurrencyUsage(kind, row.Currency, row.Count)).ToList();

    private sealed record CurrencyCount(string Currency, long Count);
}

public static class HomeCurrencyGuard
{
    public static async Task<string> GetRequiredAsync(ITenantCurrency currency,
        CancellationToken cancellationToken) =>
        await currency.GetAsync(cancellationToken)
            ?? throw new CommandRefusedException("tenant_currency_not_set_up",
                "Amounts wait until the company's home currency is configured.",
                "a tenant administrator chooses the home currency under Settings › Currencies");

    public static async Task<string> RequireAsync(ITenantCurrency currency, string supplied,
        CancellationToken cancellationToken)
    {
        var home = await GetRequiredAsync(currency, cancellationToken);
        var normalised = ITenantCurrency.Normalise(supplied);
        if (!string.Equals(normalised, home, StringComparison.Ordinal))
            throw new CommandRefusedException("currency_not_home", $"Amounts in this company are in {home}.",
                $"use {home} as the currency");
        return home;
    }
}
