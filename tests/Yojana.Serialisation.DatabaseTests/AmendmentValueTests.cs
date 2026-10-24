using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>
/// P2-T08b: finance review of AmendEntitlement above an amount, the amount derived from the policy's value per unit of
/// term or entered by the user when the policy states none, all in the tenant's currency.
/// </summary>
[Collection("serialisation host")]
public sealed class AmendmentValueTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Obligor = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly DateTime Invoice = new(2026, 9, 2, 8, 40, 0, DateTimeKind.Utc);
    private static readonly AmendmentCommitter Manager = new("manager-user", [YojanaRole.ServiceManager]);

    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<T> InScopeAsync<T>(TenantId tenant, Func<IServiceProvider, Task<T>> work)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await work(scope.ServiceProvider);
    }

    /// <summary>Goodwill reviewed by finance above 100 for up to 90 days, and always beyond (the default's day bound).</summary>
    private static AmendmentApprovalPolicy AmountBound(AmendmentApprovalPolicy policy) => policy with
    {
        Reasons = policy.Reasons.Select(r => r.ReasonCode == AmendmentReason.Goodwill
            ? r with { Tiers = [new(90, YojanaRole.ServiceManager, FinanceReviewAboveAmount: 100m), new(null, YojanaRole.ServiceManager, FinanceReview: true)] }
            : r).ToList(),
    };

    private async Task<Guid> PublishAsync(TenantId tenant, AmendmentValueTerm? value)
    {
        var code = "VAL-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, "base_warranty", EntitlementType.Manufacturer, new DateOnly(2026, 7, 31),
            PolicyValidatorTestsTerms.Standard24() with { AmendmentValue = value }, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        Ports.Register(new ItemWarrantyPolicy(item, "base_warranty", code, true));
        var marker = "AV" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, marker)])], Invoice.AddDays(-30), "test", Obligor, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        var created = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), commissioned.Lines[0].UnitId!.Value, "base_warranty", new DateOnly(2026, 9, 2), Obligor, Invoice, "service.desk",
            new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Invoice }, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, Country: "DE", SiteLocationId: Site,
            SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        Assert.Equal(new DateOnly(2028, 9, 15), created.EndDate);
        return created.EntitlementId;
    }

    private static AmendEntitlement Goodwill(Guid entitlement, DateOnly newEnd, string? financeReview = null, decimal? entered = null) =>
        new(Guid.NewGuid(), entitlement, AmendmentReason.Goodwill, Invoice.AddDays(30), "manager-user", NewEnd: newEnd, FinanceReviewRef: financeReview, Note: "ticket", CommittedBy: Manager, EnteredValue: entered);

    private async Task<CommandRefusedException> RefusedAsync(TenantId tenant, AmendEntitlement command) =>
        await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AmendEntitlementResult>(tenant, command));

    [DatabaseFact]
    public async Task Amounts_wait_for_the_tenant_currency_and_a_policy_value_must_be_in_it()
    {
        var tenant = await fixture.NewTenantWithoutCurrencyAsync("va");
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        Ports.RegisterZone(Site, "Europe/Berlin");

        // no currency yet: neither an amount bound nor a valued policy can be saved
        var unbounded = await InScopeAsync(tenant, sp => sp.GetRequiredService<AmendmentApprovalSettings>().GetAsync(CancellationToken.None));
        var noCurrency = await Assert.ThrowsAsync<CommandRefusedException>(() => InScopeAsync(tenant, async sp => { await sp.GetRequiredService<AmendmentApprovalSettings>().SetAsync(AmountBound(unbounded), CancellationToken.None); return 0; }));
        Assert.Equal("approval_policy_invalid", noCurrency.Rule);
        Assert.Contains("tenant currency", noCurrency.Message);
        var unpublished = await Assert.ThrowsAsync<CommandRefusedException>(() => PublishAsync(tenant, new AmendmentValueTerm(2.5m, "EUR", AmendmentValueTerm.PerDay)));
        Assert.Equal("tenant_currency_not_set_up", unpublished.Rule);

        Assert.Equal("EUR", await InScopeAsync(tenant, sp => sp.GetRequiredService<ITenantCurrency>().SetAsync(" eur ", CancellationToken.None)));
        var otherCurrency = await Assert.ThrowsAsync<CommandRefusedException>(() => PublishAsync(tenant, new AmendmentValueTerm(2.5m, "USD", AmendmentValueTerm.PerDay)));
        Assert.Equal("currency_not_home", otherCurrency.Rule);
        Assert.Equal("Amounts in this company are in EUR.", otherCurrency.Message);
        await InScopeAsync(tenant, async sp => { await sp.GetRequiredService<AmendmentApprovalSettings>().SetAsync(AmountBound(unbounded), CancellationToken.None); return 0; });
    }

    [DatabaseFact]
    public async Task The_value_comes_from_the_policy_or_the_user_and_finance_reviews_above_the_amount()
    {
        var tenant = await fixture.NewTenantWithoutCurrencyAsync("vb");
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        Ports.RegisterZone(Site, "Europe/Berlin");
        var before = await PublishAsync(tenant, null);

        // before any amount bound is set, goodwill is judged on days alone and asks for no value
        await InvokeAsync<AmendEntitlementResult>(tenant, Goodwill(before, new DateOnly(2028, 9, 20)));

        await InScopeAsync(tenant, sp => sp.GetRequiredService<ITenantCurrency>().SetAsync("EUR", CancellationToken.None));
        await InScopeAsync(tenant, async sp =>
        {
            var settings = sp.GetRequiredService<AmendmentApprovalSettings>();
            await settings.SetAsync(AmountBound(await settings.GetAsync(CancellationToken.None)), CancellationToken.None);
            return 0;
        });

        // 2.50 a day: 30 days are worth 75, under the bound; 50 days are worth 125, above it
        var perDay = await PublishAsync(tenant, new AmendmentValueTerm(2.5m, "EUR", AmendmentValueTerm.PerDay));
        var under = await InvokeAsync<AmendEntitlementResult>(tenant, Goodwill(perDay, new DateOnly(2028, 10, 15), entered: 9999m));
        Assert.Equal((75m, AmendmentValueSource.Policy), (under.Value, under.ValueSource));
        var above = await RefusedAsync(tenant, Goodwill(perDay, new DateOnly(2028, 12, 4)));
        Assert.Equal("finance_review_required", above.Rule);
        Assert.Contains("125", above.Message);
        var reviewed = await InvokeAsync<AmendEntitlementResult>(tenant, Goodwill(perDay, new DateOnly(2028, 12, 4), financeReview: "FIN-31"));
        Assert.Equal((125m, 50), (reviewed.Value, reviewed.DaysChanged));

        // the policy states no value: the user must enter one, and it is judged the same way
        var unvalued = await PublishAsync(tenant, null);
        Assert.Equal("value_required", (await RefusedAsync(tenant, Goodwill(unvalued, new DateOnly(2028, 10, 15)))).Rule);
        Assert.Equal("value_negative", (await RefusedAsync(tenant, Goodwill(unvalued, new DateOnly(2028, 10, 15), entered: -1m))).Rule);
        Assert.Equal("finance_review_required", (await RefusedAsync(tenant, Goodwill(unvalued, new DateOnly(2028, 10, 15), entered: 100.01m))).Rule);
        var entered = await InvokeAsync<AmendEntitlementResult>(tenant, Goodwill(unvalued, new DateOnly(2028, 10, 15), entered: 100m));
        Assert.Equal((100m, AmendmentValueSource.Entered), (entered.Value, entered.ValueSource));

        // beyond the day bound the days decide, whatever the value
        Assert.Equal("finance_review_required", (await RefusedAsync(tenant, Goodwill(perDay, new DateOnly(2029, 6, 4)))).Rule);
    }
}
