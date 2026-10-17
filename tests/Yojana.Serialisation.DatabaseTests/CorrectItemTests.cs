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

/// <summary>P3-T04, case 118 and tickets T2/T67: item corrections in stock, after sale and as a later reversal.</summary>
[Collection("serialisation host")]
public sealed class CorrectItemTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.NewGuid();
    private static readonly DateTime At = new(2026, 10, 17, 8, 0, 0, DateTimeKind.Utc);
    private InMemoryWarrantyPorts WarrantyPorts => fixture.Services.GetRequiredService<InMemoryWarrantyPorts>();
    private InMemoryStockReclassificationPort StockPort => fixture.Services.GetRequiredService<InMemoryStockReclassificationPort>();

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private async Task<(TenantId Tenant, Guid Unit, Guid Item)> CommissionAsync(string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        var item = Guid.NewGuid();
        var commissioned = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, prefix.ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N"))])], At, "receiving", Entity,
            LocationId: Guid.NewGuid(), LocationKind: LocationKind.Warehouse));
        return (tenant, commissioned.Lines[0].UnitId!.Value, item);
    }

    private static CorrectItem Command(Guid unit, Guid correctedItem, string reason, DateTime at, string role, Guid? corrects = null)
    {
        var id = Guid.NewGuid();
        var actor = role + "-user";
        return new CorrectItem(id, unit, correctedItem, reason, at, actor, Entity,
            new(id, reason, "service ticket", ["ticket:118"], CorrectionInitiatedVia.Workbench, corrects, CorrectionInitiatingRole.ServiceTechnician,
                [new(actor, [role])]),
            new(actor, [role]));
    }

    [DatabaseFact]
    public async Task In_stock_correction_reclassifies_opens_assignment_previews_policy_change_and_replays()
    {
        var setup = await CommissionAsync("ci");
        var correctedItem = Guid.NewGuid();
        WarrantyPorts.Register(new ItemWarrantyPolicy(setup.Item, CoverageFamilyCode.Base, "OLD-BASE", false));
        WarrantyPorts.Register(new ItemWarrantyPolicy(correctedItem, CoverageFamilyCode.Base, "NEW-BASE", false));
        var command = Command(setup.Unit, correctedItem, CorrectItemReason.WrongItemAtReceipt, At.AddHours(1), YojanaRole.InventoryController);

        var preview = await InvokeAsync<CorrectionPreview>(setup.Tenant, new PreviewCorrectItem(command));
        Assert.Equal((1, 0), (preview.Change, preview.Block));
        Assert.Contains("warranty_policy_changed", preview.Units[0].Detail);
        Assert.Equal(2, preview.CertificateVersions.Count);

        var result = await InvokeAsync<CorrectItemResult>(setup.Tenant, command);
        Assert.False(result.Sold);
        Assert.False(result.DocumentMismatch);
        Assert.NotNull(result.InventoryReclassificationRef);
        Assert.False(result.FinanceReviewRequired);
        Assert.Single(result.WarrantyPolicyChanges);
        var replay = await InvokeAsync<CorrectItemResult>(setup.Tenant, command);
        Assert.True(replay.Replayed);
        Assert.Equal(result.NewAssignmentId, replay.NewAssignmentId);
        Assert.Single(StockPort.Requests, request => request.CorrectionId == command.Correction.CorrectionId);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var assignments = await db.ItemAssignments.Where(row => row.UnitId == setup.Unit).OrderBy(row => row.ValidFrom).ToListAsync();
        Assert.Equal(2, assignments.Count);
        Assert.Equal(ItemAssignmentReason.Correction, assignments[1].Reason);
        Assert.Equal(correctedItem, (await db.Current.SingleAsync(row => row.UnitId == setup.Unit)).ItemId);
        Assert.Single(await db.Corrections.Where(row => row.CorrectionId == command.Correction.CorrectionId).ToListAsync());

        var published = await PublishedEventSink.WaitForAsync<UnitItemCorrected>(e => e.Header.UnitId == setup.Unit, 1, TimeSpan.FromSeconds(20));
        var corrected = Assert.Single(published);
        Assert.Equal(correctedItem, corrected.CorrectedItemId);
        Assert.True(corrected.WarrantyPolicyChanged);
    }

    [DatabaseFact]
    public async Task Sold_correction_keeps_documents_flags_the_unit_and_records_finance_review()
    {
        var setup = await CommissionAsync("cs");
        var customer = Guid.NewGuid();
        await InvokeAsync<OpenOwnershipResult>(setup.Tenant, new OpenOwnership(Guid.NewGuid(), setup.Unit, customer, PartyType.Customer, EpisodeReason.Sale,
            At.AddHours(1), "sales", Entity, Document: new("delivery", Guid.NewGuid(), 1)));
        var correctedItem = Guid.NewGuid();
        var blocked = await InvokeAsync<CorrectionPreview>(setup.Tenant, new PreviewCorrectItem(
            Command(setup.Unit, correctedItem, CorrectItemReason.WrongItemAtBuild, At.AddHours(2), YojanaRole.InventoryController)));
        Assert.Equal("approval_required", blocked.Refusal!.Rule);
        var command = Command(setup.Unit, correctedItem, CorrectItemReason.WrongItemAtBuild, At.AddHours(2), YojanaRole.ServiceSupervisor);

        var result = await InvokeAsync<CorrectItemResult>(setup.Tenant, command);
        Assert.True(result.Sold);
        Assert.True(result.DocumentMismatch);
        Assert.True(result.FinanceReviewRequired);
        Assert.Null(result.InventoryReclassificationRef);
        Assert.DoesNotContain(StockPort.Requests, request => request.CorrectionId == command.Correction.CorrectionId);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = await db.Units.SingleAsync(row => row.UnitId == setup.Unit);
        var current = await db.Current.SingleAsync(row => row.UnitId == setup.Unit);
        Assert.Contains(OverlayFlag.DocumentMismatch, JsonSerializer.Deserialize<List<string>>(unit.OverlayFlags)!);
        Assert.Contains(OverlayFlag.DocumentMismatch, JsonSerializer.Deserialize<List<string>>(current.OverlayFlags)!);
        Assert.Single(await db.Episodes.Where(row => row.UnitId == setup.Unit && row.Kind == EpisodeKind.Ownership && row.ValidTo == null).ToListAsync());
        var fact = await db.Events.SingleAsync(row => row.UnitId == setup.Unit && row.EventType == UnitEventType.ItemCorrected);
        using var attributes = JsonDocument.Parse(fact.Attributes!);
        Assert.True(attributes.RootElement.GetProperty("finance_review_required").GetBoolean());
        Assert.Equal(YojanaRole.FinanceReview, attributes.RootElement.GetProperty("finance_review_roles")[0].GetString());
    }

    [DatabaseFact]
    public async Task A_later_item_correction_can_reverse_an_earlier_one()
    {
        var setup = await CommissionAsync("cr");
        var wrongItem = Guid.NewGuid();
        var first = Command(setup.Unit, wrongItem, CorrectItemReason.ConfigurationMismatch, At.AddHours(1), YojanaRole.InventoryController);
        await InvokeAsync<CorrectItemResult>(setup.Tenant, first);
        var reversal = Command(setup.Unit, setup.Item, CorrectItemReason.WrongItemAtBuild, At.AddHours(2), YojanaRole.InventoryController, first.Correction.CorrectionId);
        await InvokeAsync<CorrectItemResult>(setup.Tenant, reversal);

        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var assignments = await db.ItemAssignments.Where(row => row.UnitId == setup.Unit).OrderBy(row => row.ValidFrom).ToListAsync();
        Assert.Equal([setup.Item, wrongItem, setup.Item], assignments.Select(row => row.ItemId));
        var reversalRecord = await db.Corrections.SingleAsync(row => row.CorrectionId == reversal.Correction.CorrectionId);
        Assert.Equal(first.Correction.CorrectionId, reversalRecord.CorrectsCorrectionId);
    }
}
