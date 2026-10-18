using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Rmas;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T12/T15: service-part cover, the statutory repair extension and entitlement-driven re-adjudication.</summary>
[Collection("serialisation host")]
public sealed class CoverageTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task InvokeEventAsync(TenantId tenant, object message)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<IMessageBus>()
            .InvokeAsync(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None, Timeout);
    }

    [DatabaseFact]
    public async Task Service_part_end_uses_later_of_parent_end_and_configured_minimum()
    {
        var parentWins = await SetUpRepairAsync("cp-parent", new DurationRule(24, DurationRule.Months), 90);
        var minimumWins = await SetUpRepairAsync("cp-min", new DurationRule(60, DurationRule.Days), 120, claimRepair: true);

        await InstallAsync(parentWins);
        await InstallAsync(minimumWins);

        var inherited = await WaitForPartEntitlementAsync(parentWins);
        var minimum = await WaitForPartEntitlementAsync(minimumWins);
        Assert.Equal(parentWins.ParentEntitlementEnd, inherited.EndDate);
        Assert.Equal(DateOnly.FromDateTime(At).AddDays(120), minimum.EndDate);
        Assert.Equal((parentWins.ParentEntitlementId, parentWins.ParentUnitId, parentWins.LinkId),
            (inherited.ServiceParentEntitlementId, inherited.InstalledParentUnitId, inherited.InstallationLinkId));
        Assert.Equal((EntitlementProvenance.ServicePart, StartTrigger.Install, DateOnly.FromDateTime(At)),
            (inherited.Provenance, inherited.StartBasis, inherited.StartDate));

        await AssertWholeUnitConsistencyAsync(parentWins);
        await AssertWholeUnitConsistencyAsync(minimumWins);
    }

    [DatabaseFact]
    public async Task Statutory_repair_extension_is_applied_once_per_root_chain()
    {
        var setup = await SetUpRepairAsync("cp-stat", new DurationRule(24, DurationRule.Months), 90,
            coverageFamily: "statutory", entitlementType: EntitlementType.Statutory,
            replacementRule: ReplacementRule.StatutoryExtensionOnce, extensionMonths: 12);
        var secondPart = await CommissionAsync(setup.Tenant, Guid.NewGuid(), "CPSTAT-P2", At.AddDays(-30));

        await InstallAsync(setup);
        var firstPartCover = await WaitForPartEntitlementAsync(setup);
        await WaitUntilAsync(setup.Tenant, db => db.Entitlements.AnyAsync(e => e.EntitlementId == setup.ParentEntitlementId
            && e.StatutoryExtensionApplied));

        var second = setup with { PartUnitId = secondPart, PartItemId = await ItemIdAsync(setup.Tenant, secondPart), LinkId = Guid.Empty };
        await InstallAsync(second);
        var secondPartCover = await WaitForPartEntitlementAsync(second);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var parent = await db.Entitlements.SingleAsync(e => e.EntitlementId == setup.ParentEntitlementId);
        Assert.True(parent.StatutoryExtensionApplied);
        Assert.Equal(setup.ParentEntitlementEnd!.Value.AddMonths(12), parent.EndDate);
        Assert.Equal(parent.EndDate, firstPartCover.EndDate);
        Assert.Equal(parent.EndDate, secondPartCover.EndDate);
        Assert.Equal(1, await db.EntitlementAmendments.CountAsync(a => a.EntitlementId == setup.ParentEntitlementId
            && a.ReasonCode == AmendmentReason.StatutoryExtension));

        await AssertWholeUnitConsistencyAsync(setup);
        await AssertWholeUnitConsistencyAsync(second);
    }

    [DatabaseFact]
    public async Task Coverage_change_re_adjudicates_open_rma_and_claim_but_not_closed_work()
    {
        var setup = await SetUpCoveredUnitAsync("cp-readj", new DurationRule(24, DurationRule.Months));
        var work = await SeedCoverageWorkAsync(setup);
        var changed = CoverageChanged(setup, Guid.NewGuid());

        await InvokeEventAsync(setup.Tenant, changed);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var openRma = await db.RmaLines.SingleAsync(l => l.RmaLineId == work.OpenRmaLineId);
        var closedRma = await db.RmaLines.SingleAsync(l => l.RmaLineId == work.ClosedRmaLineId);
        var openClaim = await db.ClaimLines.SingleAsync(l => l.ClaimLineId == work.OpenClaimLineId);
        var closedClaim = await db.ClaimLines.SingleAsync(l => l.ClaimLineId == work.ClosedClaimLineId);
        Assert.Equal((100m, 100m, ClaimCoverageSource.Warranty, setup.EntitlementId),
            (openRma.CoveragePct, openRma.ApprovedAmount, openRma.CoverageSource, openRma.EntitlementId));
        Assert.Equal((100m, 100m, ClaimCoverageSource.Warranty, setup.EntitlementId),
            (openClaim.CoveragePct, openClaim.ApprovedAmount, openClaim.CoverageSource, openClaim.EntitlementId));
        Assert.NotNull(openRma.CoverageDecisionRef);
        Assert.NotNull(openClaim.CoverageDecisionRef);
        Assert.Equal((0m, 0m, ClaimCoverageSource.Chargeable, (Guid?)null),
            (closedRma.CoveragePct, closedRma.ApprovedAmount, closedRma.CoverageSource, closedRma.CoverageDecisionRef));
        Assert.Equal((0m, 0m, ClaimCoverageSource.Chargeable, (Guid?)null),
            (closedClaim.CoveragePct, closedClaim.ApprovedAmount, closedClaim.CoverageSource, closedClaim.CoverageDecisionRef));

        var history = await scope.ServiceProvider.GetRequiredService<CoverageDecisionQueries>()
            .ForUnitAsync(setup.UnitId, CancellationToken.None);
        // Only this event's decisions: the entitlement created in setup publishes its own CoverageChanged, which the
        // outbox may deliver after the work exists and which then adds legitimate decisions of its own.
        var fromThisEvent = history.Items.Where(decision => decision.SourceEventId == changed.Header.EventId).ToList();
        Assert.Equal(2, fromThisEvent.Count);
        Assert.All(fromThisEvent, decision =>
        {
            Assert.Equal(CoverageDecisionReason.EntitlementChanged, decision.Reason);
            Assert.Equal(0m, decision.Before.ApprovedAmount);
            Assert.Equal(100m, decision.After.ApprovedAmount);
        });
    }

    [DatabaseFact]
    public async Task Coverage_change_replay_does_not_create_a_second_decision()
    {
        var setup = await SetUpCoveredUnitAsync("cp-replay", new DurationRule(24, DurationRule.Months));
        var work = await SeedCoverageWorkAsync(setup, includeClosed: false, includeRma: false);
        var changed = CoverageChanged(setup, Guid.NewGuid());

        await InvokeEventAsync(setup.Tenant, changed);
        await InvokeEventAsync(setup.Tenant, changed);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(1, await db.CoverageDecisions.CountAsync(d => d.SourceEventId == changed.Header.EventId));
        // Count only the decision events this replayed source event produced: creating the entitlement in setup publishes
        // its own Entitlement.CoverageChanged, and when the outbox delivers it after the claim exists it legitimately
        // adds a decision of its own (that was this test's intermittent failure).
        var fromThisEvent = await db.CoverageDecisions.Where(d => d.SourceEventId == changed.Header.EventId)
            .Select(d => d.CoverageDecisionId.ToString()).ToListAsync();
        var claimDecisionEvents = await db.Events.Where(e => e.UnitId == setup.UnitId
            && e.EventType == CoverageEventType.DecisionChanged && e.DocumentId == work.OpenClaimId).Select(e => e.Attributes ?? "").ToListAsync();
        Assert.Equal(1, claimDecisionEvents.Count(attributes => fromThisEvent.Any(id => attributes.Contains(id, StringComparison.OrdinalIgnoreCase))));
    }

    [DatabaseFact]
    public void Coverage_re_check_retries_a_lost_concurrency_race_instead_of_dropping_it()
    {
        // A background re-check that loses an optimistic-concurrency race to a user command must retry; before this
        // rule the message went straight to the error queue and the open work kept its old decision. Retrying is safe
        // because a replay adds no second decision (Coverage_change_replay_does_not_create_a_second_decision).
        var chain = fixture.Services.GetRequiredService<HandlerGraph>().ChainFor<EntitlementCoverageChanged>();
        Assert.NotNull(chain);
        Assert.Contains(chain.Failures, rule => rule.Match.Matches(new DbUpdateConcurrencyException("lost race")));
    }

    private async Task<CoveredUnit> SetUpCoveredUnitAsync(string prefix, DurationRule duration,
        string coverageFamily = CoverageFamilyCode.Base, string entitlementType = EntitlementType.Manufacturer,
        string replacementRule = ReplacementRule.MaxRemainingOrMinDays, int? extensionMonths = null)
    {
        var tenant = await fixture.NewTenantAsync(prefix, "EUR");
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(coverageFamily, coverageFamily, false, AssuranceOrService.Assurance));
        var replacementCode = "CP-" + Guid.NewGuid().ToString("N")[..8];
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy(replacementCode, replacementRule,
            replacementRule == ReplacementRule.MaxRemainingOrMinDays ? 90 : null, extensionMonths, null));
        var policyCode = "CP-" + Guid.NewGuid().ToString("N")[..8];
        var terms = PolicyValidatorTestsTerms.Standard24() with { Duration = duration, ReplacementPolicyCode = replacementCode };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), policyCode, policyCode,
            coverageFamily, entitlementType, new DateOnly(2026, 7, 31), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));

        var itemId = Guid.NewGuid();
        var unitId = await CommissionAsync(tenant, itemId, prefix.ToUpperInvariant()[..Math.Min(prefix.Length, 8)] + "-P", At.AddDays(-40));
        var customer = Guid.NewGuid();
        var ownership = await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), unitId, customer,
            PartyType.Customer, EpisodeReason.Sale, At.AddDays(-31), "sales", Entity));
        var created = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unitId,
            coverageFamily, new DateOnly(2026, 8, 1), Entity, At.AddDays(-30), "service.desk",
            new Dictionary<string, DateTime> { [StartTrigger.Deliver] = At.AddDays(-30) }, draft.PolicyVersionId,
            customer, PartyType.Customer, Country: "DE", SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        return new(tenant, unitId, itemId, customer, ownership.EpisodeId, created.EntitlementId, created.EndDate,
            draft.PolicyVersionId, coverageFamily);
    }

    private async Task<RepairSetup> SetUpRepairAsync(string prefix, DurationRule duration, int minimumDays,
        string coverageFamily = CoverageFamilyCode.Base, string entitlementType = EntitlementType.Manufacturer,
        string replacementRule = ReplacementRule.MaxRemainingOrMinDays, int? extensionMonths = null,
        bool claimRepair = false)
    {
        var covered = await SetUpCoveredUnitAsync(prefix, duration, coverageFamily, entitlementType, replacementRule, extensionMonths);
        var repairLocation = Guid.NewGuid();
        await InvokeAsync<ChangeCustodyResult>(covered.Tenant, new ChangeCustody(Guid.NewGuid(), covered.UnitId,
            LocationKind.InRepair, EpisodeReason.Return, At.AddDays(-10), "service.desk", Entity, repairLocation));
        var partItem = Guid.NewGuid();
        var part = await CommissionAsync(covered.Tenant, partItem,
            prefix.ToUpperInvariant()[..Math.Min(prefix.Length, 8)] + "-C", At.AddDays(-20));
        using (var scope = fixture.ScopeFor(covered.Tenant))
        {
            await scope.ServiceProvider.GetRequiredService<ServicePartEntitlementSettings>().SetAsync(
                new ServicePartEntitlementPolicy(90, new Dictionary<Guid, int> { [partItem] = minimumDays }), CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var workId = Guid.NewGuid();
            if (claimRepair)
            {
                db.Claims.Add(Claim(covered, workId, "CLM-REPAIR", ClaimStatus.Approved));
                var line = ClaimLine(covered, workId, Guid.NewGuid());
                line.CoveragePct = 100m;
                line.ApprovedAmount = 100m;
                line.CoverageSource = ClaimCoverageSource.Warranty;
                line.EntitlementId = covered.EntitlementId;
                line.PolicyVersionId = covered.PolicyVersionId;
                db.ClaimLines.Add(line);
            }
            else
            {
                db.Rmas.Add(new Rma
                {
                    TenantId = covered.Tenant, RmaId = workId, CommandId = Guid.NewGuid(), RmaNumber = "RMA-" + Guid.NewGuid().ToString("N")[..8],
                    Stage = RmaStage.Closed, RequestDate = new DateOnly(2026, 8, 15), ReturningPartyRef = covered.Customer,
                    SettlementPartyRef = covered.Customer, ServiceEntityId = Entity, ObligorEntityId = Entity,
                    ServiceCountry = "DE", Currency = "EUR", RequestedAt = At.AddDays(-17), DispositionedAt = At.AddDays(-2),
                    ClosedAt = At.AddDays(-2), RequestedBy = "service.desk", LastActorRef = "technician", LastEventId = Guid.NewGuid(),
                });
                db.RmaLines.Add(new RmaLine
                {
                    TenantId = covered.Tenant, RmaLineId = Guid.NewGuid(), RmaId = workId, Sequence = 1,
                    RequestedUnitId = covered.UnitId, AuthorisedUnitId = covered.UnitId, OwnershipEpisodeId = covered.OwnershipEpisodeId,
                    OriginalSaleDocumentType = "invoice", OriginalSaleDocumentId = Guid.NewGuid(), OriginalSaleDocumentLine = 1,
                    CoverageFamily = coverageFamily, ClaimLineKind = ClaimLineKind.Parts, RequestedAmount = 100m,
                    CoveragePct = 100m, ApprovedAmount = 100m, CoverageSource = ClaimCoverageSource.Warranty,
                    EntitlementId = covered.EntitlementId, PolicyVersionId = covered.PolicyVersionId,
                    ReceivedUnitId = covered.UnitId, ReceiptOutcome = ReturnOutcome.Accepted, Disposition = RmaDisposition.RepairAndReturn,
                });
            }
            await db.SaveChangesAsync();
            return new(covered.Tenant, covered.UnitId, part, partItem, covered.Customer, repairLocation,
                covered.OwnershipEpisodeId, covered.EntitlementId, covered.EndDate,
                claimRepair ? CoverageWorkType.Claim : CoverageWorkType.Rma, workId);
        }
    }

    private async Task InstallAsync(RepairSetup setup)
    {
        var result = await InvokeAsync<RecordAssemblyResult>(setup.Tenant, new RecordAssembly(Guid.NewGuid(), setup.ParentUnitId,
            [new ChildInput(setup.PartUnitId, Slot: "service")], At, "technician", Entity,
            Document: new DocumentRef(setup.WorkType, setup.WorkId, 1)));
        setup.LinkId = Assert.Single(result.LinkIds);
    }

    private async Task<Entitlement> WaitForPartEntitlementAsync(RepairSetup setup)
    {
        await WaitUntilAsync(setup.Tenant, db => db.Entitlements.AnyAsync(e => e.UnitId == setup.PartUnitId
            && e.Provenance == EntitlementProvenance.ServicePart));
        using var scope = fixture.ScopeFor(setup.Tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking()
            .SingleAsync(e => e.UnitId == setup.PartUnitId && e.Provenance == EntitlementProvenance.ServicePart);
    }

    private async Task AssertWholeUnitConsistencyAsync(RepairSetup setup)
    {
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var parentOwner = await db.Episodes.SingleAsync(e => e.UnitId == setup.ParentUnitId && e.Kind == EpisodeKind.Ownership && e.ValidTo == null);
        var partOwner = await db.Episodes.SingleAsync(e => e.UnitId == setup.PartUnitId && e.Kind == EpisodeKind.Ownership && e.ValidTo == null);
        var parentCustody = await db.Episodes.SingleAsync(e => e.UnitId == setup.ParentUnitId && e.Kind == EpisodeKind.Custody && e.ValidTo == null);
        var partCustody = await db.Episodes.SingleAsync(e => e.UnitId == setup.PartUnitId && e.Kind == EpisodeKind.Custody && e.ValidTo == null);
        Assert.Equal((setup.Customer, PartyType.Customer), (parentOwner.PartyRef, parentOwner.PartyType));
        Assert.Equal((setup.Customer, PartyType.Customer), (partOwner.PartyRef, partOwner.PartyType));
        Assert.Equal((LocationKind.InRepair, setup.RepairLocation), (parentCustody.LocationKind, parentCustody.LocationId));
        Assert.Equal((LocationKind.InRepair, setup.RepairLocation), (partCustody.LocationKind, partCustody.LocationId));
        Assert.Equal(1, await db.Identifiers.CountAsync(i => i.UnitId == setup.ParentUnitId && i.ValidTo == null));
        Assert.Equal(1, await db.Identifiers.CountAsync(i => i.UnitId == setup.PartUnitId && i.ValidTo == null));
        var partEvents = await db.Events.Where(e => e.UnitId == setup.PartUnitId).Select(e => e.EventType).ToListAsync();
        Assert.Contains(UnitEventType.Installed, partEvents);
        Assert.Contains(UnitEventType.OwnershipOpened, partEvents);
        Assert.Contains(UnitEventType.CustodyChanged, partEvents);
        Assert.Contains(EntitlementEventType.Created, partEvents);
        Assert.Contains(await db.Events.Where(e => e.UnitId == setup.ParentUnitId).Select(e => e.EventType).ToListAsync(),
            type => type is UnitEventType.Assembled or UnitEventType.Reassembled);
    }

    private async Task<CoverageWork> SeedCoverageWorkAsync(CoveredUnit setup, bool includeClosed = true, bool includeRma = true)
    {
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var openClaimId = Guid.NewGuid();
        var openClaimLineId = Guid.NewGuid();
        db.Claims.Add(Claim(setup, openClaimId, "CLM-OPEN", ClaimStatus.Approved));
        db.ClaimLines.Add(ClaimLine(setup, openClaimId, openClaimLineId));

        var closedClaimId = Guid.Empty;
        var closedClaimLineId = Guid.Empty;
        if (includeClosed)
        {
            closedClaimId = Guid.NewGuid();
            closedClaimLineId = Guid.NewGuid();
            db.Claims.Add(Claim(setup, closedClaimId, "CLM-CLOSED", ClaimStatus.Settled));
            db.ClaimLines.Add(ClaimLine(setup, closedClaimId, closedClaimLineId));
        }

        var openRmaId = Guid.Empty;
        var openRmaLineId = Guid.Empty;
        var closedRmaId = Guid.Empty;
        var closedRmaLineId = Guid.Empty;
        if (includeRma)
        {
            openRmaId = Guid.NewGuid();
            openRmaLineId = Guid.NewGuid();
            db.Rmas.Add(Rma(setup, openRmaId, "RMA-OPEN", RmaStage.Authorised));
            db.RmaLines.Add(RmaLine(setup, openRmaId, openRmaLineId));
            if (includeClosed)
            {
                closedRmaId = Guid.NewGuid();
                closedRmaLineId = Guid.NewGuid();
                db.Rmas.Add(Rma(setup, closedRmaId, "RMA-CLOSED", RmaStage.Closed));
                db.RmaLines.Add(RmaLine(setup, closedRmaId, closedRmaLineId));
            }
        }

        await db.SaveChangesAsync();
        return new(openClaimId, openClaimLineId, closedClaimId, closedClaimLineId,
            openRmaId, openRmaLineId, closedRmaId, closedRmaLineId);
    }

    private static Claim Claim(CoveredUnit setup, Guid id, string number, string status) => new()
    {
        TenantId = setup.Tenant, ClaimId = id, ClaimNumber = number + "-" + id.ToString("N")[..6], CommandId = Guid.NewGuid(),
        UnitId = setup.UnitId, ClaimDate = DateOnly.FromDateTime(At), ClaimantPartyRef = setup.Customer,
        SettlementPartyRef = setup.Customer, ServiceEntityId = Entity, ObligorEntityId = Entity, ServiceCountry = "DE",
        Currency = "EUR", CoverageSource = ClaimCoverageSource.Chargeable, Remedy = ClaimRemedy.Repair, Status = status,
        EntitlementIds = "[]", SubmittedAt = At.AddHours(-2), AdjudicatedAt = At.AddHours(-1), SubmittedBy = "service.desk",
        LastEventId = Guid.NewGuid(), SettledAt = status == ClaimStatus.Settled ? At.AddMinutes(-30) : null,
    };

    private static ClaimLine ClaimLine(CoveredUnit setup, Guid claimId, Guid lineId) => new()
    {
        TenantId = setup.Tenant, ClaimLineId = lineId, ClaimId = claimId, UnitId = setup.UnitId, Sequence = 1,
        Kind = ClaimLineKind.Parts, CoverageFamily = setup.CoverageFamily, RequestedAmount = 100m,
        CoveragePct = 0, ApprovedAmount = 0, Measurements = "{}", CoverageSource = ClaimCoverageSource.Chargeable,
        DenialReason = "no_applicable_coverage",
    };

    private static Rma Rma(CoveredUnit setup, Guid id, string number, string stage) => new()
    {
        TenantId = setup.Tenant, RmaId = id, CommandId = Guid.NewGuid(), RmaNumber = number + "-" + id.ToString("N")[..6],
        Stage = stage, RequestDate = DateOnly.FromDateTime(At), ReturningPartyRef = setup.Customer,
        SettlementPartyRef = setup.Customer, ServiceEntityId = Entity, ObligorEntityId = Entity, ServiceCountry = "DE",
        Currency = "EUR", RequestedAt = At.AddHours(-2), AuthorisedAt = At.AddHours(-1), RequestedBy = "service.desk",
        LastEventId = Guid.NewGuid(), ClosedAt = stage == RmaStage.Closed ? At.AddMinutes(-30) : null,
    };

    private static RmaLine RmaLine(CoveredUnit setup, Guid rmaId, Guid lineId) => new()
    {
        TenantId = setup.Tenant, RmaLineId = lineId, RmaId = rmaId, Sequence = 1, RequestedUnitId = setup.UnitId,
        AuthorisedUnitId = setup.UnitId, OwnershipEpisodeId = setup.OwnershipEpisodeId, OriginalSaleDocumentType = "invoice",
        OriginalSaleDocumentId = Guid.NewGuid(), OriginalSaleDocumentLine = 1, CoverageFamily = setup.CoverageFamily,
        ClaimLineKind = ClaimLineKind.Parts, RequestedAmount = 100m, CoveragePct = 0, ApprovedAmount = 0,
        CoverageSource = ClaimCoverageSource.Chargeable, DenialReason = "no_applicable_coverage",
    };

    private static EntitlementCoverageChanged CoverageChanged(CoveredUnit setup, Guid eventId) => new(
        new PublishedEventHeader(EntitlementCoverageChanged.Schema, EntitlementCoverageChanged.Version, eventId,
            setup.UnitId, At, At, 1, Entity, "entitlement.engine", null), [], [setup.EntitlementId], null,
        setup.EndDate, EntitlementEventType.Amended);

    private async Task<Guid> CommissionAsync(TenantId tenant, Guid itemId, string serial, DateTime at)
    {
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), itemId,
            UnitOrigin.Purchased, [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])],
            at, "warehouse", Entity, LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return Assert.Single(commissioned.Lines).UnitId!.Value;
    }

    private async Task<Guid> ItemIdAsync(TenantId tenant, Guid unitId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Current
            .Where(c => c.UnitId == unitId).Select(c => c.ItemId).SingleAsync();
    }

    private async Task WaitUntilAsync(TenantId tenant, Func<SerialisationDbContext, Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = fixture.ScopeFor(tenant);
            if (await condition(scope.ServiceProvider.GetRequiredService<SerialisationDbContext>())) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("The expected coverage state was not committed before the test timeout.");
    }

    private sealed record CoveredUnit(TenantId Tenant, Guid UnitId, Guid ItemId, Guid Customer,
        Guid OwnershipEpisodeId, Guid EntitlementId, DateOnly? EndDate, Guid PolicyVersionId, string CoverageFamily);

    private sealed record CoverageWork(Guid OpenClaimId, Guid OpenClaimLineId, Guid ClosedClaimId, Guid ClosedClaimLineId,
        Guid OpenRmaId, Guid OpenRmaLineId, Guid ClosedRmaId, Guid ClosedRmaLineId);

    private sealed record RepairSetup(TenantId Tenant, Guid ParentUnitId, Guid PartUnitId, Guid PartItemId,
        Guid Customer, Guid RepairLocation, Guid OwnershipEpisodeId, Guid ParentEntitlementId,
        DateOnly? ParentEntitlementEnd, string WorkType, Guid WorkId)
    {
        public Guid LinkId { get; set; }
    }
}
