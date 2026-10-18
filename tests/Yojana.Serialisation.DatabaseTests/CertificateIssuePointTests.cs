using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Ports;
using Yojana.Serialisation.Refurbishments;
using Yojana.Serialisation.Replacements;
using Xunit;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P6-T20: issue-point policy is exercised through published facts, the durable trigger queue and chain engine.</summary>
[Collection("serialisation host")]
public sealed class CertificateIssuePointTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("ca000000-0000-0000-0000-000000000020");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    [InlineData(ReleaseRecordIssuePoint.AtBuildRelease, true, false)]
    [InlineData(ReleaseRecordIssuePoint.AtFirstShipment, false, true)]
    [InlineData(ReleaseRecordIssuePoint.OnRequest, false, false)]
    [DatabaseTheory]
    public async Task Built_unit_issues_at_the_configured_point(string issuePoint, bool afterRelease, bool afterSale)
    {
        var setup = await SetUpAsync("cert-point-built-" + issuePoint, UnitOrigin.Manufactured, "machine");
        await SetIssuePointAsync(setup.Tenant, issuePoint);
        await ReleaseAsync(setup);
        await WaitForSettledAsync(setup.Tenant, setup.UnitId);
        Assert.Equal(afterRelease ? 1 : 0, await CountAsync(setup.Tenant, setup.UnitId));

        if (issuePoint == ReleaseRecordIssuePoint.OnRequest)
        {
            var preview = await PreviewAsync(setup);
            Assert.Equal(CertificateSelectionOutcome.IssueVersion1, Assert.Single(preview.Units).Outcome);
            await IssueSelectionAsync(setup);
        }
        else
        {
            await SellAsync(setup, At.AddHours(2));
            if (afterSale) await WaitForCountAsync(setup.Tenant, setup.UnitId, 1);
            else await WaitForSettledAsync(setup.Tenant, setup.UnitId);
        }

        Assert.Equal(afterRelease || afterSale || issuePoint == ReleaseRecordIssuePoint.OnRequest
            ? 1 : 0, await CountAsync(setup.Tenant, setup.UnitId));
        await AssertWholeUnitAsync(setup, issuePoint == ReleaseRecordIssuePoint.OnRequest ? null : setup.Customer,
            issuePoint == ReleaseRecordIssuePoint.OnRequest ? LocationKind.Warehouse : LocationKind.CustomerSite,
            certificateVersions: 1);
    }

    [InlineData(ReleaseRecordIssuePoint.AtBuildRelease)]
    [InlineData(ReleaseRecordIssuePoint.AtFirstShipment)]
    [DatabaseTheory]
    public async Task Bought_in_unit_falls_back_to_first_shipment_and_uses_party_free_purchase_facts(string issuePoint)
    {
        var supplier = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryPartyNamePort>().Register(
            new PartyLabel(supplier, "SUP-FIC", "Fictional Source Works", PartyType.Vendor));
        var setup = await SetUpAsync("cert-point-buy-" + issuePoint, UnitOrigin.Purchased, "appliance", supplier);
        await SetIssuePointAsync(setup.Tenant, issuePoint);
        await Task.Delay(300);
        Assert.Equal(0, await CountAsync(setup.Tenant, setup.UnitId));

        await SellAsync(setup, At.AddHours(2));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, 1);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var certificate = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord);
        using var payload = JsonDocument.Parse(certificate.Payload);
        Assert.Equal("purchased", payload.RootElement.GetProperty("build_source").GetString());
        Assert.Equal(At, payload.RootElement.GetProperty("build_date").GetDateTime());
        Assert.DoesNotContain("Fictional Source Works", certificate.Payload, StringComparison.Ordinal);
        Assert.Null(certificate.AsBuiltRevisionId);
        await AssertWholeUnitAsync(db, setup, setup.Customer, LocationKind.CustomerSite, 1);
    }

    [InlineData(ReleaseRecordIssuePoint.AtBuildRelease, true)]
    [InlineData(ReleaseRecordIssuePoint.AtFirstShipment, true)]
    [InlineData(ReleaseRecordIssuePoint.OnRequest, false)]
    [DatabaseTheory]
    public async Task Advance_replacement_reaches_each_configured_issue_point_without_leaving_the_unit_inconsistent(
        string issuePoint, bool issuesOnShipment)
    {
        var world = await P5AdvanceTestData.NewWorldAsync(fixture, "cert-point-replacement-" + issuePoint);
        await SetIssuePointAsync(world.Tenant, issuePoint);
        var customerCase = await P5AdvanceTestData.AddCustomerCaseAsync(fixture, world, issuePoint);
        var created = await P5AdvanceTestData.InvokeAsync<CreateWarrantyReplacementResult>(fixture, world.Tenant,
            P5AdvanceTestData.CreateCommand(customerCase, ReplacementSourceKind.Claim, customerCase.ClaimId,
                At.AddDays(2), new DateOnly(2026, 9, 20)));
        var shipped = await P5AdvanceTestData.InvokeAsync<ShipWarrantyReplacementResult>(fixture, world.Tenant,
            new ShipWarrantyReplacement(Guid.NewGuid(), created.Document.Document.WarrantyReplacementId,
                new DateOnly(2026, 9, 4), At.AddDays(3)));

        if (issuesOnShipment) await WaitForCountAsync(world.Tenant, customerCase.ReplacementUnitId, 1);
        else
        {
            await WaitForSettledAsync(world.Tenant, customerCase.ReplacementUnitId);
            Assert.Equal(0, await CountAsync(world.Tenant, customerCase.ReplacementUnitId));
            var selection = await P5AdvanceTestData.InvokeAsync<ResolveSelectionSetResult>(fixture, world.Tenant,
                new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByPastedList([customerCase.ReplacementSerial]), "quality"));
            await P5AdvanceTestData.InvokeAsync<CertificateRunResult>(fixture, world.Tenant,
                new IssueCertificate(Guid.NewGuid(), selection.SelectionId, CertificateDocumentKind.ReleaseRecord,
                    CertificateFormType.GenericRelease, world.Entity, "en", "Requested release record", false, "quality"));
            await WaitForCountAsync(world.Tenant, customerCase.ReplacementUnitId, 1);
        }

        using var scope = fixture.ScopeFor(world.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var owner = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == customerCase.ReplacementUnitId
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var custody = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == customerCase.ReplacementUnitId
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((world.Customer, PartyType.Customer, At.AddDays(3)),
            (owner.PartyRef, owner.PartyType, owner.ValidFrom));
        Assert.Equal((LocationKind.CustomerSite, world.Customer, EpisodeReason.Replacement, At.AddDays(3)),
            (custody.LocationKind, custody.PartyRef, custody.Reason, custody.ValidFrom));
        Assert.Equal(EntitlementStatus.Closed,
            (await db.Entitlements.AsNoTracking().SingleAsync(row => row.EntitlementId == customerCase.SourceEntitlementId)).Status);
        var target = await db.Entitlements.AsNoTracking().SingleAsync(row => row.EntitlementId == shipped.TargetEntitlementId);
        Assert.Equal((customerCase.ReplacementUnitId, EntitlementStatus.Active, customerCase.SourceEntitlementId),
            (target.UnitId, target.Status, target.ParentEntitlementId));
        Assert.Single(await db.Identifiers.AsNoTracking().Where(row => row.UnitId == customerCase.ReplacementUnitId
            && row.ValidTo == null).ToListAsync());
        Assert.Equal(1, await db.CertificateInstances.CountAsync(row => row.UnitId == customerCase.ReplacementUnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));
        var eventTypes = await db.Events.AsNoTracking().Where(row => row.UnitId == customerCase.ReplacementUnitId)
            .Select(row => row.EventType).ToListAsync();
        Assert.Contains(ReplacementEventType.Shipped, eventTypes);
        Assert.Contains(UnitEventType.OwnershipOpened, eventTypes);
        Assert.Contains(EntitlementEventType.Transferred, eventTypes);
        Assert.Contains(CertificateEventType.Issued, eventTypes);
        if (issuesOnShipment)
            Assert.Equal(shipped.EventId, (await db.CertificateInstances.AsNoTracking().SingleAsync(row =>
                row.UnitId == customerCase.ReplacementUnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord)).TriggeringEventId);
    }

    [InlineData(ReleaseRecordIssuePoint.AtBuildRelease, true)]
    [InlineData(ReleaseRecordIssuePoint.AtFirstShipment, true)]
    [InlineData(ReleaseRecordIssuePoint.OnRequest, false)]
    [DatabaseTheory]
    public async Task Corrected_advance_replacement_reaches_each_configured_issue_point_and_keeps_the_physical_unit_consistent(
        string issuePoint, bool issuesOnCorrection)
    {
        var setup = await new CorrectReplacementLinkTests(fixture).SetUpAsync(
            "cert-point-corrected-" + issuePoint, issuePoint);
        var correctionId = Guid.NewGuid();
        await InvokeAsync<CorrectReplacementLinkResult>(setup.Tenant, new CorrectReplacementLink(
            correctionId, setup.RecordedUnitId, setup.ActualUnitId,
            CorrectReplacementLinkReason.WrongUnitRecorded, At.AddDays(6), "service.manager", Entity,
            "Physical delivery evidence checked", new(correctionId, CorrectReplacementLinkReason.WrongUnitRecorded),
            new("service.manager", [YojanaRole.ServiceManager])));

        if (issuesOnCorrection) await WaitForCountAsync(setup.Tenant, setup.ActualUnitId, 1);
        else
        {
            await WaitForSettledAsync(setup.Tenant, setup.ActualUnitId);
            Assert.Equal(0, await CountAsync(setup.Tenant, setup.ActualUnitId));
            var selection = await P5AdvanceTestData.InvokeAsync<ResolveSelectionSetResult>(fixture, setup.Tenant,
                new ResolveSelectionSet(Guid.NewGuid(), SelectionSource.ByPastedList([
                    await PrimarySerialAsync(setup.Tenant, setup.ActualUnitId)]), "quality"));
            await P5AdvanceTestData.InvokeAsync<CertificateRunResult>(fixture, setup.Tenant,
                new IssueCertificate(Guid.NewGuid(), selection.SelectionId, CertificateDocumentKind.ReleaseRecord,
                    CertificateFormType.GenericRelease, Entity, "en", "Requested release record", false, "quality"));
            await WaitForCountAsync(setup.Tenant, setup.ActualUnitId, 1);
        }

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var owner = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.ActualUnitId
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null);
        var custody = await db.Episodes.AsNoTracking().SingleAsync(row => row.UnitId == setup.ActualUnitId
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null);
        Assert.Equal((setup.Customer, PartyType.Customer, At.AddDays(4)),
            (owner.PartyRef, owner.PartyType, owner.ValidFrom));
        Assert.Equal((LocationKind.CustomerSite, setup.Customer, EpisodeReason.Replacement, At.AddDays(4)),
            (custody.LocationKind, custody.PartyRef, custody.Reason, custody.ValidFrom));
        Assert.Single(await db.Entitlements.AsNoTracking().Where(row => row.UnitId == setup.ActualUnitId
            && row.Status == EntitlementStatus.Active).ToListAsync());
        Assert.Empty(await db.Entitlements.AsNoTracking().Where(row => row.UnitId == setup.RecordedUnitId
            && row.Status == EntitlementStatus.Active).ToListAsync());
        var identifiers = await db.Identifiers.AsNoTracking().Where(row => row.UnitId == setup.ActualUnitId
            && row.ValidTo == null).Select(row => row.Type).ToListAsync();
        Assert.Contains(IdentifierType.InternalSerial, identifiers);
        Assert.Contains(IdentifierType.CustomerAssetTag, identifiers);
        Assert.Equal(1, await db.CertificateInstances.CountAsync(row => row.UnitId == setup.ActualUnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));
        var eventTypes = await db.Events.AsNoTracking().Where(row => row.UnitId == setup.ActualUnitId)
            .Select(row => row.EventType).ToListAsync();
        Assert.Contains(ReplacementEventType.LinkCorrected, eventTypes);
        Assert.Contains(UnitEventType.OwnershipOpened, eventTypes);
        Assert.Contains(EntitlementEventType.Transferred, eventTypes);
        Assert.Contains(CertificateEventType.Issued, eventTypes);
        if (issuesOnCorrection)
        {
            var correctionEvent = await db.Events.AsNoTracking().SingleAsync(row => row.UnitId == setup.ActualUnitId
                && row.EventType == ReplacementEventType.LinkCorrected);
            Assert.Equal(correctionEvent.EventId, (await db.CertificateInstances.AsNoTracking().SingleAsync(row =>
                row.UnitId == setup.ActualUnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord)).TriggeringEventId);
        }
    }

    [DatabaseFact]
    public async Task Drop_shipment_ownership_to_a_customer_issues_without_our_custody()
    {
        var setup = await SetUpAsync("cert-point-drop", UnitOrigin.VendorAsn, "network", withLocation: false);
        await SetIssuePointAsync(setup.Tenant, ReleaseRecordIssuePoint.AtFirstShipment);

        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId,
            setup.Customer, PartyType.Customer, EpisodeReason.Sale, At.AddHours(1), "sales", Entity,
            EpisodeSource.CarrierPod));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, 1);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Empty(await db.Episodes.Where(row => row.UnitId == setup.UnitId && row.Kind == EpisodeKind.Custody).ToListAsync());
        await AssertWholeUnitAsync(db, setup, setup.Customer, null, 1);
    }

    [DatabaseFact]
    public async Task Pre_sale_refurbishment_is_absorbed_then_sale_issues_v1()
    {
        var setup = await SetUpAsync("cert-point-stock-refurb", UnitOrigin.Manufactured, "server");
        await SetIssuePointAsync(setup.Tenant, ReleaseRecordIssuePoint.AtFirstShipment);
        await OpenOurOwnershipAsync(setup, At.AddMinutes(5));
        await ReleaseAsync(setup);
        await RefurbishAsync(setup, "stock", At.AddHours(2));
        await WaitForSettledAsync(setup.Tenant, setup.UnitId);
        Assert.Equal(0, await CountAsync(setup.Tenant, setup.UnitId));

        await SellAsync(setup, At.AddHours(4));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, 1);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.True(await db.CertificateTriggers.AnyAsync(row => row.UnitId == setup.UnitId
            && row.Outcome == "absorbed" && row.RefusalRule == "release_record_issue_point_not_reached"));
        await AssertWholeUnitAsync(db, setup, setup.Customer, LocationKind.CustomerSite, 1);
    }

    [DatabaseFact]
    public async Task Sold_refurbished_and_resold_unit_gets_v2_then_absorbs_the_resale()
    {
        var setup = await SetUpAsync("cert-point-resale", UnitOrigin.Manufactured, "server");
        await SetIssuePointAsync(setup.Tenant, ReleaseRecordIssuePoint.AtFirstShipment);
        await OpenOurOwnershipAsync(setup, At.AddMinutes(5));
        await ReleaseAsync(setup);
        await SellAsync(setup, At.AddHours(2));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, 1);

        await InvokeAsync<ChangeCustodyResult>(setup.Tenant, new ChangeCustody(Guid.NewGuid(), setup.UnitId,
            LocationKind.Quarantine, EpisodeReason.Return, At.AddHours(3), "returns", Entity,
            setup.Location, Source: EpisodeSource.Receipt));
        await OpenOurOwnershipAsync(setup, At.AddHours(3));
        await RefurbishAsync(setup, "resale", At.AddHours(4));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, 2);

        HashSet<Guid> existingTriggers;
        using (var beforeScope = fixture.ScopeFor(setup.Tenant))
            existingTriggers = await beforeScope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
                .CertificateTriggers.Where(row => row.UnitId == setup.UnitId)
                .Select(row => row.TriggeringEventId).ToHashSetAsync();
        var secondCustomer = Guid.NewGuid();
        await InvokeAsync<ChangeCustodyResult>(setup.Tenant, new ChangeCustody(Guid.NewGuid(), setup.UnitId,
            LocationKind.CustomerSite, EpisodeReason.Sale, At.AddHours(6), "sales", Entity,
            Guid.NewGuid(), secondCustomer, PartyType.Customer));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId,
            secondCustomer, PartyType.Customer, EpisodeReason.Sale, At.AddHours(6), "sales", Entity));
        await WaitForNewTriggersAsync(setup.Tenant, setup.UnitId, existingTriggers, 1);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var versions = await db.CertificateInstances.AsNoTracking().Where(row => row.UnitId == setup.UnitId
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord)
            .OrderBy(row => row.Version).ToListAsync();
        Assert.Equal([1, 2], versions.Select(row => row.Version));
        Assert.Equal(CertificateSupersedeReason.Refurbishment, versions[1].SupersedeReason);
        var resaleTriggers = await db.CertificateTriggers.AsNoTracking()
            .Where(row => row.UnitId == setup.UnitId && !existingTriggers.Contains(row.TriggeringEventId))
            .ToListAsync();
        Assert.Single(resaleTriggers);
        Assert.All(resaleTriggers, row =>
        {
            Assert.Equal("absorbed", row.Outcome);
            Assert.Equal("certificate_facts_unchanged", row.RefusalRule);
        });
        await AssertWholeUnitAsync(db, setup, secondCustomer, LocationKind.CustomerSite, 2);
    }

    [DatabaseFact]
    public async Task Item_class_override_wins_and_setting_changes_leave_existing_chain_untouched()
    {
        var setup = await SetUpAsync("cert-point-override", UnitOrigin.Manufactured, "server");
        await SetIssuePointAsync(setup.Tenant, ReleaseRecordIssuePoint.AtBuildRelease,
            [new("server", ReleaseRecordIssuePoint.AtFirstShipment)]);
        await ReleaseAsync(setup);
        await WaitForSettledAsync(setup.Tenant, setup.UnitId);
        Assert.Equal(0, await CountAsync(setup.Tenant, setup.UnitId));
        await SellAsync(setup, At.AddHours(2));
        await WaitForCountAsync(setup.Tenant, setup.UnitId, 1);

        await SetIssuePointAsync(setup.Tenant, ReleaseRecordIssuePoint.OnRequest);
        await Task.Delay(300);
        Assert.Equal(1, await CountAsync(setup.Tenant, setup.UnitId));
        await AssertWholeUnitAsync(setup, setup.Customer, LocationKind.CustomerSite, 1);
    }

    [DatabaseFact]
    public async Task Selection_refuses_an_unshipped_first_shipment_unit_with_the_named_reason()
    {
        var setup = await SetUpAsync("cert-point-preview", UnitOrigin.Manufactured, "server");
        await SetIssuePointAsync(setup.Tenant, ReleaseRecordIssuePoint.AtFirstShipment);
        await ReleaseAsync(setup);
        await WaitForSettledAsync(setup.Tenant, setup.UnitId);

        var preview = await PreviewAsync(setup);

        var row = Assert.Single(preview.Units);
        Assert.Equal(CertificateSelectionOutcome.NotIssuable, row.Outcome);
        Assert.Equal("release_record_issue_point_not_reached", row.Reason);
        Assert.Equal("The unit has not yet shipped to a customer.", row.Detail);
    }

    [DatabaseFact]
    public async Task Settings_default_to_build_release_and_reject_a_stale_expected_version()
    {
        var tenant = await fixture.NewTenantAsync("cert-point-setting");
        string staleVersion;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<ReleaseRecordIssuePointSettings>();
            var initial = await settings.GetAsync(CancellationToken.None);
            Assert.Equal(ReleaseRecordIssuePoint.AtBuildRelease, initial.DefaultIssuePoint);
            Assert.Equal(ITenantSettings.MissingVersion, initial.Version);
            staleVersion = initial.Version;
            var saved = await settings.SetAsync(new UpdateReleaseRecordIssuePointSettings(
                ReleaseRecordIssuePoint.OnRequest, [], initial.Version), CancellationToken.None);
            Assert.NotEqual(initial.Version, saved.Version);
        }

        using var staleScope = fixture.ScopeFor(tenant);
        var staleSettings = staleScope.ServiceProvider.GetRequiredService<ReleaseRecordIssuePointSettings>();
        await Assert.ThrowsAsync<TenantSettingVersionConflictException>(() => staleSettings.SetAsync(
            new UpdateReleaseRecordIssuePointSettings(ReleaseRecordIssuePoint.AtFirstShipment, [], staleVersion),
            CancellationToken.None));
        Assert.Equal(ReleaseRecordIssuePoint.OnRequest,
            (await staleSettings.GetAsync(CancellationToken.None)).DefaultIssuePoint);
    }

    private async Task<Setup> SetUpAsync(string prefix, string origin, string itemClass, Guid? issuer = null,
        bool withLocation = true)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        var item = Guid.NewGuid();
        Guid? warehouse = withLocation ? Guid.NewGuid() : null;
        var customer = Guid.NewGuid();
        var serial = prefix.ToUpperInvariant()[..Math.Min(10, prefix.Length)] + "-" + Guid.NewGuid().ToString("N")[..6];
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with
        {
            ItemClass = itemClass,
        });
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(
            new ItemLabel(item, "FIC-" + itemClass.ToUpperInvariant(), "Fictional " + itemClass));
        var identifier = new IdentifierInput(origin == UnitOrigin.Manufactured
            ? IdentifierType.InternalSerial : IdentifierType.VendorSerial, serial, issuer);
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item,
            origin, [new CommissionLine([identifier])], At, "operations", Entity,
            LocationId: warehouse, LocationKind: warehouse is null ? null : LocationKind.Warehouse));
        return new(tenant, Assert.Single(commissioned.Lines).UnitId!.Value, item, warehouse, customer, serial);
    }

    private async Task SetIssuePointAsync(TenantId tenant, string issuePoint,
        IReadOnlyList<ReleaseRecordIssuePointOverride>? overrides = null)
    {
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<ReleaseRecordIssuePointSettings>();
        var current = await settings.GetAsync(CancellationToken.None);
        await settings.SetAsync(new UpdateReleaseRecordIssuePointSettings(issuePoint, overrides ?? [], current.Version),
            CancellationToken.None);
    }

    private Task<ReleaseUnitResult> ReleaseAsync(Setup setup) => InvokeAsync<ReleaseUnitResult>(setup.Tenant,
        new ReleaseUnit(Guid.NewGuid(), setup.UnitId, At.AddHours(1), "quality", Entity, "sig:quality", Revision: "A"));

    private async Task SellAsync(Setup setup, DateTime at)
    {
        await InvokeAsync<ChangeCustodyResult>(setup.Tenant, new ChangeCustody(Guid.NewGuid(), setup.UnitId,
            LocationKind.CustomerSite, EpisodeReason.Sale, at, "sales", Entity,
            Guid.NewGuid(), setup.Customer, PartyType.Customer));
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId,
            setup.Customer, PartyType.Customer, EpisodeReason.Sale, at, "sales", Entity));
    }

    private Task<OpenOwnershipResult> OpenOurOwnershipAsync(Setup setup, DateTime at) =>
        InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.UnitId,
            Entity, PartyType.Us, EpisodeReason.Production, at, "operations", Entity));

    private async Task RefurbishAsync(Setup setup, string suffix, DateTime at)
    {
        using (var scope = fixture.ScopeFor(setup.Tenant))
            await scope.ServiceProvider.GetRequiredService<RefurbishmentPolicySettings>().SetAsync(
                new RefurbishmentPolicy(RefurbishmentIdentity.SameUnitSameSerial, 3, []), CancellationToken.None);
        var opened = await InvokeAsync<OpenRefurbishmentResult>(setup.Tenant, new OpenRefurbishment(Guid.NewGuid(),
            setup.UnitId, RefurbishmentKind.Refurbish, at, "quality", Entity, Guid.NewGuid()));
        await InvokeAsync<RecordTestResult>(setup.Tenant, new RecordTest(Guid.NewGuid(), setup.UnitId,
            RefurbishmentTestKind.KeyFunctions, UnitTestResult.Pass, "{}", "fictional-fixture-" + suffix,
            at.AddMinutes(15), "quality", Entity));
        await InvokeAsync<ReleaseRefurbishmentResult>(setup.Tenant, new ReleaseRefurbishment(Guid.NewGuid(),
            opened.WorkOrderId, ConditionGrades.RefurbishedA, "graded_receipt_plus_postings", at.AddHours(1),
            "quality", Entity, setup.Location ?? Guid.NewGuid(), null, "F2", "C2"));
    }

    private async Task<CertificateSelectionPreview> PreviewAsync(Setup setup)
    {
        var selection = await InvokeAsync<ResolveSelectionSetResult>(setup.Tenant, new ResolveSelectionSet(Guid.NewGuid(),
            SelectionSource.ByPastedList([setup.Serial]), "quality"));
        using var scope = fixture.ScopeFor(setup.Tenant);
        return await scope.ServiceProvider.GetRequiredService<CertificateSelectionService>().PreviewAsync(
            new CertificateSelectionPreviewRequest(selection.SelectionId, CertificateDocumentKind.ReleaseRecord,
                CertificateFormType.GenericRelease, Entity, "en"), CancellationToken.None);
    }

    private async Task IssueSelectionAsync(Setup setup)
    {
        var selection = await InvokeAsync<ResolveSelectionSetResult>(setup.Tenant, new ResolveSelectionSet(Guid.NewGuid(),
            SelectionSource.ByPastedList([setup.Serial]), "quality"));
        await InvokeAsync<CertificateRunResult>(setup.Tenant, new IssueCertificate(Guid.NewGuid(), selection.SelectionId,
            CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease, Entity, "en",
            "Requested release record", false, "quality"));
    }

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<int> CountAsync(TenantId tenant, Guid unitId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().CertificateInstances
            .CountAsync(row => row.UnitId == unitId && row.DocumentKind == CertificateDocumentKind.ReleaseRecord);
    }

    private async Task<string> PrimarySerialAsync(TenantId tenant, Guid unitId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Identifiers.AsNoTracking()
            .Where(row => row.UnitId == unitId && row.ValidTo == null && row.Type == IdentifierType.InternalSerial)
            .Select(row => row.RawValue).SingleAsync();
    }

    private async Task WaitForCountAsync(TenantId tenant, Guid unitId, int count)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (await CountAsync(tenant, unitId) >= count) return;
            await Task.Delay(100);
        }
        Assert.Equal(count, await CountAsync(tenant, unitId));
    }

    private async Task WaitForSettledAsync(TenantId tenant, Guid unitId)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            if (await db.CertificateTriggers.AnyAsync(row => row.UnitId == unitId
                    && row.DocumentKind == CertificateDocumentKind.ReleaseRecord)) return;
            await Task.Delay(100);
        }
    }

    private async Task WaitForNewTriggersAsync(TenantId tenant, Guid unitId, HashSet<Guid> existing, int count)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            if (await db.CertificateTriggers.CountAsync(row => row.UnitId == unitId
                    && !existing.Contains(row.TriggeringEventId)) >= count) return;
            await Task.Delay(100);
        }

        using var finalScope = fixture.ScopeFor(tenant);
        var finalDb = finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(count, await finalDb.CertificateTriggers.CountAsync(row => row.UnitId == unitId
            && !existing.Contains(row.TriggeringEventId)));
    }

    private async Task AssertWholeUnitAsync(Setup setup, Guid? owner, string? custodyKind, int certificateVersions)
    {
        using var scope = fixture.ScopeFor(setup.Tenant);
        await AssertWholeUnitAsync(scope.ServiceProvider.GetRequiredService<SerialisationDbContext>(), setup,
            owner, custodyKind, certificateVersions);
    }

    private static async Task AssertWholeUnitAsync(SerialisationDbContext db, Setup setup, Guid? owner,
        string? custodyKind, int certificateVersions)
    {
        var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == setup.UnitId);
        Assert.Equal(owner, current.OwnerPartyRef);
        Assert.Equal(custodyKind, current.LocationKind);
        Assert.Equal("[]", current.LiveEntitlementIds);
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == setup.UnitId && row.ValidTo == null).ToListAsync());
        Assert.Empty(await db.Entitlements.Where(row => row.UnitId == setup.UnitId).ToListAsync());
        Assert.Equal(certificateVersions, await db.Events.CountAsync(row => row.UnitId == setup.UnitId
            && row.EventType == CertificateEventType.Issued));
        Assert.Equal(certificateVersions, await db.CertificateInstances.CountAsync(row => row.UnitId == setup.UnitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));
    }

    private sealed record Setup(TenantId Tenant, Guid UnitId, Guid ItemId, Guid? Location, Guid Customer, string Serial);
}
