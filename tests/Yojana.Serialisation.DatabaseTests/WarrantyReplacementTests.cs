using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Serialisation.Ledger;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P5-T05: replacement terms, condition gates, entitlement chain, stock issue, tag move, publication and replay.</summary>
[Collection("serialisation host")]
public sealed class WarrantyReplacementTests(SerialisationHostFixture fixture)
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

    [DatabaseTheory]
    [InlineData(ReplacementReason.WarrantyDefect, null, "STD-90", "2026-12-04")]
    [InlineData(ReplacementReason.DeadOnArrival, "PRESERVE", "PRESERVE", "2026-09-30")]
    [InlineData(ReplacementReason.SellerError, "PRESERVE", "PRESERVE", "2026-09-30")]
    [InlineData(ReplacementReason.Goodwill, "STD-90", "STD-90", "2026-12-04")]
    [InlineData(ReplacementReason.Recall, "STD-90", "STD-90", "2026-12-04")]
    public async Task Every_reason_uses_its_required_term_rule(string reason, string? requestedPolicy, string expectedPolicy, string expectedEnd)
    {
        var setup = await SetUpAsync("wrterm");
        var created = await CreateAsync(setup, reason, requestedPolicy);
        var shipped = await InvokeAsync<ShipWarrantyReplacementResult>(setup.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), created.Document.Document.WarrantyReplacementId, ShipDate, At.AddDays(4), "desk"));

        Assert.Equal((expectedPolicy, DateOnly.Parse(expectedEnd)), (shipped.PolicyApplied, shipped.ComputedEnd));
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var source = await db.Entitlements.SingleAsync(row => row.EntitlementId == setup.SourceEntitlementId);
        var target = await db.Entitlements.SingleAsync(row => row.EntitlementId == shipped.TargetEntitlementId);
        Assert.Equal(source.StartDate, target.StartDate);
        if (reason is ReplacementReason.DeadOnArrival or ReplacementReason.SellerError) Assert.Equal(source.EndDate, target.EndDate);
    }

    [DatabaseFact]
    public async Task Doa_requires_new_and_an_EU_consumer_requires_consent_for_refurbished_stock()
    {
        var doa = await SetUpAsync("wrdoa");
        await SetConditionAsync(doa.Tenant, doa.ReplacementUnitId, ConditionGrades.RefurbishedA);
        var doaDocument = await CreateAsync(doa, ReplacementReason.DeadOnArrival, "PRESERVE", consent: true);
        var doaRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ShipWarrantyReplacementResult>(doa.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), doaDocument.Document.Document.WarrantyReplacementId, ShipDate, At.AddDays(4), "desk")));
        Assert.Equal("doa_requires_new", doaRefusal.Rule);

        var eu = await SetUpAsync("wreu");
        await SetConditionAsync(eu.Tenant, eu.ReplacementUnitId, ConditionGrades.RefurbishedB);
        var euDocument = await CreateAsync(eu, ReplacementReason.WarrantyDefect, null, consent: false);
        var euRefusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ShipWarrantyReplacementResult>(eu.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), euDocument.Document.Document.WarrantyReplacementId, ShipDate, At.AddDays(4), "desk")));
        Assert.Equal("refurbished_consent_required", euRefusal.Rule);
    }

    [DatabaseFact]
    public async Task Ship_transfers_once_repoints_the_asset_tag_issues_stock_and_publishes_suppressed_accrual()
    {
        var setup = await SetUpAsync("wrship", assetTag: true);
        var created = await CreateAsync(setup, ReplacementReason.WarrantyDefect, null);
        var command = new ShipWarrantyReplacement(Guid.NewGuid(), created.Document.Document.WarrantyReplacementId, ShipDate, At.AddDays(4), "desk");
        var first = await InvokeAsync<ShipWarrantyReplacementResult>(setup.Tenant, command);
        var replay = await InvokeAsync<ShipWarrantyReplacementResult>(setup.Tenant, command);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.TargetEntitlementId, replay.TargetEntitlementId);
        Assert.True(first.AssetTagRepointed);
        Assert.True(replay.AssetTagRepointed);
        Assert.True(first.AccrualSuppressed);
        Assert.Equal(1, Stock.ReplacementIssues.Count(issue => issue.WarrantyReplacementId == first.WarrantyReplacementId));

        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var tags = await db.Identifiers.Where(row => row.Type == IdentifierType.CustomerAssetTag
                && (row.UnitId == setup.OriginalUnitId || row.UnitId == setup.ReplacementUnitId)).OrderBy(row => row.ValidFrom).ToListAsync();
            Assert.Equal(2, tags.Count);
            Assert.Equal(setup.OriginalUnitId, tags[0].UnitId);
            Assert.NotNull(tags[0].ValidTo);
            Assert.Equal(setup.ReplacementUnitId, tags[1].UnitId);
            Assert.Null(tags[1].ValidTo);
            Assert.Equal(tags[0].IdentifierId, tags[1].SupersedesIdentifierId);
            Assert.Equal(tags[1].IdentifierId, tags[0].SupersededByIdentifierId);
            Assert.Single(await db.Entitlements.Where(row => row.ParentEntitlementId == setup.SourceEntitlementId).ToListAsync());
            var transferred = await db.Entitlements.SingleAsync(row => row.EntitlementId == first.TargetEntitlementId);
            Assert.Equal((setup.SourceEntitlementId, setup.SourceEntitlementId), (transferred.ParentEntitlementId, transferred.RootEntitlementId));
            Assert.Equal(ReplacementLinkState.ClosedReturned, (await db.ReplacementLinks.SingleAsync(row => row.ReplacementLinkId == first.ReplacementLinkId)).State);

            var replacementOwnership = await db.Episodes.Where(row => row.UnitId == setup.ReplacementUnitId && row.Kind == EpisodeKind.Ownership).ToListAsync();
            var replacementCustody = await db.Episodes.Where(row => row.UnitId == setup.ReplacementUnitId && row.Kind == EpisodeKind.Custody).ToListAsync();
            var replacementOwner = Assert.Single(replacementOwnership);
            Assert.Equal(setup.OwnerPartyRef, replacementOwner.PartyRef);
            Assert.Equal(PartyType.Customer, replacementOwner.PartyType);
            Assert.Equal(EpisodeReason.Replacement, replacementOwner.Reason);
            Assert.Equal("warranty_replacement", replacementOwner.OpeningDocumentType);
            Assert.Equal(first.WarrantyReplacementId, replacementOwner.OpeningDocumentId);
            Assert.Equal(1, replacementOwner.OpeningDocumentLine);
            var customerCustody = Assert.Single(replacementCustody, row => row.ValidTo == null);
            Assert.Equal(LocationKind.CustomerSite, customerCustody.LocationKind);
            Assert.Equal(setup.OwnerPartyRef, customerCustody.PartyRef);
            Assert.Equal(PartyType.Customer, customerCustody.PartyType);
            Assert.Equal(EpisodeReason.Replacement, customerCustody.Reason);
            Assert.Equal(2, replacementCustody.Count);
            var replacementCurrent = await db.Current.SingleAsync(row => row.UnitId == setup.ReplacementUnitId);
            Assert.Equal(setup.OwnerPartyRef, replacementCurrent.OwnerPartyRef);
            Assert.Equal(setup.OwnerPartyRef, replacementCurrent.CustodyPartyRef);
            Assert.Equal(LocationKind.CustomerSite, replacementCurrent.LocationKind);

            var originalOwnership = await db.Episodes.Where(row => row.UnitId == setup.OriginalUnitId && row.Kind == EpisodeKind.Ownership).ToListAsync();
            var originalOwner = Assert.Single(originalOwnership);
            Assert.Equal(setup.OriginalOwnershipEpisodeId, originalOwner.EpisodeId);
            Assert.Null(originalOwner.ValidTo);
            Assert.Equal(setup.OwnerPartyRef, originalOwner.PartyRef);
            Assert.Equal(setup.OwnerPartyRef, (await db.Current.SingleAsync(row => row.UnitId == setup.OriginalUnitId)).OwnerPartyRef);
        }

        var shipped = Assert.Single(await PublishedEventSink.WaitForAsync<ReplacementShipped>(message =>
            message.WarrantyReplacementId == first.WarrantyReplacementId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal((ReplacementReason.WarrantyDefect, ReplacementPricePolicy.Zero, true, setup.OriginalItemId, setup.ReplacementItemId),
            (shipped.Reason, shipped.PricePolicy, shipped.AccrualSuppressed, shipped.OriginalItemId, shipped.ReplacementItemId));
        var print = Assert.Single(await PublishedEventSink.WaitForAsync<ReplacementAssetTagPrintRequested>(message =>
            message.WarrantyReplacementId == first.WarrantyReplacementId, 1, TimeSpan.FromSeconds(20)));
        Assert.Equal(setup.ReplacementUnitId, print.Header.UnitId);
        Assert.Equal("ASSET-" + setup.Marker, print.AssetTag);
    }

    [DatabaseFact]
    public async Task Creation_and_shipping_return_typed_refusals_without_partial_links_or_transfers()
    {
        var setup = await SetUpAsync("wrref");
        var bad = await Assert.ThrowsAsync<CommandRefusedException>(() => CreateAsync(setup, "exchange", null));
        Assert.Equal("replacement_reason_unknown", bad.Rule);
        var wrongPolicy = await Assert.ThrowsAsync<CommandRefusedException>(() => CreateAsync(setup, ReplacementReason.SellerError, "STD-90"));
        Assert.Equal("original_term_policy_required", wrongPolicy.Rule);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Empty(await db.ReplacementLinks.ToListAsync());
        Assert.Empty(await db.Entitlements.Where(row => row.ParentEntitlementId == setup.SourceEntitlementId).ToListAsync());
    }

    [DatabaseFact]
    public async Task Shipping_refuses_a_replacement_unit_owned_by_another_customer()
    {
        var setup = await SetUpAsync("wrowner");
        var otherOwner = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.ReplacementUnitId, otherOwner,
            PartyType.Customer, EpisodeReason.Sale, At.AddDays(-6), "test", Entity));
        var created = await CreateAsync(setup, ReplacementReason.WarrantyDefect, null);

        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<ShipWarrantyReplacementResult>(setup.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), created.Document.Document.WarrantyReplacementId, ShipDate, At.AddDays(4), "desk")));

        Assert.Equal("replacement_owner_mismatch", refusal.Rule);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Empty(await db.Entitlements.Where(row => row.ParentEntitlementId == setup.SourceEntitlementId).ToListAsync());
        var owner = await db.Episodes.SingleAsync(row => row.UnitId == setup.ReplacementUnitId && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        Assert.Equal(otherOwner, owner.PartyRef);
        Assert.DoesNotContain(Stock.ReplacementIssues, issue => issue.WarrantyReplacementId == created.Document.Document.WarrantyReplacementId);
    }

    private async Task<CreateWarrantyReplacementResult> CreateAsync(Setup setup, string reason, string? policy, bool consent = false)
    {
        var command = new CreateWarrantyReplacement(Guid.NewGuid(), ReplacementSourceKind.Claim, setup.ClaimId, setup.SourceEntitlementId,
            setup.ReplacementUnitId, reason, ReplacementPricePolicy.Zero, 0m, "EUR", "DE", true, consent,
            At.AddDays(3), Entity, "desk", ReplacementPolicyCode: policy);
        return await InvokeAsync<CreateWarrantyReplacementResult>(setup.Tenant, command);
    }

    private async Task<Setup> SetUpAsync(string prefix, bool assetTag = false)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("STD-90", ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("PRESERVE", ReplacementRule.CarryRemaining, null, null, null));
        var code = "WR-" + Guid.NewGuid().ToString("N")[..7];
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            Duration = new DurationRule(1, DurationRule.Months),
            ReplacementPolicyCode = "STD-90",
        };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, "base_warranty",
            EntitlementType.Manufacturer, new DateOnly(2026, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));

        var originalItem = Guid.NewGuid();
        var replacementItem = Guid.NewGuid();
        WarrantyPorts.Register(new ItemWarrantyPolicy(originalItem, "base_warranty", code, true));
        WarrantyPorts.RegisterZone(Site, "Europe/Berlin");
        var marker = prefix.ToUpperInvariant() + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var original = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), originalItem, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, marker + "-OLD")])], At.AddDays(-10), "test", Entity,
            LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse))).Lines[0].UnitId!.Value;
        var replacement = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), replacementItem, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, marker + "-NEW")])], At.AddDays(-9), "test", Entity,
            LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse))).Lines[0].UnitId!.Value;
        var owner = Guid.NewGuid();
        var originalOwnership = await InvokeAsync<OpenOwnershipResult>(tenant, new OpenOwnership(Guid.NewGuid(), original, owner, PartyType.Customer,
            EpisodeReason.Sale, At.AddDays(-8), "test", Entity));
        if (assetTag)
            await InvokeAsync<AssignIdentifierResult>(tenant, new AssignIdentifier(Guid.NewGuid(), original,
                new IdentifierInput(IdentifierType.CustomerAssetTag, "ASSET-" + marker), At.AddDays(-7), "test", Entity));
        var entitlement = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), original, "base_warranty",
            ClaimDate, Entity, At, "desk", new Dictionary<string, DateTime> { [StartTrigger.Deliver] = At },
            PolicyVersionId: draft.PolicyVersionId, OwnerPartyRef: owner, OwnerPartyType: PartyType.Customer, Country: "DE", SiteLocationId: Site,
            SourceDocument: new DocumentRef("invoice", Guid.NewGuid(), 1)));
        var claimId = Guid.NewGuid();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            db.Claims.Add(new Claim
            {
                TenantId = tenant, ClaimId = claimId, ClaimNumber = "CLM-2026-" + Random.Shared.Next(10000, 99999), CommandId = Guid.NewGuid(),
                UnitId = original, ClaimDate = ClaimDate, ClaimantPartyRef = owner, SettlementPartyRef = owner, ServiceEntityId = Entity,
                ObligorEntityId = Entity, ServiceCountry = "DE", Currency = "EUR", CoverageSource = ClaimCoverageSource.Warranty,
                Remedy = ClaimRemedy.Replacement, Status = ClaimStatus.Approved, EntitlementIds = JsonSerializer.Serialize(new[] { entitlement.EntitlementId }, LedgerJson.Options),
                SubmittedAt = At.AddDays(1), AdjudicatedAt = At.AddDays(2), SubmittedBy = "desk", LastEventId = Guid.NewGuid(),
            });
            await db.SaveChangesAsync();
        }
        return new(tenant, originalItem, replacementItem, original, replacement, owner, originalOwnership.EpisodeId,
            entitlement.EntitlementId, claimId, marker);
    }

    private async Task SetConditionAsync(TenantId tenant, Guid unitId, string grade)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        (await db.Current.SingleAsync(row => row.UnitId == unitId)).ConditionGrade = grade;
        await db.SaveChangesAsync();
    }

    private sealed record Setup(TenantId Tenant, Guid OriginalItemId, Guid ReplacementItemId, Guid OriginalUnitId,
        Guid ReplacementUnitId, Guid OwnerPartyRef, Guid OriginalOwnershipEpisodeId, Guid SourceEntitlementId, Guid ClaimId, string Marker);
}
