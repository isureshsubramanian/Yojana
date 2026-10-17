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

/// <summary>P2-T08: AmendEntitlement under reason codes, approval by reason and days changed, and the reduction rule (cases 129, 140).</summary>
[Collection("serialisation host")]
public sealed class AmendEntitlementTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Obligor = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();
    private static readonly DateTime Invoice = new(2026, 9, 2, 8, 40, 0, DateTimeKind.Utc);
    private static readonly AmendmentCommitter Desk = new("desk-user", [YojanaRole.ServiceDesk]);
    private static readonly AmendmentCommitter Manager = new("manager-user", [YojanaRole.ServiceManager]);

    private InMemoryWarrantyPorts Ports => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    /// <summary>A tenant with the standard 24-month base policy and units each holding a base entitlement from 2026-09-16 to 2028-09-15.</summary>
    private async Task<(TenantId Tenant, List<Guid> Entitlements, List<Guid> Units)> SetUpAsync(string prefix, int units)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var code = "BAS-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, "base_warranty", EntitlementType.Manufacturer, new DateOnly(2026, 7, 31), PolicyValidatorTestsTerms.Standard24(), "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var item = Guid.NewGuid();
        Ports.Register(new ItemWarrantyPolicy(item, "base_warranty", code, true));
        Ports.RegisterZone(Site, "Europe/Berlin");
        var marker = prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            Enumerable.Range(1, units).Select(i => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, $"{marker}-{i}")])).ToList(), Invoice.AddDays(-30), "test", Obligor, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        var unitIds = commissioned.Lines.Select(l => l.UnitId!.Value).ToList();
        var entitlements = new List<Guid>();
        foreach (var unit in unitIds)
        {
            var created = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, "base_warranty", new DateOnly(2026, 9, 2), Obligor, Invoice, "service.desk",
                new Dictionary<string, DateTime> { [StartTrigger.Invoice] = Invoice }, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer, Country: "DE", SiteLocationId: Site,
                SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
            Assert.Equal((new DateOnly(2026, 9, 16), new DateOnly(2028, 9, 15)), (created.StartDate, created.EndDate));
            entitlements.Add(created.EntitlementId);
        }

        return (tenant, entitlements, unitIds);
    }

    private static AmendEntitlement Amend(Guid entitlement, string reason, AmendmentCommitter committer, DateOnly? newEnd = null, AmendedTerm? newTerm = null, CoverageTerms? coverage = null,
        DocumentRef? document = null, string? notification = null, string? financeReview = null, DateOnly? newStart = null) =>
        new(Guid.NewGuid(), entitlement, reason, Invoice.AddDays(30), "desk-user", newStart, newEnd, newTerm, coverage, document, notification, financeReview, "ticket", committer);

    private async Task<string> RuleAsync(TenantId tenant, AmendEntitlement command) =>
        (await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AmendEntitlementResult>(tenant, command))).Rule;

    [DatabaseFact]
    public async Task An_extension_is_approved_by_reason_and_days_kept_before_and_after_and_published_as_amended()
    {
        var (tenant, entitlements, units) = await SetUpAsync("aa", 2);
        var e = entitlements[0];

        var desk = Amend(e, AmendmentReason.Correction, Desk, newEnd: new DateOnly(2028, 10, 5));
        var first = await InvokeAsync<AmendEntitlementResult>(tenant, desk);
        Assert.Equal((new DateOnly(2028, 10, 5), 20, false, YojanaRole.ServiceDesk, "desk-user"), (first.EndDate, first.DaysChanged, first.Reduction, first.ApprovalLevel, first.ApprovedBy));
        Assert.Equal(new EntitlementFieldChange(AmendedField.EndDate, "2028-09-15", "2028-10-05"), Assert.Single(first.Changes));
        var replay = await InvokeAsync<AmendEntitlementResult>(tenant, desk);
        Assert.Equal((first.EventId, true, first.EndDate, first.Changes.Count), (replay.EventId, replay.Replayed, replay.EndDate, replay.Changes.Count));

        // 45 days is above the desk's 30: the desk is refused, the manager commits
        Assert.Equal("approval_required", await RuleAsync(tenant, Amend(e, AmendmentReason.Correction, Desk, newEnd: new DateOnly(2028, 11, 19))));
        var manager = await InvokeAsync<AmendEntitlementResult>(tenant, Amend(e, AmendmentReason.Correction, Manager, newEnd: new DateOnly(2028, 11, 19)));
        Assert.Equal((45, YojanaRole.ServiceManager), (manager.DaysChanged, manager.ApprovalLevel));

        // goodwill above the finance bound needs the finance review reference
        Assert.Equal("approval_required", await RuleAsync(tenant, Amend(e, AmendmentReason.Goodwill, Desk, newEnd: new DateOnly(2028, 12, 19))));
        Assert.Equal("finance_review_required", await RuleAsync(tenant, Amend(e, AmendmentReason.Goodwill, Manager, newEnd: new DateOnly(2029, 3, 19))));
        await InvokeAsync<AmendEntitlementResult>(tenant, Amend(e, AmendmentReason.Goodwill, Manager, newEnd: new DateOnly(2029, 3, 19), financeReview: "FIN-2207"));

        // a purchase of a longer term cites its document; the end follows the new term and keeps nothing it should not
        var other = entitlements[1];
        Assert.Equal("source_document_required", await RuleAsync(tenant, Amend(other, AmendmentReason.Purchase, Desk, newTerm: new AmendedTerm(36, DurationRule.Months))));
        var purchase = await InvokeAsync<AmendEntitlementResult>(tenant, Amend(other, AmendmentReason.Purchase, Desk, newTerm: new AmendedTerm(36, DurationRule.Months), document: new DocumentRef("invoice", Guid.NewGuid(), 2)));
        Assert.Equal((new DateOnly(2029, 9, 15), 365), (purchase.EndDate, purchase.DaysChanged));
        Assert.Equal([AmendedField.Term, AmendedField.EndDate], purchase.Changes.Select(c => c.Field));

        // a statutory extension is the system's, never a person's
        Assert.Equal("system_applied_only", await RuleAsync(tenant, Amend(other, AmendmentReason.StatutoryExtension, Manager, newEnd: new DateOnly(2030, 9, 15))));
        Assert.Equal("reason_not_system", await RuleAsync(tenant, Amend(other, AmendmentReason.Correction, AmendmentCommitter.System, newEnd: new DateOnly(2029, 9, 20))));
        var statutory = await InvokeAsync<AmendEntitlementResult>(tenant, Amend(other, AmendmentReason.StatutoryExtension, AmendmentCommitter.System, newEnd: new DateOnly(2030, 9, 15)));
        Assert.Equal("system", statutory.ApprovedBy);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var rows = await db.EntitlementAmendments.AsNoTracking().Where(a => a.EntitlementId == e).ToListAsync();
            Assert.Equal(3, rows.Count);
            var managed = Assert.Single(rows, r => r.AfterValue == "2028-11-19");
            Assert.Equal(("2028-10-05", AmendedField.EndDate, AmendmentReason.Correction, 45, "manager-user"), (managed.BeforeValue, managed.Field, managed.ReasonCode, managed.DaysChanged, managed.ApprovedBy));
            var row = await db.Entitlements.AsNoTracking().SingleAsync(x => x.EntitlementId == e);
            Assert.Equal((new DateOnly(2029, 3, 19), 185), (row.EndDate, row.EndAdjustmentDays));
            Assert.Equal(new DateOnly(2029, 3, 19), (await db.Current.AsNoTracking().SingleAsync(c => c.UnitId == units[0])).WarrantyEndMax);
            Assert.Equal(3, await db.Events.CountAsync(x => x.UnitId == units[0] && x.EventType == EntitlementEventType.Amended));
            Assert.All(await db.Corrections.Where(c => c.Command == CorrectionCommand.AmendEntitlement).ToListAsync(),
                c => Assert.Equal(CorrectionRootCause.Warranty, c.RootCauseCategory));
        }

        var published = await PublishedEventSink.WaitForAsync<EntitlementAmended>(x => x.Header.UnitId == units[0], 3, TimeSpan.FromSeconds(20));
        Assert.Equal(3, published.Count);
        var goodwill = Assert.Single(published, x => x.ReasonCode == AmendmentReason.Goodwill);
        Assert.Equal((e, 120, "manager-user", (string?)null), (goodwill.EntitlementId, goodwill.DaysChanged, goodwill.ApprovedBy, goodwill.CustomerNotificationRef));
        Assert.Equal(new EntitlementFieldChange(AmendedField.EndDate, "2028-11-19", "2029-03-19"), Assert.Single(goodwill.Changes));
        var purchased = Assert.Single(await PublishedEventSink.WaitForAsync<EntitlementAmended>(x => x.Header.UnitId == units[1] && x.ReasonCode == AmendmentReason.Purchase, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal("invoice", purchased.SourceDocument?.Type);
    }

    [DatabaseFact]
    public async Task A_reduction_needs_the_manager_a_notification_after_a_certificate_and_no_open_claims()
    {
        var (tenant, entitlements, _) = await SetUpAsync("ab", 4);
        var shorter = new DateOnly(2028, 9, 5);

        // case 140: a certificate is held (the default until certificates exist), so manager and notification
        Assert.Equal("approval_required", await RuleAsync(tenant, Amend(entitlements[0], AmendmentReason.Correction, Desk, newEnd: shorter)));
        Assert.Equal("customer_notification_required", await RuleAsync(tenant, Amend(entitlements[0], AmendmentReason.Correction, Manager, newEnd: shorter)));
        var reduced = await InvokeAsync<AmendEntitlementResult>(tenant, Amend(entitlements[0], AmendmentReason.Correction, Manager, newEnd: shorter, notification: "MAIL-5531"));
        Assert.Equal((true, -10, YojanaRole.ServiceManager), (reduced.Reduction, reduced.DaysChanged, reduced.ApprovalLevel));

        // no certificate issued yet: the manager still commits, without a notification
        Ports.RegisterCertificateIssued(entitlements[1], false);
        await InvokeAsync<AmendEntitlementResult>(tenant, Amend(entitlements[1], AmendmentReason.Correction, Manager, newTerm: new AmendedTerm(12, DurationRule.Months)));

        // open claims block any reduction, a shorter term or a smaller share alike
        Ports.RegisterOpenClaims(entitlements[2], true);
        Assert.Equal("open_claims", await RuleAsync(tenant, Amend(entitlements[2], AmendmentReason.Correction, Manager, newTerm: new AmendedTerm(12, DurationRule.Months), notification: "MAIL-1")));
        var lessLabour = new CoverageTerms(100, 50, 0, "return_to_base");
        Assert.Equal("open_claims", await RuleAsync(tenant, Amend(entitlements[2], AmendmentReason.Correction, Manager, coverage: lessLabour, notification: "MAIL-1")));
        // ... but not an extension
        await InvokeAsync<AmendEntitlementResult>(tenant, Amend(entitlements[2], AmendmentReason.Correction, Desk, newEnd: new DateOnly(2028, 9, 25)));

        // a coverage reduction is raised to the manager; a service-mode change is a supersession
        Assert.Equal("approval_required", await RuleAsync(tenant, Amend(entitlements[3], AmendmentReason.Correction, Desk, coverage: lessLabour, notification: "MAIL-2")));
        Assert.Equal("service_mode_change", await RuleAsync(tenant, Amend(entitlements[3], AmendmentReason.Correction, Manager, coverage: new CoverageTerms(100, 100, 0, "on_site"))));
        var coverage = await InvokeAsync<AmendEntitlementResult>(tenant, Amend(entitlements[3], AmendmentReason.Correction, Manager, coverage: lessLabour, notification: "MAIL-2"));
        Assert.Equal((AmendedField.Coverage, true, 0), (Assert.Single(coverage.Changes).Field, coverage.Reduction, coverage.DaysChanged));

        Assert.Equal("end_and_term_together", await RuleAsync(tenant, Amend(entitlements[3], AmendmentReason.Correction, Manager, newEnd: shorter, newTerm: new AmendedTerm(12, DurationRule.Months))));
        Assert.Equal("no_change", await RuleAsync(tenant, Amend(entitlements[3], AmendmentReason.Correction, Manager, coverage: lessLabour)));
        Assert.Equal("end_before_start", await RuleAsync(tenant, Amend(entitlements[3], AmendmentReason.Correction, Manager, newEnd: new DateOnly(2026, 9, 1), notification: "MAIL-3")));
        Assert.Equal("committer_required", await RuleAsync(tenant, Amend(entitlements[3], AmendmentReason.Correction, null!, newEnd: new DateOnly(2028, 9, 20))));
        Assert.Equal("reason_unknown", await RuleAsync(tenant, Amend(entitlements[3], "whim", Manager, newEnd: new DateOnly(2028, 9, 20))));

        await InvokeAsync<EntitlementChangeResult>(tenant, new CloseEntitlement(Guid.NewGuid(), entitlements[3], EntitlementClosedReason.IncludedInError, new DateOnly(2026, 10, 1), Invoice.AddDays(31), "x"));
        Assert.Equal("entitlement_closed", await RuleAsync(tenant, Amend(entitlements[3], AmendmentReason.Correction, Manager, newEnd: new DateOnly(2028, 9, 20))));
    }

    [DatabaseFact]
    public async Task An_amended_end_survives_a_later_rebase_and_tenant_thresholds_replace_the_defaults()
    {
        var (tenant, entitlements, _) = await SetUpAsync("ac", 2);
        await InvokeAsync<AmendEntitlementResult>(tenant, Amend(entitlements[0], AmendmentReason.Goodwill, Manager, newEnd: new DateOnly(2028, 10, 15)));
        // delivery on 10 September is stronger evidence than the invoice: 24 months from then, plus the 30 goodwill days
        var rebased = await InvokeAsync<EntitlementChangeResult>(tenant, new RebaseEntitlementStart(Guid.NewGuid(), entitlements[0], StartTrigger.Deliver, new DateTime(2026, 9, 10, 8, 0, 0, DateTimeKind.Utc), "POD-1", Invoice.AddDays(40), "x"));
        Assert.Equal((new DateOnly(2026, 9, 10), new DateOnly(2028, 10, 9)), (rebased.StartDate, rebased.EndDate));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<AmendmentApprovalSettings>();
            var policy = await settings.GetAsync(CancellationToken.None);
            var strict = policy with
            {
                Reasons = policy.Reasons.Select(r => r.ReasonCode == AmendmentReason.Correction ? r with { Tiers = [new(5, YojanaRole.ServiceDesk), new(null, YojanaRole.ServiceManager)] } : r).ToList(),
            };
            await settings.SetAsync(strict, CancellationToken.None);
            var invalid = strict with { Reasons = strict.Reasons.Where(r => r.ReasonCode != AmendmentReason.Goodwill).ToList() };
            Assert.Equal("approval_policy_invalid", (await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(invalid, CancellationToken.None))).Rule);
        }

        Assert.Equal("approval_required", await RuleAsync(tenant, Amend(entitlements[1], AmendmentReason.Correction, Desk, newEnd: new DateOnly(2028, 9, 25))));
        await InvokeAsync<AmendEntitlementResult>(tenant, Amend(entitlements[1], AmendmentReason.Correction, Desk, newEnd: new DateOnly(2028, 9, 20)));
    }
}
