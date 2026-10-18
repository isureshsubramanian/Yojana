using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Refurbishments;
using Yojana.Serialisation.Replacements;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T18a: ADR-0001 reference scenario, steps 0–8, through module command contracts.</summary>
[Collection("serialisation host")]
public sealed class ReferenceScenarioTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("a5100000-0000-0000-0000-000000000001");
    private static readonly Guid Warehouse = Guid.Parse("a5100000-0000-0000-0000-000000000002");
    private static readonly Guid Repair = Guid.Parse("a5100000-0000-0000-0000-000000000003");
    private static readonly Guid SiteA = Guid.Parse("a5100000-0000-0000-0000-000000000004");
    private static readonly Guid SiteB = Guid.Parse("a5100000-0000-0000-0000-000000000005");

    [DatabaseFact]
    public async Task Steps_0_to_8_reproduce_the_reference_end_state_without_rewriting_history()
    {
        var tenant = await fixture.NewTenantAsync("reference-p5", "EUR");
        var customerA = Guid.NewGuid();
        var customerB = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(
            new PartyLabel(customerA, "FICTIONAL-A", "Fictional A Services", PartyType.Customer));
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(
            new PartyLabel(customerB, "FICTIONAL-B", "Fictional B Operations", PartyType.Customer));
        var item = Guid.NewGuid();
        var policy = await ConfigurePolicyAsync(tenant, item);
        await ConfigureRefurbishmentAsync(tenant);

        // Step 0 — S1 and S2 are both real released units before either is used in a sale or replacement.
        var s1 = await CommissionAndReleaseAsync(tenant, item, "REF-S1", new(2026, 1, 10, 8, 0, 0, DateTimeKind.Utc));
        var s2 = await CommissionAndReleaseAsync(tenant, item, "REF-S2", new(2026, 1, 11, 8, 0, 0, DateTimeKind.Utc));

        // Step 1 — delivery to A creates E1 from the delivery fact.
        var delivery = new DocumentRef("delivery", Guid.NewGuid(), 1);
        var aOwnership = await InvokeAsync<OpenOwnershipResult>(tenant,
            new OpenOwnership(Guid.NewGuid(), s1, customerA, PartyType.Customer, EpisodeReason.Sale,
                new(2026, 2, 3, 12, 0, 0, DateTimeKind.Utc), "sales", Entity,
                Source: EpisodeSource.Delivery, Document: delivery));
        await InvokeAsync<ChangeCustodyResult>(tenant,
            new ChangeCustody(Guid.NewGuid(), s1, LocationKind.CustomerSite, EpisodeReason.Sale,
                new(2026, 2, 3, 12, 0, 0, DateTimeKind.Utc), "carrier", Entity, SiteA,
                customerA, PartyType.Customer, Document: new("delivery_confirmation", Guid.NewGuid())));
        var e1 = await InvokeAsync<CreateEntitlementResult>(tenant,
            new CreateEntitlement(Guid.NewGuid(), s1, CoverageFamilyCode.Base, new(2026, 2, 1), Entity,
                new(2026, 2, 3, 12, 0, 0, DateTimeKind.Utc), "warranty",
                new Dictionary<string, DateTime> { [StartTrigger.Deliver] = new(2026, 2, 3, 12, 0, 0, DateTimeKind.Utc) },
                policy, customerA, PartyType.Customer, Country: "DE", SiteLocationId: SiteA,
                StartEvidenceRef: $"{delivery.Type}:{delivery.Id:N}", SourceDocument: delivery));
        Assert.Equal((new DateOnly(2026, 2, 3), new DateOnly(2027, 2, 3)), (e1.StartDate, e1.EndDate));
        Assert.NotNull(e1.AccrualCohortId);

        // Step 2 — the consumer choice is captured on C1 and the RMA is independently requested and authorised.
        var claim = await InvokeAsync<SubmitClaimResult>(tenant,
            new SubmitClaim(Guid.NewGuid(), s1, null, new(2026, 9, 1), customerA, customerA, Entity, Entity,
                "DE", "EUR", [new ClaimLineInput(ClaimLineKind.Parts, CoverageFamilyCode.Base, 100m)],
                [new ClaimEvidenceInput(ClaimEvidenceKind.Attachment, "DEFECT-EVIDENCE-FICTIONAL")],
                ClaimRemedy.Replacement, "CONSUMER-CHOICE-FICTIONAL", new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc), "service.desk"));
        var adjudicated = await InvokeAsync<AdjudicateClaimResult>(tenant,
            new AdjudicateClaim(Guid.NewGuid(), claim.ClaimId, new(2026, 9, 2, 9, 0, 0, DateTimeKind.Utc), "service.manager"));
        Assert.Equal((ClaimStatus.Approved, ClaimCoverageSource.Warranty), (adjudicated.Status, adjudicated.CoverageSource));
        var requested = await InvokeAsync<RequestRmaResult>(tenant,
            new RequestRma(Guid.NewGuid(), new(2026, 9, 1), customerA, customerA, Entity, Entity, "DE", "EUR",
                [new RmaRequestLineInput(UnitId: s1, RequestedAmount: 100m)],
                new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc), "service.desk"));
        var authorised = await InvokeAsync<AuthoriseRmaResult>(tenant,
            new AuthoriseRma(Guid.NewGuid(), requested.RmaId, new(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc), "service.manager"));
        Assert.Equal(e1.EntitlementId, Assert.Single(authorised.Lines).EntitlementId);

        // Step 3 — business time is the physical receipt time, even though the command is recorded later.
        var received = await InvokeAsync<ReceiveReturnResult>(tenant,
            new ReceiveReturn(Guid.NewGuid(), requested.RmaId, Warehouse,
                [new ReturnReceiptLineInput(requested.LineIds[0], new("REF-S1"), ConditionGrades.Defective, "graded_receipt")],
                new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc), new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc),
                new(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc), "returns"));
        Assert.Equal(ReturnOutcome.Accepted, Assert.Single(received.Lines).Outcome);

        // Step 4 — the replacement document transfers E1 to E2; the received return closes the link immediately.
        var replacement = await InvokeAsync<CreateWarrantyReplacementResult>(tenant,
            new CreateWarrantyReplacement(Guid.NewGuid(), ReplacementSourceKind.Claim, claim.ClaimId, e1.EntitlementId, s2,
                ReplacementReason.WarrantyDefect, ReplacementPricePolicy.Zero, 0m, "EUR", "DE", true, false,
                new(2026, 9, 10, 9, 0, 0, DateTimeKind.Utc), Entity, "service.manager",
                ReplacementConditionAllowed: ReplacementConditionAllowed.NewOrRefurbished));
        var shipped = await InvokeAsync<ShipWarrantyReplacementResult>(tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), replacement.Document.Document.WarrantyReplacementId,
                new(2026, 9, 10), new(2026, 9, 10, 10, 0, 0, DateTimeKind.Utc), "warehouse"));
        Assert.Equal((ReplacementLinkState.ClosedReturned, "STD-90", new DateOnly(2027, 2, 3), true),
            (shipped.LinkState, shipped.PolicyApplied, shipped.ComputedEnd, shipped.AccrualSuppressed));
        var reAdjudicated = await PublishedEventSink.WaitForAsync<CoverageDecisionChanged>(
            changed => changed.WorkType == CoverageWorkType.Rma && changed.WorkId == requested.RmaId,
            1, TimeSpan.FromSeconds(20));
        Assert.NotEmpty(reAdjudicated);

        // Step 5 — the same RMA sends S1 to refurbishment; release is cycle 1 and keeps its identity.
        await InvokeAsync<DispositionRmaResult>(tenant,
            new DispositionRma(Guid.NewGuid(), requested.RmaId,
                [new RmaDispositionLineInput(requested.LineIds[0], RmaDisposition.Refurbish)],
                new(2026, 9, 15), new(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc), "returns"));
        Guid workOrderId;
        using (var scope = fixture.ScopeFor(tenant))
        {
            workOrderId = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().RefurbishmentWorkOrders
                .Where(row => row.SourceRmaLineId == requested.LineIds[0]).Select(row => row.WorkOrderId).SingleAsync();
        }
        await InvokeAsync<RecordSanitisationResult>(tenant,
            new RecordSanitisation(Guid.NewGuid(), s1, "Fictional Systems", "Reference Device", "MEDIA-REF-S1",
                "solid_state", SanitisationMethod.Purge, "Fictional Erase", "full verification", "SAN-REF-S1",
                new(2026, 9, 24, 9, 0, 0, DateTimeKind.Utc), "quality", Entity, Document: new("refurbishment", workOrderId)));
        await InvokeAsync<RecordTestResult>(tenant,
            new RecordTest(Guid.NewGuid(), s1, RefurbishmentTestKind.KeyFunctions, UnitTestResult.Pass, "{}",
                "fixture-fictional-reference", new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc), "quality", Entity,
                Document: new("refurbishment", workOrderId)));
        var released = await InvokeAsync<ReleaseRefurbishmentResult>(tenant,
            new ReleaseRefurbishment(Guid.NewGuid(), workOrderId, ConditionGrades.RefurbishedA,
                "graded_receipt_plus_postings", new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc),
                "quality", Entity, Warehouse, FCode: "F2", CCode: "C2"));
        Assert.Equal((s1, RefurbishmentIdentity.SameUnitSameSerial, 1),
            (released.ReleasedUnitId, released.IdentityPolicy, released.CycleNumber));

        // Step 6 — B's refurbished sale creates a fresh root E3 and applies the one-year EU floor.
        await InvokeAsync<OpenOwnershipResult>(tenant,
            new OpenOwnership(Guid.NewGuid(), s1, customerB, PartyType.Customer, EpisodeReason.Sale,
                new(2026, 10, 19, 10, 0, 0, DateTimeKind.Utc), "sales", Entity,
                Document: new("invoice", Guid.NewGuid(), 1)));
        await InvokeAsync<ChangeCustodyResult>(tenant,
            new ChangeCustody(Guid.NewGuid(), s1, LocationKind.CustomerSite, EpisodeReason.Sale,
                new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), "carrier", Entity, SiteB,
                customerB, PartyType.Customer, Document: new("delivery_confirmation", Guid.NewGuid())));
        var e3 = await InvokeAsync<CreateEntitlementResult>(tenant,
            new CreateEntitlement(Guid.NewGuid(), s1, CoverageFamilyCode.Base, new(2026, 10, 19), Entity,
                new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), "warranty",
                new Dictionary<string, DateTime> { [StartTrigger.Deliver] = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc) },
                policy, customerB, PartyType.Customer, ConditionGrade: ConditionGrades.RefurbishedA, Channel: "refurbished_direct",
                Country: "DE", SiteLocationId: SiteB, BuyerKind: RefurbishedBuyerKind.Consumer, RefurbisherPartyRef: Entity));
        Assert.Equal((new DateOnly(2026, 10, 20), new DateOnly(2027, 10, 20), true),
            (e3.StartDate, e3.EndDate, e3.FloorApplied));

        // Step 7 — the system applies the statutory repair extension once on E2's root chain.
        var extension = new AmendEntitlement(Guid.NewGuid(), shipped.TargetEntitlementId,
            AmendmentReason.StatutoryExtension, new(2026, 10, 21, 9, 0, 0, DateTimeKind.Utc), "system",
            NewEnd: new(2028, 2, 3), CommittedBy: AmendmentCommitter.System);
        await InvokeAsync<AmendEntitlementResult>(tenant, extension);
        var repeated = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<AmendEntitlementResult>(tenant,
            extension with { CommandId = Guid.NewGuid(), EventTime = extension.EventTime.AddHours(1), NewEnd = new(2029, 2, 3) }));
        Assert.Equal("statutory_extension_already_applied", repeated.Rule);

        // Step 8's privacy erasure contract is delivered by P7; P5 retains the episode, claim and link facts it will pseudonymise.
        // P6: v1 remains the birth snapshot, refurbishment is v2, and B receives a Copy of B's warranty snapshot.
        await WaitForCertificateCountAsync(tenant, s1, CertificateDocumentKind.ReleaseRecord, 2);
        await WaitForCertificateOwnerAsync(tenant, s1, "FICTIONAL-B");
        var selection = await InvokeAsync<ResolveSelectionSetResult>(tenant,
            new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByPastedList(["REF-S1"]), "quality"));
        var copied = await InvokeAsync<CertificateRunResult>(tenant, new ReprintCertificate(Guid.NewGuid(),
            selection.SelectionId, CertificateDocumentKind.WarrantyCertificate, "Customer B delivery pack", true, "ignored"));
        Assert.Single(copied.Documents);
        Assert.True(copied.Documents[0].Copy);
        using (var archive = new ZipArchive(new MemoryStream(copied.Archive), ZipArchiveMode.Read))
        {
            var htmlEntry = Assert.Single(archive.Entries, entry => entry.Name.EndsWith("-copy.html", StringComparison.Ordinal));
            using var reader = new StreamReader(htmlEntry.Open(), Encoding.UTF8);
            var html = await reader.ReadToEndAsync();
            Assert.Contains("Fictional B Operations", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Fictional A Services", html, StringComparison.Ordinal);
            Assert.Contains("COPY", html, StringComparison.Ordinal);
        }
        await AssertEndStateAsync(tenant, s1, s2, customerA, customerB, aOwnership.EpisodeId, e1, shipped, e3, workOrderId);
    }

    private async Task<Guid> ConfigurePolicyAsync(TenantId tenant, Guid item)
    {
        await InvokeAsync<CoverageFamilyRow>(tenant,
            new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant,
            new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            Duration = new DurationRule(12, DurationRule.Months, EndInclusive: false),
            ReplacementPolicyCode = "STD-90",
            Refurbished =
            [
                new RefurbishedTerm(null, ConditionGrades.RefurbishedA, "refurbished_direct", "EU", 6, 12,
                    RefurbishedWarrantyStatement.SellerRefurbished),
            ],
        };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant,
            new CreatePolicyDraft(Guid.NewGuid(), "REFERENCE-12", "Reference 12 months", CoverageFamilyCode.Base,
                EntitlementType.Manufacturer, new(2026, 1, 1), terms, "policy"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "policy"));
        var warranties = fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();
        warranties.Register(new ItemWarrantyPolicy(item, CoverageFamilyCode.Base, "REFERENCE-12", false));
        warranties.RegisterZone(SiteA, "Europe/Berlin");
        warranties.RegisterZone(SiteB, "Europe/Berlin");
        return draft.PolicyVersionId;
    }

    private async Task ConfigureRefurbishmentAsync(TenantId tenant)
    {
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<RefurbishmentPolicySettings>().SetAsync(
            new RefurbishmentPolicy(RefurbishmentIdentity.SameUnitSameSerial, 3, []), CancellationToken.None);
    }

    private async Task<Guid> CommissionAndReleaseAsync(TenantId tenant, Guid item, string serial, DateTime at)
    {
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant,
            new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Manufactured,
                [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], at, "production", Entity,
                LocationId: Warehouse, LocationKind: LocationKind.Warehouse));
        var unit = Assert.Single(commissioned.Lines).UnitId!.Value;
        await InvokeAsync<ReleaseUnitResult>(tenant,
            new ReleaseUnit(Guid.NewGuid(), unit, at.AddHours(1), "quality", Entity, "SIG-FICTIONAL"));
        return unit;
    }

    private async Task AssertEndStateAsync(TenantId tenant, Guid s1, Guid s2, Guid customerA, Guid customerB,
        Guid aOwnershipId, CreateEntitlementResult e1, ShipWarrantyReplacementResult shipped,
        CreateEntitlementResult e3, Guid workOrderId)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var ownerships = await db.Episodes.Where(row => row.UnitId == s1 && row.Kind == EpisodeKind.Ownership)
            .OrderBy(row => row.ValidFrom).ToListAsync();
        Assert.Equal(2, ownerships.Count(row => row.PartyType == PartyType.Customer));
        Assert.Equal([customerA, customerB], ownerships.Where(row => row.PartyType == PartyType.Customer)
            .Select(row => row.PartyRef!.Value).ToList());
        var s1Owner = Assert.Single(ownerships, row => row.ValidTo == null);
        var s1Holder = await db.Episodes.SingleAsync(row => row.UnitId == s1 && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((customerB, LocationKind.CustomerSite, customerB),
            (s1Owner.PartyRef, s1Holder.LocationKind, s1Holder.PartyRef));

        var rows = await db.Entitlements.Where(row => row.EntitlementId == e1.EntitlementId
            || row.EntitlementId == shipped.TargetEntitlementId || row.EntitlementId == e3.EntitlementId).ToListAsync();
        var first = rows.Single(row => row.EntitlementId == e1.EntitlementId);
        var inherited = rows.Single(row => row.EntitlementId == shipped.TargetEntitlementId);
        var refurbished = rows.Single(row => row.EntitlementId == e3.EntitlementId);
        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.Replaced, s2),
            (first.Status, first.ClosedReason, first.SuccessorUnitId));
        Assert.Equal((EntitlementStatus.Active, s2, e1.EntitlementId, e1.EntitlementId, (Guid?)null,
                new DateOnly(2026, 2, 3), new DateOnly(2028, 2, 3), true),
            (inherited.Status, inherited.UnitId, inherited.ParentEntitlementId, inherited.RootEntitlementId,
                inherited.AccrualCohortId, inherited.StartDate, inherited.EndDate, inherited.StatutoryExtensionApplied));
        Assert.Equal(1, await db.Entitlements.CountAsync(row => row.EntitlementId == e1.EntitlementId
            || row.RootEntitlementId == e1.EntitlementId) - 1);
        Assert.Equal((EntitlementStatus.Active, s1, customerB, (Guid?)null, (Guid?)null,
                new DateOnly(2027, 10, 20), true, 12, ConditionGrades.RefurbishedA),
            (refurbished.Status, refurbished.UnitId, refurbished.OwnerPartyRef, refurbished.ParentEntitlementId,
                refurbished.RootEntitlementId, refurbished.EndDate, refurbished.RefurbishedFloorApplied,
                refurbished.RefurbishedFloorMonths, refurbished.ConditionAtCreation));
        Assert.NotNull(refurbished.AccrualCohortId);
        Assert.NotNull(refurbished.RefurbishedMatchedRow);

        var s2Owner = await db.Episodes.SingleAsync(row => row.UnitId == s2 && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var s2Holder = await db.Episodes.SingleAsync(row => row.UnitId == s2 && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((customerA, LocationKind.CustomerSite, customerA),
            (s2Owner.PartyRef, s2Holder.LocationKind, s2Holder.PartyRef));
        Assert.Single(await db.Episodes.Where(row => row.UnitId == s2 && row.Kind == EpisodeKind.Ownership).ToListAsync());

        var link = await db.ReplacementLinks.SingleAsync(row => row.ReplacementLinkId == shipped.ReplacementLinkId);
        Assert.Equal(("STD-90", new DateOnly(2027, 2, 3), ReplacementLinkState.ClosedReturned),
            (link.PolicyApplied, link.ComputedEnd, link.State));
        var work = await db.RefurbishmentWorkOrders.SingleAsync(row => row.WorkOrderId == workOrderId);
        Assert.Equal((RefurbishmentStatus.Released, 1, s1), (work.Status, work.CycleNumber, work.ReleasedUnitId));
        Assert.Equal(1, await db.EntitlementAmendments.CountAsync(row => row.EntitlementId == inherited.EntitlementId
            && row.ReasonCode == AmendmentReason.StatutoryExtension));

        var releaseCertificates = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == s1
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).OrderBy(row => row.Version).ToListAsync();
        Assert.Equal([1, 2], releaseCertificates.Take(2).Select(row => row.Version));
        var releaseV1 = releaseCertificates.Single(row => row.Version == 1);
        var releaseV2 = releaseCertificates.Single(row => row.Version == 2);
        Assert.Equal(CertificateStatus.Superseded, releaseV1.Status);
        Assert.Equal(CanonicalJson.Hash(releaseV1.Payload), releaseV1.PayloadHash);
        using (var v1Payload = JsonDocument.Parse(releaseV1.Payload))
            Assert.Equal(JsonValueKind.Null, v1Payload.RootElement.GetProperty("refurbishment_cycle").ValueKind);
        using (var v2Payload = JsonDocument.Parse(releaseV2.Payload))
            Assert.Equal(1, v2Payload.RootElement.GetProperty("refurbishment_cycle").GetInt32());
        var currentWarranty = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == s1
                && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate && row.Status == CertificateStatus.Issued)
            .SingleAsync();
        Assert.Contains("Fictional B Operations", currentWarranty.OwnerSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("Fictional A Services", currentWarranty.OwnerSnapshot, StringComparison.Ordinal);
        Assert.Single(await db.ReprintLogs.Where(row => row.CertificateId == currentWarranty.CertificateId).ToListAsync());

        Assert.Single(await db.Identifiers.Where(row => row.UnitId == s1 && row.Type == IdentifierType.InternalSerial).ToListAsync());
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == s2 && row.Type == IdentifierType.InternalSerial).ToListAsync());
        Assert.True(await db.Entitlements.TemporalAll().CountAsync(row => row.EntitlementId == e1.EntitlementId) >= 2);
        Assert.True(await db.Entitlements.TemporalAll().CountAsync(row => row.EntitlementId == inherited.EntitlementId) >= 2);
        Assert.True(await db.Episodes.TemporalAll().CountAsync(row => row.EpisodeId == aOwnershipId) >= 2);

        var s1Events = await db.Events.Where(row => row.UnitId == s1).Select(row => row.EventType).ToListAsync();
        var s2Events = await db.Events.Where(row => row.UnitId == s2).Select(row => row.EventType).ToListAsync();
        Assert.Contains(UnitEventType.Released, s1Events);
        Assert.Contains(RmaEventType.Requested, s1Events);
        Assert.Contains(RmaEventType.Received, s1Events);
        Assert.Contains(ReplacementEventType.Shipped, s1Events);
        Assert.Contains(RefurbishmentEventType.Released, s1Events);
        Assert.Contains(ReplacementEventType.Shipped, s2Events);
        Assert.Contains(EntitlementEventType.Transferred, s2Events);
        Assert.Contains(EntitlementEventType.Amended, s2Events);
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task WaitForCertificateCountAsync(TenantId tenant, Guid unitId, string kind, int count)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            if (await db.CertificateInstances.CountAsync(row => row.UnitId == unitId && row.DocumentKind == kind) >= count) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Certificate chain {kind} for {unitId} did not reach {count} versions.");
    }

    private async Task WaitForCertificateOwnerAsync(TenantId tenant, Guid unitId, string ownerCode)
    {
        var until = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            if (await db.CertificateInstances.AnyAsync(row => row.UnitId == unitId
                    && row.DocumentKind == CertificateDocumentKind.WarrantyCertificate
                    && row.Status == CertificateStatus.Issued && row.OwnerSnapshot != null
                    && row.OwnerSnapshot.Contains(ownerCode))) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Warranty certificate for {unitId} did not reach owner {ownerCode}.");
    }
}
