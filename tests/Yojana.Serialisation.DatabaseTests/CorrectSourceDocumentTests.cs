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

/// <summary>P3-T14 / case 143 / ticket T70: a subset moves to the neighbouring order without rewriting ledger history.</summary>
[Collection("serialisation host")]
public sealed class CorrectSourceDocumentTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime ReceivedAt = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ShippedAt = ReceivedAt.AddDays(2);
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly CorrectionCommitter OrderDesk = new("order-desk", [YojanaRole.OrderDeskSupervisor]);
    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();

    private sealed record Setup(Guid ItemId, Guid Customer, Guid CorrectedCustomer, DocumentRef WrongDelivery,
        DocumentRef WrongInvoice, DocumentRef CorrectOrder, DocumentRef CorrectInvoice, Guid OldPolicy, Guid CorrectPolicy,
        IReadOnlyList<string> Serials, IReadOnlyList<Guid> Units);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    [DatabaseFact]
    public async Task Thirty_six_of_fifty_six_are_repointed_rederived_relabelled_and_a_later_correction_reverses_them()
    {
        var tenant = await fixture.NewTenantAsync("csd");
        var setup = await ArrangeAsync(tenant, 56, ownerChanges: false, createWrongCover: true);
        var selection = await SelectAsync(tenant, setup.Serials.Take(36).ToList());
        Dictionary<Guid, List<Guid>> originalWrongFactIds;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            originalWrongFactIds = (await db.Events.AsNoTracking().Where(e => setup.Units.Take(36).Contains(e.UnitId)
                && e.DocumentType == setup.WrongDelivery.Type && e.DocumentId == setup.WrongDelivery.Id)
                .Select(e => new { e.UnitId, e.EventId }).ToListAsync()).GroupBy(e => e.UnitId)
                .ToDictionary(g => g.Key, g => g.Select(e => e.EventId).ToList());
        }
        var correctionId = Guid.NewGuid();
        var command = new CorrectSourceDocument(Guid.NewGuid(), selection, setup.WrongDelivery, setup.CorrectOrder,
            CorrectSourceDocumentReason.ReleasedUnderWrongOrder, ShippedAt.AddDays(5), OrderDesk.UserRef, Entity,
            Correction: new(correctionId, CorrectSourceDocumentReason.ReleasedUnderWrongOrder,
                EvidenceRefs: ["ticket:T70"], InitiatingRole: CorrectionInitiatingRole.CustomerService), CommittedBy: OrderDesk);

        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewCorrectSourceDocument(selection,
            setup.WrongDelivery, setup.CorrectOrder, command.Reason, command.EventTime, command.ActorRef,
            command.LegalEntityId, Correction: command.Correction, CommittedBy: OrderDesk));
        Assert.Equal((36, 36, 0), (preview.UnitCount, preview.Change, preview.Block));
        Assert.Equal(36, preview.PrintJobs.Count);
        Assert.Equal(72, preview.EntitlementChanges.Count);

        var result = await InvokeAsync<CorrectSourceDocumentResult>(tenant, command);
        Assert.Equal((36, 36, 36, 36, false), (result.UnitsCorrected, result.EntitlementsSuperseded,
            result.EntitlementsCreated, result.LabelReprintsRequested, result.OwnerChanged));

        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var selected = setup.Units.Take(36).ToList();
            var untouched = setup.Units.Skip(36).ToList();
            var episodes = await db.Episodes.Where(e => selected.Contains(e.UnitId) && e.Kind == EpisodeKind.Ownership && e.ValidTo == null).ToListAsync();
            Assert.All(episodes, e => Assert.Equal((setup.CorrectOrder.Type, setup.CorrectOrder.Id), (e.OpeningDocumentType, e.OpeningDocumentId)));
            var facts = await db.Events.Where(e => selected.Contains(e.UnitId) && e.EventType == UnitEventType.OwnershipSourceDocumentCorrected).ToListAsync();
            Assert.Equal(36, facts.Count);
            Assert.All(facts, e => Assert.Equal(setup.CorrectOrder.Id, e.DocumentId));
            Assert.All(facts, fact =>
            {
                using var attributes = JsonDocument.Parse(fact.Attributes!);
                var corrected = attributes.RootElement.GetProperty("corrected_event_ids").EnumerateArray()
                    .Select(value => value.GetGuid()).ToHashSet();
                Assert.True(originalWrongFactIds[fact.UnitId].ToHashSet().SetEquals(corrected));
            });
            var cover = await db.Entitlements.Where(e => setup.Units.Contains(e.UnitId)).ToListAsync();
            Assert.Equal(36, cover.Count(e => selected.Contains(e.UnitId) && e.ClosedReason == EntitlementClosedReason.SourceDocumentCorrected));
            Assert.Equal(36, cover.Count(e => selected.Contains(e.UnitId) && e.Status != EntitlementStatus.Closed && e.PolicyVersionId == setup.CorrectPolicy));
            Assert.Equal(20, cover.Count(e => untouched.Contains(e.UnitId) && e.Status != EntitlementStatus.Closed && e.PolicyVersionId == setup.OldPolicy));
            var originalIds = originalWrongFactIds.Values.SelectMany(ids => ids).ToList();
            var originals = await db.Events.AsNoTracking().Where(e => originalIds.Contains(e.EventId)).ToListAsync();
            Assert.Equal(originalIds.Count, originals.Count);
            Assert.All(originals, e => Assert.Equal((setup.WrongDelivery.Type, setup.WrongDelivery.Id), (e.DocumentType, e.DocumentId)));
        }

        Assert.Equal(36, (await PublishedEventSink.WaitForAsync<LabelReprintJobRequested>(
            e => e.CorrectionId == correctionId, 36, TimeSpan.FromSeconds(30))).Count);
        Assert.Equal(36, (await PublishedEventSink.WaitForAsync<EntitlementSuperseded>(
            e => setup.Units.Take(36).Contains(e.Header.UnitId) && e.NewPolicyVersionId == setup.CorrectPolicy, 36, TimeSpan.FromSeconds(30))).Count);
        Assert.Equal(36, (await PublishedEventSink.WaitForAsync<EntitlementCreated>(
            e => setup.Units.Take(36).Contains(e.Header.UnitId) && e.PolicyVersionId == setup.CorrectPolicy, 36, TimeSpan.FromSeconds(30))).Count);
        var summary = Assert.Single(await PublishedEventSink.WaitForSummaryAsync<OwnershipSourceDocumentCorrected>(
            e => e.Correction.CorrectionId == correctionId, 1, TimeSpan.FromSeconds(30)));
        Assert.False(summary.OwnerChanged);

        var reversalId = Guid.NewGuid();
        var reversal = await InvokeAsync<CorrectSourceDocumentResult>(tenant, new CorrectSourceDocument(Guid.NewGuid(), selection,
            setup.CorrectOrder, setup.WrongDelivery, CorrectSourceDocumentReason.ReleasedUnderWrongOrder,
            ShippedAt.AddDays(6), OrderDesk.UserRef, Entity,
            Correction: new(reversalId, CorrectSourceDocumentReason.ReleasedUnderWrongOrder,
                CorrectsCorrectionId: correctionId, InitiatingRole: CorrectionInitiatingRole.CustomerService), CommittedBy: OrderDesk));
        Assert.Equal((36, 36, 36), (reversal.UnitsCorrected, reversal.EntitlementsSuperseded, reversal.EntitlementsCreated));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var episodes = await db.Episodes.Where(e => setup.Units.Take(36).Contains(e.UnitId)
                && e.Kind == EpisodeKind.Ownership && e.ValidTo == null).ToListAsync();
            Assert.All(episodes, e => Assert.Equal(setup.WrongDelivery.Id, e.OpeningDocumentId));
            Assert.Equal(correctionId, (await db.Corrections.SingleAsync(c => c.CorrectionId == reversalId)).CorrectsCorrectionId);
            Assert.Equal(36, await db.Entitlements.CountAsync(e => setup.Units.Take(36).Contains(e.UnitId)
                && e.Status != EntitlementStatus.Closed && e.PolicyVersionId == setup.OldPolicy));
        }
    }

    [DatabaseFact]
    public async Task Owner_change_needs_a_distinct_service_manager_and_closes_and_reopens_ownership()
    {
        var tenant = await fixture.NewTenantAsync("cso");
        var setup = await ArrangeAsync(tenant, 1, ownerChanges: true, createWrongCover: true);
        var selection = await SelectAsync(tenant, setup.Serials);
        var correctionId = Guid.NewGuid();
        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewCorrectSourceDocument(selection,
            setup.WrongDelivery, setup.CorrectOrder, CorrectSourceDocumentReason.WrongDeliverySelected,
            ShippedAt.AddDays(5), OrderDesk.UserRef, Entity,
            Correction: new(correctionId, CorrectSourceDocumentReason.WrongDeliverySelected,
                InitiatingRole: CorrectionInitiatingRole.CustomerService), CommittedBy: OrderDesk));
        Assert.Equal("approval_required", preview.Refusal!.Rule);

        var approved = new CorrectionMetadata(correctionId, CorrectSourceDocumentReason.WrongDeliverySelected,
            InitiatingRole: CorrectionInitiatingRole.CustomerService,
            Approvers: [new("service-manager", [YojanaRole.ServiceManager])]);
        var result = await InvokeAsync<CorrectSourceDocumentResult>(tenant, new CorrectSourceDocument(Guid.NewGuid(), selection,
            setup.WrongDelivery, setup.CorrectOrder, CorrectSourceDocumentReason.WrongDeliverySelected,
            ShippedAt.AddDays(5), OrderDesk.UserRef, Entity, Correction: approved, CommittedBy: OrderDesk));
        Assert.True(result.OwnerChanged);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var episodes = await db.Episodes.Where(e => e.UnitId == setup.Units[0] && e.Kind == EpisodeKind.Ownership)
            .OrderBy(e => e.ValidTo == null ? 1 : 0).ToListAsync();
        Assert.Equal(2, episodes.Count);
        Assert.Equal(setup.Customer, episodes[0].PartyRef);
        Assert.NotNull(episodes[0].ValidTo);
        Assert.Equal(setup.CorrectedCustomer, episodes[1].PartyRef);
        Assert.Null(episodes[1].ValidTo);
    }

    [DatabaseFact]
    public async Task Five_hundred_units_use_a_bounded_number_of_database_statements()
    {
        const int count = 500;
        var tenant = await fixture.NewTenantAsync("cs5");
        var setup = await ArrangeAsync(tenant, count, ownerChanges: false, createWrongCover: false);
        var selection = await SelectAsync(tenant, setup.Serials);
        var command = new CorrectSourceDocument(Guid.NewGuid(), selection, setup.WrongDelivery, setup.CorrectOrder,
            CorrectSourceDocumentReason.InvoiceSplitError, ShippedAt.AddDays(5), OrderDesk.UserRef, Entity,
            Correction: new(Guid.NewGuid(), CorrectSourceDocumentReason.InvoiceSplitError,
                InitiatingRole: CorrectionInitiatingRole.CustomerService), CommittedBy: OrderDesk);

        var (result, statements) = await StatementCounter.CountAsync(() => InvokeAsync<CorrectSourceDocumentResult>(tenant, command));
        Assert.Equal(count, result.UnitsCorrected);
        Assert.Equal(count, result.EntitlementsCreated);
        // EF batches the episode, entitlement, ledger, KPI and outbox writes; this ceiling rejects a read per unit.
        Assert.True(statements < 250, $"500-unit source correction took {statements} statements");
    }

    private async Task<Setup> ArrangeAsync(TenantId tenant, int count, bool ownerChanges, bool createWrongCover)
    {
        var item = Guid.NewGuid();
        var warehouse = Guid.NewGuid();
        var site = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var correctedCustomer = ownerChanges ? Guid.NewGuid() : customer;
        var marker = "SD" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var serials = Enumerable.Range(1, count).Select(i => $"{marker}-{i:0000}").ToList();
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(CoverageFamilyCode.Base,
            "Base warranty", false, AssuranceOrService.Assurance));
        var oldPolicy = await PolicyAsync(tenant, "OLD", 24);
        var correctPolicy = await PolicyAsync(tenant, "NEW", 12);

        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            serials.Select(s => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, s)])).ToList(),
            ReceivedAt, "test", Entity, LocationId: warehouse, LocationKind: LocationKind.Warehouse));
        var wrongOrder = new DocumentRef("sales_order", Guid.NewGuid());
        var correctOrder = new DocumentRef("sales_order", Guid.NewGuid());
        var wrongDelivery = new ShipmentDocument(new("delivery", Guid.NewGuid()), customer, ShippedAt,
            [new DocumentLine(1, item, count, serials.Select(s => new DocumentSerial(s)).ToList())], site,
            BaseOrder: wrongOrder, CustomerPoRef: "PO-WRONG");
        Documents.Add(wrongDelivery);
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(new(Guid.NewGuid(), Entity, ShippedAt, "test"), wrongDelivery.Document));
        // The tenant default opens ownership on delivery, not shipment, so the episode being corrected needs the POD.
        var pod = new DeliveryConfirmation(new DocumentRef("pod", Guid.NewGuid()), wrongDelivery.Document, ShippedAt, site);
        Documents.Add(pod);
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesDeliveryConfirmed(new(Guid.NewGuid(), Entity, ShippedAt, "test"), pod.Document));

        var wrongInvoice = new InvoiceDocument(new("invoice", Guid.NewGuid()), wrongOrder, customer, Entity,
            ShippedAt.AddDays(1), [new InvoiceLine(7, Guid.NewGuid(), count, count * 10m, "GBP", "warranty_service",
                PolicyVersionId: oldPolicy, Target: new(WarrantyTargetRule.SameOrder))], "PO-WRONG", "GB");
        var correctInvoice = new InvoiceDocument(new("invoice", Guid.NewGuid()), correctOrder, correctedCustomer, Entity,
            ShippedAt.AddDays(2), [new InvoiceLine(8, Guid.NewGuid(), count, count * 10m, "GBP", "warranty_service",
                PolicyVersionId: correctPolicy, Target: new(WarrantyTargetRule.SameOrder))], "PO-CORRECT", "GB");
        Documents.Add(wrongInvoice);
        Documents.Add(correctInvoice);
        var units = await UnitIdsAsync(tenant, serials);
        if (createWrongCover)
        {
            foreach (var unit in units)
            {
                await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit,
                    CoverageFamilyCode.Base, DateOnly.FromDateTime(wrongInvoice.PostedAt), Entity, wrongInvoice.PostedAt,
                    "test", new Dictionary<string, DateTime> { [StartTrigger.Invoice] = wrongInvoice.PostedAt }, oldPolicy,
                    customer, PartyType.Customer, SourceDocument: wrongInvoice.Document with { Line = 7 },
                    CustomerPoRef: wrongInvoice.CustomerPoRef));
            }
        }
        return new(item, customer, correctedCustomer, wrongDelivery.Document, wrongInvoice.Document, correctOrder,
            correctInvoice.Document, oldPolicy, correctPolicy, serials, units);
    }

    private async Task<Guid> PolicyAsync(TenantId tenant, string prefix, int months)
    {
        var code = $"{prefix}-{Guid.NewGuid():N}";
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            Duration = new DurationRule(months, DurationRule.Months, DurationRule.Clamp, true),
        };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code,
            CoverageFamilyCode.Base, EntitlementType.Manufacturer, new DateOnly(2026, 1, 1), terms, "test"));
        return (await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"))).PolicyVersionId;
    }

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
}
