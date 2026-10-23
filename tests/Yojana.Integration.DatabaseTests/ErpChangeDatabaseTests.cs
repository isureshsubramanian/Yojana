using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors.CX01.Testing;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Connectors.CX02.Testing;
using Yojana.Integration.Connectors.CX04;
using Yojana.Integration.Connectors.CX04.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.ErpChange;
using Yojana.Integration.Gateway;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Security;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ErpChangeDatabaseTests(IntegrationHostFixture fixture)
{
    private const string Actor = "fictional-integration-operator";
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Only_inventory_controllers_are_authorised_for_stock_resolution_and_signoff()
    {
        var tenant = await fixture.NewTenantAsync("ecauth");
        using var scope = fixture.ScopeFor(tenant);
        var authorisation = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
        ClaimsPrincipal Principal(string role) => new(new ClaimsIdentity(
        [
            new System.Security.Claims.Claim(YojanaClaim.TenantId, tenant.ToString()),
            new System.Security.Claims.Claim(YojanaClaim.Role, role),
        ], "test"));

        Assert.True((await authorisation.AuthorizeAsync(Principal(YojanaRole.InventoryController), null,
            YojanaPolicy.InventoryController)).Succeeded);
        foreach (var role in new[]
                 {
                     YojanaRole.TenantAdministrator, YojanaRole.IntegrationOperator,
                     YojanaRole.ItemMasterOwner,
                 })
            Assert.False((await authorisation.AuthorizeAsync(Principal(role), null,
                YojanaPolicy.InventoryController)).Succeeded);
    }

    [DatabaseFact]
    public async Task Cx01_to_cx02_simulators_complete_steps_one_to_six()
    {
        await using var source = new Cx01Simulator();
        await using var target = new Cx02Simulator();
        var legalEntity = Guid.NewGuid();
        var settings = new Cx02ConnectionSettings(target.BaseUri, Cx02Simulator.DefaultTenant,
            Cx02Simulator.DefaultTenant, Cx02Simulator.DefaultEnvironment, Cx02Simulator.CompanyId,
            "1.0", TokenUri: target.TokenUri(), PollPageSize: 2);
        var credential = new Cx02ClientCredential("fictional-client", target.Options.ClientSecret,
            "fictional-client-state");
        var request = new RegisterConnection(Guid.CreateVersion7(), Cx02Capabilities.ConnectorCode,
            "Fictional replacement connection", legalEntity,
            JsonSerializer.SerializeToElement(settings, IntegrationContracts.Json),
            [new(Cx02Client.CredentialPurpose,
                JsonSerializer.Serialize(credential, IntegrationContracts.Json))]);

        await CompleteEmptySimulatorRunAsync(source, legalEntity, request);
    }

    [DatabaseFact]
    public async Task Cx01_to_cx04_simulators_complete_steps_one_to_six()
    {
        await using var source = new Cx01Simulator();
        await using var target = new Cx04Simulator();
        var legalEntity = Guid.NewGuid();
        var settings = new Cx04ConnectionSettings(target.BaseUri, "fictional-connected-application",
            new Uri("https://yojana.invalid/integration/callback"),
            Yojana.Integration.Connectors.CX04.Cx04LicenceTier.Essentials,
            PollPageSize: 2);
        var request = new RegisterConnection(Guid.CreateVersion7(), Cx04Capabilities.ConnectorCode,
            "Fictional replacement connection", legalEntity,
            JsonSerializer.SerializeToElement(settings, IntegrationContracts.Json), []);

        await CompleteEmptySimulatorRunAsync(source, legalEntity, request, async (tenant, connectionId) =>
        {
            Cx04ConsentStart start;
            using (var begin = fixture.ScopeFor(tenant))
                start = await begin.ServiceProvider.GetRequiredService<Cx04Authorisation>()
                    .BeginAsync(connectionId, default);
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler);
            using var response = await client.GetAsync(start.ConsentUrl);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            var query = System.Web.HttpUtility.ParseQueryString(response.Headers.Location!.Query);
            using var complete = fixture.ScopeFor(tenant);
            await complete.ServiceProvider.GetRequiredService<Cx04Authorisation>().CompleteAsync(connectionId,
                new(query["code"]!, query["state"]!), Actor, default);
        });
    }

    [DatabaseFact]
    public async Task Checks_are_explicit_advancement_is_gated_and_cancel_leaves_source_untouched()
    {
        var tenant = await fixture.NewTenantAsync("ecrun");
        var entity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, entity);
        var target = await fixture.AddConnectionAsync(tenant, entity, "CX02");
        Guid runId;
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var setting = await db.ConnectionGatewaySettings.SingleAsync(row => row.ConnectionId == target);
            setting.ReadOnly = true;
            setting.OutboundMode = "none";
            db.ConnectionHealth.Add(new ConnectionHealth
            {
                TenantId = tenant, ConnectionId = target, Status = "healthy", IsLive = true,
                LastDailyCheckResult = "clean", LastDailyCheckAt = fixture.Clock.UtcNow,
                UpdatedAt = fixture.Clock.UtcNow,
            });
            var run = Run(tenant, source, target, ErpChangeStep.Register);
            db.ErpChangeRuns.Add(run);
            db.ErpChangeTransitions.Add(new()
            {
                TenantId = tenant, ErpChangeTransitionId = Guid.CreateVersion7(),
                ErpChangeRunId = run.ErpChangeRunId, FromStep = 0, ToStep = ErpChangeStep.Register,
                Kind = "started", ActorRef = Actor, OccurredAt = At,
            });
            runId = run.ErpChangeRunId;
            await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<ErpChangeRunService>();
            var blocked = await Assert.ThrowsAsync<ErpChangeRuleException>(() =>
                service.AdvanceAsync(runId, Actor, default));
            Assert.Equal("erp_change_exit_unmet", blocked.Rule);
            Assert.True((await service.CheckAsync(runId, Actor, default)).Passed);
            Assert.Equal(ErpChangeStep.MasterData,
                (await service.AdvanceAsync(runId, Actor, default)).CurrentStep);

            var cancelled = await service.CancelAsync(runId, new("Customer postponed the change."), Actor, default);
            Assert.Equal(ErpChangeRunStatus.Cancelled, cancelled.Status);
            Assert.Equal(ConnectionStatus.Active,
                (await db.Connections.SingleAsync(row => row.ConnectionId == source)).Status);
            Assert.Equal(ConnectionStatus.Paused,
                (await db.Connections.SingleAsync(row => row.ConnectionId == target)).Status);
            Assert.Equal("none", (await db.ConnectionGatewaySettings.SingleAsync(row =>
                row.ConnectionId == target)).OutboundMode);
            Assert.Equal(3, await db.ErpChangeTransitions.CountAsync(row => row.ErpChangeRunId == runId));
        }
    }

    [DatabaseFact]
    public async Task One_active_run_is_enforced_per_tenant_and_tenant_queries_are_isolated()
    {
        var firstTenant = await fixture.NewTenantAsync("econe");
        var secondTenant = await fixture.NewTenantAsync("ectwo");
        var entity = Guid.NewGuid();
        var firstA = await fixture.AddConnectionAsync(firstTenant, entity);
        var firstB = await fixture.AddConnectionAsync(firstTenant, entity, "CX02");
        var secondA = await fixture.AddConnectionAsync(secondTenant, entity);
        var secondB = await fixture.AddConnectionAsync(secondTenant, entity, "CX04");
        using (var scope = fixture.ScopeFor(firstTenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.ErpChangeRuns.Add(Run(firstTenant, firstA, firstB, 1));
            await db.SaveChangesAsync();
            db.ErpChangeRuns.Add(Run(firstTenant, firstA, firstB, 1));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        using (var scope = fixture.ScopeFor(secondTenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.ErpChangeRuns.Add(Run(secondTenant, secondA, secondB, 1));
            await db.SaveChangesAsync();
            Assert.Single(await db.ErpChangeRuns.ToListAsync());
        }
    }

    [DatabaseFact]
    public async Task Natural_key_remap_maps_exact_item_code_and_queues_unmatched_and_ambiguous_records()
    {
        var tenant = await fixture.NewTenantAsync("ecmap");
        var entity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, entity);
        var target = await fixture.AddConnectionAsync(tenant, entity, "CX02");
        var item = Guid.NewGuid();
        var otherItem = Guid.NewGuid();
        var partyA = Guid.NewGuid();
        var partyB = Guid.NewGuid();
        var targetItem = Guid.NewGuid();
        var targetDifferentCode = Guid.NewGuid();
        var targetParty = Guid.NewGuid();
        var run = Run(tenant, source, target, ErpChangeStep.MasterData);
        using (var scope = fixture.ScopeFor(tenant))
        {
            var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            AddReference(integration, tenant, source, "item", "SRV-220", item, "SRV-220", "Rack server");
            AddReference(integration, tenant, source, "item", "NET-48", otherItem, "NET-48", "48-port switch");
            AddReference(integration, tenant, target, "item", "SRV-220", targetItem, "SRV-220", "Rack  server");
            AddReference(integration, tenant, target, "item", "NET-048", targetDifferentCode, "NET-048", "48-port switch");
            AddParty(integration, tenant, source, "P-1", partyA, "Fictional North IT", "GB 123 45", "GB");
            AddParty(integration, tenant, source, "P-2", partyB, "Fictional North IT Services", "GB12345", "GB");
            AddParty(integration, tenant, target, "C-9", targetParty, "Fictional North IT", "GB-123-45", "gb");
            integration.ErpChangeRuns.Add(run);
            await integration.SaveChangesAsync();
            var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
            serialisation.Current.AddRange(
                Current(tenant, Guid.NewGuid(), item, partyA),
                Current(tenant, Guid.NewGuid(), otherItem, partyB));
            await serialisation.SaveChangesAsync();

            var result = await scope.ServiceProvider.GetRequiredService<ErpChangeMasterDataService>()
                .RemapAsync(run.ErpChangeRunId, Actor, default);
            Assert.Equal(1, result.ItemsMapped);
            Assert.Equal(3, result.ExceptionsOpen);
            integration.ChangeTracker.Clear();
            Assert.Equal(item, (await integration.ExternalIdMaps.SingleAsync(row =>
                row.ConnectionId == target && row.EntityType == "item" && row.ExternalId == "SRV-220"
                && row.ValidTo == null)).ModuleId);
            Assert.Equal(2, await integration.ExternalIdMaps.CountAsync(row => row.ConnectionId == target
                && row.EntityType == "item" && row.ExternalId == "SRV-220"));
            var exceptions = await integration.ErpChangeMasterDataExceptions.Where(row =>
                row.ErpChangeRunId == run.ErpChangeRunId).ToListAsync();
            Assert.Contains(exceptions, row => row.TargetExternalId == "NET-048"
                && row.Problem == MasterDataProblem.Unmatched);
            Assert.Contains(exceptions, row => row.TargetExternalId == "C-9"
                && row.Problem == MasterDataProblem.Ambiguous);
            Assert.DoesNotContain(await integration.ExternalIdMaps.Where(row => row.ConnectionId == target
                && row.ExternalId == "NET-048").ToListAsync(), row => row.ModuleId == otherItem);
            var missingSource = exceptions.Single(row => row.Problem == MasterDataProblem.MissingInTarget
                && row.EntityKind == "item");
            await scope.ServiceProvider.GetRequiredService<ErpChangeMasterDataService>().MarkNotNeededAsync(
                run.ErpChangeRunId, missingSource.ErpChangeMasterDataExceptionId,
                new("The target record must still be created before step 2 can pass."), Actor, default);
            var check = await scope.ServiceProvider.GetRequiredService<ErpChangeRunService>()
                .CheckAsync(run.ErpChangeRunId, Actor, default);
            Assert.False(check.Passed);
            Assert.True(check.Counts["missing_mappings"] > 0);
        }
    }

    [DatabaseFact]
    public async Task Stock_comparison_emits_every_reason_class_and_covered_units_block_signoff()
    {
        var tenant = await fixture.NewTenantAsync("ecstock");
        var entity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, entity);
        var target = await fixture.AddConnectionAsync(tenant, entity, "CX04");
        var item = Guid.NewGuid();
        var location = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var formatted = Guid.NewGuid();
        var moved = Guid.NewGuid();
        var absorbed = Guid.NewGuid();
        var run = Run(tenant, source, target, ErpChangeStep.Stock);
        using var scope = fixture.ScopeFor(tenant);
        var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        AddReference(integration, tenant, source, "item", "STO-24", item, "STO-24", "24-bay storage array");
        AddMap(integration, tenant, source, "location", "MAIN-A", location);
        AddMap(integration, tenant, target, "location", "MAIN-A", location);
        integration.ErpChangeRuns.Add(run);
        integration.InboundMessages.AddRange(
            StockMessage(tenant, source, "A-1", "MISS-001", "MAIN-A"),
            StockMessage(tenant, source, "A-2", "SW48-2506-1121", "MAIN-A"),
            StockMessage(tenant, source, "A-3", "MOVE-003", "MAIN-A"),
            StockMessage(tenant, target, "B-2", "sw48 2506 1121", "MAIN-A"),
            StockMessage(tenant, target, "B-3", "MOVE-003", "REPAIR"),
            StockMessage(tenant, target, "B-4", "EXTRA-004", "MAIN-A"));
        await integration.SaveChangesAsync();
        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        AddUnit(serialisation, tenant, missing, item, location, "MISS-001", entity, covered: true, 1);
        AddUnit(serialisation, tenant, formatted, item, location, "SW48-2506-1121", entity, covered: false, 2);
        AddUnit(serialisation, tenant, moved, item, location, "MOVE-003", entity, covered: false, 3);
        AddUnit(serialisation, tenant, absorbed, item, location, "DUP-004", entity, covered: false, 4);
        await serialisation.SaveChangesAsync();

        var service = scope.ServiceProvider.GetRequiredService<ErpChangeStockService>();
        var result = await service.ReconcileAsync(run.ErpChangeRunId, "fictional-inventory-controller", default);
        Assert.Equal(4, result.UnitsCompared);
        var reasons = await integration.ErpChangeStockDifferences.Where(row => row.IsCurrent)
            .Select(row => row.ReasonClass).ToListAsync();
        Assert.Contains(StockDifferenceReason.MissingInTarget, reasons);
        Assert.Contains(StockDifferenceReason.ExtraInTarget, reasons);
        Assert.Contains(StockDifferenceReason.LocationDiffers, reasons);
        Assert.Contains(StockDifferenceReason.SerialDiffersByNormalisation, reasons);
        var normalised = await integration.ErpChangeStockDifferences.SingleAsync(row => row.UnitId == formatted);
        Assert.Equal(ErpChangeDifferenceStatus.Resolved, normalised.Status);
        var blocked = await Assert.ThrowsAsync<ErpChangeRuleException>(() => service.SignOffAsync(
            run.ErpChangeRunId, new("Reviewed remaining opening-position differences."),
            "fictional-inventory-controller", default));
        Assert.Equal("erp_change_covered_differences_open", blocked.Rule);
        var missingDifference = await integration.ErpChangeStockDifferences.SingleAsync(row => row.UnitId == missing);
        var identifierCommand = Guid.NewGuid();
        run.CurrentStep = ErpChangeStep.DualRun;
        run.DualRunStartedAt = fixture.Clock.UtcNow;
        await integration.SaveChangesAsync();
        var dual = scope.ServiceProvider.GetRequiredService<ErpChangeDualRunService>();
        var divergence = await dual.ReportAsync(run.ErpChangeRunId,
            new(ErpChangeDivergenceKind.Stock, "unit", missing, "MISS-001", "present", "missing",
                StockDifferenceId: missingDifference.ErpChangeStockDifferenceId), default);
        var corrected = await dual.RaiseCorrectionAsync(run.ErpChangeRunId, divergence.DivergenceId,
            new(new(StockResolutionCommand.CorrectIdentifier, identifierCommand,
                IdentifierType: IdentifierType.InternalSerial, NewIdentifier: "MISS-001-CORRECTED",
                Note: "The opening position used the corrected chassis label.")),
            "fictional-inventory-controller", default);
        Assert.Equal(ErpChangeDivergenceResolution.CorrectionRaised, corrected.Resolution);
        run.CurrentStep = ErpChangeStep.Stock;
        await integration.SaveChangesAsync();
        var movedDifference = await integration.ErpChangeStockDifferences.SingleAsync(row => row.UnitId == moved);
        var custodyCommand = Guid.NewGuid();
        await service.ResolveAsync(run.ErpChangeRunId, movedDifference.ErpChangeStockDifferenceId,
            new(StockResolutionCommand.ChangeCustody, custodyCommand, LocationId: Guid.NewGuid(),
                LocationKind: LocationKind.InRepair, Note: "Opening position confirmed by cycle count."),
            "fictional-inventory-controller", default);
        var absorbedDifference = await integration.ErpChangeStockDifferences.SingleAsync(row =>
            row.UnitId == absorbed);
        var mergeCommand = Guid.NewGuid();
        await service.ResolveAsync(run.ErpChangeRunId, absorbedDifference.ErpChangeStockDifferenceId,
            new(StockResolutionCommand.MergeUnits, mergeCommand, SurvivorUnitId: formatted,
                AbsorbedUnitId: absorbed, Note: "Two opening records represented the same switch."),
            "fictional-inventory-controller", default);
        serialisation.ChangeTracker.Clear();
        var ordinary = await serialisation.Events.Where(row => row.UnitId == missing || row.UnitId == moved
                || row.UnitId == formatted || row.UnitId == absorbed)
            .OrderBy(row => row.Seq).ToListAsync();
        Assert.Contains(ordinary, row => row.EventType == UnitEventType.IdentifierCorrected
            && row.Attributes!.Contains(MigrationReconciliationReason.Value));
        Assert.Contains(ordinary, row => row.EventType == UnitEventType.CustodyChanged
            && row.Attributes!.Contains(MigrationReconciliationReason.Value));
        Assert.Contains(ordinary, row => row.EventType == UnitEventType.Merged
            && row.Attributes!.Contains(MigrationReconciliationReason.Value));
        var signoff = await service.SignOffAsync(run.ErpChangeRunId,
            new("One remote-only record remains for receipt review."),
            "fictional-inventory-controller", default);
        Assert.Equal(1, signoff.ResidualCount);
    }

    private static ErpChangeRun Run(TenantId tenant, Guid source, Guid target, int step) => new()
    {
        TenantId = tenant, ErpChangeRunId = Guid.CreateVersion7(), SourceConnectionId = source,
        TargetConnectionId = target, CurrentStep = step, Status = ErpChangeRunStatus.Active,
        StartedBy = Actor, StartedAt = At,
    };

    private async Task CompleteEmptySimulatorRunAsync(Cx01Simulator source, Guid legalEntity,
        RegisterConnection targetRequest, Func<TenantId, Guid, Task>? activateTarget = null)
    {
        fixture.Clock.UtcNow = At;
        var tenant = await fixture.NewTenantAsync("ecsim");
        using var scope = fixture.ScopeFor(tenant);
        await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>().SeedGenericEntriesAsync(default);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        await db.ConnectorCatalogue.ExecuteUpdateAsync(update => update.SetProperty(row => row.Enabled, true));
        var sourceRequest = source.RegistrationRequest() with { LegalEntityId = legalEntity };
        var sourceConnection = await scope.ServiceProvider.GetRequiredService<ConnectionCommands>()
            .RegisterAsync(sourceRequest, Actor, default);
        var runs = scope.ServiceProvider.GetRequiredService<ErpChangeRunService>();
        var run = await runs.StartAsync(new(sourceConnection.ConnectionId, targetRequest), Actor, default);
        if (activateTarget is not null) await activateTarget(tenant, run.Target.ConnectionId);
        db.ChangeTracker.Clear();
        var health = await db.ConnectionHealth.SingleAsync(row => row.ConnectionId == run.Target.ConnectionId);
        health.Status = "healthy";
        health.IsLive = true;
        health.LastDailyCheckAt = fixture.Clock.UtcNow;
        health.LastDailyCheckResult = "clean";
        await db.SaveChangesAsync();
        Assert.True((await runs.CheckAsync(run.RunId, Actor, default)).Passed);
        Assert.Equal(ErpChangeStep.MasterData,
            (await runs.AdvanceAsync(run.RunId, Actor, default)).CurrentStep);
        await scope.ServiceProvider.GetRequiredService<ErpChangeMasterDataService>()
            .RemapAsync(run.RunId, Actor, default);
        Assert.True((await runs.CheckAsync(run.RunId, Actor, default)).Passed);
        Assert.Equal(ErpChangeStep.Stock,
            (await runs.AdvanceAsync(run.RunId, Actor, default)).CurrentStep);
        var stock = scope.ServiceProvider.GetRequiredService<ErpChangeStockService>();
        Assert.Equal(0, (await stock.ReconcileAsync(run.RunId,
            "fictional-inventory-controller", default)).UnitsCompared);
        Assert.Equal(0, (await stock.SignOffAsync(run.RunId,
            new("No residual stock differences."), "fictional-inventory-controller", default)).ResidualCount);
        Assert.True((await runs.CheckAsync(run.RunId, Actor, default)).Passed);
        Assert.Equal(ErpChangeStep.DualRun,
            (await runs.AdvanceAsync(run.RunId, Actor, default)).CurrentStep);

        var documentId = Guid.NewGuid();
        var inboundId = Guid.CreateVersion7();
        var receipt = new GoodsReceiptPosted(
            new("receipt", "GR-ARCHIVE", DateOnly.FromDateTime(At), "GR-ARCHIVE"),
            "SUP-ARCHIVE", []);
        var eventCount = await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events.CountAsync();
        var cleanSettings = scope.ServiceProvider.GetRequiredService<ErpChangeCleanPeriodSettings>();
        var clean = await cleanSettings.GetAsync(default);
        await cleanSettings.SetAsync(run.Target.ConnectionId, new(1, clean.SettingsVersion), Actor, default);
        var dual = scope.ServiceProvider.GetRequiredService<ErpChangeDualRunService>();
        await dual.StartAsync(run.RunId, Actor, default);
        fixture.Clock.UtcNow = At.AddDays(30);
        Assert.True((await dual.RunDailyAsync(run.RunId, Actor, default)).Clean);
        Assert.True((await runs.CheckAsync(run.RunId, Actor, default)).Passed);
        Assert.Equal(ErpChangeStep.Cutover,
            (await runs.AdvanceAsync(run.RunId, Actor, default)).CurrentStep);

        var cutover = scope.ServiceProvider.GetRequiredService<ErpChangeCutoverService>();
        await cutover.BeginAsync(run.RunId, new("reference_only"), Actor, default);
        var validationTypes = (await cutover.ViewAsync(run.RunId, default)).Items
            .Where(row => row.Code.StartsWith("validate:", StringComparison.Ordinal))
            .Select(row => row.Code["validate:".Length..]).ToList();
        foreach (var type in validationTypes)
            await cutover.RecordValidationAsync(run.RunId,
                new(type, ErpChangeValidationMethod.TestCompany,
                    "The connector simulator accepted the target validation scenario."), Actor, default);
        await cutover.EnableTargetAsync(run.RunId, Actor, default);
        await cutover.DrainAsync(run.RunId, Actor, default);
        await cutover.ConfirmFirstOutboundAsync(run.RunId,
            new("CreateCreditMemo", "CM-SIM-1", "Confirmed in the target simulator."), Actor, default);
        await cutover.ReconcileAsync(run.RunId, Actor, default);
        var cutoverCheck = await runs.CheckAsync(run.RunId, Actor, default);
        Assert.True(cutoverCheck.Passed, cutoverCheck.Detail + " "
            + string.Join("; ", (await cutover.ViewAsync(run.RunId, default)).Items
                .Where(row => row.State != ErpChangeChecklistState.Passed)
                .Select(row => $"{row.Code}={row.State}:{row.Detail}")));
        Assert.Equal(ErpChangeStep.Archive,
            (await runs.AdvanceAsync(run.RunId, Actor, default)).CurrentStep);
        AddMap(db, tenant, sourceConnection.ConnectionId, "document", "GR-ARCHIVE", documentId);
        AddMap(db, tenant, sourceConnection.ConnectionId, "party", "SUP-ARCHIVE", Guid.NewGuid());
        db.CanonicalDocuments.Add(new CanonicalDocument
        {
            TenantId = tenant, DocumentId = documentId, ConnectionId = sourceConnection.ConnectionId,
            ErpEntity = "receipt", ErpExternalId = "GR-ARCHIVE", MessageType = nameof(GoodsReceiptPosted),
            Payload = JsonSerializer.Serialize(receipt, IntegrationContracts.Json), OccurredAt = At,
            UpdatedAt = At, SourceInboundMessageId = inboundId,
        });
        db.InboundMessages.Add(new InboundMessage
        {
            TenantId = tenant, InboundMessageId = inboundId, MessageId = Guid.CreateVersion7(),
            ConnectionId = sourceConnection.ConnectionId, ConnectorCode = "CX01", ErpEntity = "receipt",
            ErpExternalId = "GR-ARCHIVE", ErpChangeMarker = "1", MarkerKind = "version",
            ContractVersion = "1.0", MessageType = nameof(GoodsReceiptPosted), RawPayload = "{}",
            CanonicalMessage = JsonSerializer.Serialize(new { payload = receipt }, IntegrationContracts.Json),
            OccurredAt = At, ObservedAt = At, AcceptedAt = At, IsLatest = true,
            TranslationStatus = InboundTranslationStatus.Completed,
        });
        await db.SaveChangesAsync();
        var archived = await cutover.ArchiveAsync(run.RunId, Actor, default);
        Assert.True(archived.Archived);
        Assert.True((await runs.CheckAsync(run.RunId, Actor, default)).Passed);
        Assert.Equal(ErpChangeRunStatus.Completed,
            (await runs.AdvanceAsync(run.RunId, Actor, default)).Status);
        db.ChangeTracker.Clear();
        var oldMap = await db.ExternalIdMaps.SingleAsync(row => row.ConnectionId == sourceConnection.ConnectionId
            && row.EntityType == "document" && row.ExternalId == "GR-ARCHIVE");
        Assert.Equal(At.AddDays(30), oldMap.ValidTo);
        Assert.True(await db.InboundMessages.AnyAsync(row => row.InboundMessageId == inboundId));
        Assert.False(await db.ConnectionCredentials.AnyAsync(row => row.ConnectionId == sourceConnection.ConnectionId
            && row.RetiredAt == null));
        var resolved = await scope.ServiceProvider.GetRequiredService<IPurchasingDocumentQueryPort>()
            .GetReceiptAsync(new Yojana.Serialisation.Contracts.DocumentRef("receipt", documentId), default);
        Assert.NotNull(resolved);
        Assert.Equal(eventCount,
            await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Events.CountAsync());
    }

    private static UnitCurrent Current(TenantId tenant, Guid unit, Guid item, Guid? owner = null) => new()
    {
        TenantId = tenant, UnitId = unit, ItemId = item, LifecycleStatus = LifecycleStatus.Released,
        Disposition = UnitDisposition.Available, LocationId = Guid.NewGuid(),
        LocationKind = LocationKind.Warehouse, OwnerPartyRef = owner, LastEventTime = At,
    };

    private static void AddReference(IntegrationDbContext db, TenantId tenant, Guid connection,
        string kind, string external, Guid moduleId, string code, string name)
    {
        db.ReferenceProjections.Add(new ReferenceProjection
        {
            TenantId = tenant, EntityType = kind, ModuleId = moduleId, Code = code,
            DisplayName = name, AttributesJson = JsonSerializer.Serialize(
                new Yojana.Integration.Gateway.ItemReferenceAttributes(false, null, code),
                IntegrationContracts.Json), UpdatedAt = At,
        });
        AddMap(db, tenant, connection, kind, external, moduleId);
    }

    private static void AddParty(IntegrationDbContext db, TenantId tenant, Guid connection,
        string external, Guid moduleId, string name, string taxId, string country)
    {
        db.ReferenceProjections.Add(new ReferenceProjection
        {
            TenantId = tenant, EntityType = "party", ModuleId = moduleId, Code = external,
            DisplayName = name, AttributesJson = JsonSerializer.Serialize(
                new Yojana.Integration.Gateway.PartyReferenceAttributes(taxId, country, "customer"),
                IntegrationContracts.Json), UpdatedAt = At,
        });
        AddMap(db, tenant, connection, "party", external, moduleId);
    }

    private static void AddMap(IntegrationDbContext db, TenantId tenant, Guid connection,
        string kind, string external, Guid moduleId) => db.ExternalIdMaps.Add(new ExternalIdMap
    {
        TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(), ConnectionId = connection,
        EntityType = kind, ExternalId = external, ModuleId = moduleId, ValidFrom = At,
        NaturalKeyHash = new string('a', 64), MappingSource = ExternalIdMappingSource.Auto,
        MappedBy = "system:test", MappedAt = At,
    });

    private static InboundMessage StockMessage(TenantId tenant, Guid connection, string id,
        string serial, string location)
    {
        var line = new CanonicalLine(1, "1", "STO-24", 1, "EA", [new(serial)], [], location);
        var payload = new GoodsReceiptPosted(new("receipt", id, DateOnly.FromDateTime(At), id), "SUP-1", [line]);
        return new()
        {
            TenantId = tenant, InboundMessageId = Guid.CreateVersion7(), MessageId = Guid.CreateVersion7(),
            ConnectionId = connection, ConnectorCode = "test", ErpEntity = "receipt", ErpExternalId = id,
            ErpChangeMarker = id, MarkerKind = "native", ContractVersion = "1.0",
            MessageType = nameof(GoodsReceiptPosted), CaptureMode = "backfill", RawPayload = "{}",
            CanonicalMessage = JsonSerializer.Serialize(new { payload }, IntegrationContracts.Json),
            OccurredAt = At, ObservedAt = At, AcceptedAt = At, IsLatest = true,
            TranslationStatus = InboundTranslationStatus.Completed,
        };
    }

    private static void AddUnit(SerialisationDbContext db, TenantId tenant, Guid unitId, Guid itemId,
        Guid locationId, string serial, Guid legalEntity, bool covered, long sequence)
    {
        var eventId = Guid.CreateVersion7();
        db.Units.Add(new Unit
        {
            TenantId = tenant, UnitId = unitId, LifecycleStatus = LifecycleStatus.Released,
            Origin = UnitOrigin.Purchased, SerialisationPoint = SerialisationPoint.Receipt,
            RetentionClass = "standard_10y", CreatedAt = At, CreatedEventId = eventId,
            CreatedEventTime = At,
        });
        db.Identifiers.Add(new UnitIdentifier
        {
            TenantId = tenant, IdentifierId = Guid.CreateVersion7(), UnitId = unitId,
            Type = IdentifierType.InternalSerial, Scheme = IdentifierScheme.Internal,
            RawValue = serial, NormalisedValue = ErpChangeNaturalKeys.Serial(serial),
            ScopeKey = itemId.ToString("N"), ValidFrom = At, ActorRef = "system:test",
            RecordedTime = At, EventId = eventId,
        });
        db.Current.Add(new UnitCurrent
        {
            TenantId = tenant, UnitId = unitId, ItemId = itemId,
            LifecycleStatus = LifecycleStatus.Released, Disposition = UnitDisposition.Available,
            LocationId = locationId, LocationKind = LocationKind.Warehouse,
            LiveEntitlementIds = covered ? $"[\"{Guid.NewGuid():D}\"]" : "[]",
            WarrantyEndMax = covered ? new DateOnly(2028, 9, 1) : null,
            LastEventSeq = 0, LastEventTime = At,
        });
        db.Episodes.Add(new CustodyEpisode
        {
            TenantId = tenant, EpisodeId = Guid.CreateVersion7(), UnitId = unitId,
            Kind = EpisodeKind.Custody, LegalEntityId = legalEntity, LocationId = locationId,
            LocationKind = LocationKind.Warehouse, Reason = EpisodeReason.Receipt,
            Source = "test", Confidence = Confidence.High, ValidFrom = At, OpeningEventId = eventId,
        });
    }
}
