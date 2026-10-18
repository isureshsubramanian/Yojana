using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Certificates;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P6-T20 defect coverage: every customer-sale writer crosses the ledger append observer boundary.</summary>
[Collection("serialisation host")]
public sealed class CertificateSaleHookTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Entity = Guid.Parse("ca000000-0000-0000-0000-000000000021");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);
    private static readonly CorrectionCommitter Committer = new("shipping-supervisor",
        [YojanaRole.ShippingSupervisor, YojanaRole.ServiceSupervisor]);

    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();

    [InlineData(ReleaseRecordIssuePoint.AtFirstShipment)]
    [InlineData(ReleaseRecordIssuePoint.AtBuildRelease)]
    [DatabaseTheory]
    public async Task Sales_shipment_documents_issue_once_through_single_and_500_unit_bulk_paths(
        string issuePoint)
    {
        const int bulkCount = 500;
        var tenant = await fixture.NewTenantAsync("cert-sale-doc-" + issuePoint);
        await SetIssuePointAsync(tenant, issuePoint);
        var item = RegisterItem("document-" + issuePoint);
        var warehouse = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var site = Guid.NewGuid();
        var prefix = "SH" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var serials = Enumerable.Range(0, bulkCount + 1).Select(i => $"{prefix}-{i:0000}").ToList();
        var receipt = new ReceiptDocument(new("receipt", Guid.NewGuid()), Guid.NewGuid(), At,
            [new DocumentLine(1, item, serials.Count, serials.Select(value => new DocumentSerial(value)).ToList(),
                warehouse, LocationKind.Warehouse)]);
        Documents.Add(receipt);
        Assert.Equal(serials.Count, (await InvokeAsync<ConsumedEventResult>(tenant,
            new PurchasingReceiptPosted(Envelope(At), receipt.Document))).UnitsAffected);

        var single = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, At.AddHours(1),
            [new DocumentLine(1, item, 1, [new(serials[0])])], site);
        Documents.Add(single);
        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(Envelope(single.PostedAt), single.Document))).UnitsAffected);
        var singleUnit = await UnitIdAsync(tenant, serials[0]);
        await WaitForActiveCountAsync(tenant, [singleUnit], 1);

        var bulk = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, At.AddHours(2),
            [new DocumentLine(1, item, bulkCount,
                serials.Skip(1).Select(value => new DocumentSerial(value)).ToList())], site);
        Documents.Add(bulk);
        Assert.Equal(bulkCount, (await InvokeAsync<ConsumedEventResult>(tenant,
            new SalesShipmentPosted(Envelope(bulk.PostedAt), bulk.Document))).UnitsAffected);
        var unitIds = await UnitIdsAsync(tenant, serials);
        await WaitForActiveCountAsync(tenant, unitIds, serials.Count);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var current = await db.Current.AsNoTracking().Where(row => unitIds.Contains(row.UnitId)).ToListAsync();
        Assert.Equal(serials.Count, current.Count);
        Assert.All(current, row =>
        {
            Assert.Null(row.OwnerPartyRef);
            Assert.Equal(LocationKind.InTransit, row.LocationKind);
            Assert.Null(row.LocationId);
            Assert.Equal("[]", row.LiveEntitlementIds);
        });
        Assert.Equal(serials.Count, await db.Identifiers.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.ValidTo == null));
        Assert.Equal(0, await db.Episodes.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null));
        Assert.Equal(serials.Count, await db.Episodes.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null
            && row.LocationKind == LocationKind.InTransit));
        Assert.Empty(await db.Entitlements.Where(row => unitIds.Contains(row.UnitId)).ToListAsync());
        Assert.Equal(serials.Count, await db.Events.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.EventType == UnitEventType.Shipped));
        Assert.Equal(0, await db.Events.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.EventType == UnitEventType.OwnershipOpened));
        Assert.Equal(serials.Count, await db.Events.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.EventType == CertificateEventType.Issued));
        Assert.Equal(serials.Count, await db.CertificateTriggers.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord));

        // This 500-unit case deliberately produces a large published-event tail after the transactional certificate
        // hand-off has finished. Do not leave that tail for the next database test to inherit.
        await WaitForMessagingIdleAsync(tenant);
    }

    [InlineData(ReleaseRecordIssuePoint.AtFirstShipment)]
    [InlineData(ReleaseRecordIssuePoint.AtBuildRelease)]
    [DatabaseTheory]
    public async Task Vendor_drop_shipment_document_issues_from_customer_custody_without_duplicate_ownership_work(
        string issuePoint)
    {
        var tenant = await fixture.NewTenantAsync("cert-sale-drop-" + issuePoint);
        await SetIssuePointAsync(tenant, issuePoint);
        var item = RegisterItem("drop-" + issuePoint);
        var customer = Guid.NewGuid();
        var site = Guid.NewGuid();
        var serial = "DS" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var shipment = new VendorShipment(new("vendor_shipment", Guid.NewGuid()), Guid.NewGuid(), customer,
            site, At.AddHours(1), [new DocumentLine(1, item, 1, [new(serial)])],
            "fictional-carrier-ref", DeliveredAt: At.AddHours(3));
        Documents.Add(shipment);

        Assert.Equal(1, (await InvokeAsync<ConsumedEventResult>(tenant,
            new PurchasingVendorShipConfirmed(Envelope(At.AddHours(10)), shipment.Document))).UnitsAffected);
        var unitId = await UnitIdAsync(tenant, serial);
        await WaitForActiveCountAsync(tenant, [unitId], 1);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var current = await db.Current.AsNoTracking().SingleAsync(row => row.UnitId == unitId);
        Assert.Equal(customer, current.OwnerPartyRef);
        Assert.Equal(LocationKind.CustomerSite, current.LocationKind);
        Assert.Equal(site, current.LocationId);
        Assert.Equal("[]", current.LiveEntitlementIds);
        Assert.Single(await db.Identifiers.Where(row => row.UnitId == unitId && row.ValidTo == null).ToListAsync());
        Assert.Single(await db.Episodes.Where(row => row.UnitId == unitId
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null
            && row.PartyRef == customer).ToListAsync());
        Assert.Single(await db.Episodes.Where(row => row.UnitId == unitId
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null
            && row.LocationKind == LocationKind.CustomerSite && row.LocationId == site).ToListAsync());
        Assert.Empty(await db.Entitlements.Where(row => row.UnitId == unitId).ToListAsync());
        Assert.Single(await db.Events.Where(row => row.UnitId == unitId
            && row.EventType == UnitEventType.Delivered).ToListAsync());
        Assert.Single(await db.Events.Where(row => row.UnitId == unitId
            && row.EventType == UnitEventType.OwnershipOpened).ToListAsync());
        Assert.Single(await db.CertificateTriggers.Where(row => row.UnitId == unitId
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord).ToListAsync());
    }

    [InlineData(ReleaseRecordIssuePoint.AtFirstShipment)]
    [InlineData(ReleaseRecordIssuePoint.AtBuildRelease)]
    [DatabaseTheory]
    public async Task Shipment_correction_withdraws_wrong_v1_and_issues_the_right_unit_v1(string issuePoint)
    {
        var tenant = await fixture.NewTenantAsync("cert-sale-correction-" + issuePoint);
        await SetIssuePointAsync(tenant, issuePoint);
        var item = RegisterItem("correction-" + issuePoint);
        var warehouse = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var site = Guid.NewGuid();
        var marker = "SC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var serials = new[] { marker + "-REC", marker + "-PHY" };
        var receipt = new ReceiptDocument(new("receipt", Guid.NewGuid()), Guid.NewGuid(), At,
            [new DocumentLine(1, item, 2, serials.Select(value => new DocumentSerial(value)).ToList(),
                warehouse, LocationKind.Warehouse)]);
        Documents.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Envelope(At), receipt.Document));
        var shipment = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, At.AddHours(1),
            [new DocumentLine(1, item, 1, [new(serials[0])])], site);
        Documents.Add(shipment);
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Envelope(shipment.PostedAt), shipment.Document));
        var unitIds = await UnitIdsAsync(tenant, serials);
        var recordedUnit = await UnitIdAsync(tenant, serials[0]);
        var physicalUnit = await UnitIdAsync(tenant, serials[1]);
        await WaitForActiveCountAsync(tenant, [recordedUnit], 1);

        var recorded = await SelectAsync(tenant, [serials[0]]);
        var physical = await SelectAsync(tenant, [serials[1]]);
        var correctionId = Guid.NewGuid();
        await InvokeAsync<CorrectDocumentSerialsResult>(tenant, new CorrectDocumentSerials(Guid.NewGuid(),
            shipment.Document, recorded, physical, CorrectDocumentSerialsReason.ShippedWrongUnits,
            At.AddHours(2), Committer.UserRef, Entity,
            new(correctionId, CorrectDocumentSerialsReason.ShippedWrongUnits,
                InitiatingRole: CorrectionInitiatingRole.WarehouseLead), Committer));
        await WaitForCorrectedCertificatesAsync(tenant, recordedUnit, physicalUnit);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var states = await db.Current.AsNoTracking().Where(row => unitIds.Contains(row.UnitId))
            .ToDictionaryAsync(row => row.UnitId);
        Assert.Null(states[recordedUnit].OwnerPartyRef);
        Assert.Equal(LocationKind.Warehouse, states[recordedUnit].LocationKind);
        Assert.Equal(warehouse, states[recordedUnit].LocationId);
        Assert.Null(states[physicalUnit].OwnerPartyRef);
        Assert.Equal(LocationKind.InTransit, states[physicalUnit].LocationKind);
        Assert.Null(states[physicalUnit].LocationId);
        Assert.All(states.Values, row => Assert.Equal("[]", row.LiveEntitlementIds));
        Assert.Equal(0, await db.Episodes.CountAsync(row => unitIds.Contains(row.UnitId)
            && row.Kind == EpisodeKind.Ownership && row.ValidTo == null));
        Assert.Single(await db.Episodes.Where(row => row.UnitId == recordedUnit
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null
            && row.LocationKind == LocationKind.Warehouse && row.LocationId == warehouse).ToListAsync());
        Assert.Single(await db.Episodes.Where(row => row.UnitId == physicalUnit
            && row.Kind == EpisodeKind.Custody && row.ValidTo == null
            && row.LocationKind == LocationKind.InTransit).ToListAsync());
        Assert.Empty(await db.Entitlements.Where(row => unitIds.Contains(row.UnitId)).ToListAsync());
        Assert.Equal(2, await db.Identifiers.CountAsync(row => unitIds.Contains(row.UnitId) && row.ValidTo == null));

        var wrong = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.UnitId == recordedUnit
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord);
        var right = await db.CertificateInstances.AsNoTracking().SingleAsync(row => row.UnitId == physicalUnit
            && row.DocumentKind == CertificateDocumentKind.ReleaseRecord);
        Assert.Equal((1, CertificateStatus.Superseded), (wrong.Version, wrong.Status));
        Assert.Equal((1, CertificateStatus.Issued), (right.Version, right.Status));
        Assert.Single(await db.Events.Where(row => row.UnitId == recordedUnit
            && row.EventType == UnitEventType.ShipmentReversed).ToListAsync());
        Assert.Single(await db.Events.Where(row => row.UnitId == recordedUnit
            && row.EventType == CertificateEventType.Withdrawn).ToListAsync());
        Assert.Single(await db.Events.Where(row => row.UnitId == physicalUnit
            && row.EventType == UnitEventType.Shipped).ToListAsync());
        var preview = await scope.ServiceProvider.GetRequiredService<CertificateSelectionService>().PreviewAsync(
            new(recorded, CertificateDocumentKind.ReleaseRecord, CertificateFormType.GenericRelease,
                Entity, "en"), CancellationToken.None);
        var recordedPreview = Assert.Single(preview.Units);
        Assert.Equal(CertificateSelectionOutcome.NotIssuable, recordedPreview.Outcome);
        Assert.Equal("release_record_issue_point_not_reached", recordedPreview.Reason);
        var publication = Assert.Single(await PublishedEventSink.WaitForAsync<CertificateWithdrawn>(row =>
            row.Header.UnitId == recordedUnit && row.TriggeringCorrectionEventId != Guid.Empty, 1, Timeout));
        Assert.Equal(CertificateSupersedeReason.Correction, publication.Reason);
    }

    private Guid RegisterItem(string suffix)
    {
        var item = Guid.NewGuid();
        fixture.Services.GetRequiredService<InMemoryItemPorts>().Register(InMemoryItemPorts.DefaultProfile(item) with
        {
            ItemClass = "sale-hook",
        });
        fixture.Services.GetRequiredService<InMemoryItemNamePort>().Register(
            new ItemLabel(item, "FIC-" + suffix.ToUpperInvariant(), "Fictional sale-hook unit"));
        return item;
    }

    private async Task SetIssuePointAsync(TenantId tenant, string issuePoint)
    {
        using var scope = fixture.ScopeFor(tenant);
        var settings = scope.ServiceProvider.GetRequiredService<ReleaseRecordIssuePointSettings>();
        var current = await settings.GetAsync(CancellationToken.None);
        await settings.SetAsync(new UpdateReleaseRecordIssuePointSettings(issuePoint, [], current.Version),
            CancellationToken.None);
    }

    private async Task<Guid> SelectAsync(TenantId tenant, IReadOnlyList<string> serials)
    {
        var result = await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(Guid.NewGuid(),
            SelectionSource.ByPastedList(serials), "shipping-supervisor"));
        return result.SelectionId;
    }

    private async Task<Guid> UnitIdAsync(TenantId tenant, string serial) =>
        Assert.Single(await UnitIdsAsync(tenant, [serial]));

    private async Task<List<Guid>> UnitIdsAsync(TenantId tenant, IReadOnlyCollection<string> serials)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Identifiers.AsNoTracking()
            .Where(row => serials.Contains(row.RawValue) && row.ValidTo == null)
            .OrderBy(row => row.RawValue).Select(row => row.UnitId).ToListAsync();
    }

    private async Task WaitForActiveCountAsync(TenantId tenant, IReadOnlyCollection<Guid> unitIds, int count)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = fixture.ScopeFor(tenant);
            var found = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
                .CertificateInstances.CountAsync(row => unitIds.Contains(row.UnitId)
                    && row.DocumentKind == CertificateDocumentKind.ReleaseRecord
                    && row.Status == CertificateStatus.Issued);
            if (found == count) return;
            await Task.Delay(100);
        }

        using var finalScope = fixture.ScopeFor(tenant);
        Assert.Equal(count, await finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
            .CertificateInstances.CountAsync(row => unitIds.Contains(row.UnitId)
                && row.DocumentKind == CertificateDocumentKind.ReleaseRecord
                && row.Status == CertificateStatus.Issued));
    }

    private async Task WaitForMessagingIdleAsync(TenantId tenant)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            using var scope = fixture.ScopeFor(tenant);
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var pending = await db.Database.SqlQueryRaw<int>("""
                SELECT CAST(
                    (SELECT COUNT(*) FROM [wolverine].[wolverine_outgoing_envelopes])
                    + (SELECT COUNT(*) FROM [wolverine].[wolverine_incoming_envelopes]
                       WHERE [status] <> 'Handled') AS int) AS [Value]
                """).SingleAsync();
            if (pending == 0) return;
            await Task.Delay(100);
        }

        throw new TimeoutException("The Wolverine durable queues did not become idle after the 500-unit posting.");
    }

    private async Task WaitForCorrectedCertificatesAsync(TenantId tenant, Guid recordedUnit, Guid physicalUnit)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            using var scope = fixture.ScopeFor(tenant);
            var rows = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>()
                .CertificateInstances.AsNoTracking()
                .Where(row => row.UnitId == recordedUnit || row.UnitId == physicalUnit)
                .Select(row => new { row.UnitId, row.Status }).ToListAsync();
            if (rows.Any(row => row.UnitId == recordedUnit && row.Status == CertificateStatus.Superseded)
                && rows.Any(row => row.UnitId == physicalUnit && row.Status == CertificateStatus.Issued))
                return;
            await Task.Delay(100);
        }

        Assert.Fail("Certificate correction work did not settle before the timeout.");
    }

    private static ThinEnvelope Envelope(DateTime occurredAt) =>
        new(Guid.NewGuid(), Entity, occurredAt, "sale-hook-test");

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }
}
