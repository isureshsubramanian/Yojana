using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Yojana.Serialisation.Identity;
using Yojana.Serialisation.Data.Entities;
using Wolverine;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DeadLetters;
using Yojana.Integration.Gateway;
using Yojana.Integration.Outbox;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;

namespace Yojana.Integration.DatabaseTests;

public sealed partial class Cx01DocumentDatabaseTests
{
    [DatabaseFact]
    public async Task Credit_has_no_ownership_fallback_and_other_commands_use_only_open_ownership()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var entity = Guid.NewGuid();
        var at = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        async Task<T> Invoke<T>(object command) where T : class => await bus.InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() });
        var item = await scope.ServiceProvider.GetRequiredService<GatewayReferenceStore>().UpsertItemAsync(connection,
            new(Cx01DocumentScenarios.Unit.ItemCode, "Fictional source item", OpenEnum.Known("serial"), "EA", false, null), default);
        await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().SaveChangesAsync();
        var commissioned = await Invoke<CommissionUnitsResult>(new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new([new(IdentifierType.InternalSerial, "FIC-BUYER-UNIT")])], at, "source-test", entity));
        var unit = commissioned.Lines[0].UnitId!.Value;
        var distributor = Guid.NewGuid(); var endUser = Guid.NewGuid();
        await Invoke<OpenOwnershipResult>(new OpenOwnership(Guid.NewGuid(), unit, distributor, PartyType.Distributor, EpisodeReason.Sale, at.AddMinutes(1), "source-test", entity));
        await Invoke<OpenOwnershipResult>(new OpenOwnership(Guid.NewGuid(), unit, endUser, PartyType.EndUser, EpisodeReason.Sale, at.AddMinutes(2), "source-test", entity));
        var source = scope.ServiceProvider.GetRequiredService<IOutboundUnitSourcePort>();
        Assert.Equal(endUser, (await source.ReadAsync(unit, null, default)).CustomerPartyRef);
        Assert.Null((await source.ReadAsync(unit, null, default, allowOwnershipFallback: false)).CustomerPartyRef);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ExternalIdMaps.Add(Map(tenant, connection, "party", "FIC-END-USER", endUser));
        await db.SaveChangesAsync();
        var header = new PublishedEventHeader("test", "1.0", Guid.NewGuid(), unit, at.AddHours(1), at.AddHours(1), 1, entity, "source-test", null);
        await PublishedEventOutboundHandler.Handle(new RmaDispositioned(header, Guid.NewGuid(), Guid.NewGuid(), unit, "credit", null, null, "USD"),
            scope.ServiceProvider.GetRequiredService<OutboundCommandStager>(), default);
        var credit = await db.OutboundCommands.SingleAsync(x => x.ConnectionId == connection && x.CommandType == nameof(CreateCreditMemo));
        Assert.Empty(JsonSerializer.Deserialize<CreateCreditMemo>(credit.Payload, IntegrationContracts.Json)!.CustomerRef);
        await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
        Assert.Contains("customer_ref", (await db.DeadLetterItems.SingleAsync(x => x.SourceId == credit.OutboundCommandId)).Failure);
        await Invoke<CloseOwnershipResult>(new CloseOwnership(Guid.NewGuid(), unit, EpisodeReason.Return, at.AddMinutes(3), "source-test", entity));
        Assert.Null((await source.ReadAsync(unit, null, default)).CustomerPartyRef);
    }

    [DocumentDatabaseTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stock_source_is_the_event_location_even_after_later_custody_moves(bool carriesLocation)
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var at = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        var entity = Guid.NewGuid(); var origin = Guid.NewGuid(); var destination = Guid.NewGuid(); var later = Guid.NewGuid();
        async Task<T> Invoke<T>(object command) where T : class => await bus.InvokeAsync<T>(command, new DeliveryOptions { TenantId = tenant.ToString() });
        var item = await scope.ServiceProvider.GetRequiredService<GatewayReferenceStore>().UpsertItemAsync(connection,
            new(Cx01DocumentScenarios.Unit.ItemCode, "Fictional source item", OpenEnum.Known("serial"), "EA", false, null), default);
        await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().SaveChangesAsync();
        var commissioned = await Invoke<CommissionUnitsResult>(new CommissionUnits(Guid.NewGuid(), item, UnitOrigin.Purchased,
            [new([new(IdentifierType.InternalSerial, Cx01DocumentScenarios.Unit.Serial)])], at, "source-test", entity,
            LocationId: origin, LocationKind: LocationKind.Warehouse));
        var unit = commissioned.Lines[0].UnitId!.Value;
        await Invoke<ChangeCustodyResult>(new ChangeCustody(Guid.NewGuid(), unit, LocationKind.Quarantine, "return", at.AddHours(1), "source-test", entity, LocationId: destination));
        await Invoke<ChangeCustodyResult>(new ChangeCustody(Guid.NewGuid(), unit, LocationKind.Warehouse, "restock", at.AddHours(2), "source-test", entity, LocationId: later));
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ExternalIdMaps.AddRange(Map(tenant, connection, "location", "store", origin),
            Map(tenant, connection, "location", "later", later));
        await db.SaveChangesAsync();
        var settings = scope.ServiceProvider.GetRequiredService<Cx01Settings>();
        var configured = await settings.GetAsync(connection, default);
        await settings.SetAsync(connection, configured with { Documents = configured.Documents! with
        {
            LocationMap = new Dictionary<string, Cx01Warehouse>(configured.Documents.LocationMap!) { ["quarantine"] = new("02") },
        } }, default);
        var header = new PublishedEventHeader(RmaReceived.Schema, RmaReceived.Version, Guid.NewGuid(), unit, at.AddHours(1), at.AddHours(3), 1, entity, "source-test", null);
        await PublishedEventOutboundHandler.Handle(new RmaReceived(header, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), unit, unit, null,
            "accepted", "defective", "graded_value", "2026-09", carriesLocation ? origin : null), scope.ServiceProvider.GetRequiredService<OutboundCommandStager>(), default);
        var row = await db.OutboundCommands.SingleAsync(x => x.ConnectionId == connection);
        var stock = JsonSerializer.Deserialize<ChangeStockStatusOrLocation>(row.Payload, IntegrationContracts.Json)!;
        Assert.Equal("store", stock.FromLocationOrStatus);
        Assert.Equal(Cx01DocumentScenarios.Unit.Serial, stock.Unit.IdentifierValue);
        var result = await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
        Assert.True(result.Completed == 1, row.LastFailure);
        Assert.Single(simulator.DocumentCreateCounts);
    }

    [DatabaseFact]
    public async Task Every_repark_is_audited_and_expiry_keeps_the_task_completed()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var command = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "REPARK-HISTORY");
        var row = await StageAsync(scope, tenant, connection, command with { ShipToRef = "" });
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Parked);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var task = await db.ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == row.OutboundCommandId);
        await scope.ServiceProvider.GetRequiredService<DeadLetterService>().CompleteManualTaskAsync(task.ManualOutboundTaskId,
            new(CorrectedDraft: JsonSerializer.SerializeToElement(command, IntegrationContracts.Json)), "first-operator", default);
        var completedAt = task.CompletedAt;
        var sink = scope.ServiceProvider.GetRequiredService<DeadLetterParkingSink>();
        await sink.ParkAsync(row, "max_outbox_age", "Expired", default);
        await db.SaveChangesAsync();
        Assert.Equal(ManualTaskStatus.Completed, task.Status);
        Assert.Equal("first-operator", task.CompletedBy);
        Assert.Equal(completedAt, task.CompletedAt);
        await sink.ParkAsync(row, OutboundCompleteness.MissingSourceField, "Missing source fields: ship_to_ref", default);
        await db.SaveChangesAsync();
        Assert.Equal(ManualTaskStatus.Open, task.Status);
        var parks = await db.DeadLetterEvents.Where(x => x.DeadLetterItemId == task.DeadLetterItemId && x.Action == DeadLetterAction.Parked).ToListAsync();
        Assert.Equal(3, parks.Count);
        using var audit = JsonDocument.Parse(parks.Single(x => x.DetailsJson.Contains("first-operator", StringComparison.Ordinal) && x.Reason!.Contains("ship_to_ref", StringComparison.Ordinal)).DetailsJson);
        Assert.Equal("first-operator", audit.RootElement.GetProperty("previous_task").GetProperty("completed_by").GetString());
        Assert.Equal(completedAt, audit.RootElement.GetProperty("previous_task").GetProperty("completed_at").GetDateTime());
    }

    [DatabaseFact]
    public async Task Concurrent_corrections_complete_a_manual_task_only_once()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var setup = fixture.ScopeFor(tenant);
        var command = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "CONCURRENT-CORRECTION");
        var row = await StageAsync(setup, tenant, connection, command with { ShipToRef = "" });
        await setup.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default);
        var task = await setup.ServiceProvider.GetRequiredService<IntegrationDbContext>().ManualOutboundTasks.SingleAsync(x => x.OutboundCommandId == row.OutboundCommandId);
        async Task<string> Correct(string actor)
        {
            using var scope = fixture.ScopeFor(tenant);
            try
            {
                await scope.ServiceProvider.GetRequiredService<DeadLetterService>().CompleteManualTaskAsync(task.ManualOutboundTaskId,
                    new(CorrectedDraft: JsonSerializer.SerializeToElement(command, IntegrationContracts.Json)), actor, default);
                return "completed";
            }
            catch (DeadLetterRuleException ex) { return ex.Rule; }
        }
        var results = await Task.WhenAll(Correct("one"), Correct("two"));
        Assert.Single(results, x => x == "completed");
        Assert.Single(results, x => x == "manual_task_closed");
    }

    [DatabaseFact]
    public async Task Query_before_create_adopts_a_remote_document_even_when_the_stored_draft_is_incomplete()
    {
        await using var simulator = new Cx01Simulator { EnableDocumentPosts = true };
        var (tenant, connection) = await ConnectionAsync(simulator);
        using var scope = fixture.ScopeFor(tenant);
        var command = (CreateReplacementDelivery)Cx01DocumentScenarios.Command("delivery", "ALREADY-POSTED");
        var row = await StageAsync(scope, tenant, connection, command);
        var relay = scope.ServiceProvider.GetRequiredService<OutboundRelay>();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Completed);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.Entry(row).ReloadAsync();
        row.Payload = JsonSerializer.Serialize(command with { ShipToRef = "" }, IntegrationContracts.Json);
        row.Status = OutboundCommandStatus.Pending;
        await db.SaveChangesAsync();
        Assert.Equal(1, (await relay.RelayConnectionAsync(connection, default)).Completed);
        Assert.False(await db.DeadLetterItems.AnyAsync(x => x.SourceId == row.OutboundCommandId));
        Assert.Equal(1, simulator.DocumentCreateCounts[Cx01DocumentScenarios.Collection("delivery")]);
    }

    [DatabaseFact]
    public async Task Overlapping_custody_is_selected_deterministically_and_warned_without_failing_the_read()
    {
        var tenant = await fixture.NewTenantAsync("srcwarn");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unit = Guid.NewGuid(); var expected = Guid.NewGuid();
        var at = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        db.Episodes.AddRange(new CustodyEpisode
        {
            TenantId = tenant, EpisodeId = Guid.NewGuid(), UnitId = unit, Kind = EpisodeKind.Custody,
            LocationKind = LocationKind.Warehouse, LocationId = Guid.NewGuid(), ValidFrom = at, ValidTo = at.AddHours(4),
        }, new CustodyEpisode
        {
            TenantId = tenant, EpisodeId = Guid.NewGuid(), UnitId = unit, Kind = EpisodeKind.Custody,
            LocationKind = LocationKind.Warehouse, LocationId = expected, ValidFrom = at.AddHours(1), ValidTo = at.AddHours(5),
        });
        await db.SaveChangesAsync();
        var logger = new SourceWarningLogger();
        var port = ActivatorUtilities.CreateInstance<OutboundUnitSourcePort>(scope.ServiceProvider, logger);
        Assert.Equal(expected, (await port.ReadAsync(unit, null, default, custodyAt: at.AddHours(2))).LocationId);
        Assert.Equal(1, logger.Warnings);
    }

    private sealed class SourceWarningLogger : ILogger<OutboundUnitSourcePort>
    {
        public int Warnings { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings++;
        }
    }

    [DatabaseFact]
    public async Task Generic_connector_without_a_source_check_sends_its_own_incomplete_payload()
    {
        fixture.Connector.Configure(FakeOutboundBehaviour.Succeed);
        var tenant = await fixture.NewTenantAsync("gensource");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), IntegrationHostFixture.FakeConnectorCode);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        (await db.ConnectionGatewaySettings.SingleAsync(x => x.ConnectionId == connection)).OutboundMode = "documents";
        var command = new CreateServiceOrder(Guid.NewGuid(), "service/" + Guid.NewGuid().ToString("N"), "GENERIC-SOURCE-" + Guid.NewGuid().ToString("N"),
            "", new(Guid.NewGuid(), ""), "", "");
        await StageAsync(scope, tenant, connection, command);
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>().RelayConnectionAsync(connection, default)).Completed);
        Assert.Equal(1, fixture.Connector.CreateCallsFor(connection));
        Assert.False(await db.DeadLetterItems.AnyAsync(x => x.ConnectionId == connection));
    }
}
