using System.Text.Json;
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
/// P3-T12 / case 141 / tickets T66a and T71: partial and whole-list replacement are the same selection-set command;
/// the correction is zero-stock-delta, entitlement-preserving and reversible by a later correction.
/// </summary>
[Collection("serialisation host")]
public sealed class CorrectDocumentSerialsTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime ReceivedAt = new(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ShippedAt = ReceivedAt.AddDays(1);
    private static readonly CorrectionCommitter Committer = new("shipping-supervisor", [YojanaRole.ShippingSupervisor, YojanaRole.ServiceSupervisor]);

    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();
    private InMemoryStockUnitEventPort Stock => fixture.Services.GetRequiredService<InMemoryStockUnitEventPort>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Whole_list_correction_moves_custody_and_cover_publishes_the_summary_and_can_be_reversed()
    {
        var tenant = await fixture.NewTenantAsync("cds");
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var site = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var marker = "CD" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var serials = Enumerable.Range(1, 4).Select(i => $"{marker}-{i:000}").ToList();

        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base, "Base warranty", false, AssuranceOrService.Assurance));
        var code = "CDS-" + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2026, 1, 1), PolicyValidatorTestsTerms.Standard24(), "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));

        var receipt = new ReceiptDocument(new("receipt", Guid.NewGuid()), Guid.NewGuid(), ReceivedAt,
            [new DocumentLine(1, item, 4, serials.Select(s => new DocumentSerial(s)).ToList(), warehouse, LocationKind.Warehouse)]);
        Documents.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Envelope(ReceivedAt), receipt.Document));
        var shipment = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, ShippedAt,
            [new DocumentLine(1, item, 2, serials.Take(2).Select(s => new DocumentSerial(s)).ToList())],
            site, IsDropShip: true, CustomerPoRef: "PO-141", IsInvoiced: true);
        Documents.Add(shipment);
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(Envelope(ShippedAt), shipment.Document));

        var units = await UnitIdsAsync(tenant, serials);
        var identifierIds = await IdentifierIdsAsync(tenant, units);
        foreach (var unit in units.Take(2))
        {
            await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, CoverageFamilyCode.Base,
                DateOnly.FromDateTime(ShippedAt), Entity, ShippedAt, "entitlement", new Dictionary<string, DateTime> { [StartTrigger.Deliver] = ShippedAt },
                draft.PolicyVersionId, customer, PartyType.Customer, SourceDocument: shipment.Document, CustomerPoRef: "PO-141"));
        }

        var recorded = await SelectAsync(tenant, serials.Take(2).ToList());
        var physical = await SelectAsync(tenant, serials.Skip(2).ToList());
        var correctionId = Guid.NewGuid();
        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewCorrectDocumentSerials(shipment.Document,
            recorded, physical, CorrectDocumentSerialsReason.ShippedWrongUnits, ShippedAt.AddDays(2), Committer.UserRef, Entity,
            new(correctionId, CorrectDocumentSerialsReason.ShippedWrongUnits, EvidenceRefs: ["photo:141"], InitiatingRole: CorrectionInitiatingRole.WarehouseLead), Committer));
        Assert.Equal((4, 4, 0), (preview.UnitCount, preview.Change, preview.Block));
        Assert.Equal(4, preview.UnitEvents.Count);
        Assert.Equal(4, preview.EntitlementChanges.Count);

        var commandId = Guid.NewGuid();
        var first = await InvokeAsync<CorrectDocumentSerialsResult>(tenant, new CorrectDocumentSerials(commandId, shipment.Document,
            recorded, physical, CorrectDocumentSerialsReason.ShippedWrongUnits, ShippedAt.AddDays(2), Committer.UserRef, Entity,
            new(correctionId, CorrectDocumentSerialsReason.ShippedWrongUnits, EvidenceRefs: ["photo:141"], InitiatingRole: CorrectionInitiatingRole.WarehouseLead), Committer));
        Assert.Equal((2, 2, 2, 2, true, true), (first.Reversed, first.Shipped, first.EntitlementsVoided,
            first.EntitlementsCreated, first.WholeListReplacement, first.FinanceReviewRequired));
        Assert.True((await InvokeAsync<CorrectDocumentSerialsResult>(tenant, new CorrectDocumentSerials(commandId, shipment.Document,
            recorded, physical, CorrectDocumentSerialsReason.ShippedWrongUnits, ShippedAt.AddDays(2), Committer.UserRef, Entity,
            new(correctionId, CorrectDocumentSerialsReason.ShippedWrongUnits, InitiatingRole: CorrectionInitiatingRole.WarehouseLead), Committer))).Replayed);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var state = await db.Current.Where(c => units.Contains(c.UnitId)).ToDictionaryAsync(c => c.UnitId);
            Assert.All(units.Take(2), id => Assert.Null(state[id].OwnerPartyRef));
            Assert.All(units.Skip(2), id => Assert.Equal(customer, state[id].OwnerPartyRef));
            Assert.All(units.Take(2), id => Assert.Equal(warehouse, state[id].LocationId));
            Assert.All(units.Skip(2), id => Assert.Equal(site, state[id].LocationId));
            Assert.All(units.Take(2), id => Assert.Equal(LocationKind.Warehouse, state[id].LocationKind));
            Assert.All(units.Skip(2), id => Assert.Equal(LocationKind.CustomerSite, state[id].LocationKind));
            Assert.All(units.Take(2), id => Assert.Equal("[]", state[id].LiveEntitlementIds));
            Assert.All(units.Skip(2), id => Assert.NotEqual("[]", state[id].LiveEntitlementIds));
            var openEpisodes = await db.Episodes.Where(row => units.Contains(row.UnitId) && row.ValidTo == null).ToListAsync();
            Assert.All(units.Take(2), id =>
            {
                Assert.DoesNotContain(openEpisodes, row => row.UnitId == id && row.Kind == EpisodeKind.Ownership);
                var custody = Assert.Single(openEpisodes, row => row.UnitId == id && row.Kind == EpisodeKind.Custody);
                Assert.Equal((warehouse, LocationKind.Warehouse), (custody.LocationId, custody.LocationKind));
            });
            Assert.All(units.Skip(2), id =>
            {
                var ownership = Assert.Single(openEpisodes, row => row.UnitId == id && row.Kind == EpisodeKind.Ownership);
                Assert.Equal((customer, PartyType.Customer), (ownership.PartyRef, ownership.PartyType));
                var custody = Assert.Single(openEpisodes, row => row.UnitId == id && row.Kind == EpisodeKind.Custody);
                Assert.Equal((site, LocationKind.CustomerSite), (custody.LocationId, custody.LocationKind));
            });
            var correctionFacts = await db.Events.Where(e => units.Contains(e.UnitId)
                && (e.EventType == UnitEventType.ShipmentReversed || e.EventType == UnitEventType.Shipped)
                && e.DocumentId == shipment.Document.Id).OrderBy(e => e.Seq).ToListAsync();
            Assert.Equal(6, correctionFacts.Count); // two original shipments, two reversals, two corrective shipments
            Assert.All(correctionFacts.Where(e => e.EventType == UnitEventType.ShipmentReversed), e => Assert.Equal(ShippedAt, e.EventTime));
            using var attributes = JsonDocument.Parse(correctionFacts.First(e => e.EventType == UnitEventType.ShipmentReversed).Attributes!);
            Assert.Equal(shipment.Document.Id, attributes.RootElement.GetProperty("correction_of_document_id").GetGuid());
            Assert.Equal(correctionId, attributes.RootElement.GetProperty("correction_id").GetGuid());
            var cover = await db.Entitlements.Where(e => units.Contains(e.UnitId)).ToListAsync();
            Assert.All(cover.Where(e => units.Take(2).Contains(e.UnitId)), e => Assert.Equal(EntitlementClosedReason.VoidedDocumentCorrection, e.ClosedReason));
            Assert.All(cover.Where(e => units.Skip(2).Contains(e.UnitId)), e => Assert.Equal(shipment.Document.Id, e.SourceDocumentId));
            Assert.Equal(identifierIds, await db.Identifiers.Where(row => units.Contains(row.UnitId) && row.ValidTo == null)
                .OrderBy(row => row.IdentifierId).Select(row => row.IdentifierId).ToListAsync());
        }

        Assert.Contains(Stock.Corrections, c => c.CorrectionId == correctionId && c.QuantityDelta == 0 && c.RecordedUnitIds.Count == 2);
        var summary = await WaitForSummaryAsync(correctionId);
        Assert.True(summary.FinanceReviewRequired);
        Assert.True(summary.WholeListReplacement);
        Assert.Equal(2, (await PublishedEventSink.WaitForAsync<CustodyChanged>(e => e.Header.Document?.Id == shipment.Document.Id
            && e.TriggerEventType == UnitEventType.ShipmentReversed, 2, TimeSpan.FromSeconds(20))).Count);

        var reversalId = Guid.NewGuid();
        var reversal = await InvokeAsync<CorrectDocumentSerialsResult>(tenant, new CorrectDocumentSerials(Guid.NewGuid(), shipment.Document,
            physical, recorded, CorrectDocumentSerialsReason.ShippedWrongUnits, ShippedAt.AddDays(3), Committer.UserRef, Entity,
            new(reversalId, CorrectDocumentSerialsReason.ShippedWrongUnits, CorrectsCorrectionId: correctionId,
                InitiatingRole: CorrectionInitiatingRole.WarehouseLead), Committer));
        Assert.Equal(2, reversal.Reversed);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(correctionId, (await db.Corrections.SingleAsync(c => c.CorrectionId == reversalId)).CorrectsCorrectionId);
            var state = await db.Current.Where(c => units.Contains(c.UnitId)).ToDictionaryAsync(c => c.UnitId);
            Assert.All(units.Take(2), id =>
            {
                Assert.Equal(customer, state[id].OwnerPartyRef);
                Assert.NotEqual("[]", state[id].LiveEntitlementIds);
            });
            Assert.All(units.Skip(2), id =>
            {
                Assert.Null(state[id].OwnerPartyRef);
                Assert.Equal("[]", state[id].LiveEntitlementIds);
            });
            Assert.All(units.Take(2), id => Assert.Equal(site, state[id].LocationId));
            Assert.All(units.Skip(2), id => Assert.Equal(warehouse, state[id].LocationId));
            Assert.All(units.Take(2), id => Assert.Equal(LocationKind.CustomerSite, state[id].LocationKind));
            Assert.All(units.Skip(2), id => Assert.Equal(LocationKind.Warehouse, state[id].LocationKind));
            var openEpisodes = await db.Episodes.Where(row => units.Contains(row.UnitId) && row.ValidTo == null).ToListAsync();
            Assert.All(units.Take(2), id =>
            {
                var ownership = Assert.Single(openEpisodes, row => row.UnitId == id && row.Kind == EpisodeKind.Ownership);
                Assert.Equal((customer, PartyType.Customer), (ownership.PartyRef, ownership.PartyType));
                var custody = Assert.Single(openEpisodes, row => row.UnitId == id && row.Kind == EpisodeKind.Custody);
                Assert.Equal((site, LocationKind.CustomerSite), (custody.LocationId, custody.LocationKind));
            });
            Assert.All(units.Skip(2), id =>
            {
                Assert.DoesNotContain(openEpisodes, row => row.UnitId == id && row.Kind == EpisodeKind.Ownership);
                var custody = Assert.Single(openEpisodes, row => row.UnitId == id && row.Kind == EpisodeKind.Custody);
                Assert.Equal((warehouse, LocationKind.Warehouse), (custody.LocationId, custody.LocationKind));
            });
            Assert.Equal(identifierIds, await db.Identifiers.Where(row => units.Contains(row.UnitId) && row.ValidTo == null)
                .OrderBy(row => row.IdentifierId).Select(row => row.IdentifierId).ToListAsync());
            Assert.Equal(10, await db.Events.CountAsync(row => units.Contains(row.UnitId)
                && (row.EventType == UnitEventType.Shipped || row.EventType == UnitEventType.ShipmentReversed)
                && row.DocumentId == shipment.Document.Id));
        }
    }

    [DatabaseFact]
    public async Task Count_and_item_mismatch_are_named_refusals_and_write_nothing()
    {
        var tenant = await fixture.NewTenantAsync("cdv");
        var warehouse = Guid.NewGuid();
        var a = await CommissionAsync(tenant, Guid.NewGuid(), warehouse, "CVA" + Guid.NewGuid().ToString("N")[..5]);
        var b = await CommissionAsync(tenant, Guid.NewGuid(), warehouse, "CVB" + Guid.NewGuid().ToString("N")[..5]);
        var document = new TransferDocument(new("transfer", Guid.NewGuid()), ShippedAt, warehouse, LocationKind.Warehouse,
            Guid.NewGuid(), LocationKind.Warehouse, [new DocumentLine(1, a.Item, 1, [new(a.Serial)])]);
        Documents.Add(document);
        await InvokeAsync<ConsumedEventResult>(tenant, new InventoryTransferPosted(Envelope(ShippedAt), document.Document));
        var recorded = await SelectAsync(tenant, [a.Serial]);
        var empty = await SelectAsync(tenant, []);
        var count = await InvokeAsync<CorrectionPreview>(tenant, new PreviewCorrectDocumentSerials(document.Document, recorded, empty,
            CorrectDocumentSerialsReason.ShippedWrongUnits, ShippedAt.AddDays(1), "x", Entity, CommittedBy: Committer));
        Assert.Equal("document_correction_count_mismatch", count.Refusal!.Rule);
        var other = await SelectAsync(tenant, [b.Serial]);
        var item = await InvokeAsync<CorrectionPreview>(tenant, new PreviewCorrectDocumentSerials(document.Document, recorded, other,
            CorrectDocumentSerialsReason.ShippedWrongUnits, ShippedAt.AddDays(1), "x", Entity, CommittedBy: Committer));
        Assert.Equal("document_correction_item_mismatch", item.Refusal!.Rule);
    }

    [DatabaseFact]
    public async Task A_subset_of_the_document_is_a_partial_replacement()
    {
        var tenant = await fixture.NewTenantAsync("cdp");
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var marker = "CP" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var serials = new[] { $"{marker}-1", $"{marker}-2", $"{marker}-3" };
        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            serials.Select(s => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, s)])).ToList(),
            ReceivedAt, "test", Entity, LocationId: warehouse, LocationKind: LocationKind.Warehouse));
        var document = new TransferDocument(new("transfer", Guid.NewGuid()), ShippedAt, warehouse, LocationKind.Warehouse,
            destination, LocationKind.Warehouse, [new DocumentLine(1, item, 2, serials.Take(2).Select(s => new DocumentSerial(s)).ToList())]);
        Documents.Add(document);
        await InvokeAsync<ConsumedEventResult>(tenant, new InventoryTransferPosted(Envelope(ShippedAt), document.Document));

        var recorded = await SelectAsync(tenant, [serials[0]]);
        var physical = await SelectAsync(tenant, [serials[2]]);
        var result = await InvokeAsync<CorrectDocumentSerialsResult>(tenant, new CorrectDocumentSerials(Guid.NewGuid(), document.Document,
            recorded, physical, CorrectDocumentSerialsReason.ShippedWrongUnits, ShippedAt.AddDays(1), Committer.UserRef, Entity,
            new(Guid.NewGuid(), CorrectDocumentSerialsReason.ShippedWrongUnits, InitiatingRole: CorrectionInitiatingRole.WarehouseLead), Committer));
        Assert.False(result.WholeListReplacement);
        Assert.Equal((1, 1), (result.Reversed, result.Shipped));
    }

    [DatabaseFact]
    public async Task A_500_serial_correction_has_bounded_statement_count()
    {
        const int count = 500;
        var tenant = await fixture.NewTenantAsync("cd5");
        var item = Guid.NewGuid();
        var source = Guid.NewGuid();
        var destination = Guid.NewGuid();
        var marker = "C5" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var recordedSerials = Enumerable.Range(1, count).Select(i => $"{marker}-R{i:0000}").ToList();
        var physicalSerials = Enumerable.Range(1, count).Select(i => $"{marker}-P{i:0000}").ToList();
        var all = recordedSerials.Concat(physicalSerials).ToList();
        var receipt = new ReceiptDocument(new("receipt", Guid.NewGuid()), Guid.NewGuid(), ReceivedAt,
            [new DocumentLine(1, item, all.Count, all.Select(s => new DocumentSerial(s)).ToList(), source, LocationKind.Warehouse)]);
        Documents.Add(receipt);
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(Envelope(ReceivedAt), receipt.Document));
        var transfer = new TransferDocument(new("transfer", Guid.NewGuid()), ShippedAt, source, LocationKind.Warehouse,
            destination, LocationKind.Warehouse, [new DocumentLine(1, item, count, recordedSerials.Select(s => new DocumentSerial(s)).ToList())]);
        Documents.Add(transfer);
        await InvokeAsync<ConsumedEventResult>(tenant, new InventoryTransferPosted(Envelope(ShippedAt), transfer.Document));
        var recorded = await SelectAsync(tenant, recordedSerials);
        var physical = await SelectAsync(tenant, physicalSerials);
        var command = new CorrectDocumentSerials(Guid.NewGuid(), transfer.Document, recorded, physical,
            CorrectDocumentSerialsReason.ShippedWrongUnits, ShippedAt.AddDays(1), Committer.UserRef, Entity,
            new(Guid.NewGuid(), CorrectDocumentSerialsReason.ShippedWrongUnits, InitiatingRole: CorrectionInitiatingRole.WarehouseLead), Committer);

        var (result, statements) = await StatementCounter.CountAsync(() => InvokeAsync<CorrectDocumentSerialsResult>(tenant, command));
        Assert.Equal((count, count), (result.Reversed, result.Shipped));
        // The ceiling allows EF's batched event, episode, projection and outbox writes, but not a read per selected unit.
        Assert.True(statements < 250, $"500-serial correction took {statements} statements");
    }

    private static ThinEnvelope Envelope(DateTime at) => new(Guid.NewGuid(), Entity, at, "correction-test");

    private async Task<Guid> SelectAsync(TenantId tenant, IReadOnlyList<string> serials)
    {
        var id = Guid.NewGuid();
        await InvokeAsync<ResolveSelectionSetResult>(tenant, new ResolveSelectionSet(id, SelectionSource.ByPastedList(serials), "test"));
        return id;
    }

    private async Task<List<Guid>> UnitIdsAsync(TenantId tenant, IReadOnlyList<string> serials)
    {
        using var scope = fixture.ScopeFor(tenant);
        var normalised = serials.Select(s => s.Replace("-", "", StringComparison.Ordinal)).ToList();
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Identifiers.AsNoTracking()
            .Where(i => normalised.Contains(i.NormalisedValue)).OrderBy(i => i.NormalisedValue).Select(i => i.UnitId).ToListAsync();
    }

    private async Task<List<Guid>> IdentifierIdsAsync(TenantId tenant, IReadOnlyList<Guid> units)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Identifiers.AsNoTracking()
            .Where(row => units.Contains(row.UnitId) && row.ValidTo == null).OrderBy(row => row.IdentifierId)
            .Select(row => row.IdentifierId).ToListAsync();
    }

    private async Task<(Guid Item, string Serial)> CommissionAsync(TenantId tenant, Guid item, Guid warehouse, string serial)
    {
        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], ReceivedAt, "test", Entity,
            LocationId: warehouse, LocationKind: LocationKind.Warehouse));
        return (item, serial);
    }

    private static async Task<DocumentSerialsCorrected> WaitForSummaryAsync(Guid correctionId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (PublishedEventSink.Summaries.OfType<DocumentSerialsCorrected>().FirstOrDefault(e => e.Correction.CorrectionId == correctionId) is { } found) return found;
            await Task.Delay(100);
        }
        return Assert.IsType<DocumentSerialsCorrected>(PublishedEventSink.Summaries.OfType<DocumentSerialsCorrected>().FirstOrDefault(e => e.Correction.CorrectionId == correctionId));
    }
}
