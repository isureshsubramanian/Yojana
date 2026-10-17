using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Replacements;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T13: case 144 replacement correction preserves history and makes both units wholly consistent.</summary>
[Collection("serialisation host")]
public sealed class CorrectReplacementLinkTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly ClaimDate = new(2026, 9, 2);
    private static readonly DateOnly ShipDate = new(2026, 9, 5);
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly Guid Site = Guid.NewGuid();

    private InMemoryWarrantyPorts WarrantyPorts => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();
    private InMemoryStockUnitEventPort Stock => fixture.Services.GetRequiredService<InMemoryStockUnitEventPort>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Correction_swaps_whole_unit_state_preserves_policy_and_reason_and_replays_once()
    {
        var setup = await SetUpAsync("crlfull");
        var correctionId = Guid.NewGuid();
        var command = new CorrectReplacementLink(correctionId, setup.RecordedUnitId, setup.ActualUnitId,
            CorrectReplacementLinkReason.WrongUnitRecorded, At.AddDays(6), "service.manager", Entity,
            "Physical delivery evidence checked", new(correctionId, CorrectReplacementLinkReason.WrongUnitRecorded),
            new("service.manager", [YojanaRole.ServiceManager]));

        var first = await InvokeAsync<CorrectReplacementLinkResult>(setup.Tenant, command);
        var replay = await InvokeAsync<CorrectReplacementLinkResult>(setup.Tenant, command);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first, replay with { Replayed = false });
        Assert.Equal((setup.PolicyApplied, setup.ComputedEnd, ReplacementReason.WarrantyDefect),
            (first.PolicyApplied, first.ComputedEnd, first.ReplacementReason));
        Assert.True(first.AssetTagRepointed);
        Assert.Single(Stock.Corrections, row => row.CorrectionId == correctionId
            && row.QuantityDelta == 0 && row.RecordedUnitIds.SequenceEqual(new[] { setup.RecordedUnitId })
            && row.PhysicalUnitIds.SequenceEqual(new[] { setup.ActualUnitId }));

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var oldLink = await db.ReplacementLinks.SingleAsync(row => row.ReplacementLinkId == setup.RecordedLinkId);
            var newLink = await db.ReplacementLinks.SingleAsync(row => row.ReplacementLinkId == first.CorrectedReplacementLinkId);
            Assert.Equal(ReplacementLinkState.Corrected, oldLink.State);
            Assert.Equal(newLink.ReplacementLinkId, oldLink.CorrectedByLinkId);
            Assert.Equal((setup.RecordedLinkId, setup.ActualUnitId, setup.PolicyApplied, setup.ComputedEnd,
                    ReplacementReason.WarrantyDefect, correctionId),
                (newLink.PreviousReplacementLinkId, newLink.ReplacementUnitId, newLink.PolicyApplied,
                    newLink.ComputedEnd, newLink.ReplacementReason, newLink.CorrectionCommandId));

            var recordedCurrent = await db.Current.SingleAsync(row => row.UnitId == setup.RecordedUnitId);
            var actualCurrent = await db.Current.SingleAsync(row => row.UnitId == setup.ActualUnitId);
            Assert.Equal((Entity, LocationKind.Warehouse, setup.RecordedStockLocation),
                (recordedCurrent.OwnerPartyRef, recordedCurrent.LocationKind, recordedCurrent.LocationId));
            Assert.Equal((setup.Customer, setup.Customer, LocationKind.CustomerSite, (Guid?)null),
                (actualCurrent.OwnerPartyRef, actualCurrent.CustodyPartyRef, actualCurrent.LocationKind, actualCurrent.LocationId));

            var recordedOwner = await db.Episodes.SingleAsync(row => row.UnitId == setup.RecordedUnitId
                && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
            var actualOwner = await db.Episodes.SingleAsync(row => row.UnitId == setup.ActualUnitId
                && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
            Assert.Equal((Entity, PartyType.Us), (recordedOwner.PartyRef, recordedOwner.PartyType));
            Assert.Equal((setup.Customer, PartyType.Customer), (actualOwner.PartyRef, actualOwner.PartyType));

            var wrongEntitlement = await db.Entitlements.SingleAsync(row => row.EntitlementId == first.VoidedEntitlementId);
            var target = await db.Entitlements.SingleAsync(row => row.EntitlementId == first.TargetEntitlementId);
            var restored = await db.Entitlements.SingleAsync(row => row.EntitlementId == first.RestoredSourceEntitlementId);
            Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.VoidedDocumentCorrection, setup.RecordedUnitId),
                (wrongEntitlement.Status, wrongEntitlement.ClosedReason, wrongEntitlement.UnitId));
            Assert.Equal((EntitlementStatus.Active, setup.ActualUnitId, setup.ComputedEnd, newLink.ReplacementLinkId),
                (target.Status, target.UnitId, target.EndDate, target.ReplacementLinkId));
            Assert.Equal((EntitlementStatus.Closed, setup.OriginalUnitId), (restored.Status, restored.UnitId));

            var openTags = await db.Identifiers.Where(row => row.Type == IdentifierType.CustomerAssetTag && row.ValidTo == null).ToListAsync();
            Assert.Equal(setup.ActualUnitId, Assert.Single(openTags).UnitId);
            Assert.Equal(3, await db.Identifiers.CountAsync(row => row.Type == IdentifierType.InternalSerial && row.ValidTo == null));
            Assert.Single(await db.Corrections.Where(row => row.CorrectionId == correctionId).ToListAsync());

            var eventTypes = await db.Events.Where(row => row.UnitId == setup.RecordedUnitId || row.UnitId == setup.ActualUnitId)
                .Select(row => row.EventType).ToListAsync();
            Assert.Contains(ReplacementEventType.LinkCorrected, eventTypes);
            Assert.Equal(2, eventTypes.Count(type => type == ReplacementEventType.CertificateReissueRequested));
            Assert.Contains(ReplacementEventType.AssetTagPrintRequested, eventTypes);
            Assert.Contains(EntitlementEventType.Voided, eventTypes);
            Assert.True(eventTypes.Count(type => type == EntitlementEventType.Transferred) >= 2);
        }

        var corrected = Assert.Single(await PublishedEventSink.WaitForAsync<ReplacementLinkCorrected>(message =>
            message.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((setup.RecordedUnitId, setup.ActualUnitId, setup.PolicyApplied, setup.ComputedEnd),
            (corrected.RecordedReplacementUnitId, corrected.ActualReplacementUnitId, corrected.PolicyApplied, corrected.ComputedEnd));
        var certificates = await PublishedEventSink.WaitForAsync<CertificateReissueRequested>(message =>
            message.ReplacementLinkId == first.CorrectedReplacementLinkId, 2, TimeSpan.FromSeconds(20));
        Assert.Equal([setup.RecordedUnitId, setup.ActualUnitId], certificates.Select(message => message.Header.UnitId).Order().ToList());
        Assert.All(certificates, message => Assert.Equal(ReplacementCertificateDocumentKind.All, message.DocumentKinds));
    }

    [DatabaseFact]
    public async Task Preview_and_commit_require_service_manager_approval_without_mutating_the_link()
    {
        var setup = await SetUpAsync("crlapprove");
        var supervisor = new CorrectionCommitter("service.supervisor", [YojanaRole.ServiceSupervisor]);
        var preview = await InvokeAsync<CorrectionPreview>(setup.Tenant, new PreviewCorrectReplacementLink(
            setup.RecordedUnitId, setup.ActualUnitId, CorrectReplacementLinkReason.WrongUnitScannedAtShipping,
            At.AddDays(6), "service.supervisor", Entity, CommittedBy: supervisor));
        Assert.Equal("approval_required", preview.Refusal?.Rule);
        Assert.Equal(2, preview.Block);

        var commandId = Guid.NewGuid();
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<CorrectReplacementLinkResult>(setup.Tenant,
            new CorrectReplacementLink(commandId, setup.RecordedUnitId, setup.ActualUnitId,
                CorrectReplacementLinkReason.WrongUnitScannedAtShipping, At.AddDays(6), "service.supervisor", Entity,
                CommittedBy: supervisor)));
        Assert.Equal("approval_required", refusal.Rule);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(ReplacementLinkState.ClosedReturned,
            (await db.ReplacementLinks.SingleAsync(row => row.ReplacementLinkId == setup.RecordedLinkId)).State);
        Assert.Empty(await db.Corrections.Where(row => row.CorrectionId == commandId).ToListAsync());
    }

    internal async Task<Setup> SetUpAsync(string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var code = "CRL-" + Guid.NewGuid().ToString("N")[..7];
        var terms = PolicyValidatorTestsTerms.Standard24() with { Duration = new(1, DurationRule.Months), ReplacementPolicyCode = "STD-90" };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, "base_warranty",
            EntitlementType.Manufacturer, new DateOnly(2026, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));

        var originalItem = Guid.NewGuid();
        var replacementItem = Guid.NewGuid();
        WarrantyPorts.Register(new ItemWarrantyPolicy(originalItem, "base_warranty", code, true));
        WarrantyPorts.RegisterZone(Site, "Europe/Berlin");
        var marker = prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var original = await CommissionAsync(tenant, originalItem, marker + "-OLD", Guid.NewGuid(), At.AddDays(-10));
        var recordedLocation = Guid.NewGuid();
        var recorded = await CommissionAsync(tenant, replacementItem, marker + "-REC", recordedLocation, At.AddDays(-9));
        var actual = await CommissionAsync(tenant, replacementItem, marker + "-ACT", Guid.NewGuid(), At.AddDays(-8));
        var customer = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), original, customer, PartyType.Customer,
            EpisodeReason.Sale, At.AddDays(-7), "test", Entity));
        await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), original,
            new(IdentifierType.CustomerAssetTag, "ASSET-" + marker), At.AddDays(-6), "test", Entity));
        var entitlement = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), original,
            "base_warranty", ClaimDate, Entity, At, "desk", new Dictionary<string, DateTime> { [StartTrigger.Deliver] = At },
            PolicyVersionId: draft.PolicyVersionId, OwnerPartyRef: customer, OwnerPartyType: PartyType.Customer,
            Country: "DE", SiteLocationId: Site, SourceDocument: new("invoice", Guid.NewGuid(), 1)));
        var claimId = Guid.NewGuid();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Claims.Add(new Claim
            {
                TenantId = tenant, ClaimId = claimId, ClaimNumber = "CLM-2026-" + Random.Shared.Next(10000, 99999),
                CommandId = Guid.NewGuid(), UnitId = original, ClaimDate = ClaimDate, ClaimantPartyRef = customer,
                SettlementPartyRef = customer, ServiceEntityId = Entity, ObligorEntityId = Entity, ServiceCountry = "DE",
                Currency = "EUR", CoverageSource = ClaimCoverageSource.Warranty, Remedy = ClaimRemedy.Replacement,
                Status = ClaimStatus.Approved, EntitlementIds = JsonSerializer.Serialize(new[] { entitlement.EntitlementId }, LedgerJson.Options),
                SubmittedAt = At.AddDays(1), AdjudicatedAt = At.AddDays(2), SubmittedBy = "desk", LastEventId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }

        var created = await InvokeAsync<CreateWarrantyReplacementResult>(tenant, new CreateWarrantyReplacement(Guid.NewGuid(),
            ReplacementSourceKind.Claim, claimId, entitlement.EntitlementId, recorded, ReplacementReason.WarrantyDefect,
            ReplacementPricePolicy.Zero, 0m, "EUR", "DE", true, false, At.AddDays(3), Entity, "desk"));
        var shipped = await InvokeAsync<ShipWarrantyReplacementResult>(tenant, new ShipWarrantyReplacement(Guid.NewGuid(),
            created.Document.Document.WarrantyReplacementId, ShipDate, At.AddDays(4), "desk"));
        return new(tenant, original, recorded, actual, customer, recordedLocation, shipped.ReplacementLinkId,
            shipped.PolicyApplied, shipped.ComputedEnd);
    }

    private async Task<Guid> CommissionAsync(TenantId tenant, Guid item, string serial, Guid location, DateTime at) =>
        (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new(IdentifierType.InternalSerial, serial)])], at, "test", Entity,
            LocationId: location, LocationKind: LocationKind.Warehouse))).Lines[0].UnitId!.Value;

    internal sealed record Setup(TenantId Tenant, Guid OriginalUnitId, Guid RecordedUnitId, Guid ActualUnitId,
        Guid Customer, Guid RecordedStockLocation, Guid RecordedLinkId, string PolicyApplied, DateOnly ComputedEnd);
}
