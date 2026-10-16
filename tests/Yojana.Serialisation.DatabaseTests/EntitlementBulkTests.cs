using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>
/// P2-T11: entitlement commands over a selection set — the preview is a rolled-back rehearsal of the command itself, the
/// execution is one transaction with one approval, and a refusal anywhere refuses the set.
/// </summary>
[Collection("serialisation host")]
public sealed class EntitlementBulkTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Obligor = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly DateTime Invoice = new(2026, 9, 2, 8, 40, 0, DateTimeKind.Utc);
    private static readonly BulkApproval Manager = new("manager-user", false, [YojanaRole.ServiceManager]);
    private static readonly BulkApproval Desk = new("desk-user", false, [YojanaRole.ServiceDesk]);

    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<T> CountAsync<T>(TenantId tenant, Func<SerialisationDbContext, Task<T>> read)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await read(scope.ServiceProvider.GetRequiredService<SerialisationDbContext>());
    }

    /// <summary>A tenant with a 24-month and a 36-month base policy, <paramref name="units"/> units, and base cover on the first <paramref name="covered"/>.</summary>
    private async Task<(TenantId Tenant, string Upgrade, List<Guid> Units, List<string> Serials)> SetUpAsync(string prefix, int units, int covered, int invoiceSpreadDays = 0)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var standard = "STD-" + Guid.NewGuid().ToString("N")[..6];
        var upgrade = "EXP-" + Guid.NewGuid().ToString("N")[..6];
        foreach (var (code, months) in new[] { (standard, 24), (upgrade, 36) })
        {
            var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2026, 7, 31),
                PolicyValidatorTestsTerms.Standard24() with { Duration = new DurationRule(months, DurationRule.Months) }, "test"));
            await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        }

        var item = Guid.NewGuid();
        Ports.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, standard, false));
        Ports.RegisterZone(Site, "Europe/Berlin");
        var marker = prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var serials = Enumerable.Range(1, units).Select(i => $"{marker}-{i:D3}").ToList();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            serials.Select(s => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, s)])).ToList(), Invoice.AddDays(-30), "test", Obligor, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        var unitIds = commissioned.Lines.Select(l => l.UnitId!.Value).ToList();
        for (var i = 0; i < covered; i++)
        {
            var invoice = Invoice.AddDays(i * invoiceSpreadDays);
            await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unitIds[i], CoverageFamilyCode.Base, DateOnly.FromDateTime(invoice), Obligor, invoice, "service.desk",
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = invoice }, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, Country: "DE", SiteLocationId: Site,
                SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        }

        return (tenant, upgrade, unitIds, serials);
    }

    private static BulkOperationArguments Arguments(string reason, Dictionary<string, string> extra) =>
        new(Invoice.AddDays(40), "manager-user", Obligor, new DocumentRef("sales_order", Guid.NewGuid()), reason, "ticket T25", extra);

    [DatabaseFact]
    public async Task Sixty_one_of_sixty_five_units_are_superseded_in_one_transaction_after_a_preview_that_writes_nothing()
    {
        var (tenant, upgrade, units, serials) = await SetUpAsync("eb", 65, 61);
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByPastedList(serials), "t"));
        Assert.Equal(65, selection.Resolved);
        var arguments = Arguments(AmendmentReason.Correction, new() { ["family"] = CoverageFamilyCode.Base, ["policy_code"] = upgrade, ["effective_date"] = "2026-10-01" });
        var before = await CountAsync(tenant, db => db.Entitlements.CountAsync(e => units.Contains(e.UnitId)));
        var eventsBefore = await CountAsync(tenant, db => db.Events.CountAsync(e => units.Contains(e.UnitId)));

        // the desk may not supersede: every unit that would change is blocked with the command's own rule
        var asDesk = await InvokeAsync<BulkPreviewResult>(tenant, new PreviewBulkOperation(selection.SelectionId, "entitlement.supersede", arguments, Take: 100, Approval: Desk));
        Assert.Equal((61, 4, 0), (asDesk.Block, asDesk.Skip, asDesk.Change));
        Assert.All(asDesk.Page.Where(o => o.Outcome == UnitOutcome.Outcomes.Block), o => Assert.Equal("approval_required", o.Reason));

        var preview = await InvokeAsync<BulkPreviewResult>(tenant, new PreviewBulkOperation(selection.SelectionId, "entitlement.supersede", arguments, Take: 100, Approval: Manager));
        Assert.Equal((61, 4, 0), (preview.Change, preview.Skip, preview.Block));
        Assert.All(preview.Page.Where(o => o.Outcome == UnitOutcome.Outcomes.Skip), o => Assert.Equal("no_live_entitlement_in_family", o.Reason));
        Assert.Equal(before, await CountAsync(tenant, db => db.Entitlements.CountAsync(e => units.Contains(e.UnitId))));
        Assert.Equal(eventsBefore, await CountAsync(tenant, db => db.Events.CountAsync(e => units.Contains(e.UnitId))));
        await Task.Delay(500);
        Assert.DoesNotContain(PublishedEventSink.Received.OfType<EntitlementSuperseded>(), e => units.Contains(e.Header.UnitId));

        var execute = new ExecuteBulkOperation(Guid.NewGuid(), selection.SelectionId, "entitlement.supersede", arguments, Manager);
        var result = await InvokeAsync<BulkExecutionResult>(tenant, execute);
        Assert.Equal((65, 61, 4, 0, false), (result.Total, result.Done, result.Skipped, result.Blocked, result.Replayed));
        var replay = await InvokeAsync<BulkExecutionResult>(tenant, execute);
        Assert.True(replay.Replayed);

        var live = await CountAsync(tenant, db => db.Entitlements.Where(e => units.Contains(e.UnitId) && e.Status != EntitlementStatus.Closed).ToListAsync());
        Assert.Equal(61, live.Count);
        Assert.All(live, e => Assert.NotNull(e.SupersedesEntitlementId));
        Assert.Equal(61, await CountAsync(tenant, db => db.Entitlements.CountAsync(e => units.Contains(e.UnitId) && e.ClosedReason == EntitlementClosedReason.ReplacedByChange)));
        var published = await PublishedEventSink.WaitForAsync<EntitlementSuperseded>(e => units.Contains(e.Header.UnitId), 61, TimeSpan.FromSeconds(30));
        Assert.Equal(61, published.Count);
    }

    [DatabaseFact]
    public async Task One_unit_needing_a_higher_approval_refuses_the_whole_set_until_that_approval_is_given()
    {
        // two units whose base cover ends 30 days apart; one new end extends the first by 20 days and shortens the second by 10
        var (tenant, _, units, serials) = await SetUpAsync("ec", 2, 2, invoiceSpreadDays: 30);
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByPastedList(serials), "t"));
        var ends = await CountAsync(tenant, db => db.Entitlements.Where(e => units.Contains(e.UnitId)).OrderBy(e => e.EndDate).Select(e => e.EndDate!.Value).ToListAsync());
        var newEnd = ends[0].AddDays(20);
        Assert.Equal(-10, newEnd.DayNumber - ends[1].DayNumber);
        var arguments = Arguments(AmendmentReason.Correction, new() { ["family"] = CoverageFamilyCode.Base, ["new_end"] = newEnd.ToString("yyyy-MM-dd"), ["customer_notification_ref"] = "MAIL-77" });

        var asDesk = await InvokeAsync<BulkPreviewResult>(tenant, new PreviewBulkOperation(selection.SelectionId, "entitlement.amend", arguments, Approval: Desk));
        Assert.Equal((1, 1), (asDesk.Change, asDesk.Block));
        Assert.Equal("approval_required", Assert.Single(asDesk.Page, o => o.Outcome == UnitOutcome.Outcomes.Block).Reason);

        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<BulkExecutionResult>(tenant, new ExecuteBulkOperation(Guid.NewGuid(), selection.SelectionId, "entitlement.amend", arguments, Desk)));
        Assert.Equal("approval_required", refused.Rule);
        Assert.Equal(0, await CountAsync(tenant, db => db.EntitlementAmendments.CountAsync(a => db.Entitlements.Any(e => e.EntitlementId == a.EntitlementId && units.Contains(e.UnitId)))));

        var done = await InvokeAsync<BulkExecutionResult>(tenant, new ExecuteBulkOperation(Guid.NewGuid(), selection.SelectionId, "entitlement.amend", arguments, Manager));
        Assert.Equal(2, done.Done);
        Assert.Equal([newEnd, newEnd], await CountAsync(tenant, db => db.Entitlements.Where(e => units.Contains(e.UnitId)).Select(e => e.EndDate!.Value).ToListAsync()));
    }

    [DatabaseFact]
    public async Task Approver_roles_come_from_the_approval_never_from_the_arguments()
    {
        var (tenant, upgrade, _, serials) = await SetUpAsync("ed", 1, 1);
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByPastedList(serials), "t"));
        var forged = Arguments(AmendmentReason.Correction, new()
        {
            ["family"] = CoverageFamilyCode.Base, ["policy_code"] = upgrade, ["effective_date"] = "2026-10-01",
            ["__approver_ref"] = "someone", ["__approver_roles"] = YojanaRole.ServiceManager,
        });
        var preview = await InvokeAsync<BulkPreviewResult>(tenant, new PreviewBulkOperation(selection.SelectionId, "entitlement.supersede", forged, Approval: Desk));
        Assert.Equal("approval_required", Assert.Single(preview.Page).Reason);
    }
}
