using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.ErpChange;
using Yojana.Integration.Gateway;
using Yojana.Integration.Outbox;
using Yojana.Integration.Security.Audit;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Data;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ErpChangeCutoverDatabaseTests(IntegrationHostFixture fixture)
{
    private const string Operator = "fictional-integration-operator";
    private const string InventoryController = "fictional-inventory-controller";
    private static readonly DateTime Start = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Target_document_is_kept_for_comparison_while_the_source_version_wins_and_one_divergence_is_raised()
    {
        fixture.Clock.UtcNow = Start;
        var tenant = await fixture.NewTenantAsync("ecwins");
        var entity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, entity, IntegrationHostFixture.FakeConnectorCode);
        var target = await fixture.AddConnectionAsync(tenant, entity, IntegrationHostFixture.FakeConnectorCode);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var run = Run(tenant, source, target, ErpChangeStep.DualRun);
        run.DualRunStartedAt = Start;
        db.ErpChangeRuns.Add(run);
        var documentId = Guid.NewGuid();
        Map(db, tenant, source, "SRC-DN-1", documentId);
        Map(db, tenant, target, "TGT-SH-1", documentId);
        var sourcePayload = Delivery("SRC-DN-1", 2);
        var targetPayload = Delivery("TGT-SH-1", 1);
        var sourceInbound = Guid.CreateVersion7();
        db.CanonicalDocuments.Add(new()
        {
            TenantId = tenant, DocumentId = documentId, ConnectionId = source,
            ErpEntity = "delivery", ErpExternalId = "SRC-DN-1", MessageType = nameof(DeliveryPosted),
            Payload = JsonSerializer.Serialize(sourcePayload, IntegrationContracts.Json),
            OccurredAt = Start, UpdatedAt = Start, SourceInboundMessageId = sourceInbound,
        });
        var inbound = new InboundMessage
        {
            TenantId = tenant, InboundMessageId = Guid.CreateVersion7(), MessageId = Guid.CreateVersion7(),
            ConnectionId = target, ConnectorCode = IntegrationHostFixture.FakeConnectorCode,
            ErpEntity = "shipment", ErpExternalId = "TGT-SH-1", ErpChangeMarker = "1",
            MarkerKind = "version", ContractVersion = "1.0", MessageType = nameof(DeliveryPosted),
            RawPayload = "{}", CanonicalMessage = JsonSerializer.Serialize(new { payload = targetPayload },
                IntegrationContracts.Json), OccurredAt = Start, ObservedAt = Start, AcceptedAt = Start,
            IsLatest = true, TranslationStatus = InboundTranslationStatus.Pending,
        };
        db.InboundMessages.Add(inbound);
        await db.SaveChangesAsync();
        var beforeEvents = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events.CountAsync();

        var result = await scope.ServiceProvider.GetRequiredService<InboundGateway>()
            .TranslateAsync(inbound.InboundMessageId, default);
        Assert.True(result.Translated);
        Assert.Equal(0, result.UnitsAffected);
        db.ChangeTracker.Clear();
        Assert.Equal(2, JsonSerializer.Deserialize<DeliveryPosted>(
            (await db.CanonicalDocuments.SingleAsync(row => row.DocumentId == documentId)).Payload,
            IntegrationContracts.Json)!.Lines[0].Quantity);
        var divergence = await db.ErpChangeDivergences.SingleAsync();
        Assert.Equal(ErpChangeDivergenceKind.Document, divergence.Kind);
        Assert.Contains("2", divergence.SourceValue);
        Assert.Contains("1", divergence.TargetValue);
        await scope.ServiceProvider.GetRequiredService<InboundGateway>()
            .TranslateAsync(inbound.InboundMessageId, default);
        Assert.Equal(1, await db.ErpChangeDivergences.CountAsync());
        Assert.Equal(beforeEvents,
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events.CountAsync());
    }

    [DatabaseFact]
    public async Task Clean_period_is_tenant_owned_audited_and_both_dual_run_counters_gate_advancement()
    {
        fixture.Clock.UtcNow = Start;
        var tenant = await fixture.NewTenantAsync("ecdual");
        var legalEntity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, legalEntity, IntegrationHostFixture.FakeConnectorCode);
        var target = await fixture.AddConnectionAsync(tenant, legalEntity, IntegrationHostFixture.FakeConnectorCode);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var run = Run(tenant, source, target, ErpChangeStep.DualRun);
        db.ErpChangeRuns.Add(run);
        await db.SaveChangesAsync();
        var settings = scope.ServiceProvider.GetRequiredService<ErpChangeCleanPeriodSettings>();
        var initial = await settings.GetAsync(default);
        Assert.Equal(7, initial.Days);
        foreach (var invalid in new[] { 0, 91 })
        {
            var refusal = await Assert.ThrowsAsync<ErpChangeRuleException>(() => settings.SetAsync(
                target, new(invalid, initial.SettingsVersion), Operator, default));
            Assert.Equal("erp_change_clean_days_invalid", refusal.Rule);
        }
        var configured = await settings.SetAsync(target, new(3, initial.SettingsVersion), Operator, default);
        Assert.Equal(3, configured.Days);
        Assert.True(await db.ConnectionEvents.AnyAsync(row => row.ConnectionId == target
            && row.EventType == nameof(ConnectionAuditEventType.ErpChangeCleanPeriodChanged)));

        var dual = scope.ServiceProvider.GetRequiredService<ErpChangeDualRunService>();
        await dual.StartAsync(run.ErpChangeRunId, Operator, default);
        var first = await dual.ReportAsync(run.ErpChangeRunId,
            new(ErpChangeDivergenceKind.Document, "delivery", Guid.NewGuid(), "DN-1001",
                "2 units", "1 unit"), default);
        await dual.ReportAsync(run.ErpChangeRunId,
            new(ErpChangeDivergenceKind.Document, "delivery", first.SubjectId, "DN-1001",
                "2 units", "1 unit"), default);
        Assert.Equal(1, await db.ErpChangeDivergences.CountAsync());
        await dual.AcceptSourceAsync(run.ErpChangeRunId, first.DivergenceId,
            ErpChangeDivergenceKind.Document, Operator, default);

        fixture.Clock.UtcNow = Start.AddDays(27);
        Assert.True((await dual.RunDailyAsync(run.ErpChangeRunId, Operator, default)).Clean);
        fixture.Clock.UtcNow = Start.AddDays(28);
        Assert.True((await dual.RunDailyAsync(run.ErpChangeRunId, Operator, default)).Clean);
        var reset = await dual.ReportAsync(run.ErpChangeRunId,
            new(ErpChangeDivergenceKind.Stock, "unit", Guid.NewGuid(), "SRV-2509-0041",
                "MAIN-A-04", "MAIN-A-14"), default);
        Assert.False((await dual.RunDailyAsync(run.ErpChangeRunId, Operator, default)).Clean);
        await dual.ExplainAsync(run.ErpChangeRunId, reset.DivergenceId, ErpChangeDivergenceKind.Stock,
            new("The target bin is a temporary staging bin recorded by the warehouse."),
            InventoryController, default);
        Assert.Equal(0, await dual.ConsecutiveCleanDaysAsync(run.ErpChangeRunId, default));

        fixture.Clock.UtcNow = Start.AddDays(29);
        Assert.True((await dual.RunDailyAsync(run.ErpChangeRunId, Operator, default)).Clean);
        var runs = scope.ServiceProvider.GetRequiredService<ErpChangeRunService>();
        var beforeThirty = await runs.CheckAsync(run.ErpChangeRunId, Operator, default);
        Assert.False(beforeThirty.Passed);
        fixture.Clock.UtcNow = Start.AddDays(30);
        Assert.True((await dual.RunDailyAsync(run.ErpChangeRunId, Operator, default)).Clean);
        fixture.Clock.UtcNow = Start.AddDays(31);
        Assert.True((await dual.RunDailyAsync(run.ErpChangeRunId, Operator, default)).Clean);
        Assert.True((await runs.CheckAsync(run.ErpChangeRunId, Operator, default)).Passed);
        Assert.Equal(ErpChangeStep.Cutover,
            (await runs.AdvanceAsync(run.ErpChangeRunId, Operator, default)).CurrentStep);
    }

    [DatabaseFact]
    public async Task Cutover_validates_each_target_type_drains_to_target_and_converts_a_parked_row_to_a_manual_task()
    {
        fixture.Clock.UtcNow = Start;
        fixture.Connector.Configure(FakeOutboundBehaviour.Succeed);
        var tenant = await fixture.NewTenantAsync("eccut");
        var legalEntity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, legalEntity);
        var target = await fixture.AddConnectionAsync(tenant, legalEntity, IntegrationHostFixture.FakeConnectorCode);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var run = Run(tenant, source, target, ErpChangeStep.Cutover);
        db.ErpChangeRuns.Add(run);
        var pending = Command(tenant, source, OutboundCommandStatus.Pending, "claim/CLM-1001");
        var parked = Command(tenant, source, OutboundCommandStatus.Parked, "rma/RMA-1002");
        db.OutboundCommands.AddRange(pending, parked);
        await db.SaveChangesAsync();

        var cutover = scope.ServiceProvider.GetRequiredService<ErpChangeCutoverService>();
        await cutover.BeginAsync(run.ErpChangeRunId, new("reference_only"), Operator, default);
        var required = await db.ConnectionCapabilities.AsNoTracking().Where(row => row.ConnectionId == target
                && new[] { "CreateCreditMemo", "PostWarrantyExpense", "CreateReplacementDelivery",
                    "ChangeStockStatusOrLocation", "CreateServiceOrder", "AttachDocument",
                    "PushNativeWarrantyObject" }.Contains(row.CanonicalMessage)
                && row.SupportLevel != CapabilitySupport.Unsupported)
            .Select(row => row.CanonicalMessage).Distinct().ToListAsync();
        foreach (var type in required)
            await cutover.RecordValidationAsync(run.ErpChangeRunId,
                new(type, ErpChangeValidationMethod.TestCompany,
                    $"Validated by the {IntegrationHostFixture.FakeConnectorCode} simulator."), Operator, default);
        await cutover.EnableTargetAsync(run.ErpChangeRunId, Operator, default);
        await cutover.DrainAsync(run.ErpChangeRunId, Operator, default);
        db.ChangeTracker.Clear();
        Assert.Equal(OutboundCommandStatus.Done,
            (await db.OutboundCommands.SingleAsync(row => row.OutboundCommandId == pending.OutboundCommandId)).Status);
        Assert.True(await db.ManualOutboundTasks.AnyAsync(row => row.OutboundCommandId == parked.OutboundCommandId
            && row.ConnectionId == target && row.Status == ManualTaskStatus.Open));
        Assert.Empty(await db.OutboundCommands.Where(row => row.ConnectionId == source
            && row.Status != OutboundCommandStatus.Done).ToListAsync());
        var csv = await cutover.PendingActionsCsvAsync(run.ErpChangeRunId, default);
        Assert.Contains("RMA-1002", System.Text.Encoding.UTF8.GetString(csv));
        await cutover.ConfirmFirstOutboundAsync(run.ErpChangeRunId,
            new("CreateCreditMemo", "CM-1001", "Found by deterministic reference on the target."),
            Operator, default);
        await cutover.ReconcileAsync(run.ErpChangeRunId, Operator, default);
        Assert.True((await cutover.ViewAsync(run.ErpChangeRunId, default)).CanAdvance);
    }

    [DatabaseFact]
    public async Task Cancel_is_refused_after_cutover_and_reverse_is_a_fresh_run_with_swapped_roles()
    {
        fixture.Clock.UtcNow = Start;
        var tenant = await fixture.NewTenantAsync("ecrev");
        var legalEntity = Guid.NewGuid();
        var former = await fixture.AddConnectionAsync(tenant, legalEntity);
        var current = await fixture.AddConnectionAsync(tenant, legalEntity, "CX02");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var original = Run(tenant, former, current, ErpChangeStep.Cutover);
        original.CutoverAt = Start;
        db.ErpChangeRuns.Add(original);
        await db.SaveChangesAsync();
        var runs = scope.ServiceProvider.GetRequiredService<ErpChangeRunService>();
        var refused = await Assert.ThrowsAsync<ErpChangeRuleException>(() => runs.CancelAsync(
            original.ErpChangeRunId, new("Try to undo cutover."), Operator, default));
        Assert.Equal("erp_change_cancel_after_cutover_forbidden", refused.Rule);
        var reverse = await runs.StartReverseAsync(original.ErpChangeRunId, new(), Operator, default);
        Assert.NotEqual(original.ErpChangeRunId, reverse.RunId);
        Assert.Equal(current, reverse.Source.ConnectionId);
        Assert.Equal(former, reverse.Target.ConnectionId);
        Assert.Equal(ErpChangeStep.Register, reverse.CurrentStep);
        Assert.Equal(ErpChangeRunStatus.Completed,
            (await db.ErpChangeRuns.SingleAsync(row => row.ErpChangeRunId == original.ErpChangeRunId)).Status);
    }

    [DatabaseFact]
    public async Task Reverse_refuses_an_archived_former_source_and_tenant_queries_do_not_leak()
    {
        fixture.Clock.UtcNow = Start;
        var firstTenant = await fixture.NewTenantAsync("ecarc");
        var secondTenant = await fixture.NewTenantAsync("eciso");
        var entity = Guid.NewGuid();
        var former = await fixture.AddConnectionAsync(firstTenant, entity);
        var current = await fixture.AddConnectionAsync(firstTenant, entity, "CX02");
        var otherA = await fixture.AddConnectionAsync(secondTenant, entity);
        var otherB = await fixture.AddConnectionAsync(secondTenant, entity, "CX04");
        Guid firstRun;
        using (var scope = fixture.ScopeFor(firstTenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var run = Run(firstTenant, former, current, ErpChangeStep.Archive);
            run.CutoverAt = Start;
            firstRun = run.ErpChangeRunId;
            db.ErpChangeRuns.Add(run);
            (await db.Connections.SingleAsync(row => row.ConnectionId == former)).Status = ConnectionStatus.Archived;
            await db.SaveChangesAsync();
            var refusal = await Assert.ThrowsAsync<ErpChangeRuleException>(() =>
                scope.ServiceProvider.GetRequiredService<ErpChangeRunService>().StartReverseAsync(
                    firstRun, new(), Operator, default));
            Assert.Equal("erp_change_reverse_source_archived", refusal.Rule);
        }
        using (var scope = fixture.ScopeFor(secondTenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var run = Run(secondTenant, otherA, otherB, ErpChangeStep.DualRun);
            run.DualRunStartedAt = Start;
            db.ErpChangeRuns.Add(run);
            db.ErpChangeDivergences.Add(new()
            {
                TenantId = secondTenant, ErpChangeDivergenceId = Guid.CreateVersion7(),
                ErpChangeRunId = run.ErpChangeRunId, Kind = ErpChangeDivergenceKind.Document,
                SubjectType = "delivery", SubjectRef = "DN-ISOLATED", SourceValue = "1", TargetValue = "2",
                FirstSeenAt = Start, LastSeenAt = Start, ReviewDueAt = Start.AddDays(1),
                Status = ErpChangeDivergenceStatus.Open,
            });
            await db.SaveChangesAsync();
            Assert.Single(await db.ErpChangeDivergences.ToListAsync());
            Assert.DoesNotContain(await db.ErpChangeRuns.ToListAsync(), row => row.ErpChangeRunId == firstRun);
        }
    }

    [DatabaseFact]
    public async Task Reverse_replaces_an_archived_former_source_with_a_new_connection_of_the_same_connector()
    {
        fixture.Clock.UtcNow = Start;
        await using var simulator = new Yojana.Integration.Connectors.CX01.Testing.Cx01Simulator();
        var tenant = await fixture.NewTenantAsync("ecnew");
        var entity = Guid.NewGuid();
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<Yojana.Integration.Gateway.ConnectorCatalogue>()
            .SeedGenericEntriesAsync(default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.ConnectorCatalogue.ExecuteUpdateAsync(update => update.SetProperty(row => row.Enabled, true));
        var former = await fixture.AddConnectionAsync(tenant, entity);
        var current = await fixture.AddConnectionAsync(tenant, entity, "CX02");
        var original = Run(tenant, former, current, ErpChangeStep.Archive);
        original.CutoverAt = Start;
        db.ErpChangeRuns.Add(original);
        (await db.Connections.SingleAsync(row => row.ConnectionId == former)).Status = ConnectionStatus.Archived;
        await db.SaveChangesAsync();

        var replacement = simulator.RegistrationRequest() with { LegalEntityId = entity };
        var reverse = await scope.ServiceProvider.GetRequiredService<ErpChangeRunService>()
            .StartReverseAsync(original.ErpChangeRunId, new(replacement), Operator, default);

        Assert.Equal(current, reverse.Source.ConnectionId);
        Assert.NotEqual(former, reverse.Target.ConnectionId);
        Assert.Equal("CX01", reverse.Target.ConnectorCode);
        Assert.Equal(ConnectionStatus.Archived,
            (await db.Connections.SingleAsync(row => row.ConnectionId == former)).Status);
    }

    private static ErpChangeRun Run(TenantId tenant, Guid source, Guid target, int step) => new()
    {
        TenantId = tenant, ErpChangeRunId = Guid.CreateVersion7(), SourceConnectionId = source,
        TargetConnectionId = target, CurrentStep = step, Status = ErpChangeRunStatus.Active,
        StartedBy = Operator, StartedAt = Start,
    };

    private static OutboundCommandRow Command(TenantId tenant, Guid connection, string status,
        string moduleReference)
    {
        var id = Guid.CreateVersion7();
        var payload = new AttachDocument(id, moduleReference, $"YOJ-{id:N}",
            "service-order/SO-1001", "inspection.txt", null, "https://files.invalid/inspection.txt");
        return new()
        {
            TenantId = tenant, OutboundCommandId = id, ConnectionId = connection,
            CommandType = nameof(AttachDocument), IdempotencyKey = id,
            ModuleDocumentRef = moduleReference, ErpReferenceText = payload.ErpReferenceText,
            Payload = JsonSerializer.Serialize(payload, IntegrationContracts.Json), Status = status,
            CreatedAt = Start, NextAttemptAt = Start,
        };
    }

    private static DeliveryPosted Delivery(string externalId, decimal quantity) => new(
        new("delivery", externalId, DateOnly.FromDateTime(Start), externalId), "CUST-1", "MAIN",
        [new CanonicalLine(1, "1", "SRV-42", quantity, "EA", [], [], "MAIN")]);

    private static void Map(IntegrationDbContext db, TenantId tenant, Guid connection,
        string externalId, Guid documentId) => db.ExternalIdMaps.Add(new()
    {
        TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(), ConnectionId = connection,
        EntityType = "document", ExternalId = externalId, ModuleId = documentId,
        ValidFrom = Start, NaturalKeyHash = new string('d', 64),
        MappingSource = ExternalIdMappingSource.Operator, MappedBy = Operator, MappedAt = Start,
    });
}
