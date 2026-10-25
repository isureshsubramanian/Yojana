using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Connectors.CX04;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Serialisation.Certificates;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class BomProjectionDatabaseTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Projection_replaces_lines_keeps_unresolved_items_and_rejects_out_of_order_changes()
    {
        var tenant = await fixture.NewTenantAsync("bomproj");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        var store = scope.ServiceProvider.GetRequiredService<GatewayReferenceStore>();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var component = await store.UpsertItemAsync(connection,
            new("FIC-NVME", "Fictional NVMe drive", OpenEnum.Known("serial"), "EA", false, null,
                IsInventoryItem: true), default);
        await db.SaveChangesAsync();
        var firstAt = new DateTimeOffset(2026, 9, 1, 8, 25, 0, TimeSpan.Zero);
        Assert.True(await store.UpsertBomAsync(connection, Bom("FIC-SRV-TWIN", firstAt,
            new(0, OpenEnum.Known("item"), "FIC-NVME", "Fictional NVMe drive", 4, "EA"),
            new(1, OpenEnum.Known("item"), "FIC-UNRESOLVED", "Unresolved adapter", 1, "EA")), default));
        await db.SaveChangesAsync();

        var header = await db.BomProjections.SingleAsync();
        var initial = await db.BomProjectionLines.OrderBy(row => row.LineNo).ToListAsync();
        Assert.Equal(component, initial[0].ItemId);
        Assert.Null(initial[1].ItemId);
        Assert.Equal("FIC-UNRESOLVED", initial[1].ItemExternalId);
        Assert.Equal("Unresolved adapter", initial[1].Description);
        Assert.Equal(64, header.ContentHash.Length);

        Assert.False(await store.UpsertBomAsync(connection, Bom("FIC-SRV-TWIN", firstAt.AddSeconds(1),
            new(0, OpenEnum.Known("item"), "FIC-NVME", "Fictional NVMe drive", 4, "EA"),
            new(1, OpenEnum.Known("item"), "FIC-UNRESOLVED", "Unresolved adapter", 1, "EA")), default));

        Assert.False(await store.UpsertBomAsync(connection, Bom("FIC-SRV-TWIN", firstAt.AddMinutes(-1),
            new BomUpsertedLine(9, OpenEnum.Known("item"), "OLD", "Old line", 1, "EA")), default));
        Assert.True(await store.UpsertBomAsync(connection, Bom("FIC-SRV-TWIN", firstAt.AddMinutes(1),
            new BomUpsertedLine(4, OpenEnum.Known("text"), null, "Fit bracket before test", 0, null)), default));
        await db.SaveChangesAsync();
        var replaced = await db.BomProjectionLines.SingleAsync();
        Assert.Equal(4, replaced.LineNo);
        Assert.Equal("text", replaced.Kind);
        Assert.Equal(header.BomProjectionId, replaced.BomProjectionId);

        Assert.True(await store.UpsertBomAsync(connection,
            new("FIC-SRV-TWIN", firstAt.AddMinutes(2), true, []), default));
        await db.SaveChangesAsync();
        Assert.True((await db.BomProjections.SingleAsync()).Deleted);
        Assert.Empty(await db.BomProjectionLines.ToListAsync());
    }

    [DatabaseFact]
    public async Task Query_port_returns_full_BOM_and_both_expected_component_shapes()
    {
        var tenant = await fixture.NewTenantAsync("bom-port");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        using var scope = fixture.ScopeFor(tenant);
        var store = scope.ServiceProvider.GetRequiredService<GatewayReferenceStore>();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var parent = await store.UpsertItemAsync(connection,
            new("FIC-SRV-TWIN", "Fictional twin server", OpenEnum.Known("serial"), "EA", false), default);
        var serial = await store.UpsertItemAsync(connection,
            new("FIC-NVME", "Fictional NVMe drive", OpenEnum.Known("serial"), "EA", false, null,
                IsInventoryItem: true), default);
        _ = await store.UpsertItemAsync(connection,
            new("FIC-LABOUR", "Assembly labour", OpenEnum.Known("none"), "H", false, null,
                IsInventoryItem: false), default);
        var at = new DateTimeOffset(2026, 9, 1, 8, 25, 0, TimeSpan.Zero);
        await db.SaveChangesAsync();
        await store.UpsertBomAsync(connection, Bom("FIC-SRV-TWIN", at,
            new(0, OpenEnum.Known("item"), "FIC-NVME", "Fictional NVMe drive", 2, "EA"),
            new(1, OpenEnum.Known("item"), "FIC-LABOUR", "Assembly labour", 4, "H"),
            new(2, OpenEnum.Known("resource"), "FIC-BENCH", "Assembly bench", 1, "H"),
            new(3, OpenEnum.Known("text"), null, "Test after assembly", 0, null)), default);
        await db.SaveChangesAsync();

        var port = scope.ServiceProvider.GetRequiredService<IBomQueryPort>();
        var bill = Assert.IsType<BillOfMaterials>(await port.BillOfMaterialsAsync(parent, default));
        Assert.Equal(at, bill.AsAt);
        Assert.Equal(4, bill.Lines.Count);
        Assert.True(bill.Lines[0].Serialised);
        Assert.False(bill.Lines[1].InventoryItem);
        var expected = await port.ExpectedSerialisedComponentsAsync(parent, new DateOnly(2026, 9, 1), default);
        var component = Assert.Single(expected);
        Assert.Equal(serial, component.ComponentItemId);
        Assert.Equal("0", component.Slot);
        Assert.Equal(2, component.Quantity);
        var secondParent = Guid.CreateVersion7();
        using var commands = new BomCommandObserver();
        var batched = await port.ExpectedSerialisedComponentsAsync([
            new(parent, new DateOnly(2026, 9, 1)), new(secondParent, new DateOnly(2026, 9, 1))], default);
        Assert.Single(batched[new(parent, new DateOnly(2026, 9, 1))]);
        Assert.Empty(batched[new(secondParent, new DateOnly(2026, 9, 1))]);
        Assert.Equal(1, commands.Reads);
    }

    [DatabaseFact]
    public async Task Synced_BOM_does_not_block_release_while_the_tenant_setting_is_off()
    {
        var tenant = await fixture.NewTenantAsync("bom-off");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        var at = new DateTime(2026, 9, 1, 8, 25, 0, DateTimeKind.Utc);
        using var scope = fixture.ScopeFor(tenant);
        var store = scope.ServiceProvider.GetRequiredService<GatewayReferenceStore>();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var parent = await store.UpsertItemAsync(connection,
            new("FIC-SRV-OFF", "Fictional rack server", OpenEnum.Known("serial"), "EA", false), default);
        _ = await store.UpsertItemAsync(connection,
            new("FIC-NVME-OFF", "Fictional NVMe drive", OpenEnum.Known("serial"), "EA", false), default);
        await db.SaveChangesAsync();
        await store.UpsertBomAsync(connection, Bom("FIC-SRV-OFF", new DateTimeOffset(at),
            new BomUpsertedLine(0, OpenEnum.Known("item"), "FIC-NVME-OFF", "Fictional NVMe drive", 1, "EA")),
            default);
        await db.SaveChangesAsync();

        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var commissioned = await bus.InvokeAsync<CommissionUnitsResult>(new CommissionUnits(Guid.CreateVersion7(),
            parent, UnitOrigin.Manufactured,
            [new CommissionLine([new IdentifierInput(IdentifierType.InternalSerial, "FIC-BOM-OFF-001")])],
            at, "test", Guid.CreateVersion7()), new DeliveryOptions { TenantId = tenant.ToString() });
        var unitId = Assert.Single(commissioned.Lines).UnitId!.Value;

        var released = await bus.InvokeAsync<ReleaseUnitResult>(new ReleaseUnit(Guid.CreateVersion7(), unitId,
            at.AddHours(1), "test", Guid.CreateVersion7(), "release-signature"),
            new DeliveryOptions { TenantId = tenant.ToString() });

        Assert.Equal(LifecycleStatus.Released, released.LifecycleStatus);
    }

    [DatabaseFact]
    public async Task Full_BOM_setting_is_refused_without_an_active_capable_connection()
    {
        foreach (var connector in new string?[] { null, Cx02Capabilities.ConnectorCode, Cx04Capabilities.ConnectorCode })
        {
            var tenant = await fixture.NewTenantAsync("bom-refusal");
            if (connector is not null)
                _ = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(), connector);
            using var scope = fixture.ScopeFor(tenant);
            var settings = scope.ServiceProvider.GetRequiredService<ReleaseRecordIssuePointSettings>();
            var current = await settings.GetAsync(default);
            var overrides = connector == Cx02Capabilities.ConnectorCode
                ? new[] { new ReleaseRecordIssuePointOverride("server", current.DefaultIssuePoint,
                    ReleaseRecordBomContent.FullBom) }
                : current.ItemClassOverrides;
            var exception = await Assert.ThrowsAsync<CommandRefusedException>(() => settings.SetAsync(
                new(current.DefaultIssuePoint, overrides, current.Version,
                    connector == Cx02Capabilities.ConnectorCode
                        ? ReleaseRecordBomContent.SerialisedOnly : ReleaseRecordBomContent.FullBom), default));
            Assert.Equal("full_bom_needs_erp_bom", exception.Rule);
        }
    }

    [DatabaseFact]
    public async Task Backfill_gives_an_older_cx01_connection_the_bom_capability_and_retranslates_trees_skipped_as_unsupported()
    {
        // Found 2026-10-26 on the first live read: the connection predated P7-T15, had no BomUpserted capability,
        // and every tree was completed as unsupported. Other messages and other connectors are left alone.
        var tenant = await fixture.NewTenantAsync("bomcap");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7());
        var other = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(), Cx02Capabilities.ConnectorCode);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.ConnectionCapabilities.Where(row => row.ConnectionId == connection
            && row.CanonicalMessage == nameof(BomUpserted)).ExecuteDeleteAsync();
        var tree = SkippedAsUnsupported(tenant, connection, "ProductTrees", "YD-SRV-2U", nameof(BomUpserted));
        var item = SkippedAsUnsupported(tenant, connection, "Items", "YD-CPU-32C", nameof(ItemUpserted));
        db.InboundMessages.AddRange(tree.Message, item.Message);
        db.ExceptionTasks.AddRange(tree.Task, item.Task);
        await db.SaveChangesAsync();

        await db.Database.ExecuteSqlRawAsync(Migrations.P7T15BomCapabilityBackfill.BackfillSql);
        await db.Database.ExecuteSqlRawAsync(Migrations.P7T15BomCapabilityBackfill.BackfillSql);

        var capabilities = await db.ConnectionCapabilities.AsNoTracking()
            .Where(row => row.CanonicalMessage == nameof(BomUpserted))
            .ToDictionaryAsync(row => row.ConnectionId, row => row.SupportLevel);
        Assert.Equal(CapabilitySupport.Native, capabilities[connection]);
        Assert.Equal(CapabilitySupport.Unsupported, capabilities[other]);
        var messages = await db.InboundMessages.AsNoTracking().ToDictionaryAsync(row => row.InboundMessageId);
        Assert.Equal(InboundTranslationStatus.Pending, messages[tree.Message.InboundMessageId].TranslationStatus);
        Assert.Null(messages[tree.Message.InboundMessageId].TranslatedAt);
        Assert.Equal(InboundTranslationStatus.Completed, messages[item.Message.InboundMessageId].TranslationStatus);
        Assert.Equal([item.Message.InboundMessageId],
            await db.ExceptionTasks.AsNoTracking().Select(row => row.InboundMessageId).ToListAsync());
    }

    private (InboundMessage Message, IntegrationExceptionTask Task) SkippedAsUnsupported(TenantId tenant,
        Guid connection, string entity, string externalId, string messageType)
    {
        var at = fixture.Clock.UtcNow.AddHours(-1);
        var message = new InboundMessage
        {
            TenantId = tenant, InboundMessageId = Guid.CreateVersion7(), MessageId = Guid.CreateVersion7(),
            ConnectionId = connection, ConnectorCode = "CX01", ErpEntity = entity, ErpExternalId = externalId,
            ErpChangeMarker = "hash", MarkerKind = "content_hash", ContractVersion = "1.0", MessageType = messageType,
            CaptureMode = "poll", RawPayload = "{}", CanonicalMessage = "{}", OccurredAt = at, ObservedAt = at,
            AcceptedAt = at, IsLatest = true, TranslationStatus = InboundTranslationStatus.Completed, TranslatedAt = at,
        };
        return (message, new IntegrationExceptionTask
        {
            TenantId = tenant, ExceptionTaskId = Guid.CreateVersion7(), ConnectionId = connection,
            InboundMessageId = message.InboundMessageId, Kind = "unsupported_inbound",
            SubjectRef = $"{entity}:{externalId}", Detail = $"{messageType} is unsupported by this connection.",
            Status = "open", CreatedAt = at,
        });
    }

    private static BomUpserted Bom(string parent, DateTimeOffset at, params BomUpsertedLine[] lines) =>
        new(parent, at, false, lines);

    private sealed class BomCommandObserver : IObserver<DiagnosticListener>,
        IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly ConcurrentBag<IDisposable> subscriptions = [];
        private int reads;
        public int Reads => Volatile.Read(ref reads);

        public BomCommandObserver() => subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));

        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == "Microsoft.EntityFrameworkCore") subscriptions.Add(listener.Subscribe(this));
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuted.Name
                && value.Value is CommandExecutedEventData data
                && data.Command.CommandText.Contains("[integration].[bom_projection]", StringComparison.Ordinal))
                Interlocked.Increment(ref reads);
        }

        public void OnError(Exception error) { }
        public void OnCompleted() { }
        public void Dispose() { foreach (var subscription in subscriptions) subscription.Dispose(); }
    }
}
