using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P2-T09: coverage changes replace one live entitlement in a family atomically (case 123, ticket T25).</summary>
[Collection("serialisation host")]
public sealed class SupersedeEntitlementTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Obligor = Guid.NewGuid();
    private static readonly DateTime Invoice = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly AmendmentCommitter Manager = new("manager-user", [YojanaRole.ServiceManager]);
    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private static PolicyTerms Terms(int months, string mode, decimal pct = 100) => new(
        new DurationRule(months, DurationRule.Months), DurationBasis.Time, [], new StartRule(StartTrigger.Invoice, [], null),
        new CoverageTerms(pct, pct, pct, mode), [], ["WORLD"], Transferability.Yes, "full", null, [], null, AssuranceOrService.Assurance, PolicyTerms.TimeZoneUtc);

    private async Task<PolicyDraftResult> PolicyAsync(TenantId tenant, string code, string family, int months, string mode, decimal pct = 100)
    {
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, family, EntitlementType.Manufacturer, new DateOnly(2026, 1, 1), Terms(months, mode, pct), "test"));
        return await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
    }

    private async Task<(TenantId Tenant, Guid Unit, CreateEntitlementResult Old, PolicyDraftResult Standard, PolicyDraftResult Express, PolicyDraftResult Other, PolicyDraftResult Reduced)> SetUpAsync(string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("service_level", "Service level", false, AssuranceOrService.Assurance));
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("other_cover", "Other cover", false, AssuranceOrService.Assurance));
        var standard = await PolicyAsync(tenant, "STD-" + Guid.NewGuid().ToString("N")[..5], "service_level", 12, "on_site");
        var express = await PolicyAsync(tenant, "EXP-" + Guid.NewGuid().ToString("N")[..5], "service_level", 36, "depot");
        var other = await PolicyAsync(tenant, "OTH-" + Guid.NewGuid().ToString("N")[..5], "other_cover", 36, "depot");
        var reduced = await PolicyAsync(tenant, "RED-" + Guid.NewGuid().ToString("N")[..5], "service_level", 6, "depot", 50);
        var item = Guid.NewGuid();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "SUP-" + Guid.NewGuid().ToString("N")[..10])])], Invoice, "test", Obligor, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        var unit = commissioned.Lines.Single().UnitId!.Value;
        var old = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, "service_level", new DateOnly(2026, 9, 1), Obligor, Invoice, "desk",
            new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Invoice }, standard.PolicyVersionId, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer,
            PayerPartyRef: Guid.NewGuid(), PayerPartyType: PartyType.Customer, SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        return (tenant, unit, old, standard, express, other, reduced);
    }

    private static SupersedeEntitlement Command(Guid entitlement, Guid policy, DateOnly effective, Guid? id = null, string reason = AmendmentReason.Correction, string? notification = null, DocumentRef? document = null) =>
        new(id ?? Guid.NewGuid(), entitlement, effective, reason, Invoice.AddMonths(2), "manager-user", policy, SourceDocument: document, CustomerNotificationRef: notification, CommittedBy: Manager);

    private async Task<string> RuleAsync(TenantId tenant, SupersedeEntitlement command) =>
        (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<SupersedeEntitlementResult>(tenant, command))).Rule;

    [DatabaseFact]
    public async Task Active_cover_is_superseded_once_without_a_second_created_event_and_replay_is_idempotent()
    {
        var (tenant, unit, old, _, express, _, _) = await SetUpAsync("su");
        var command = Command(old.EntitlementId, express.PolicyVersionId, new DateOnly(2026, 12, 1));
        var result = await InvokeAsync<SupersedeEntitlementResult>(tenant, command);
        var replay = await InvokeAsync<SupersedeEntitlementResult>(tenant, command);
        Assert.Equal((result.EventId, result.NewEntitlementId, true), (replay.EventId, replay.NewEntitlementId, replay.Replayed));
        Assert.Equal((new DateOnly(2026, 12, 1), new DateOnly(2029, 11, 30)), (result.StartDate, result.EndDate));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var rows = await db.Entitlements.AsNoTracking().Where(e => e.UnitId == unit).OrderBy(e => e.StartDate).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.ReplacedByChange, new DateOnly(2026, 11, 30), command.EffectiveDate, result.NewEntitlementId),
                (rows[0].Status, rows[0].ClosedReason, rows[0].EndDate, rows[0].ClosedEffective, rows[0].SuccessorEntitlementId));
            Assert.Equal((old.EntitlementId, old.EntitlementId, rows[0].OwnerPartyRef, rows[0].ObligorEntityId, rows[0].PayerPartyRef, rows[0].AccrualCohortId),
                (rows[1].SupersedesEntitlementId, rows[1].RootEntitlementId, rows[1].OwnerPartyRef, rows[1].ObligorEntityId, rows[1].PayerPartyRef, rows[1].AccrualCohortId));
            Assert.Equal(1, await db.Events.CountAsync(e => e.UnitId == unit && e.EventType == EntitlementEventType.Superseded));
            Assert.Equal(1, await db.Events.CountAsync(e => e.UnitId == unit && e.EventType == EntitlementEventType.Created));
        }

        var published = Assert.Single(await PublishedEventSink.WaitForAsync<EntitlementSuperseded>(e => e.Header.UnitId == unit, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((old.EntitlementId, result.NewEntitlementId, express.PolicyVersionId), (published.SupersededEntitlementId, published.NewEntitlementId, published.NewPolicyVersionId));
    }

    [DatabaseFact]
    public async Task Family_version_dates_and_reduction_guards_refuse_invalid_supersessions()
    {
        var (tenant, _, old, standard, express, other, reduced) = await SetUpAsync("sr");
        Assert.Equal("policy_family_differs", await RuleAsync(tenant, Command(old.EntitlementId, other.PolicyVersionId, new DateOnly(2026, 12, 1))));
        Assert.Equal("no_change", await RuleAsync(tenant, Command(old.EntitlementId, standard.PolicyVersionId, new DateOnly(2026, 12, 1))));
        Assert.Equal("effective_date_out_of_range", await RuleAsync(tenant, Command(old.EntitlementId, express.PolicyVersionId, old.StartDate!.Value)));
        Assert.Equal("effective_date_out_of_range", await RuleAsync(tenant, Command(old.EntitlementId, express.PolicyVersionId, old.EndDate!.Value.AddDays(1))));
        Assert.Equal("customer_notification_required", await RuleAsync(tenant, Command(old.EntitlementId, reduced.PolicyVersionId, new DateOnly(2026, 12, 1))));
        Ports.RegisterOpenClaims(old.EntitlementId, true);
        Assert.Equal("open_claims", await RuleAsync(tenant, Command(old.EntitlementId, reduced.PolicyVersionId, new DateOnly(2026, 12, 1), notification: "NOTICE-1")));
    }
}
