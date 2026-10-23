using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Yojana.Integration.Backfills;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.ErpChange;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ErpChangeFollowUpDatabaseTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-integration-operator";
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Parked_list_is_keyset_paged_tenant_isolated_and_reports_manual_ownership()
    {
        fixture.Clock.UtcNow = At;
        var tenant = await fixture.NewTenantAsync("ecpark");
        var otherTenant = await fixture.NewTenantAsync("ecparkother");
        var entity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, entity);
        var target = await fixture.AddConnectionAsync(tenant, entity);
        var otherConnection = await fixture.AddConnectionAsync(otherTenant, Guid.NewGuid());
        Guid owned;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var run = Run(tenant, source, target, ErpChangeStep.Cutover);
            run.TargetOutboundMode = "documents";
            db.ErpChangeRuns.Add(run);
            var commands = Enumerable.Range(0, 3).Select(index => Command(tenant, target,
                At.AddMinutes(index + 1), $"SO-{index + 1}")).ToList();
            owned = commands[1].OutboundCommandId;
            db.OutboundCommands.AddRange(commands);
            foreach (var command in commands)
                db.DeadLetterItems.Add(Letter(tenant, target, command, command.CreatedAt,
                    $"The target refused {command.ErpReferenceText}."));
            db.ManualOutboundTasks.Add(new ManualOutboundTask
            {
                TenantId = tenant, ManualOutboundTaskId = Guid.CreateVersion7(),
                DeadLetterItemId = null, OutboundCommandId = owned, ConnectionId = target,
                CommandType = nameof(CreateServiceOrder), Desk = "service", Draft = "{}",
                Status = ManualTaskStatus.Open, CreatedAt = At.AddMinutes(2),
            });
            await db.SaveChangesAsync();

            var service = scope.ServiceProvider.GetRequiredService<ErpChangeCutoverService>();
            var first = await service.ParkedAsync(run.ErpChangeRunId, null, 2, default);
            Assert.Equal(["SO-3", "SO-2"], first.Items.Select(row => row.DocumentReference));
            Assert.All(first.Items, row => Assert.Equal(1, row.UnitCount));
            Assert.True(first.Items.Single(row => row.OutboundCommandId == owned).OwnedByManualTask);
            Assert.NotNull(first.NextCursor);
            var second = await service.ParkedAsync(run.ErpChangeRunId, first.NextCursor, 2, default);
            Assert.Equal(["SO-1"], second.Items.Select(row => row.DocumentReference));
            Assert.All(first.Items.Concat(second.Items), row => Assert.Contains("target refused", row.ParkedReason));
        }
        using (var scope = fixture.ScopeFor(otherTenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.OutboundCommands.Add(Command(otherTenant, otherConnection, At.AddHours(1), "OTHER-1"));
            await db.SaveChangesAsync();
        }
        using (var scope = fixture.ScopeFor(tenant))
        {
            var runId = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>()
                .ErpChangeRuns.Select(row => row.ErpChangeRunId).SingleAsync();
            var rows = await scope.ServiceProvider.GetRequiredService<ErpChangeCutoverService>()
                .ParkedAsync(runId, null, 100, default);
            Assert.DoesNotContain(rows.Items, row => row.DocumentReference == "OTHER-1");
        }
    }

    [DatabaseFact]
    public async Task Run_history_is_newest_first_keyset_paged_named_and_maps_all_outcomes()
    {
        fixture.Clock.UtcNow = At;
        var tenant = await fixture.NewTenantAsync("echistory");
        var entity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, entity);
        var target = await fixture.AddConnectionAsync(tenant, entity);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        (await db.Connections.SingleAsync(row => row.ConnectionId == source)).DisplayLabel = "Asterra operations";
        (await db.Connections.SingleAsync(row => row.ConnectionId == target)).DisplayLabel = "Silverline operations";
        db.ErpChangeRuns.AddRange(
            Closed(tenant, source, target, At, ErpChangeRunStatus.Completed, "completed"),
            Closed(tenant, source, target, At.AddDays(1), ErpChangeRunStatus.Cancelled, "cancelled by operator"),
            Closed(tenant, source, target, At.AddDays(2), ErpChangeRunStatus.Completed, "reverse_erp_change_started"),
            Run(tenant, source, target, ErpChangeStep.Stock, At.AddDays(3)));
        await db.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<ErpChangeRunService>();
        var first = await service.ListAsync(new(null, 2), default);
        Assert.Equal(["in_progress", "reversed"], first.Items.Select(row => row.Outcome));
        Assert.Equal("Asterra operations", first.Items[0].SourceName);
        Assert.Equal("Silverline operations", first.Items[0].TargetName);
        Assert.DoesNotContain(first.Items[0].StartedBy, Actor, StringComparison.Ordinal);
        var second = await service.ListAsync(new(first.NextCursor, 2), default);
        Assert.Equal(["cancelled", "completed"], second.Items.Select(row => row.Outcome));
        Assert.True(first.Items[0].StartedAt > first.Items[1].StartedAt);
    }

    [DatabaseFact]
    public async Task Stock_reason_counts_cover_open_and_resolved_rows_independently_of_the_page_filter()
    {
        fixture.Clock.UtcNow = At;
        var tenant = await fixture.NewTenantAsync("ecreasons");
        var source = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var target = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var run = Run(tenant, source, target, ErpChangeStep.Stock);
        run.UnitsCompared = 4;
        db.ErpChangeRuns.Add(run);
        db.ErpChangeStockDifferences.AddRange(
            Difference(tenant, run.ErpChangeRunId, StockDifferenceReason.MissingInTarget,
                ErpChangeDifferenceStatus.Open, true, 1),
            Difference(tenant, run.ErpChangeRunId, StockDifferenceReason.MissingInTarget,
                ErpChangeDifferenceStatus.Resolved, false, 2),
            Difference(tenant, run.ErpChangeRunId, StockDifferenceReason.LocationDiffers,
                ErpChangeDifferenceStatus.Open, false, 3),
            Difference(tenant, run.ErpChangeRunId, StockDifferenceReason.LocationDiffers,
                ErpChangeDifferenceStatus.Resolved, false, 4));
        await db.SaveChangesAsync();
        var page = await scope.ServiceProvider.GetRequiredService<ErpChangeStockService>().ListAsync(
            run.ErpChangeRunId, new(StockDifferenceReason.MissingInTarget, null,
                ErpChangeDifferenceStatus.Open, null, null, 1), default);
        Assert.Single(page.Items);
        Assert.Equal(2, page.Open);
        Assert.Equal(2, page.Resolved);
        Assert.Equal(1, page.CoveredOpen);
        Assert.Equal((1, 1, 1), page.ReasonCounts.Where(row => row.Reason == StockDifferenceReason.MissingInTarget)
            .Select(row => (row.Open, row.Resolved, row.CoveredOpen)).Single());
        Assert.Equal((1, 1, 0), page.ReasonCounts.Where(row => row.Reason == StockDifferenceReason.LocationDiffers)
            .Select(row => (row.Open, row.Resolved, row.CoveredOpen)).Single());
    }

    [DatabaseFact]
    public async Task Unit_history_figures_use_resolution_commands_and_target_documents_not_elapsed_time()
    {
        fixture.Clock.UtcNow = At;
        var tenant = await fixture.NewTenantAsync("echg");
        var entity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, entity);
        var target = await fixture.AddConnectionAsync(tenant, entity);
        using var scope = fixture.ScopeFor(tenant);
        var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var run = Run(tenant, source, target, ErpChangeStep.Cutover);
        run.TargetOutboundMode = "documents";
        integration.ErpChangeRuns.Add(run);
        await integration.SaveChangesAsync();
        var service = scope.ServiceProvider.GetRequiredService<ErpChangeRunService>();
        _ = await service.CheckAsync(run.ErpChangeRunId, Actor, default);
        Assert.Equal(0, (await service.DetailAsync(run.ErpChangeRunId, default)).UnitHistoriesChanged);

        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var businessUnit = AddUnitEvent(serialisation, tenant, entity, "SRV-BUSINESS-01", 1,
            At.AddMinutes(10), null, null, null);
        await serialisation.SaveChangesAsync();
        integration.ChangeTracker.Clear();
        fixture.Clock.UtcNow = At.AddHours(1);
        _ = await service.CheckAsync(run.ErpChangeRunId, Actor, default);
        var afterBusiness = await service.DetailAsync(run.ErpChangeRunId, default);
        Assert.Equal(0, afterBusiness.UnitHistoriesChanged);
        Assert.Equal(0, afterBusiness.DeliberateUnitHistoriesChanged);

        var resolutionCommand = Guid.CreateVersion7();
        var deliberateUnit = AddUnitEvent(serialisation, tenant, entity, "SRV-RESOLUTION-01", 2,
            At.AddMinutes(20), resolutionCommand, null, null);
        integration.ErpChangeStockDifferences.Add(new()
        {
            TenantId = tenant, ErpChangeStockDifferenceId = Guid.CreateVersion7(),
            ErpChangeRunId = run.ErpChangeRunId, UnitId = deliberateUnit,
            Identifier = "SRV-RESOLUTION-01", ReasonClass = StockDifferenceReason.LocationDiffers,
            HasLiveEntitlement = true, SuggestedResolution = StockResolutionCommand.ChangeCustody,
            Status = ErpChangeDifferenceStatus.Resolved, ResolutionCommand = StockResolutionCommand.ChangeCustody,
            ResolutionCommandId = resolutionCommand, ResolvedBy = Actor, ResolvedAt = At.AddMinutes(20),
            ComparedAt = At.AddMinutes(15), IsCurrent = true,
        });
        var inboundId = Guid.CreateVersion7();
        var documentId = Guid.CreateVersion7();
        integration.InboundMessages.Add(new InboundMessage
        {
            TenantId = tenant, InboundMessageId = inboundId, MessageId = Guid.CreateVersion7(),
            ConnectionId = target, ConnectorCode = "CX01", ErpEntity = "delivery",
            ErpExternalId = "DEL-COMPARE-01", ErpChangeMarker = "1", MarkerKind = "version",
            ContractVersion = "1.0", MessageType = nameof(DeliveryPosted),
            CaptureMode = BackfillCaptureModes.ComparisonOnly, RawPayload = "{}", CanonicalMessage = "{}",
            OccurredAt = At.AddMinutes(30), ObservedAt = At.AddMinutes(30), AcceptedAt = At.AddMinutes(30),
            IsLatest = true, TranslationStatus = InboundTranslationStatus.Completed,
            TranslatedAt = At.AddMinutes(30),
        });
        integration.CanonicalDocuments.Add(new CanonicalDocument
        {
            TenantId = tenant, DocumentId = documentId, ConnectionId = target,
            ErpEntity = "delivery", ErpExternalId = "DEL-COMPARE-01",
            MessageType = nameof(DeliveryPosted), Payload = "{}", OccurredAt = At.AddMinutes(30),
            UpdatedAt = At.AddMinutes(30), SourceInboundMessageId = inboundId,
        });
        var unexpectedUnit = AddUnitEvent(serialisation, tenant, entity, "SRV-TARGET-01", 3,
            At.AddMinutes(30), null, "delivery", documentId);
        await serialisation.SaveChangesAsync();
        await integration.SaveChangesAsync();
        integration.ChangeTracker.Clear();
        _ = await service.CheckAsync(run.ErpChangeRunId, Actor, default);
        var detail = await service.DetailAsync(run.ErpChangeRunId, default);
        Assert.Equal(1, detail.UnitHistoriesChanged);
        Assert.Equal(1, detail.DeliberateUnitHistoriesChanged);
        var unexpected = await service.UnitHistoryChangesAsync(run.ErpChangeRunId,
            MigrationUnitHistoryChangeKind.Unexpected, null, 10, default);
        Assert.Equal(1, unexpected.Total);
        Assert.Equal(unexpectedUnit, Assert.Single(unexpected.Items).UnitId);
        var deliberate = await service.UnitHistoryChangesAsync(run.ErpChangeRunId,
            MigrationUnitHistoryChangeKind.Deliberate, null, 10, default);
        Assert.Equal(1, deliberate.Total);
        Assert.Equal(deliberateUnit, Assert.Single(deliberate.Items).UnitId);
        Assert.DoesNotContain(unexpected.Items.Concat(deliberate.Items), row => row.UnitId == businessUnit);
    }

    private static Guid AddUnitEvent(SerialisationDbContext db, TenantId tenant, Guid entity,
        string identifier, long sequence, DateTime at, Guid? commandId, string? documentType,
        Guid? documentId)
    {
        var unitId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7();
        db.Units.Add(new Unit
        {
            TenantId = tenant, UnitId = unitId, LifecycleStatus = LifecycleStatus.Released,
            Origin = UnitOrigin.Purchased, SerialisationPoint = SerialisationPoint.Receipt,
            RetentionClass = "standard_10y", CreatedAt = at, CreatedEventId = eventId,
            CreatedEventTime = at,
        });
        db.Identifiers.Add(new UnitIdentifier
        {
            TenantId = tenant, IdentifierId = Guid.CreateVersion7(), UnitId = unitId,
            Type = IdentifierType.InternalSerial, Scheme = IdentifierScheme.Internal,
            RawValue = identifier, NormalisedValue = identifier.Replace("-", "", StringComparison.Ordinal),
            ScopeKey = "server", ValidFrom = at, ActorRef = Actor, RecordedTime = at, EventId = eventId,
        });
        db.Events.Add(new UnitEvent
        {
            TenantId = tenant, EventId = eventId, UnitId = unitId, Seq = sequence,
            EventType = UnitEventType.Commissioned, EventTime = at, RecordedTime = at,
            LegalEntityId = entity, ActorRef = Actor, IdempotencyKey = Guid.NewGuid().ToString("N"),
            DocumentType = documentType, DocumentId = documentId,
            Attributes = commandId is null ? null : JsonSerializer.Serialize(new { command_id = commandId }),
        });
        return unitId;
    }

    private static ErpChangeRun Run(TenantId tenant, Guid source, Guid target, int step,
        DateTime? startedAt = null) => new()
    {
        TenantId = tenant, ErpChangeRunId = Guid.CreateVersion7(), SourceConnectionId = source,
        TargetConnectionId = target, CurrentStep = step, Status = ErpChangeRunStatus.Active,
        StartedBy = Actor, StartedAt = startedAt ?? At, UnitHistoryCheckedAt = startedAt ?? At,
    };

    private static ErpChangeRun Closed(TenantId tenant, Guid source, Guid target, DateTime startedAt,
        string status, string reason)
    {
        var run = Run(tenant, source, target, ErpChangeStep.Archive, startedAt);
        run.Status = status; run.ClosedAt = startedAt.AddHours(1); run.ClosedBy = Actor; run.CloseReason = reason;
        return run;
    }

    private static OutboundCommandRow Command(TenantId tenant, Guid connection, DateTime at, string reference) => new()
    {
        TenantId = tenant, OutboundCommandId = Guid.CreateVersion7(), ConnectionId = connection,
        CommandType = nameof(CreateServiceOrder), IdempotencyKey = Guid.NewGuid(),
        ModuleDocumentRef = $"service/{reference}", ErpReferenceText = reference, Payload = "{}",
        Status = OutboundCommandStatus.Parked, CreatedAt = at, CompletedAt = at,
        LastFailure = "The target refused the service order.",
    };

    private static DeadLetterItem Letter(TenantId tenant, Guid connection, OutboundCommandRow command,
        DateTime at, string failure) => new()
    {
        TenantId = tenant, DeadLetterItemId = Guid.CreateVersion7(), ConnectionId = connection,
        Direction = DeadLetterDirection.Outbound, RecordType = command.CommandType,
        SourceKind = DeadLetterSourceKind.OutboundCommand, SourceId = command.OutboundCommandId,
        ReasonCode = "cutover_manual", RawPayload = "{}", CanonicalAttempt = "{}",
        Failure = failure, Status = DeadLetterStatus.NeedsAttention, CreatedAt = at, UpdatedAt = at,
    };

    private static ErpChangeStockDifference Difference(TenantId tenant, Guid runId, string reason,
        string status, bool covered, int sequence) => new()
    {
        TenantId = tenant, ErpChangeStockDifferenceId = Guid.CreateVersion7(), ErpChangeRunId = runId,
        Identifier = $"HW-{sequence:000}", ItemName = "Fictional rack server", ReasonClass = reason,
        HasLiveEntitlement = covered, SuggestedResolution = "fix_in_new_erp", Status = status,
        ComparedAt = At.AddMinutes(sequence), IsCurrent = true,
    };
}
