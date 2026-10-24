using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Currency;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Entitlements;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class CurrencyRulesTests(SerialisationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Money_threshold_locks_the_home_currency_with_its_usage_count()
    {
        var tenant = await fixture.NewTenantAsync("currency-threshold", importJurisdictionStarter: false);
        using var scope = fixture.ScopeFor(tenant);
        var currency = scope.ServiceProvider.GetRequiredService<ITenantCurrency>();
        Assert.True((await currency.GetStatusAsync(CancellationToken.None)).Changeable);
        Assert.Equal("USD", await currency.SetAsync("USD", CancellationToken.None));

        var settings = scope.ServiceProvider.GetRequiredService<AmendmentApprovalSettings>();
        var policy = await settings.GetAsync(CancellationToken.None);
        await settings.SetAsync(policy with
        {
            Reasons = policy.Reasons.Select(reason => reason.ReasonCode == AmendmentReason.Goodwill
                ? reason with { Tiers = [new(null, YojanaRole.ServiceManager, FinanceReviewAboveAmount: 500m)] }
                : reason).ToList(),
        }, CancellationToken.None);

        var status = await currency.GetStatusAsync(CancellationToken.None);
        Assert.False(status.Changeable);
        Assert.Contains(status.Usage, row => row == new CurrencyUsage("approval thresholds", 1));
        var locked = await Assert.ThrowsAsync<TenantCurrencyLockedException>(() =>
            currency.SetAsync("EUR", CancellationToken.None));
        Assert.Contains("1 approval thresholds", locked.Message);
    }

    [DatabaseFact]
    public async Task Upgrade_report_counts_each_kind_and_currency_without_rewriting_rows()
    {
        var tenant = await fixture.NewTenantAsync("currency-report", "USD", importJurisdictionStarter: false);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var at = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        for (var index = 0; index < 2; index++)
        {
            var id = Guid.NewGuid();
            db.JurisdictionRules.Add(new JurisdictionRule
            {
                TenantId = tenant, RuleId = id, RootRuleId = id, CommandId = Guid.NewGuid(), Version = 1,
                Kind = "minimum_plan_term", Scope = "any", BuyerClass = "consumer", PriceMin = 100m + index,
                Currency = "EUR", DateTested = "contract", EffectiveFrom = new DateOnly(2026, 1, 1), Value = "12",
                CreatedBy = "legal-admin", ApprovedBy = "legal-admin", CreatedAt = at,
            });
        }
        await db.SaveChangesAsync();

        var report = scope.ServiceProvider.GetRequiredService<ICurrencyUsageReport>();
        Assert.Equal(new OtherCurrencyUsage("jurisdiction price thresholds", "EUR", 2),
            Assert.Single(await report.CountOtherAsync("USD", CancellationToken.None)));
        Assert.Equal(2, await db.JurisdictionRules.CountAsync(row => row.TenantId == tenant && row.Currency == "EUR"));
    }
}
