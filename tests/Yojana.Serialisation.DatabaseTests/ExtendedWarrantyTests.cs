using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P2-T07: plans sold after the sale stack beside the base in add-on families, start at purchase or base expiry, and publish Entitlement.ExtendedSold.</summary>
[Collection("serialisation host")]
public sealed class ExtendedWarrantyTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Obligor = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly DateTime Invoice = new(2026, 9, 2, 8, 40, 0, DateTimeKind.Utc);

    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<PolicyDraftResult> PublishAsync(TenantId tenant, string family, string type, PolicyTerms terms)
    {
        var code = family.ToUpperInvariant()[..3] + "-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, family, type, new DateOnly(2026, 7, 31), terms, "test"));
        return await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
    }

    private static PolicyTerms Plan(int months, string trigger, EligibilityRule? eligibility) => PolicyValidatorTestsTerms.Standard24() with
    {
        Duration = new DurationRule(months, DurationRule.Months), Start = new StartRule(trigger, [], null), AssuranceOrService = AssuranceOrService.Service, Refurbished = [], Eligibility = eligibility,
    };

    private async Task<(TenantId Tenant, Guid Item, List<Guid> Units)> SetUpAsync(string prefix, int units)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("extended_term", "Extended term", true, AssuranceOrService.Service));
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("accidental_damage", "Accidental damage", true, AssuranceOrService.Service));
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("service_level", "Service level", false, AssuranceOrService.Service));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var baseCode = (await PublishAsync(tenant, "base_warranty", EntitlementType.Manufacturer, PolicyValidatorTestsTerms.Standard24())).PolicyCode;
        var extendedCode = (await PublishAsync(tenant, "extended_term", EntitlementType.Extended, Plan(12, StartTrigger.BaseExpiry, new EligibilityRule(EligibilityRule.WithinBaseCoverage, "base_warranty")))).PolicyCode;
        var item = Guid.NewGuid();
        Ports.Register(new ItemWarrantyPolicy(item, "base_warranty", baseCode, true));
        Ports.Register(new ItemWarrantyPolicy(item, "extended_term", extendedCode, true));
        Ports.RegisterZone(Site, "Europe/Berlin");
        var marker = prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            Enumerable.Range(1, units).Select(i => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{marker}-{i}")])).ToList(), Invoice.AddDays(-30), "test", Obligor, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return (tenant, item, commissioned.Lines.Select(l => l.UnitId!.Value).ToList());
    }

    private Task<CreateEntitlementResult> CreateBaseAsync(TenantId tenant, Guid unit) => InvokeAsync<CreateEntitlementResult>(tenant,
        new CreateEntitlement(Guid.NewGuid(), unit, "base_warranty", new DateOnly(2026, 9, 2), Obligor, Invoice, "service.desk", new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Invoice },
            OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, Country: "DE", SiteLocationId: Site, SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));

    private static AttachExtendedWarranty Attach(Guid unit, string family, DateTime purchasedAt, decimal price = 199.00m, Guid? version = null) =>
        new(Guid.NewGuid(), unit, family, DateOnly.FromDateTime(purchasedAt), Obligor, purchasedAt, price, "EUR", new DocumentRef("invoice", Guid.NewGuid(), 3), purchasedAt, "service.desk",
            PolicyVersionId: version, PayerPartyRef: Guid.NewGuid(), PayerPartyType: PartyType.Customer, Country: "DE", SiteLocationId: Site, CustomerPoRef: "PO-4471");

    private static async Task<string> RuleAsync(Func<Task> act) => (await Assert.ThrowsAsync<CommandRefusedException>(act)).Rule;

    [DatabaseFact]
    public async Task An_extended_term_starts_at_base_expiry_stacks_beside_the_base_and_is_published_as_sold_with_its_schedule()
    {
        var (tenant, item, units) = await SetUpAsync("xa", 1);
        var unit = units[0];
        var baseEntitlement = await CreateBaseAsync(tenant, unit);
        Assert.Equal(new DateOnly(2028, 9, 15), baseEntitlement.EndDate);

        var command = Attach(unit, "extended_term", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        var sold = await InvokeAsync<AttachExtendedWarrantyResult>(tenant, command);
        Assert.Equal((StartTrigger.BaseExpiry, new DateOnly(2028, 9, 16), new DateOnly(2029, 9, 15), baseEntitlement.EntitlementId, false),
            (sold.StartBasis, sold.StartDate, sold.EndDate, sold.BaseEntitlementId, sold.Replayed));
        Assert.Equal(199.00m, sold.RevenueSchedule.Sum(p => p.Amount));
        Assert.Equal(13, sold.RevenueSchedule.Count);

        var replay = await InvokeAsync<AttachExtendedWarrantyResult>(tenant, command);
        Assert.Equal((sold.EntitlementId, sold.EventId, true), (replay.EntitlementId, replay.EventId, replay.Replayed));
        Assert.Equal(sold.RevenueSchedule, replay.RevenueSchedule);
        Assert.Equal("family_occupied", await RuleAsync(() => InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(unit, "extended_term", new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Utc)))));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var row = await db.Entitlements.AsNoTracking().SingleAsync(e => e.EntitlementId == sold.EntitlementId);
            Assert.Equal((EntitlementType.Extended, AssuranceOrService.Service, EntitlementStatus.Active, (Guid?)null, command.PayerPartyRef, "PO-4471"),
                (row.Type, row.AssuranceOrService, row.Status, row.AccrualCohortId, row.PayerPartyRef, row.CustomerPoRef));
            var current = await db.Current.AsNoTracking().SingleAsync(c => c.UnitId == unit);
            Assert.Equal(new DateOnly(2029, 9, 15), current.WarrantyEndMax);
            Assert.Contains(sold.EntitlementId.ToString(), current.LiveEntitlementIds, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(baseEntitlement.EntitlementId.ToString(), current.LiveEntitlementIds, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, await db.Events.CountAsync(e => e.UnitId == unit && e.EventType == EntitlementEventType.ExtendedSold));
        }

        var timeout = TimeSpan.FromSeconds(20);
        var published = Assert.Single(await PublishedEventSink.WaitForAsync<EntitlementExtendedSold>(e => e.Header.UnitId == unit, 1, timeout));
        Assert.Equal((EntitlementExtendedSold.Schema, sold.EntitlementId, item, "extended_term", Obligor, command.PayerPartyRef, 199.00m, "EUR", 12),
            (published.Header.Schema, published.EntitlementId, published.ItemId, published.CoverageFamily, published.ObligorEntityId, published.PayerPartyRef, published.Price, published.Currency, published.TermMonths));
        Assert.Equal((new DateOnly(2028, 9, 16), new DateOnly(2029, 9, 15), command.SourceDocument), (published.StartDate, published.EndDate, published.SourceDocument));
        Assert.Equal(sold.RevenueSchedule, published.RevenueSchedule);
        // a plan is deferred revenue, not an accrual: no Entitlement.Created for it
        await PublishedEventSink.WaitForAsync<EntitlementCoverageChanged>(e => e.Header.UnitId == unit && e.Cause == EntitlementEventType.ExtendedSold, 1, timeout);
        Assert.DoesNotContain(PublishedEventSink.Received.OfType<EntitlementCreated>(), e => e.EntitlementId == sold.EntitlementId);
    }

    [DatabaseFact]
    public async Task An_add_on_starts_at_purchase_in_the_policy_zone_and_eligibility_family_and_price_rules_refuse_with_their_rule()
    {
        var (tenant, _, units) = await SetUpAsync("xb", 3);
        var accidental = await PublishAsync(tenant, "accidental_damage", EntitlementType.Extended, Plan(24, StartTrigger.PlanPurchase, new EligibilityRule(EligibilityRule.WithinDaysOfBaseStart, "base_warranty", 30)));
        var serviceLevel = await PublishAsync(tenant, "service_level", EntitlementType.Extended, Plan(12, StartTrigger.PlanPurchase, null));
        var assuranceAddOn = await PublishAsync(tenant, "accidental_damage", EntitlementType.Extended, Plan(12, StartTrigger.PlanPurchase, null) with { AssuranceOrService = AssuranceOrService.Assurance });
        foreach (var unit in units.Take(2)) await CreateBaseAsync(tenant, unit);

        // 23:30 UTC on 2026-10-15 is 16 October in Berlin, the 30th day after the base start of 16 September
        var lastDay = await InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(units[0], "accidental_damage", new DateTime(2026, 10, 15, 23, 30, 0, DateTimeKind.Utc), 89.50m, accidental.PolicyVersionId));
        Assert.Equal((StartTrigger.PlanPurchase, new DateOnly(2026, 10, 16), new DateOnly(2028, 10, 15)), (lastDay.StartBasis, lastDay.StartDate, lastDay.EndDate));
        Assert.Equal(89.50m, lastDay.RevenueSchedule.Sum(p => p.Amount));

        var late = new DateTime(2026, 10, 16, 23, 30, 0, DateTimeKind.Utc);
        Assert.Equal("purchase_window_passed", await RuleAsync(() => InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(units[1], "accidental_damage", late, version: accidental.PolicyVersionId))));
        Assert.Equal("base_not_live", await RuleAsync(() => InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(units[2], "accidental_damage", Invoice, version: accidental.PolicyVersionId))));
        Assert.Equal("base_not_live", await RuleAsync(() => InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(units[2], "extended_term", Invoice))));
        Assert.Equal("family_not_add_on", await RuleAsync(() => InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(units[1], "service_level", Invoice, version: serviceLevel.PolicyVersionId))));
        Assert.Equal("policy_not_separately_priced", await RuleAsync(() => InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(units[1], "accidental_damage", Invoice, version: assuranceAddOn.PolicyVersionId))));
        Assert.Equal("policy_family_differs", await RuleAsync(() => InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(units[1], "extended_term", Invoice, version: accidental.PolicyVersionId))));
        Assert.Equal("price_not_positive", await RuleAsync(() => InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(units[1], "extended_term", Invoice, 0m))));
        Assert.Equal("family_unknown", await RuleAsync(() => InvokeAsync<AttachExtendedWarrantyResult>(tenant, Attach(units[1], "battery_retention", Invoice))));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(0, await db.Entitlements.CountAsync(e => e.UnitId == units[1] && e.CoverageFamily != "base_warranty"));
    }
}
