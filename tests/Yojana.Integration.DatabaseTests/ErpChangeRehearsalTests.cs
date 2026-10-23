using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
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
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;
using Yojana.Serialisation.Data;
using Yojana.Serialisation.Data.Entities;
using Yojana.Integration.Verification;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ErpChangeRehearsalTests(
    IntegrationHostFixture fixture,
    ITestOutputHelper output)
{
    private const string Operator = "fictional-integration-operator";
    private const string InventoryController = "fictional-inventory-controller";
    private static readonly DateTime Start = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Seeded_tenant_round_trips_through_both_second_adapters_without_changing_history()
    {
        var reports = new List<ErpChangeRehearsalCaseReport>();
        string? failure = null;
        try
        {
            var isolationTenant = await SeedIsolationTenantAsync();
            var isolationBefore = await SnapshotLedgerAsync(isolationTenant);
            reports.Add(await RunCx02Async());
            reports.Add(await RunCx04Async());
            var isolationAfter = await SnapshotLedgerAsync(isolationTenant);
            Assert.Equal(isolationBefore.Hash, isolationAfter.Hash);
            Assert.Equal(isolationBefore.Rows, isolationAfter.Rows);
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            throw;
        }
        finally
        {
            var path = await ErpChangeRehearsalReportWriter.WriteAsync(new(2026, 10, 23), reports,
                failure is null ? "PASS" : "FAIL", failure);
            output.WriteLine("ERP change rehearsal report: {0}", path);
        }
    }

    private async Task<ErpChangeRehearsalCaseReport> RunCx02Async()
    {
        await using var target = new Cx02Simulator();
        SeedCx02(target);
        var settings = new Cx02ConnectionSettings(target.BaseUri, Cx02Simulator.DefaultTenant,
            Cx02Simulator.DefaultTenant, Cx02Simulator.DefaultEnvironment, Cx02Simulator.CompanyId,
            "1.0", TokenUri: target.TokenUri(), PollPageSize: 20);
        var credential = new Cx02ClientCredential("fictional-client", target.Options.ClientSecret,
            "fictional-client-state");
        var request = new RegisterConnection(Guid.CreateVersion7(), Cx02Capabilities.ConnectorCode,
            "Fictional CX02 replacement connection", Guid.Empty,
            JsonSerializer.SerializeToElement(settings, IntegrationContracts.Json),
            [new(Cx02Client.CredentialPurpose,
                JsonSerializer.Serialize(credential, IntegrationContracts.Json))]);
        return await RunCaseAsync("CX01 → CX02 → CX01", request, cx02EdgeCase: true, null);
    }

    private async Task<ErpChangeRehearsalCaseReport> RunCx04Async()
    {
        await using var target = new Cx04Simulator(new()
        {
            LicenceTier = Yojana.Integration.Connectors.CX04.Testing.Cx04LicenceTier.Enterprise,
        });
        var settings = new Cx04ConnectionSettings(target.BaseUri, "fictional-connected-application",
            new Uri("https://yojana.invalid/integration/callback"),
            Yojana.Integration.Connectors.CX04.Cx04LicenceTier.Enterprise,
            PollPageSize: 20);
        var request = new RegisterConnection(Guid.CreateVersion7(), Cx04Capabilities.ConnectorCode,
            "Fictional CX04 replacement connection", Guid.Empty,
            JsonSerializer.SerializeToElement(settings, IntegrationContracts.Json), []);
        return await RunCaseAsync("CX01 → CX04 → CX01", request, cx02EdgeCase: false,
            async (tenant, connectionId) =>
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
                    new(query["code"]!, query["state"]!), Operator, default);
            });
    }

    private async Task<ErpChangeRehearsalCaseReport> RunCaseAsync(
        string route,
        RegisterConnection targetRequest,
        bool cx02EdgeCase,
        Func<TenantId, Guid, Task>? activateTarget)
    {
        fixture.Clock.UtcNow = Start;
        await using var sourceSimulator = new Cx01Simulator();
        var tenant = await fixture.NewTenantAsync(cx02EdgeCase ? "r02" : "r04");
        var legalEntity = Guid.CreateVersion7();
        var steps = new List<ErpChangeRehearsalStepReport>();
        var divergences = new List<string>();
        var exceptions = new List<string>();
        using var scope = fixture.ScopeFor(tenant);
        var acceptanceSettings = scope.ServiceProvider.GetRequiredService<AcceptanceGateSettings>();
        var acceptanceDefaults = await acceptanceSettings.GetAsync(default);
        await acceptanceSettings.SetAsync(new(acceptanceDefaults.Connectors.Select(row =>
                row.ConnectorCode == targetRequest.ConnectorCode ? row with { Required = false } : row).ToList(),
            acceptanceDefaults.SettingsVersion), default);
        await EnableConnectorsAsync(scope);
        var sourceRequest = sourceSimulator.RegistrationRequest() with
        {
            LegalEntityId = legalEntity,
            DisplayLabel = "Fictional primary CX01 connection",
        };
        var source = await scope.ServiceProvider.GetRequiredService<ConnectionCommands>()
            .RegisterAsync(sourceRequest, Operator, default);
        targetRequest = targetRequest with { LegalEntityId = legalEntity };
        var runs = scope.ServiceProvider.GetRequiredService<ErpChangeRunService>();
        var forward = await runs.StartAsync(new(source.ConnectionId, targetRequest), Operator, default);
        if (activateTarget is not null) await activateTarget(tenant, forward.Target.ConnectionId);
        await SetHealthyAsync(scope, forward.Target.ConnectionId);

        var seed = await SeedTenantAsync(scope, tenant, legalEntity, source.ConnectionId,
            forward.Target.ConnectionId, cx02EdgeCase);
        var before = await SnapshotLedgerAsync(tenant);
        var stateBefore = await SnapshotStateAsync(tenant);

        await CompleteRunAsync(scope, forward.RunId, cx02EdgeCase, steps, divergences, exceptions);
        await AssertHistoricalReceiptAsync(scope, seed.ReceiptDocumentId);
        var archivedRefusal = await Assert.ThrowsAsync<ErpChangeRuleException>(() =>
            runs.StartReverseAsync(forward.RunId, new(), Operator, default));
        Assert.Equal("erp_change_reverse_source_archived", archivedRefusal.Rule);
        exceptions.Add("Reverse with the archived CX01 connection was refused with erp_change_reverse_source_archived; a new CX01 connection was supplied.");

        var replacementRequest = sourceSimulator.RegistrationRequest() with
        {
            LegalEntityId = legalEntity,
            DisplayLabel = "Fictional replacement CX01 connection",
        };
        var reverse = await runs.StartReverseAsync(forward.RunId, new(replacementRequest), Operator, default);
        Assert.NotEqual(source.ConnectionId, reverse.Target.ConnectionId);
        Assert.Equal("CX01", reverse.Target.ConnectorCode);
        await SetHealthyAsync(scope, reverse.Target.ConnectionId);
        await SeedReverseOpeningPositionAsync(scope, tenant, reverse.Target.ConnectionId, seed);
        await CompleteRunAsync(scope, reverse.RunId, false, steps, divergences, exceptions);
        await AssertHistoricalShipmentAsync(scope, seed.ShipmentDocumentId);

        var after = await SnapshotLedgerAsync(tenant);
        var stateAfter = await SnapshotStateAsync(tenant);
        Assert.Equal(before.Rows, after.Rows);
        Assert.Equal(before.Hash, after.Hash);
        Assert.Equal(stateBefore.ToArray(), stateAfter.ToArray());

        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var archived = await db.Connections.AsNoTracking().Where(row => row.TenantId == tenant
                && row.Status == ConnectionStatus.Archived)
            .Select(row => row.ConnectionId).ToListAsync();
        Assert.Contains(source.ConnectionId, archived);
        Assert.Contains(forward.Target.ConnectionId, archived);
        Assert.All(await db.ExternalIdMaps.AsNoTracking().Where(row => row.TenantId == tenant
                && archived.Contains(row.ConnectionId)).ToListAsync(),
            row => Assert.NotNull(row.ValidTo));

        return new(route, ["CX01", forward.Target.ConnectorCode, "CX01"],
            new Dictionary<string, int>
            {
                ["units"] = seed.UnitIds.Count,
                ["unit events"] = before.Rows,
                ["items"] = seed.Items.Count,
                ["parties"] = seed.PartyCount,
                ["stock locations"] = seed.Locations.Count,
                ["open RMAs"] = 1,
                ["open claims"] = 1,
            }, steps, divergences, exceptions, before.Hash, after.Hash, before.Rows, after.Rows, "PASS");
    }

    private async Task CompleteRunAsync(IServiceScope scope, Guid runId, bool resolveCx02EdgeCase,
        List<ErpChangeRehearsalStepReport> steps, List<string> divergences, List<string> exceptions)
    {
        var runs = scope.ServiceProvider.GetRequiredService<ErpChangeRunService>();
        var started = fixture.Clock.UtcNow;
        var check = await runs.CheckAsync(runId, Operator, default);
        Assert.True(check.Passed, check.Detail);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        steps.Add(Step(1, "Register", started, check));
        Assert.Equal(ErpChangeStep.MasterData,
            (await runs.AdvanceAsync(runId, Operator, default)).CurrentStep);

        started = fixture.Clock.UtcNow;
        var ledgerBeforeResolution = await CurrentTenantLedgerAsync(scope);
        var master = scope.ServiceProvider.GetRequiredService<ErpChangeMasterDataService>();
        await master.RemapAsync(runId, Operator, default);
        if (resolveCx02EdgeCase)
        {
            var blocked = await runs.CheckAsync(runId, Operator, default);
            Assert.False(blocked.Passed);
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var open = await db.ErpChangeMasterDataExceptions.Where(row => row.ErpChangeRunId == runId
                    && row.Status == ErpChangeExceptionStatus.Open)
                .OrderBy(row => row.Problem).ThenBy(row => row.TargetExternalId).ToListAsync();
            Assert.Contains(open, row => row.Problem == MasterDataProblem.Unmatched
                && row.TargetExternalId == "HW-SWITCH-48X");
            Assert.Contains(open, row => row.Problem == MasterDataProblem.Ambiguous
                && row.TargetExternalId == "C-AMB");
            var unmatched = open.Single(row => row.Problem == MasterDataProblem.Unmatched);
            var ambiguous = open.Single(row => row.Problem == MasterDataProblem.Ambiguous);
            var switchId = await db.ReferenceProjections.Where(row => row.EntityType == "item"
                    && row.Code == "HW-SWITCH-48")
                .Select(row => row.ModuleId).SingleAsync();
            var ownerId = await db.ReferenceProjections.Where(row => row.EntityType == "party"
                    && row.Code == "CUST-NORTH")
                .Select(row => row.ModuleId).SingleAsync();
            var targetConnectionId = await db.ErpChangeRuns.Where(row => row.ErpChangeRunId == runId)
                .Select(row => row.TargetConnectionId).SingleAsync();
            Assert.False(await db.ExternalIdMaps.AnyAsync(row => row.ConnectionId == targetConnectionId
                && row.ExternalId == "HW-SWITCH-48X" && row.ModuleId == switchId && row.ValidTo == null));
            await master.ResolveAsync(runId, unmatched.ErpChangeMasterDataExceptionId,
                new(switchId, "Mapped after an inventory controller confirmed the replacement item code."),
                Operator, candidateOnly: false, default);
            await master.ResolveAsync(runId, ambiguous.ErpChangeMasterDataExceptionId,
                new(ownerId, "Selected the customer record with the active installed units."),
                Operator, candidateOnly: true, default);
            foreach (var missing in open.Where(row => row.Problem == MasterDataProblem.MissingInTarget))
                await master.MarkNotNeededAsync(runId, missing.ErpChangeMasterDataExceptionId,
                    new("The unmatched target item was explicitly mapped to this source item."), Operator, default);
            var ledgerAfterResolution = await CurrentTenantLedgerAsync(scope);
            Assert.Equal(ledgerBeforeResolution.Hash, ledgerAfterResolution.Hash);
            Assert.Equal(ledgerBeforeResolution.Rows, ledgerAfterResolution.Rows);
            exceptions.Add("CX02 unmatched item HW-SWITCH-48X was explicitly mapped; no item-code auto-merge occurred.");
            exceptions.Add("CX02 party C-AMB matched two candidates and was explicitly mapped to the party with live units.");
            if (open.Any(row => row.Problem == MasterDataProblem.MissingInTarget))
                exceptions.Add("The paired missing-source item exception was closed after the explicit unmatched-item mapping.");
        }
        else
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            var open = await db.ErpChangeMasterDataExceptions.Where(row => row.ErpChangeRunId == runId
                    && row.Status == ErpChangeExceptionStatus.Open)
                .ToListAsync();
            Assert.All(open, row => Assert.Equal(0, row.LiveUnitsAffected));
            foreach (var row in open)
                await master.MarkNotNeededAsync(runId, row.ErpChangeMasterDataExceptionId,
                    new("The simulator-only record has no live unit and is outside the rehearsal opening position."),
                    Operator, default);
            if (open.Count > 0)
                exceptions.Add($"{open.Count} simulator-only master-data record(s) with no live units were explicitly marked not needed.");
        }
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        check = await runs.CheckAsync(runId, Operator, default);
        Assert.True(check.Passed, check.Detail);
        steps.Add(Step(2, "Re-map master data", started, check));
        Assert.Equal(ErpChangeStep.Stock, (await runs.AdvanceAsync(runId, Operator, default)).CurrentStep);

        started = fixture.Clock.UtcNow;
        var stock = scope.ServiceProvider.GetRequiredService<ErpChangeStockService>();
        var compared = await stock.ReconcileAsync(runId, InventoryController, default);
        Assert.True(compared.UnitsCompared > 0);
        await stock.SignOffAsync(runId, new("Opening-position residuals were reviewed in the simulators."),
            InventoryController, default);
        check = await runs.CheckAsync(runId, Operator, default);
        Assert.True(check.Passed, check.Detail);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        steps.Add(Step(3, "Reconcile stock", started, check));
        Assert.Equal(ErpChangeStep.DualRun, (await runs.AdvanceAsync(runId, Operator, default)).CurrentStep);

        started = fixture.Clock.UtcNow;
        var dual = scope.ServiceProvider.GetRequiredService<ErpChangeDualRunService>();
        var dualStart = await dual.StartAsync(runId, Operator, default);
        Assert.Equal(30, dualStart.MinimumDays);
        var divergence = await dual.ReportAsync(runId,
            new(ErpChangeDivergenceKind.Document, "shipment", Guid.CreateVersion7(), "SHIP-DUAL-1",
                "source-confirmed", "target-pending"), default);
        var resolved = await dual.AcceptSourceAsync(runId, divergence.DivergenceId,
            ErpChangeDivergenceKind.Document, new(Guid.CreateVersion7()), Operator, default);
        divergences.Add($"{resolved.SubjectRef}: source won during dual run; resolution {resolved.Resolution}.");
        for (var day = 24; day <= 30; day++)
        {
            fixture.Clock.UtcNow = started.AddDays(day);
            Assert.True((await dual.RunDailyAsync(runId, Operator, default)).Clean);
        }
        check = await runs.CheckAsync(runId, Operator, default);
        Assert.True(check.Passed, check.Detail);
        steps.Add(Step(4, "Dual run", started, check));
        Assert.Equal(ErpChangeStep.Cutover, (await runs.AdvanceAsync(runId, Operator, default)).CurrentStep);

        started = fixture.Clock.UtcNow;
        var cutover = scope.ServiceProvider.GetRequiredService<ErpChangeCutoverService>();
        await cutover.BeginAsync(runId, new("reference_only"), Operator, default);
        var validationTypes = (await cutover.ViewAsync(runId, default)).Items
            .Where(row => row.Code.StartsWith("validate:", StringComparison.Ordinal))
            .Select(row => row.Code["validate:".Length..]).ToList();
        foreach (var commandType in validationTypes)
            await cutover.RecordValidationAsync(runId,
                new(commandType, ErpChangeValidationMethod.TestCompany,
                    "The connector simulator accepted the controlled validation scenario."), Operator, default);
        await cutover.EnableTargetAsync(runId, Operator, default);
        await cutover.DrainAsync(runId, Operator, default);
        await cutover.ConfirmFirstOutboundAsync(runId,
            new("CreateCreditMemo", "CM-REHEARSAL-1", "Confirmed in the connector simulator."),
            Operator, default);
        await cutover.ReconcileAsync(runId, Operator, default);
        check = await runs.CheckAsync(runId, Operator, default);
        Assert.True(check.Passed, check.Detail);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        steps.Add(Step(5, "Cut over outbound", started, check));
        Assert.Equal(ErpChangeStep.Archive, (await runs.AdvanceAsync(runId, Operator, default)).CurrentStep);

        started = fixture.Clock.UtcNow;
        var archived = await cutover.ArchiveAsync(runId, Operator, default);
        Assert.True(archived.Archived, archived.FailureCode);
        check = await runs.CheckAsync(runId, Operator, default);
        Assert.True(check.Passed, check.Detail);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddMinutes(1);
        steps.Add(Step(6, "Archive former source", started, check));
        var detail = await runs.DetailAsync(runId, default);
        Assert.Equal(0, detail.UnitHistoriesChanged);
        if (detail.Source.ConnectorCode != "CX01")
            Assert.Equal(ErpChangeRunStatus.Completed,
                (await runs.AdvanceAsync(runId, Operator, default)).Status);
    }

    private static ErpChangeRehearsalStepReport Step(int number, string name, DateTime started,
        ErpChangeStepCheckView check) => new(number, name, started, check.CheckedAt,
        check.Passed, check.Detail);

    private async Task EnableConnectorsAsync(IServiceScope scope)
    {
        await scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>().SeedGenericEntriesAsync(default);
        await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ConnectorCatalogue
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.Enabled, true));
    }

    private async Task SetHealthyAsync(IServiceScope scope, Guid connectionId)
    {
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.ChangeTracker.Clear();
        var health = await db.ConnectionHealth.SingleAsync(row => row.ConnectionId == connectionId);
        health.Status = "healthy";
        health.IsLive = true;
        health.LastDailyCheckAt = fixture.Clock.UtcNow;
        health.LastDailyCheckResult = "clean";
        await db.SaveChangesAsync();
    }

    private async Task<RehearsalSeed> SeedTenantAsync(IServiceScope scope, TenantId tenant,
        Guid legalEntity, Guid sourceConnection, Guid targetConnection, bool cx02EdgeCase)
    {
        var server = Guid.CreateVersion7();
        var storage = Guid.CreateVersion7();
        var network = Guid.CreateVersion7();
        var pc = Guid.CreateVersion7();
        var owner = Guid.CreateVersion7();
        var duplicateParty = Guid.CreateVersion7();
        var supplier = Guid.CreateVersion7();
        var main = Guid.CreateVersion7();
        var repair = Guid.CreateVersion7();
        var quarantine = Guid.CreateVersion7();
        var customerSite = Guid.CreateVersion7();
        var receiptDocument = Guid.CreateVersion7();
        var shipmentDocument = Guid.CreateVersion7();
        var items = new Dictionary<string, Guid>
        {
            ["HW-SERVER-2U"] = server,
            ["HW-STORAGE-24"] = storage,
            ["HW-SWITCH-48"] = network,
            ["HW-PC-SFF"] = pc,
        };
        var locations = new Dictionary<string, Guid>
        {
            ["MAIN"] = main,
            ["REPAIR"] = repair,
            ["QUARANTINE"] = quarantine,
            ["CUSTOMER"] = customerSite,
        };
        var units = new[]
        {
            new UnitSeed(Guid.CreateVersion7(), server, "SRV-2609-001", main, LocationKind.Warehouse,
                UnitDisposition.Available, null, null),
            new UnitSeed(Guid.CreateVersion7(), storage, "STO-2609-001", main, LocationKind.Warehouse,
                UnitDisposition.Available, null, null),
            new UnitSeed(Guid.CreateVersion7(), network, "NET-2609-001", repair, LocationKind.InRepair,
                UnitDisposition.InProgress, null, null),
            new UnitSeed(Guid.CreateVersion7(), pc, "PC-2609-001", customerSite, LocationKind.CustomerSite,
                UnitDisposition.Active, owner, null),
            new UnitSeed(Guid.CreateVersion7(), pc, "PC-2609-002", customerSite, LocationKind.CustomerSite,
                UnitDisposition.Available, owner, shipmentDocument),
            new UnitSeed(Guid.CreateVersion7(), storage, "STO-2609-002", quarantine, LocationKind.Quarantine,
                UnitDisposition.Inactive, null, null),
        };

        var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        AddReference(integration, tenant, "item", server, "HW-SERVER-2U", "Fictional 2U rack server");
        AddReference(integration, tenant, "item", storage, "HW-STORAGE-24", "Fictional 24-bay storage array");
        AddReference(integration, tenant, "item", network, "HW-SWITCH-48", "Fictional 48-port network switch");
        AddReference(integration, tenant, "item", pc, "HW-PC-SFF", "Fictional small-form-factor PC");
        AddPartyReference(integration, tenant, owner, "CUST-NORTH", "Fictional North Systems", "IN-AA111", "IN");
        if (cx02EdgeCase)
            AddPartyReference(integration, tenant, duplicateParty, "CUST-NORTH-OLD",
                "Fictional North Systems Services", "INAA111", "in");
        AddPartyReference(integration, tenant, supplier, "SUP-CORE", "Fictional Core Distribution", "IN-BB222", "IN");
        foreach (var item in items) AddMap(integration, tenant, sourceConnection, "item", item.Key, item.Value);
        AddMap(integration, tenant, sourceConnection, "party", "CUST-NORTH", owner);
        if (cx02EdgeCase)
            AddMap(integration, tenant, sourceConnection, "party", "CUST-NORTH-OLD", duplicateParty);
        AddMap(integration, tenant, sourceConnection, "party", "SUP-CORE", supplier);
        foreach (var location in locations)
        {
            AddMap(integration, tenant, sourceConnection, "location", location.Key, location.Value);
            AddMap(integration, tenant, targetConnection, "location", location.Key, location.Value);
        }
        if (!cx02EdgeCase)
        {
            foreach (var item in items) AddMap(integration, tenant, targetConnection, "item", item.Key, item.Value);
            AddMap(integration, tenant, targetConnection, "party", "CUST-NORTH", owner);
        }
        AddMap(integration, tenant, sourceConnection, "document", "REC-HW-100", receiptDocument);
        AddMap(integration, tenant, targetConnection, "document", "SHIP-HW-200", shipmentDocument);
        var receipt = new GoodsReceiptPosted(new("receipt", "REC-HW-100", new(2026, 8, 20), "REC-HW-100"),
            "SUP-CORE", [Line(1, "HW-SERVER-2U", "SRV-2609-001", "MAIN")]);
        const string shipmentItem = "HW-PC-SFF";
        var shipmentParty = cx02EdgeCase ? "C-AMB" : "CUST-NORTH";
        var shipment = new DeliveryPosted(new("delivery", "SHIP-HW-200", new(2026, 8, 25), "SHIP-HW-200"),
            shipmentParty, "CUSTOMER", [Line(1, shipmentItem, "PC-2609-002", "CUSTOMER")]);
        AddCanonicalDocument(integration, tenant, sourceConnection, receiptDocument, "receipt", "REC-HW-100",
            nameof(GoodsReceiptPosted), receipt, Start.AddDays(-12));
        AddCanonicalDocument(integration, tenant, targetConnection, shipmentDocument, "delivery", "SHIP-HW-200",
            nameof(DeliveryPosted), shipment, Start.AddDays(-7));
        foreach (var unit in units.Where(value => SerialisedOnHandCustodyPolicy.IncludedLocationKinds
                     .Contains(value.LocationKind)))
            integration.InboundMessages.Add(StockMessage(tenant, sourceConnection,
                "SRC-" + unit.Serial, ItemCode(items, unit.ItemId), unit.Serial,
                LocationCode(locations, unit.LocationId), Start.AddDays(-2)));
        if (!cx02EdgeCase)
            foreach (var unit in units.Where(value => SerialisedOnHandCustodyPolicy.IncludedLocationKinds
                         .Contains(value.LocationKind)))
                integration.InboundMessages.Add(StockMessage(tenant, targetConnection,
                    "TGT-" + unit.Serial, ItemCode(items, unit.ItemId), unit.Serial,
                    LocationCode(locations, unit.LocationId), Start.AddDays(-2)));
        await integration.SaveChangesAsync();

        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var seq = 1L;
        foreach (var unit in units)
        {
            var commissioned = Guid.CreateVersion7(Start.AddDays(-20).AddTicks(seq));
            serialisation.Units.Add(new Unit
            {
                TenantId = tenant, UnitId = unit.UnitId, LifecycleStatus = LifecycleStatus.Released,
                Origin = UnitOrigin.Purchased, SerialisationPoint = SerialisationPoint.Receipt,
                RetentionClass = "standard_10y", CreatedAt = Start.AddDays(-20),
                CreatedEventId = commissioned, CreatedEventTime = Start.AddDays(-20),
            });
            serialisation.Identifiers.Add(new UnitIdentifier
            {
                TenantId = tenant, IdentifierId = Guid.CreateVersion7(), UnitId = unit.UnitId,
                Type = IdentifierType.InternalSerial, Scheme = IdentifierScheme.Internal,
                RawValue = unit.Serial, NormalisedValue = ErpChangeNaturalKeys.Serial(unit.Serial),
                ScopeKey = unit.ItemId.ToString("N"), ValidFrom = Start.AddDays(-20), ActorRef = Operator,
                RecordedTime = Start.AddDays(-20), EventId = commissioned,
            });
            serialisation.Current.Add(new UnitCurrent
            {
                TenantId = tenant, UnitId = unit.UnitId, ItemId = unit.ItemId,
                LifecycleStatus = LifecycleStatus.Released, Disposition = unit.Disposition,
                LocationId = unit.LocationId, LocationKind = unit.LocationKind,
                CustodyPartyRef = unit.OwnerParty, CustodyKind = EpisodeKind.Custody,
                OwnerPartyRef = unit.OwnerParty, LiveEntitlementIds = "[]",
                LastEventSeq = seq, LastEventTime = Start.AddDays(-1),
            });
            serialisation.Episodes.Add(new CustodyEpisode
            {
                TenantId = tenant, EpisodeId = Guid.CreateVersion7(), UnitId = unit.UnitId,
                Kind = EpisodeKind.Custody, PartyRef = unit.OwnerParty, LegalEntityId = legalEntity,
                LocationId = unit.LocationId, LocationKind = unit.LocationKind,
                Reason = unit.LocationKind == LocationKind.CustomerSite ? EpisodeReason.Loan : EpisodeReason.Receipt,
                Source = "rehearsal", Confidence = Confidence.High, ValidFrom = Start.AddDays(-20),
                OpeningEventId = commissioned,
            });
            serialisation.Events.Add(new UnitEvent
            {
                TenantId = tenant, EventTime = Start.AddDays(-20), EventId = commissioned,
                UnitId = unit.UnitId, Seq = seq++, EventType = UnitEventType.Commissioned,
                BizStep = "receiving", Disposition = unit.Disposition, LocationId = unit.LocationId,
                LegalEntityId = legalEntity, ActorRef = Operator,
                DocumentType = unit.UnitId == units[0].UnitId ? "receipt" : null,
                DocumentId = unit.UnitId == units[0].UnitId ? receiptDocument : unit.DocumentId,
                DocumentLine = unit.DocumentId is not null || unit.UnitId == units[0].UnitId ? 1 : null,
                RecordedTime = Start.AddDays(-20), PostingPeriod = "2026-08",
                IdempotencyKey = HashKey("seed", unit.Serial, "commissioned"),
                Attributes = JsonSerializer.Serialize(new { seed = "erp_change_rehearsal", serial = unit.Serial }),
            });
        }
        serialisation.Rmas.Add(new Rma
        {
            TenantId = tenant, RmaId = Guid.CreateVersion7(), CommandId = Guid.CreateVersion7(),
            RmaNumber = "RMA-2026-0042", Stage = RmaStage.Authorised, RequestDate = new(2026, 8, 29),
            ReturningPartyRef = owner, SettlementPartyRef = owner, ServiceEntityId = legalEntity,
            ObligorEntityId = legalEntity, ServiceCountry = "IN", Currency = "INR",
            RequestedAt = Start.AddDays(-3), AuthorisedAt = Start.AddDays(-2), RequestedBy = Operator,
            LastEventId = Guid.CreateVersion7(), LastModifiedBy = Operator, LastModifiedAt = Start.AddDays(-2),
        });
        serialisation.Claims.Add(new Claim
        {
            TenantId = tenant, ClaimId = Guid.CreateVersion7(), ClaimNumber = "CLM-2026-00042",
            CommandId = Guid.CreateVersion7(), UnitId = units[2].UnitId, ClaimDate = new(2026, 8, 30),
            ReportedDescription = "Port cage damage during installation.",
            ClaimantPartyRef = owner, SettlementPartyRef = owner, ServiceEntityId = legalEntity,
            ObligorEntityId = legalEntity, ServiceCountry = "IN", Currency = "INR",
            CoverageSource = ClaimCoverageSource.Warranty, Remedy = ClaimRemedy.Repair,
            Status = ClaimStatus.Submitted,
            SubmittedAt = Start.AddDays(-2), SubmittedBy = Operator, LastEventId = Guid.CreateVersion7(),
            LastModifiedBy = Operator, LastModifiedAt = Start.AddDays(-2),
        });
        await serialisation.SaveChangesAsync();
        return new(units.Select(value => value.UnitId).ToList(), items, locations,
            receiptDocument, shipmentDocument, owner, cx02EdgeCase ? 3 : 2);
    }

    private async Task SeedReverseOpeningPositionAsync(IServiceScope scope, TenantId tenant,
        Guid connectionId, RehearsalSeed seed)
    {
        var integration = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        foreach (var item in seed.Items) AddMap(integration, tenant, connectionId, "item", item.Key, item.Value);
        AddMap(integration, tenant, connectionId, "party", "CUST-NORTH", seed.OwnerPartyId);
        foreach (var location in seed.Locations)
            AddMap(integration, tenant, connectionId, "location", location.Key, location.Value);
        var serialisation = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var current = await serialisation.Current.AsNoTracking().Where(row => seed.UnitIds.Contains(row.UnitId)
                && SerialisedOnHandCustodyPolicy.IncludedLocationKinds.Contains(row.LocationKind!))
            .ToListAsync();
        var identifiers = await serialisation.Identifiers.AsNoTracking().Where(row => seed.UnitIds.Contains(row.UnitId)
                && row.ValidTo == null)
            .ToDictionaryAsync(row => row.UnitId, row => row.RawValue);
        foreach (var unit in current)
            integration.InboundMessages.Add(StockMessage(tenant, connectionId, "RETURN-" + identifiers[unit.UnitId],
                ItemCode(seed.Items, unit.ItemId), identifiers[unit.UnitId],
                LocationCode(seed.Locations, unit.LocationId!.Value), fixture.Clock.UtcNow));
        await integration.SaveChangesAsync();
    }

    private async Task AssertHistoricalReceiptAsync(IServiceScope scope, Guid documentId)
    {
        var document = await scope.ServiceProvider.GetRequiredService<IPurchasingDocumentQueryPort>()
            .GetReceiptAsync(new("receipt", documentId), default);
        Assert.NotNull(document);
        Assert.NotEmpty(document.Lines);
    }

    private async Task AssertHistoricalShipmentAsync(IServiceScope scope, Guid documentId)
    {
        var document = await scope.ServiceProvider.GetRequiredService<ISalesDocumentQueryPort>()
            .GetShipmentAsync(new("delivery", documentId), default);
        Assert.NotNull(document);
        Assert.NotEmpty(document.Lines);
    }

    private async Task<TenantId> SeedIsolationTenantAsync()
    {
        fixture.Clock.UtcNow = Start;
        var tenant = await fixture.NewTenantAsync("riso");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var unitId = Guid.CreateVersion7();
        var eventId = Guid.CreateVersion7(Start.AddDays(-1));
        db.Units.Add(new Unit
        {
            TenantId = tenant, UnitId = unitId, LifecycleStatus = LifecycleStatus.Released,
            Origin = UnitOrigin.Purchased, SerialisationPoint = SerialisationPoint.Receipt,
            RetentionClass = "standard_10y", CreatedAt = Start.AddDays(-1),
            CreatedEventId = eventId, CreatedEventTime = Start.AddDays(-1),
        });
        db.Events.Add(new UnitEvent
        {
            TenantId = tenant, EventTime = Start.AddDays(-1), EventId = eventId, UnitId = unitId,
            Seq = 1, EventType = UnitEventType.Commissioned, LegalEntityId = Guid.CreateVersion7(),
            ActorRef = Operator, RecordedTime = Start.AddDays(-1),
            IdempotencyKey = HashKey("isolation", unitId.ToString("N")),
        });
        await db.SaveChangesAsync();
        return tenant;
    }

    private async Task<LedgerSnapshot> CurrentTenantLedgerAsync(IServiceScope scope)
    {
        var tenant = scope.ServiceProvider.GetRequiredService<Yojana.Platform.Contracts.Tenancy.ITenantContext>()
            .TenantId;
        return await SnapshotLedgerAsync(tenant);
    }

    private async Task<LedgerSnapshot> SnapshotLedgerAsync(TenantId tenant)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<SerialisationDbContext>();
        var live = (await db.Events.AsNoTracking().ToListAsync()).Select(row => LedgerRow.From("live", row));
        var archive = (await db.ArchivedEvents.AsNoTracking().ToListAsync())
            .Select(row => LedgerRow.From("archive", row));
        var rows = live.Concat(archive).OrderBy(row => row.EventTime).ThenBy(row => row.EventId)
            .ThenBy(row => row.Store, StringComparer.Ordinal).ToList();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(rows, IntegrationContracts.Json);
        return new(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), rows.Count);
    }

    private async Task<IReadOnlyList<UnitStateSnapshot>> SnapshotStateAsync(TenantId tenant)
    {
        using var scope = fixture.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<SerialisationDbContext>().Current.AsNoTracking()
            .OrderBy(row => row.UnitId).Select(row => new UnitStateSnapshot(row.UnitId, row.LifecycleStatus,
                row.Disposition, row.LocationId, row.LocationKind, row.CustodyPartyRef, row.CustodyKind,
                row.OwnerPartyRef)).ToListAsync();
    }

    private static void SeedCx02(Cx02Simulator simulator)
    {
        simulator.Seed("items",
            Cx02Item("HW-SERVER-2U", "Fictional 2U rack server"),
            Cx02Item("HW-STORAGE-24", "Fictional 24-bay storage array"),
            Cx02Item("HW-SWITCH-48X", "Fictional 48-port network switch"),
            Cx02Item("HW-PC-SFF", "Fictional small-form-factor PC"));
        simulator.Seed("customers", Cx02SimulatorEntity.From(new
        {
            id = Guid.CreateVersion7(), number = "C-AMB", displayName = "Fictional North Systems",
            taxRegistrationNumber = "IN AA111", countryLetterCode = "IN",
            lastModifiedDateTime = "2026-09-01T08:00:00Z",
        }));
        simulator.Seed("trackedItemLedgerEntries",
            Cx02Stock(2001, "HW-SERVER-2U", "SRV-2609-001", "MAIN"),
            Cx02Stock(2002, "HW-STORAGE-24", "STO-2609-001", "MAIN"),
            Cx02Stock(2003, "HW-SWITCH-48X", "NET-2609-001", "REPAIR"),
            Cx02Stock(2004, "HW-STORAGE-24", "STO-2609-002", "QUARANTINE"));
    }

    private static Cx02SimulatorEntity Cx02Item(string number, string name) => Cx02SimulatorEntity.From(new
    {
        id = Guid.CreateVersion7(), number, displayName = name, itemTrackingCode = "Serial Specific",
        baseUnitOfMeasureCode = "EA", lastModifiedDateTime = "2026-09-01T08:00:00Z",
    });

    private static Cx02SimulatorEntity Cx02Stock(int entry, string item, string serial, string location) =>
        Cx02SimulatorEntity.From(new
        {
            id = Guid.CreateVersion7(), entryNumber = entry, itemNumber = item, postingDate = "2026-08-30",
            entryType = "Positive Adjmt.", sourceNumber = "OPENING", documentNumber = "OPEN-" + entry,
            documentType = "Positive Adjustment", quantity = 1m, serialNumber = serial, lotNumber = "",
            packageNumber = "", locationCode = location, lastModifiedDateTime = "2026-09-01T08:00:00Z",
        });

    private static CanonicalLine Line(int number, string item, string serial, string location) =>
        new(number, number.ToString(), item, 1, "EA", [new(serial)], [], location);

    private static InboundMessage StockMessage(TenantId tenant, Guid connection, string id,
        string item, string serial, string location, DateTime occurredAt)
    {
        var payload = new InventoryAdjusted(
            new("adjustment", id, DateOnly.FromDateTime(occurredAt), id),
            OpenEnum.Known("in"), [Line(1, item, serial, location)], "opening_position");
        return new()
        {
            TenantId = tenant, InboundMessageId = Guid.CreateVersion7(), MessageId = Guid.CreateVersion7(),
            ConnectionId = connection, ConnectorCode = "rehearsal", ErpEntity = "adjustment",
            ErpExternalId = id, ErpChangeMarker = id, MarkerKind = "native", ContractVersion = "1.0",
            MessageType = nameof(InventoryAdjusted), CaptureMode = "backfill", RawPayload = "{}",
            CanonicalMessage = JsonSerializer.Serialize(new { payload }, IntegrationContracts.Json),
            OccurredAt = occurredAt, ObservedAt = occurredAt, AcceptedAt = occurredAt,
            IsLatest = true, TranslationStatus = InboundTranslationStatus.Completed,
        };
    }

    private static void AddCanonicalDocument(IntegrationDbContext db, TenantId tenant, Guid connection,
        Guid documentId, string entity, string externalId, string messageType, object payload, DateTime occurredAt)
    {
        db.CanonicalDocuments.Add(new CanonicalDocument
        {
            TenantId = tenant, DocumentId = documentId, ConnectionId = connection,
            ErpEntity = entity, ErpExternalId = externalId, MessageType = messageType,
            Payload = JsonSerializer.Serialize(payload, IntegrationContracts.Json), OccurredAt = occurredAt,
            UpdatedAt = occurredAt, SourceInboundMessageId = Guid.CreateVersion7(),
        });
    }

    private static void AddReference(IntegrationDbContext db, TenantId tenant, string type,
        Guid id, string code, string name) => db.ReferenceProjections.Add(new ReferenceProjection
    {
        TenantId = tenant, EntityType = type, ModuleId = id, Code = code, DisplayName = name,
        AttributesJson = JsonSerializer.Serialize(new ItemReferenceAttributes(false, null, code),
            IntegrationContracts.Json), UpdatedAt = Start,
    });

    private static void AddPartyReference(IntegrationDbContext db, TenantId tenant, Guid id,
        string code, string name, string taxId, string country) => db.ReferenceProjections.Add(new ReferenceProjection
    {
        TenantId = tenant, EntityType = "party", ModuleId = id, Code = code, DisplayName = name,
        AttributesJson = JsonSerializer.Serialize(new PartyReferenceAttributes(taxId, country, "customer"),
            IntegrationContracts.Json), UpdatedAt = Start,
    });

    private static void AddMap(IntegrationDbContext db, TenantId tenant, Guid connection,
        string type, string externalId, Guid moduleId) => db.ExternalIdMaps.Add(new ExternalIdMap
    {
        TenantId = tenant, ExternalIdMapId = Guid.CreateVersion7(), ConnectionId = connection,
        EntityType = type, ExternalId = externalId, ModuleId = moduleId, ValidFrom = Start,
        NaturalKeyHash = HashKey(type, externalId), MappingSource = ExternalIdMappingSource.Auto,
        MappedBy = Operator, MappedAt = Start,
    });

    private static string ItemCode(IReadOnlyDictionary<string, Guid> items, Guid itemId) =>
        items.Single(value => value.Value == itemId).Key;

    private static string LocationCode(IReadOnlyDictionary<string, Guid> locations, Guid locationId) =>
        locations.Single(value => value.Value == locationId).Key;

    private static string HashKey(params string[] values) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|', values))))
            .ToLowerInvariant();

    private sealed record UnitSeed(Guid UnitId, Guid ItemId, string Serial, Guid LocationId,
        string LocationKind, string Disposition, Guid? OwnerParty, Guid? DocumentId);

    private sealed record RehearsalSeed(IReadOnlyList<Guid> UnitIds,
        IReadOnlyDictionary<string, Guid> Items,
        IReadOnlyDictionary<string, Guid> Locations,
        Guid ReceiptDocumentId,
        Guid ShipmentDocumentId,
        Guid OwnerPartyId,
        int PartyCount);

    private sealed record LedgerSnapshot(string Hash, int Rows);

    private sealed record UnitStateSnapshot(Guid UnitId, string LifecycleStatus, string Disposition,
        Guid? LocationId, string? LocationKind, Guid? CustodyPartyRef, string? CustodyKind,
        Guid? OwnerPartyRef);

    private sealed record LedgerRow(string Store, TenantId TenantId, DateTime EventTime, Guid EventId,
        Guid UnitId, Guid? MergedFromUnitId, long Seq, string EventType, string? BizStep,
        string? Disposition, Guid? LocationId, Guid LegalEntityId, string ActorRef,
        string? DocumentType, Guid? DocumentId, int? DocumentLine, DateTime RecordedTime,
        string? PostingPeriod, string IdempotencyKey, Guid? ReversesEventId, string? Attributes)
    {
        public static LedgerRow From(string store, UnitEventRow row) => new(store, row.TenantId,
            row.EventTime, row.EventId, row.UnitId, row.MergedFromUnitId, row.Seq, row.EventType,
            row.BizStep, row.Disposition, row.LocationId, row.LegalEntityId, row.ActorRef,
            row.DocumentType, row.DocumentId, row.DocumentLine, row.RecordedTime, row.PostingPeriod,
            row.IdempotencyKey, row.ReversesEventId, row.Attributes);
    }
}
