using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Entitlements;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P2-T12: invoice warranty lines resolve targets, preserve sale facts, defer unresolved work and replay once.</summary>
[Collection("serialisation host")]
public sealed class WarrantySalesLineTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Obligor = Guid.NewGuid();
    private InMemoryDocumentPorts Documents => fixture.Services.GetRequiredService<InMemoryDocumentPorts>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<PolicyDraftResult> PolicyAsync(TenantId tenant, string family, bool stacking, int months, string mode = "depot", bool regulated = false)
    {
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily(family, family, stacking, AssuranceOrService.Service));
        await InvokeAsync<ReplacementPolicyRow>(tenant, new DefineReplacementPolicy("RP-" + family, ReplacementRule.MaxRemainingOrMinDays, 90, null, null));
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            Duration = new DurationRule(months, DurationRule.Months),
            Start = new StartRule(StartTrigger.PlanPurchase, [], null),
            Coverage = PolicyValidatorTestsTerms.Standard24().Coverage with { ServiceMode = mode },
            AssuranceOrService = AssuranceOrService.Service,
            ReplacementPolicyCode = "RP-" + family,
            ServiceContractRegulated = regulated,
            Refurbished = [],
        };
        var code = family.ToUpperInvariant()[..Math.Min(3, family.Length)] + Guid.NewGuid().ToString("N")[..6];
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), code, code, family, EntitlementType.Extended, new DateOnly(2020, 1, 1), terms, "test"));
        return await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
    }

    private async Task<(Guid Item, List<Guid> Units, List<string> Serials)> UnitsAsync(TenantId tenant, string prefix, int count, DocumentRef document)
    {
        var item = Guid.NewGuid();
        var serials = Enumerable.Range(1, count).Select(i => $"{prefix}-{i:D3}").ToList();
        var result = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            serials.Select(s => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, s)])).ToList(), At.AddDays(-5), "test", Obligor, Document: document));
        return (item, result.Lines.Select(l => l.UnitId!.Value).ToList(), serials);
    }

    private static ThinEnvelope Envelope() => new(Guid.NewGuid(), Obligor, At, "sales");

    [DatabaseFact]
    public async Task Same_order_three_units_stack_with_invoice_and_po_and_replay_once()
    {
        var tenant = await fixture.NewTenantAsync("wl");
        var policy = await PolicyAsync(tenant, "extended_term", true, 36);
        var order = new DocumentRef("sales_order", Guid.NewGuid());
        var setup = await UnitsAsync(tenant, "WL" + Guid.NewGuid().ToString("N")[..5], 3, order);
        var invoiceRef = new DocumentRef("invoice", Guid.NewGuid());
        Documents.Add(new InvoiceDocument(invoiceRef, order, Guid.NewGuid(), Obligor, At,
            [new InvoiceLine(7, Guid.NewGuid(), 3, 900m, "EUR", "warranty_service", policy.PolicyCode, policy.PolicyVersionId, new WarrantyLineTarget(WarrantyTargetRule.SameOrder))],
            CustomerPoRef: "PO-36M", Country: "DE"));

        var first = await InvokeAsync<ConsumedEventResult>(tenant, new SalesInvoicePosted(Envelope(), invoiceRef));
        var replay = await InvokeAsync<ConsumedEventResult>(tenant, new SalesInvoicePosted(Envelope(), invoiceRef));
        Assert.Equal(3, first.UnitsAffected);
        using var scope = fixture.ScopeFor(tenant);
        var rows = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking().Where(e => setup.Units.Contains(e.UnitId)).ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, e =>
        {
            Assert.Equal((invoiceRef.Type, invoiceRef.Id, 7), (e.SourceDocumentType, e.SourceDocumentId, e.SourceDocumentLine));
            Assert.Equal("PO-36M", e.CustomerPoRef);
        });
        Assert.True(replay.Duplicate || replay.UnitsAffected == 3);
    }

    [DatabaseFact]
    public async Task Attached_serial_range_resolves_and_an_explicit_list_can_close_an_exception()
    {
        var tenant = await fixture.NewTenantAsync("wr");
        var policy = await PolicyAsync(tenant, "extended_term", true, 12);
        var scheme = "WR" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        fixture.Services.GetRequiredService<InMemorySerialSchemes>().Register(new SerialSchemeDefinition(scheme, IdentifierType.InternalSerial, scheme, "yyMM", "-", 6));
        var item = Guid.NewGuid();
        // four units: the range on line 2 covers the first three, line 3 is resolved later with the fourth
        var serials = Enumerable.Range(0, 4).Select(i => $"{scheme}2610-{i:D6}").ToList();
        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            serials.Select(s => new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, s)])).ToList(), At.AddDays(-2), "test", Obligor));
        var invoiceRef = new DocumentRef("invoice", Guid.NewGuid());
        Documents.Add(new InvoiceDocument(invoiceRef, new DocumentRef("sales_order", Guid.NewGuid()), Guid.NewGuid(), Obligor, At,
            [new InvoiceLine(2, Guid.NewGuid(), 3, 300m, "EUR", "warranty_service", policy.PolicyCode, policy.PolicyVersionId,
                new WarrantyLineTarget(WarrantyTargetRule.AttachedList, Scheme: scheme, RangeStart: serials[0], RangeEnd: serials[2])),
             new InvoiceLine(3, Guid.NewGuid(), 1, 100m, "EUR", "warranty_service", policy.PolicyCode, policy.PolicyVersionId,
                new WarrantyLineTarget(WarrantyTargetRule.AttachedList))], Country: "DE"));
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesInvoicePosted(Envelope(), invoiceRef));

        Guid exceptionId;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            Assert.Equal(3, await db.Entitlements.CountAsync(e => e.SourceDocumentId == invoiceRef.Id && e.SourceDocumentLine == 2));
            exceptionId = (await db.WarrantyLineExceptions.AsNoTracking().SingleAsync(e => e.DocumentId == invoiceRef.Id && e.DocumentLine == 3)).ExceptionId;
        }
        await InvokeAsync<WarrantyLineExceptionRow>(tenant, new ResolveWarrantyLineException(exceptionId, [serials[3]], "service.desk"));
        using var finalScope = fixture.ScopeFor(tenant);
        Assert.Equal(WarrantyLineExceptionStatus.Resolved,
            (await finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>().WarrantyLineExceptions.AsNoTracking().SingleAsync(e => e.ExceptionId == exceptionId)).Status);
    }

    [DatabaseFact]
    public async Task Non_stacking_purchase_extends_matching_service_mode_and_supersedes_a_changed_mode()
    {
        var tenant = await fixture.NewTenantAsync("wm");
        var current = await PolicyAsync(tenant, "service_level", false, 12, "depot");
        var changed = await PolicyAsync(tenant, "service_level", false, 24, "on_site");
        var order = new DocumentRef("sales_order", Guid.NewGuid());
        var setup = await UnitsAsync(tenant, "WM" + Guid.NewGuid().ToString("N")[..5], 2, order);
        foreach (var unit in setup.Units)
            await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), unit, "service_level", DateOnly.FromDateTime(At), Obligor, At.AddDays(-1), "test",
                new Dictionary<string, DateTime> { [StartTrigger.PlanPurchase] = At.AddDays(-1) }, current.PolicyVersionId));

        var invoiceRef = new DocumentRef("invoice", Guid.NewGuid());
        Documents.Add(new InvoiceDocument(invoiceRef, order, Guid.NewGuid(), Obligor, At,
            [new InvoiceLine(1, Guid.NewGuid(), 2, 200m, "EUR", "warranty_service", current.PolicyCode, current.PolicyVersionId, new WarrantyLineTarget(WarrantyTargetRule.SameOrder))], Country: "DE"));
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesInvoicePosted(Envelope(), invoiceRef));
        using (var scope = fixture.ScopeFor(tenant))
            Assert.Equal(2, await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().EntitlementAmendments.CountAsync(a => a.SourceDocumentId == invoiceRef.Id));

        var secondRef = new DocumentRef("invoice", Guid.NewGuid());
        Documents.Add(new InvoiceDocument(secondRef, order, Guid.NewGuid(), Obligor, At.AddDays(1),
            [new InvoiceLine(1, Guid.NewGuid(), 2, 200m, "EUR", "warranty_service", changed.PolicyCode, changed.PolicyVersionId, new WarrantyLineTarget(WarrantyTargetRule.SameOrder))], Country: "DE"));
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesInvoicePosted(Envelope(), secondRef));
        using var finalScope = fixture.ScopeFor(tenant);
        Assert.Equal(2, await finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.CountAsync(e => e.SupersedesEntitlementId != null));
    }

    [DatabaseFact]
    public async Task Referenced_delivery_resolves_later_from_the_original_trigger_and_regulation_is_a_visible_exception()
    {
        var tenant = await fixture.NewTenantAsync("wd");
        var policy = await PolicyAsync(tenant, "extended_term", true, 12);
        var shipmentRef = new DocumentRef("delivery", Guid.NewGuid());
        var item = Guid.NewGuid();
        var serial = "WD-" + Guid.NewGuid().ToString("N");
        await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, serial)])], At.AddDays(-2), "test", Obligor));
        var invoiceRef = new DocumentRef("invoice", Guid.NewGuid());
        Documents.Add(new InvoiceDocument(invoiceRef, new DocumentRef("sales_order", Guid.NewGuid()), Guid.NewGuid(), Obligor, At,
            [new InvoiceLine(1, Guid.NewGuid(), 1, 100m, "EUR", "warranty_service", policy.PolicyCode, policy.PolicyVersionId,
                new WarrantyLineTarget(WarrantyTargetRule.ReferencedDocument, shipmentRef))], Country: "DE"));
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesInvoicePosted(Envelope(), invoiceRef));
        using (var scope = fixture.ScopeFor(tenant))
            Assert.Equal(WarrantyLineMissingInput.ReferencedDocumentNotPosted,
                (await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().WarrantyLineExceptions.AsNoTracking().SingleAsync(e => e.DocumentId == invoiceRef.Id)).MissingInput);

        var customer = Guid.NewGuid();
        Documents.Add(new ShipmentDocument(shipmentRef, customer, At.AddDays(2), [new DocumentLine(1, item, 1, [new DocumentSerial(serial)])], BaseOrder: new DocumentRef("sales_order", Guid.NewGuid())));
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesShipmentPosted(new ThinEnvelope(Guid.NewGuid(), Obligor, At.AddDays(2), "sales"), shipmentRef));
        var confirmationRef = new DocumentRef("delivery_confirmation", Guid.NewGuid());
        Documents.Add(new DeliveryConfirmation(confirmationRef, shipmentRef, At.AddDays(3), null, Evidence: "carrier_pod"));
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesDeliveryConfirmed(new ThinEnvelope(Guid.NewGuid(), Obligor, At.AddDays(3), "sales"), confirmationRef));
        for (var i = 0; i < 100; i++)
        {
            using var scope = fixture.ScopeFor(tenant);
            if (await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AnyAsync(e => e.SourceDocumentId == invoiceRef.Id)) break;
            await Task.Delay(100);
        }
        using (var scope = fixture.ScopeFor(tenant))
        {
            var entitlement = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.AsNoTracking().SingleAsync(e => e.SourceDocumentId == invoiceRef.Id);
            Assert.Equal(DateOnly.FromDateTime(At), entitlement.StartDate);
        }

        var regulated = await PolicyAsync(tenant, "regulated_plan", true, 12, regulated: true);
        using (var scope = fixture.ScopeFor(tenant))
            await scope.ServiceProvider.GetRequiredService<WarrantyReferenceSettings>().SetAsync(new WarrantyReferencePolicy([new("DE", false)]), CancellationToken.None);
        var regulatedInvoice = new DocumentRef("invoice", Guid.NewGuid());
        Documents.Add(new InvoiceDocument(regulatedInvoice, shipmentRef, customer, Obligor, At,
            [new InvoiceLine(4, Guid.NewGuid(), 1, 100m, "EUR", "warranty_service", regulated.PolicyCode, regulated.PolicyVersionId,
                new WarrantyLineTarget(WarrantyTargetRule.ReferencedDocument, shipmentRef))], Country: "DE"));
        await InvokeAsync<ConsumedEventResult>(tenant, new SalesInvoicePosted(Envelope(), regulatedInvoice));
        using var finalScope = fixture.ScopeFor(tenant);
        Assert.Equal(WarrantyLineMissingInput.RegulatedJurisdiction,
            (await finalScope.ServiceProvider.GetRequiredService<SerialisationDbContext>().WarrantyLineExceptions.AsNoTracking().SingleAsync(e => e.DocumentId == regulatedInvoice.Id)).MissingInput);
    }
}
