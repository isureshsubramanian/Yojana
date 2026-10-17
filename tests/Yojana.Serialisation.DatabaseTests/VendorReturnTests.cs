using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Replacements;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class VendorReturnTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Return_to_vendor_and_vendor_replacement_move_ownership_and_only_the_inbound_chain()
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, "vendor-roundtrip");
        var customerCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, "vendor-original");
        var vendor = Guid.NewGuid();
        var inbound = await CreateInboundCoverAsync(world, customerCase.OriginalUnitId, vendor);
        var rma = await P5AdvanceTestData.SeedAuthorisedRmaAsync(fixture, customerCase);
        await P5AdvanceTestData.ReceiveAsync(fixture, customerCase, rma, customerCase.OriginalSerial, At.AddDays(2));
        await P5AdvanceTestData.InvokeAsync<DispositionRmaResult>(fixture, world.Tenant,
            new DispositionRma(Guid.NewGuid(), rma.RmaId, [new(rma.RmaLineId, RmaDisposition.ReturnToVendor, vendor)],
                new(2026, 9, 4), At.AddDays(3)));

        var vendorReplacement = await P5AdvanceTestData.CommissionAsync(fixture, world, "VENDOR-NEW");
        await P5AdvanceTestData.InvokeAsync<OpenOwnershipResult>(fixture, world.Tenant,
            new OpenOwnership(Guid.NewGuid(), vendorReplacement.UnitId, vendor, PartyType.Vendor, EpisodeReason.Receipt,
                At.AddDays(-2), "vendor", world.Entity));
        await P5AdvanceTestData.InvokeAsync<ChangeCustodyResult>(fixture, world.Tenant,
            new ChangeCustody(Guid.NewGuid(), vendorReplacement.UnitId, LocationKind.Vendor, EpisodeReason.Receipt,
                At.AddDays(-1), "vendor", world.Entity, PartyRef: vendor, PartyType: PartyType.Vendor));
        var command = new ReceiveVendorReplacement(Guid.NewGuid(), rma.RmaLineId, inbound, vendorReplacement.UnitId, Guid.NewGuid(),
            new(2026, 9, 5), At.AddDays(4), world.Entity);
        var received = await P5AdvanceTestData.InvokeAsync<ReceiveVendorReplacementResult>(fixture, world.Tenant, command);
        Assert.True((await P5AdvanceTestData.InvokeAsync<ReceiveVendorReplacementResult>(fixture, world.Tenant, command)).Replayed);

        using var scope = fixture.ScopeFor(world.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var originalOwner = await db.Episodes.SingleAsync(row => row.UnitId == customerCase.OriginalUnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var originalCustody = await db.Episodes.SingleAsync(row => row.UnitId == customerCase.OriginalUnitId && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((vendor, PartyType.Vendor, RmaDisposition.ReturnToVendor), (originalOwner.PartyRef, originalOwner.PartyType, originalOwner.Reason));
        Assert.Equal((LocationKind.Vendor, vendor), (originalCustody.LocationKind, originalCustody.PartyRef));

        var replacementOwner = await db.Episodes.SingleAsync(row => row.UnitId == vendorReplacement.UnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var replacementCustody = await db.Episodes.SingleAsync(row => row.UnitId == vendorReplacement.UnitId && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((world.Entity, PartyType.Us), (replacementOwner.PartyRef, replacementOwner.PartyType));
        Assert.Equal(LocationKind.Warehouse, replacementCustody.LocationKind);
        var sourceInbound = await db.Entitlements.SingleAsync(row => row.EntitlementId == inbound);
        var targetInbound = await db.Entitlements.SingleAsync(row => row.EntitlementId == received.TargetEntitlementId);
        Assert.Equal((EntitlementStatus.Closed, EntitlementClosedReason.Replaced), (sourceInbound.Status, sourceInbound.ClosedReason));
        Assert.Equal((EntitlementDirection.Inbound, inbound, inbound, vendorReplacement.UnitId),
            (targetInbound.Direction, targetInbound.ParentEntitlementId, targetInbound.RootEntitlementId, targetInbound.UnitId));
        var customerCover = await db.Entitlements.SingleAsync(row => row.EntitlementId == customerCase.SourceEntitlementId);
        Assert.Equal((EntitlementStatus.Active, (Guid?)null), (customerCover.Status, customerCover.RootEntitlementId));
        var link = await db.ReplacementLinks.SingleAsync(row => row.ReplacementLinkId == received.ReplacementLinkId);
        Assert.Equal((ReplacementDirection.VendorFacing, vendor, customerCase.OriginalUnitId, vendorReplacement.UnitId),
            (link.Direction, link.CounterpartyPartyRef, link.OriginalUnitId, link.ReplacementUnitId));
        Assert.Equal(2, await db.Identifiers.CountAsync(row => row.UnitId == customerCase.OriginalUnitId || row.UnitId == vendorReplacement.UnitId));
        Assert.Contains(await db.Events.Where(row => row.UnitId == customerCase.OriginalUnitId).Select(row => row.EventType).ToListAsync(), type => type == UnitEventType.OwnershipTransferred);
        Assert.Contains(await db.Events.Where(row => row.UnitId == vendorReplacement.UnitId).Select(row => row.EventType).ToListAsync(), type => type == ReplacementEventType.VendorReceived);
        Assert.Contains(await db.Events.Where(row => row.UnitId == vendorReplacement.UnitId).Select(row => row.EventType).ToListAsync(), type => type == EntitlementEventType.Transferred);
    }

    private async Task<Guid> CreateInboundCoverAsync(P5AdvanceWorld world, Guid unitId, Guid vendor)
    {
        var code = "SUP-" + Guid.NewGuid().ToString("N")[..8];
        var terms = PolicyValidatorTestsTerms.Standard24() with { Duration = new DurationRule(12, DurationRule.Months), ReplacementPolicyCode = "ADV-90" };
        var draft = await P5AdvanceTestData.InvokeAsync<PolicyDraftResult>(fixture, world.Tenant,
            new CreatePolicyDraft(Guid.NewGuid(), code, code, CoverageFamilyCode.Supplier, EntitlementType.Manufacturer,
                new(2026, 1, 1), terms, "test"));
        await P5AdvanceTestData.InvokeAsync<PolicyDraftResult>(fixture, world.Tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        var created = await P5AdvanceTestData.InvokeAsync<CreateEntitlementResult>(fixture, world.Tenant,
            new CreateEntitlement(Guid.NewGuid(), unitId, CoverageFamilyCode.Supplier, new(2026, 9, 1), vendor, At,
                "vendor", new Dictionary<string, DateTime> { [StartTrigger.Deliver] = At }, PolicyVersionId: draft.PolicyVersionId,
                PayerPartyRef: vendor, PayerPartyType: PartyType.Vendor, Country: "DE", SiteLocationId: world.Site,
                SourceDocument: new DocumentRef("purchase_receipt", Guid.NewGuid(), 1)));
        using var scope = fixture.ScopeFor(world.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var entitlement = await db.Entitlements.SingleAsync(row => row.EntitlementId == created.EntitlementId);
        entitlement.Direction = EntitlementDirection.Inbound;
        entitlement.OwnerPartyRef = null;
        entitlement.OwnerPartyType = null;
        entitlement.ObligorEntityId = vendor;
        entitlement.PayerPartyRef = vendor;
        entitlement.PayerPartyType = PartyType.Vendor;
        await db.SaveChangesAsync();
        return entitlement.EntitlementId;
    }
}
