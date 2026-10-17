using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Xunit;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Commands;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Ports;

namespace Yojana.Serialisation.DatabaseTests;

/// <summary>P3-T06 / case 122: covered stock, exemptions, kit capture and guarded reversal.</summary>
[Collection("serialisation host")]
public sealed class LateSerializationTests(SerialisationHostFixture fixture)
{
    private static readonly Guid Entity = Guid.Parse("a0000000-0000-0000-0000-000000000006");
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    private async Task<T> InvokeAsync<T>(TenantId tenant, object command) where T : class
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IMessageBus>().InvokeAsync<T>(command,
            new DeliveryOptions { TenantId = tenant.ToString() }, CancellationToken.None);
    }

    private LateSerialization Command(Guid item, Guid correction, IReadOnlyList<LateSerializationStockLocation> stock,
        IReadOnlyList<LateSerializationKitCapture>? kits = null, Guid? corrects = null) =>
        new(Guid.NewGuid(), item, At.AddDays(1), LateSerializationReason.CodingErrorAtItemSetup, "item-owner", Entity,
            new("item_change", Guid.NewGuid()), stock, kits ?? [],
            new(correction, LateSerializationReason.CodingErrorAtItemSetup, EvidenceRefs: ["stocktake:122"], CorrectsCorrectionId: corrects,
                InitiatingRole: CorrectionInitiatingRole.SerialAdministrator,
                Approvers: [new("inventory-1", [YojanaRole.InventoryController]), new("quality-1", [YojanaRole.Quality])]),
            new("item-owner", [YojanaRole.ItemMasterOwner]), kits is { Count: > 0 } ? Guid.NewGuid() : null);

    private void RegisterNonSerialItem(Guid item, IReadOnlyList<OpenStockQuantity> stock)
    {
        var itemPort = fixture.Services.GetRequiredService<InMemoryItemPorts>();
        itemPort.Register(InMemoryItemPorts.DefaultProfile(item) with { TrackingMode = ItemSerialisationProfile.TrackingModes.None });
        fixture.Services.GetRequiredService<InMemoryStockQuantityPort>().Set(item, stock);
    }

    [DatabaseFact]
    public async Task Preview_and_commit_cover_stock_publish_the_item_fact_and_record_exemption()
    {
        var tenant = await fixture.NewTenantAsync("late");
        var item = Guid.NewGuid();
        var location = Guid.NewGuid();
        var source = new DocumentRef("stocktake", Guid.NewGuid());
        RegisterNonSerialItem(item, [new(location, "warehouse", 2)]);
        var correction = Guid.NewGuid();
        var stockLine = new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "LATE-122-001")]);
        var command = Command(item, correction,
        [
            new(Guid.NewGuid(), location, "warehouse", source, [stockLine], 1, "unreadable legacy label")
        ]);

        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewLateSerialization(command));
        Assert.Single(preview.UnitsToCommission!);
        Assert.Single(preview.Exemptions!);
        Assert.Null(preview.Refusal);

        var result = await InvokeAsync<LateSerializationResult>(tenant, command);
        Assert.Equal((1, 1, false), (result.Commissioned, result.Exempted, result.Reversed));
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            var change = await db.ItemTrackingModeChanges.SingleAsync(c => c.CorrectionId == correction);
            Assert.Equal((ItemSerialisationProfile.TrackingModes.None, ItemSerialisationProfile.TrackingModes.Serial),
                (change.PreviousTrackingMode, change.TrackingMode));
            var commissioned = await db.Events.SingleAsync(e => e.EventType == UnitEventType.Commissioned && e.DocumentId == source.Id);
            using var attributes = JsonDocument.Parse(commissioned.Attributes!);
            Assert.True(attributes.RootElement.GetProperty("commissioned_late").GetBoolean());
            Assert.Equal(correction, attributes.RootElement.GetProperty("correction_id").GetGuid());
            Assert.Single(await db.Corrections.Where(c => c.CorrectionId == correction && c.Command == CorrectionCommand.LateSerialization).ToListAsync());
        }

        // The effective local tracking fact now overlays the still-non-serial item port for ordinary future receipts.
        var later = await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "LATE-122-002")])], At.AddDays(2), "receiving", Entity));
        Assert.Equal(1, later.Commissioned);

        var published = await PublishedEventSink.WaitForSummaryAsync<ItemTrackingModeChanged>(e => e.Correction.CorrectionId == correction, 1, TimeSpan.FromSeconds(10));
        Assert.True(Assert.Single(published).StockCoverage.Single().ExemptQuantity == 1);
    }

    [DatabaseFact]
    public async Task Existing_kit_quantity_is_commissioned_and_replaced_through_correct_as_built()
    {
        var tenant = await fixture.NewTenantAsync("latekit");
        var parentItem = Guid.NewGuid();
        var componentItem = Guid.NewGuid();
        RegisterNonSerialItem(componentItem, []);
        var parentLine = new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "LATE-KIT-P")]);
        var parent = (await InvokeAsync<CommissionUnitsResult>(tenant, new CommissionUnits(Guid.NewGuid(), parentItem, UnitOrigin.Manufactured,
            [parentLine], At, "line", Entity))).Lines[0].UnitId!.Value;
        var assembly = await InvokeAsync<RecordAssemblyResult>(tenant, new RecordAssembly(Guid.NewGuid(), parent,
            [new ChildInput(null, componentItem, Quantity: 1, Slot: "module-1")], At.AddHours(1), "line", Entity));
        var correction = Guid.NewGuid();
        var kitLine = new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "LATE-KIT-C")]);
        var capture = new LateSerializationKitCapture(Guid.NewGuid(), assembly.LinkIds[0], At.AddHours(1),
            new("production_order", Guid.NewGuid()), [kitLine]);
        var command = Command(componentItem, correction, [], [capture]);

        var preview = await InvokeAsync<CorrectionPreview>(tenant, new PreviewLateSerialization(command));
        Assert.True(Assert.Single(preview.KitsNeedingAsBuiltCapture!).CapturedByCommand);
        var result = await InvokeAsync<LateSerializationResult>(tenant, command);
        Assert.Equal((1, 1, 0), (result.Commissioned, result.KitsCaptured, result.KitsNeedingAsBuiltCapture));

        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        Assert.NotNull((await db.AssemblyLinks.SingleAsync(l => l.LinkId == assembly.LinkIds[0])).RemovedAt);
        var replacement = await db.AssemblyLinks.SingleAsync(l => l.ParentUnitId == parent && l.ChildUnitId != null && l.RemovedAt == null);
        Assert.Equal("module-1", replacement.Slot);
        var fact = await db.Events.SingleAsync(e => e.UnitId == parent && e.EventType == UnitEventType.AsBuiltCorrected);
        Assert.Equal(correction, JsonDocument.Parse(fact.Attributes!).RootElement.GetProperty("correction_id").GetGuid());
        Assert.Equal(1, await db.Corrections.CountAsync(c => c.CorrectionId == correction));
        Assert.False(await db.Corrections.AnyAsync(c => c.Command == CorrectionCommand.CorrectAsBuilt && c.CorrectionId == correction));
    }

    [DatabaseFact]
    public async Task A_later_correction_reverses_an_unused_change_but_activity_blocks_reversal()
    {
        var tenant = await fixture.NewTenantAsync("laterev");
        var unused = Guid.NewGuid();
        RegisterNonSerialItem(unused, []);
        var firstId = Guid.NewGuid();
        await InvokeAsync<LateSerializationResult>(tenant, Command(unused, firstId, []));
        var reverse = Command(unused, Guid.NewGuid(), [], corrects: firstId) with { EffectiveAt = At.AddDays(2) };
        var reversed = await InvokeAsync<LateSerializationResult>(tenant, reverse);
        Assert.True(reversed.Reversed);
        Assert.Equal(ItemSerialisationProfile.TrackingModes.None, reversed.TrackingMode);

        var active = Guid.NewGuid();
        var location = Guid.NewGuid();
        RegisterNonSerialItem(active, [new(location, "warehouse", 1)]);
        var activeId = Guid.NewGuid();
        var activeLine = new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "LATE-ACTIVE-1")]);
        await InvokeAsync<LateSerializationResult>(tenant, Command(active, activeId,
            [new(Guid.NewGuid(), location, "warehouse", new("stocktake", Guid.NewGuid()),
                [activeLine])]));
        var blocked = Command(active, Guid.NewGuid(), [], corrects: activeId) with { EffectiveAt = At.AddDays(2) };
        var refusal = await Assert.ThrowsAsync<CommandRefusedException>(() => InvokeAsync<LateSerializationResult>(tenant, blocked));
        Assert.Equal("serialised_activity_after_tracking_change", refusal.Rule);
    }
}
