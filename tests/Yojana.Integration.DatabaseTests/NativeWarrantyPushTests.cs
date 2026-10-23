using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Backfills;
using Yojana.Integration.Coexistence;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Gateway;
using Yojana.Integration.Outbox;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class NativeWarrantyPushTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-service-manager";

    [DatabaseFact]
    public async Task Entitlement_change_kinds_coalesce_the_latest_warranty_values_and_frozen_sends_nothing()
    {
        await using var simulator = new Cx01Simulator();
        AddCard(simulator, 901);
        var (tenant, connection) = await RegisteredAsync(simulator);
        var unit = Guid.CreateVersion7();
        await AddUnitAndMapAsync(tenant, connection, unit, "901", LifecycleStatus.Released);
        using var scope = fixture.ScopeFor(tenant);
        var contracts = scope.ServiceProvider.GetRequiredService<WarrantyContractMaintenanceSettings>();
        await contracts.UpdateAsync(connection,
            new(WarrantyContractsMaintainedIn.Module, (await contracts.GetAsync(default)).Version), Actor, default);
        var settings = scope.ServiceProvider.GetRequiredService<CoexistenceSettings>();
        await settings.UpdateAsync(connection,
            new(NativeWarrantyObjectModes.Pushed, null, null), Actor, default);
        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var entitlement = Entitlement(tenant, unit, new DateOnly(2028, 8, 31));
        serialisation.Entitlements.Add(entitlement);
        await serialisation.SaveChangesAsync();
        var pushes = scope.ServiceProvider.GetRequiredService<NativeWarrantyPushService>();
        var causes = new[]
        {
            "entitlement.created", "entitlement.amended", "entitlement.superseded", "entitlement.closed",
        };
        for (var index = 0; index < causes.Length; index++)
        {
            entitlement.EndDate = new DateOnly(2028, 8, 31).AddMonths(index);
            if (index == causes.Length - 1)
            {
                entitlement.Status = EntitlementStatus.Closed;
                entitlement.ClosedReason = EntitlementClosedReason.Refunded;
                entitlement.ClosedEffective = new DateOnly(2026, 9, 30);
                entitlement.ClosedAt = fixture.Clock.UtcNow;
            }
            await serialisation.SaveChangesAsync();
            var eventId = Guid.CreateVersion7();
            var header = new PublishedEventHeader(EntitlementCoverageChanged.Schema,
                EntitlementCoverageChanged.Version, eventId, unit, fixture.Clock.UtcNow,
                fixture.Clock.UtcNow, index + 1, Guid.NewGuid(), Actor, null);
            var changed = new EntitlementCoverageChanged(header, [], [], null, entitlement.EndDate,
                causes[index]);
            await NativeWarrantyPushHandler.Handle(changed, pushes, default);
        }

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var queued = Assert.Single(await db.OutboundCommands.Where(row => row.ConnectionId == connection
            && row.CommandType == nameof(PushNativeWarrantyObject)
            && row.Status == OutboundCommandStatus.Pending).ToListAsync());
        var command = JsonSerializer.Deserialize<PushNativeWarrantyObject>(queued.Payload,
            IntegrationContracts.Json)!;
        Assert.Null(command.WarrantyEndDate);
        Assert.True(command.IncludesWarrantyContractFields);
        Assert.Equal(1, await db.NativeWarrantyPushStates.CountAsync(row => row.ConnectionId == connection));

        var frozen = await settings.UpdateAsync(connection,
            new(NativeWarrantyObjectModes.Frozen, null, null), Actor, default);
        Assert.Equal(NativeWarrantyObjectModes.Frozen, frozen.NativeWarrantyObjectMode);
        db.ChangeTracker.Clear();
        Assert.Equal(NativeWarrantyPushService.DroppedFrozenOutcome,
            (await db.OutboundCommands.SingleAsync(row => row.OutboundCommandId == queued.OutboundCommandId)).Outcome);
        Assert.Equal(0, await pushes.QueueUnitAsync(unit, Guid.CreateVersion7(), default));
        var freezeAudit = (await db.ConnectionEvents.Where(row => row.ConnectionId == connection
                && row.EventType == "CoexistenceSettingsChanged").ToListAsync())[^1];
        using var audit = JsonDocument.Parse(freezeAudit.DetailsJson);
        Assert.Equal(1, audit.RootElement.EnumerateObject().Single(property =>
            property.Name.EndsWith("queued_pushes", StringComparison.OrdinalIgnoreCase)).Value.GetInt32());
    }

    [DatabaseFact]
    public async Task Frozen_backfill_resumes_from_its_persisted_cursor_and_state_is_tenant_isolated()
    {
        await using var simulator = new Cx01Simulator();
        AddCard(simulator, 911);
        AddCard(simulator, 912);
        var (tenant, connection) = await RegisteredAsync(simulator);
        var first = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var second = Guid.Parse("20000000-0000-0000-0000-000000000002");
        await AddUnitAndMapAsync(tenant, connection, first, "911", LifecycleStatus.Released);
        await AddUnitAndMapAsync(tenant, connection, second, "912", LifecycleStatus.Void);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var settings = scope.ServiceProvider.GetRequiredService<CoexistenceSettings>();
            await settings.UpdateAsync(connection,
                new(NativeWarrantyObjectModes.Pushed, null, null), Actor, default);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var cursor = await db.BackfillCursors.SingleAsync(row => row.ConnectionId == connection
                && row.Entity == NativeWarrantyPushService.BackfillEntity);
            cursor.PageSize = 1;
            await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<BackfillService>()
                .ProcessAsync(cursor.BackfillCursorId, 1, default);
            db.ChangeTracker.Clear();
            cursor = await db.BackfillCursors.SingleAsync(row => row.BackfillCursorId == cursor.BackfillCursorId);
            Assert.Equal(1, cursor.ProcessedMessages);
            Assert.Equal(BackfillStatus.Running, cursor.Status);

            await settings.UpdateAsync(connection,
                new(NativeWarrantyObjectModes.Frozen, null, null), Actor, default);
            db.ChangeTracker.Clear();
            Assert.Equal(BackfillStatus.Paused,
                (await db.BackfillCursors.SingleAsync(row => row.BackfillCursorId == cursor.BackfillCursorId)).Status);
            await settings.UpdateAsync(connection,
                new(NativeWarrantyObjectModes.Pushed, null, null), Actor, default);
            await scope.ServiceProvider.GetRequiredService<BackfillService>()
                .ProcessAsync(cursor.BackfillCursorId, null, default);
            db.ChangeTracker.Clear();
            cursor = await db.BackfillCursors.SingleAsync(row => row.BackfillCursorId == cursor.BackfillCursorId);
            Assert.Equal(BackfillStatus.Completed, cursor.Status);
            Assert.Equal(2, cursor.ProcessedMessages);
            Assert.Equal(2, await db.NativeWarrantyPushStates.CountAsync(row => row.ConnectionId == connection));
        }

        var other = await NewTenantAsync("nw-other");
        using var otherScope = fixture.ScopeFor(other);
        var otherDb = otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(0, await otherDb.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM integration.native_warranty_push_state").SingleAsync());
        otherDb.NativeWarrantyPushStates.Add(new()
        {
            TenantId = tenant, ConnectionId = connection, UnitId = Guid.CreateVersion7(), RemoteRecordKey = "911",
            DesiredModuleStatus = LifecycleStatus.Released, UpdatedAt = fixture.Clock.UtcNow,
        });
        var denied = await Assert.ThrowsAsync<DbUpdateException>(() => otherDb.SaveChangesAsync());
        Assert.Equal(33504, Assert.IsType<Microsoft.Data.SqlClient.SqlException>(denied.InnerException).Number);
    }

    [DatabaseFact]
    public async Task Remote_edit_parks_one_conflict_resolution_pushes_and_rejection_uses_dead_letter_actions()
    {
        await using var simulator = new Cx01Simulator();
        AddCard(simulator, 921);
        var (tenant, connection) = await RegisteredAsync(simulator);
        var unit = Guid.CreateVersion7();
        await AddUnitAndMapAsync(tenant, connection, unit, "921", LifecycleStatus.Void);
        using var scope = fixture.ScopeFor(tenant);
        var drift = scope.ServiceProvider.GetRequiredService<CoexistenceDriftService>();
        Assert.Equal(0, (await drift.ScanAsync(connection, default)).ConflictsRaised);
        var settings = scope.ServiceProvider.GetRequiredService<CoexistenceSettings>();
        await settings.UpdateAsync(connection,
            new(NativeWarrantyObjectModes.Pushed, null, null), Actor, default);
        var pushes = scope.ServiceProvider.GetRequiredService<NativeWarrantyPushService>();
        Assert.Equal(1, await pushes.QueueUnitAsync(unit, Guid.CreateVersion7(), default));
        simulator.EditEquipmentCardBeforeNextPatch = card => card["StatusOfSerialNumber"] = "sns_Inactive";
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        Assert.Equal(0, simulator.PatchAttempts);
        Assert.Equal("sns_Inactive", Card(simulator, 921)["StatusOfSerialNumber"]);

        var conflicts = scope.ServiceProvider.GetRequiredService<CoexistenceConflicts>();
        var conflict = Assert.Single((await conflicts.ListAsync(connection, "open", null, 20, default)).Items);
        Assert.Equal(CoexistenceConflictKind.PushedWarrantyFieldChangedRemotely, conflict.Kind);
        Assert.Equal("sns_Active", JsonSerializer.Deserialize<NativeWarrantyRemoteValues>(
            conflict.BaselineValue!, IntegrationContracts.Json)!.Status);
        Assert.Equal("sns_Inactive", JsonSerializer.Deserialize<NativeWarrantyRemoteValues>(
            conflict.RemoteValue!, IntegrationContracts.Json)!.Status);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var parked = await db.OutboundCommands.SingleAsync(row => row.ConnectionId == connection
            && row.CommandType == nameof(PushNativeWarrantyObject));
        Assert.Equal(OutboundCommandStatus.Parked, parked.Status);
        Assert.Equal(OutboundConnectorOutcome.CoexistenceConflict, parked.Outcome);
        Assert.Empty(await db.DeadLetterItems.Where(row => row.ConnectionId == connection).ToListAsync());

        await conflicts.ResolveAsync(conflict.ConflictId,
            new(CoexistenceConflictDecision.KeepModule, "The module remains authoritative."), Actor, default);
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Completed);
        Assert.Equal(1, simulator.PatchAttempts);
        Assert.Equal("sns_Terminated", Card(simulator, 921)["StatusOfSerialNumber"]);
        Assert.Equal(0, (await drift.ScanAsync(connection, default)).ConflictsRaised);

        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var stored = await serialisation.Units.SingleAsync(row => row.UnitId == unit);
        stored.LifecycleStatus = LifecycleStatus.Released;
        await serialisation.SaveChangesAsync();
        await pushes.QueueUnitAsync(unit, Guid.CreateVersion7(), default);
        simulator.RejectNextEquipmentCardPatchWith = HttpStatusCode.BadRequest;
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        db.ChangeTracker.Clear();
        var letter = await db.DeadLetterItems.SingleAsync(row => row.ConnectionId == connection);
        Assert.Equal(OutboundConnectorOutcome.Rejected, letter.ReasonCode);
        Assert.Equal(DeadLetterStatus.NeedsAttention, letter.Status);
        var task = await db.ManualOutboundTasks.SingleAsync(row => row.DeadLetterItemId == letter.DeadLetterItemId);
        Assert.Equal(ManualTaskStatus.Open, task.Status);
        Assert.Equal(nameof(PushNativeWarrantyObject), task.CommandType);
    }

    private async Task<(TenantId Tenant, Guid Connection)> RegisteredAsync(Cx01Simulator simulator)
    {
        var tenant = await NewTenantAsync("nw");
        using var scope = fixture.ScopeFor(tenant);
        var result = await scope.ServiceProvider.GetRequiredService<ConnectionCommands>()
            .RegisterAsync(simulator.RegistrationRequest(), Actor, default);
        await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ConnectionGatewaySettings
            .Where(row => row.ConnectionId == result.ConnectionId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.ReadOnly, false));
        return (tenant, result.ConnectionId);
    }

    private async Task<TenantId> NewTenantAsync(string prefix)
    {
        var tenant = await fixture.NewTenantAsync(prefix);
        using var scope = fixture.ScopeFor(tenant);
        var catalogue = scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>();
        await catalogue.SeedGenericEntriesAsync(default);
        await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ConnectorCatalogue
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Enabled, true));
        return tenant;
    }

    private async Task AddUnitAndMapAsync(TenantId tenant, Guid connection, Guid unit, string remoteKey,
        string status)
    {
        using var scope = fixture.ScopeFor(tenant);
        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var eventId = Guid.CreateVersion7();
        serialisation.Units.Add(new Unit
        {
            TenantId = tenant, UnitId = unit, LifecycleStatus = status, Origin = UnitOrigin.Purchased,
            SerialisationPoint = SerialisationPoint.Receipt, RetentionClass = "standard_10y",
            CreatedAt = fixture.Clock.UtcNow, CreatedEventId = eventId, CreatedEventTime = fixture.Clock.UtcNow,
        });
        serialisation.Identifiers.Add(new UnitIdentifier
        {
            TenantId = tenant, IdentifierId = Guid.CreateVersion7(), UnitId = unit,
            Type = IdentifierType.InternalSerial, Scheme = IdentifierScheme.Internal,
            ScopeKey = "HW-SERVER-100", RawValue = "SN-" + remoteKey,
            NormalisedValue = "SN" + remoteKey, ValidFrom = fixture.Clock.UtcNow,
            ActorRef = Actor, RecordedTime = fixture.Clock.UtcNow, EventId = eventId,
        });
        await serialisation.SaveChangesAsync();
        var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        integration.ExternalIdMaps.Add(new()
        {
            TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(), ConnectionId = connection,
            EntityType = NativeWarrantyPushService.MappingEntityType, ExternalId = remoteKey,
            ModuleId = unit, ValidFrom = fixture.Clock.UtcNow, NaturalKeyHash = "card-" + remoteKey,
            MappingSource = ExternalIdMappingSource.Migration, MappedBy = Actor, MappedAt = fixture.Clock.UtcNow,
        });
        await integration.SaveChangesAsync();
    }

    private static Entitlement Entitlement(TenantId tenant, Guid unit, DateOnly end) => new()
    {
        TenantId = tenant, EntitlementId = Guid.CreateVersion7(), UnitId = unit,
        Scope = EntitlementScope.Unit, Type = EntitlementType.Manufacturer,
        CoverageFamily = CoverageFamilyCode.Base, Direction = EntitlementDirection.Outbound,
        ObligorEntityId = Guid.NewGuid(), PolicyVersionId = Guid.NewGuid(), StartTrigger = StartTrigger.Deliver,
        StartDate = new DateOnly(2026, 9, 1), StartTimeZone = "Asia/Kolkata", EndDate = end,
        DurationValue = 24, DurationUnit = DurationRule.Months, EndOfMonthRule = DurationRule.Clamp,
        DurationBasis = Yojana.Serialisation.Contracts.DurationBasis.Time,
        Transferable = Transferability.Yes, AssuranceOrService = AssuranceOrService.Assurance,
        Status = EntitlementStatus.Active, Confidence = "high", Provenance = EntitlementProvenance.Created,
        CreatedEventId = Guid.CreateVersion7(), CreatedAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
    };

    private static void AddCard(Cx01Simulator simulator, long number) => simulator.EquipmentCards.Add(new()
    {
        ["EquipmentCardNum"] = number, ["InternalSerialNum"] = "SN-" + number,
        ["ItemCode"] = "HW-SERVER-100", ["CustomerCode"] = "C-FICTIONAL-HARDWARE",
        ["StatusOfSerialNumber"] = "sns_Active", ["ContractCode"] = null,
        ["ContractEndDate"] = null, ["DeliveryCode"] = "FIC-DELIVERY-" + number,
        ["InvoiceCode"] = null,
    });

    private static Dictionary<string, object?> Card(Cx01Simulator simulator, long number) =>
        simulator.EquipmentCards.Single(card => Convert.ToInt64(card["EquipmentCardNum"]) == number);
}
