using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P2-T16: receipt posts first; its durable follow-on creates supplier cover once without changing customer cover.</summary>
[Collection("serialisation host")]
public sealed class InboundEntitlementTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime Received = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object message) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(message, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<int> InboundCountAsync(TenantId tenant, Guid receiptId)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Entitlements.CountAsync(e => e.SourceDocumentId == receiptId);
    }

    [DatabaseFact]
    public async Task Vendor_terms_create_one_inbound_entitlement_per_received_serial_and_replay_creates_none()
    {
        var tenant = await fixture.NewTenantAsync("ie");
        var vendor = Guid.NewGuid();
        var item = Guid.NewGuid();
        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("supplier_warranty", "Supplier warranty", true, AssuranceOrService.Assurance));
        var terms = PolicyValidatorTestsTerms.Standard24() with
        {
            Start = new StartRule(StartTrigger.Receipt, [], null),
            ReferenceTimeZoneRule = PolicyTerms.TimeZoneUtc,
            Refurbished = [],
            ReplacementPolicyCode = null,
        };
        var draft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "SUP-" + Guid.NewGuid().ToString("N")[..6], "Supplier warranty", "supplier_warranty", EntitlementType.Manufacturer, new DateOnly(2026, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(draft.PolicyVersionId, "test"));
        fixture.Services.GetRequiredService<InMemoryWarrantyPorts>().Register(new VendorWarrantyTerm(vendor, item, draft.PolicyVersionId));

        var marker = "IE" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant();
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), vendor, Received,
            [new DocumentLine(1, item, 2, [new DocumentSerial(marker + "-1"), new DocumentSerial(marker + "-2")], Guid.NewGuid(), LocationKind.Warehouse)]);
        fixture.Services.GetRequiredService<InMemoryDocumentPorts>().Add(receipt);
        var envelope = new ThinEnvelope(Guid.NewGuid(), Guid.NewGuid(), Received, "test");
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(envelope, receipt.Document));
        // Let the receipt's own follow-on finish first: run side by side, the second insert is refused by the one-live-per-family
        // index and the message retried, which is the protection working but not what this test is about.
        for (var waited = 0; waited < 200 && await InboundCountAsync(tenant, receipt.Document.Id) < 2; waited++) await Task.Delay(100);
        var first = await InvokeAsync<CreateInboundEntitlementsResult>(tenant, new CreateInboundEntitlements(envelope, receipt.Document));
        var replay = await InvokeAsync<CreateInboundEntitlementsResult>(tenant, new CreateInboundEntitlements(envelope, receipt.Document));
        Assert.Contains(first.Created, new[] { 0, 2 }); // the durable local queue may have handled it before this explicit replay
        Assert.True(replay.Duplicate);

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var rows = await db.Entitlements.Where(e => e.SourceDocumentId == receipt.Document.Id).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, e =>
        {
            Assert.Equal(EntitlementDirection.Inbound, e.Direction);
            Assert.Equal(vendor, e.ObligorEntityId);
            Assert.Null(e.AccrualCohortId);
            Assert.Equal(new DateOnly(2026, 10, 10), e.StartDate);
        });
        Assert.All(await db.Current.Where(c => rows.Select(e => e.UnitId).Contains(c.UnitId)).ToListAsync(), c =>
        {
            Assert.Equal("[]", c.LiveEntitlementIds);
            Assert.Null(c.WarrantyEndMax);
        });
        Assert.DoesNotContain(PublishedEventSink.Received.OfType<EntitlementCreated>(), e => rows.Select(r => r.EntitlementId).Contains(e.EntitlementId));

        await InvokeAsync<CoverageFamilyRow>(tenant, new DefineCoverageFamily("base_warranty", "Base warranty", false, AssuranceOrService.Assurance));
        var baseDraft = await InvokeAsync<PolicyDraftResult>(tenant, new CreatePolicyDraft(Guid.NewGuid(), "BASE-" + Guid.NewGuid().ToString("N")[..6], "Customer warranty", "base_warranty", EntitlementType.Manufacturer, new DateOnly(2026, 1, 1), terms, "test"));
        await InvokeAsync<PolicyDraftResult>(tenant, new PublishPolicyVersion(baseDraft.PolicyVersionId, "test"));
        var outbound = await InvokeAsync<CreateEntitlementResult>(tenant, new CreateEntitlement(Guid.NewGuid(), rows[0].UnitId, "base_warranty", new DateOnly(2026, 10, 10), Guid.NewGuid(), Received.AddMinutes(1), "test",
            new Dictionary<string, DateTime> { [StartTrigger.Receipt] = Received }, PolicyVersionId: baseDraft.PolicyVersionId, OwnerPartyRef: Guid.NewGuid(), OwnerPartyType: PartyType.Customer));
        using var verifyScope = fixture.ScopeFor(tenant);
        var verify = verifyScope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.Equal(2, await verify.Entitlements.CountAsync(e => e.UnitId == rows[0].UnitId && e.Status != EntitlementStatus.Closed));
        var current = await verify.Current.SingleAsync(c => c.UnitId == rows[0].UnitId);
        Assert.Equal(outbound.EndDate, current.WarrantyEndMax);
        Assert.Contains(outbound.EntitlementId.ToString(), current.LiveEntitlementIds, StringComparison.OrdinalIgnoreCase);
    }

    [DatabaseFact]
    public async Task No_vendor_terms_create_no_cover()
    {
        var tenant = await fixture.NewTenantAsync("in");
        var receipt = new ReceiptDocument(new DocumentRef("receipt", Guid.NewGuid()), Guid.NewGuid(), Received,
            [new DocumentLine(1, Guid.NewGuid(), 1, [new DocumentSerial("IN-" + Guid.NewGuid().ToString("N"))], Guid.NewGuid(), LocationKind.Warehouse)]);
        fixture.Services.GetRequiredService<InMemoryDocumentPorts>().Add(receipt);
        var envelope = new ThinEnvelope(Guid.NewGuid(), Guid.NewGuid(), Received, "test");
        await InvokeAsync<ConsumedEventResult>(tenant, new PurchasingReceiptPosted(envelope, receipt.Document));
        var result = await InvokeAsync<CreateInboundEntitlementsResult>(tenant, new CreateInboundEntitlements(envelope, receipt.Document));
        Assert.Equal(0, result.Created);
    }
}
