using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.ErpChange;
using Yojana.Integration.Verification;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Serialisation.Contracts;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ConnectorAcceptanceDatabaseTests(IntegrationHostFixture fixture)
{
    private const string Operator = "fictional-integration-operator";
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Run_snapshots_all_item_kinds_and_a_failed_item_keeps_the_run_failed()
    {
        fixture.Clock.UtcNow = At;
        var tenant = await fixture.NewTenantAsync("acclife");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), "CX02");
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<ConnectorAcceptanceService>();
        var run = await service.StartAsync(connection, new("Asterra pre-production"), Operator, default);
        Assert.Contains(run.Items, item => item.Kind == ConnectorAcceptanceItemKind.Fact);
        Assert.Equal(11, run.Items.Count(item => item.Kind == ConnectorAcceptanceItemKind.EdgeScenario));
        Assert.Contains(run.Items, item => item.Kind == ConnectorAcceptanceItemKind.OutboundCommand);
        var failed = run.Items.First(item => item.Result == ConnectorAcceptanceItemResult.Pending);
        run = await service.MarkAsync(connection, run.RunId, failed.ItemId,
            new(ConnectorAcceptanceItemResult.Failed, "The remote reply did not match.", "Run note 14"),
            Operator, default);
        var sticky = await Assert.ThrowsAsync<ConnectorAcceptanceRuleException>(() => service.MarkAsync(
            connection, run.RunId, failed.ItemId,
            new(ConnectorAcceptanceItemResult.Passed, null, "Retried in the same run"), Operator, default));
        Assert.Equal("acceptance_failed_item_is_final", sticky.Rule);
        run = await MarkPendingPassedAsync(service, run);
        run = await service.CompleteAsync(connection, run.RunId, Operator, default);
        Assert.Equal(ConnectorAcceptanceRunStatus.Failed, run.Status);
        Assert.False((await service.OverviewAsync(connection, default)).AcceptancePassed);

        var passing = await service.StartAsync(connection, new("Asterra corrected pre-production"), Operator, default);
        passing = await MarkPendingPassedAsync(service, passing);
        passing = await service.CompleteAsync(connection, passing.RunId, Operator, default);
        Assert.Equal(ConnectorAcceptanceRunStatus.Passed, passing.Status);
        Assert.True((await service.OverviewAsync(connection, default)).AcceptancePassed);
    }

    [DatabaseFact]
    public async Task Result_import_rejects_bad_shape_wrong_connector_bad_hash_and_replay()
    {
        fixture.Clock.UtcNow = At;
        var tenant = await fixture.NewTenantAsync("accimp");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid(), "CX02");
        using var scope = fixture.ScopeFor(tenant);
        var service = scope.ServiceProvider.GetRequiredService<ConnectorAcceptanceService>();
        var run = await service.StartAsync(connection, new("Asterra pre-production"), Operator, default);
        var badShape = await Assert.ThrowsAsync<ConnectorAcceptanceRuleException>(() => service.ImportAsync(
            connection, run.RunId, new(JsonSerializer.SerializeToElement(new { connector = "CX02" })),
            Operator, default));
        Assert.Equal("acceptance_import_shape_invalid", badShape.Rule);

        var result = new EdgeScenarioResult("duplicate-notification", "passed", null, At, At.AddMinutes(1));
        var wrongHash = EdgeResultFileHash.Compute("CX04", At, [result]);
        var wrongConnector = File("CX04", At, [result], wrongHash);
        var wrong = await Assert.ThrowsAsync<ConnectorAcceptanceRuleException>(() => service.ImportAsync(
            connection, run.RunId, new(wrongConnector), Operator, default));
        Assert.Equal("acceptance_import_connector_mismatch", wrong.Rule);

        var badHash = File("CX02", At, [result], new string('0', 64));
        var changed = await Assert.ThrowsAsync<ConnectorAcceptanceRuleException>(() => service.ImportAsync(
            connection, run.RunId, new(badHash), Operator, default));
        Assert.Equal("acceptance_import_hash_invalid", changed.Rule);

        var hash = EdgeResultFileHash.Compute("CX02", At, [result]);
        var valid = File("CX02", At, [result], hash);
        Assert.Equal(1, (await service.ImportAsync(connection, run.RunId, new(valid), Operator, default)).Imported);
        var replay = await Assert.ThrowsAsync<ConnectorAcceptanceRuleException>(() => service.ImportAsync(
            connection, run.RunId, new(valid), Operator, default));
        Assert.Equal("acceptance_import_replayed", replay.Rule);
    }

    [DatabaseFact]
    public async Task Acceptance_runs_are_tenant_isolated()
    {
        fixture.Clock.UtcNow = At;
        var first = await fixture.NewTenantAsync("acciso1");
        var second = await fixture.NewTenantAsync("acciso2");
        var sharedId = Guid.CreateVersion7();
        await AddConnectionWithIdAsync(first, sharedId);
        await AddConnectionWithIdAsync(second, sharedId);
        using (var scope = fixture.ScopeFor(first))
            await scope.ServiceProvider.GetRequiredService<ConnectorAcceptanceService>()
                .StartAsync(sharedId, new("Asterra first sandbox"), Operator, default);
        using (var scope = fixture.ScopeFor(second))
        {
            var view = await scope.ServiceProvider.GetRequiredService<ConnectorAcceptanceService>()
                .OverviewAsync(sharedId, default);
            Assert.Empty(view.Runs);
        }
    }

    [DatabaseFact]
    public async Task Cutover_gate_refuses_by_default_can_be_disabled_and_accepts_a_passing_run()
    {
        fixture.Clock.UtcNow = At;
        var tenant = await fixture.NewTenantAsync("accgate");
        var entity = Guid.NewGuid();
        var source = await fixture.AddConnectionAsync(tenant, entity, "CX01");
        var target = await fixture.AddConnectionAsync(tenant, entity, "CX02");
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var firstRun = ChangeRun(tenant, source, target);
        db.ErpChangeRuns.Add(firstRun);
        await db.SaveChangesAsync();
        var cutover = scope.ServiceProvider.GetRequiredService<ErpChangeCutoverService>();
        await PrepareCutoverAsync(db, cutover, firstRun.ErpChangeRunId, target);
        var refused = await Assert.ThrowsAsync<CommandRefusedException>(() => cutover.EnableTargetAsync(
            firstRun.ErpChangeRunId, Operator, default));
        Assert.Equal("acceptance_required_before_go_live", refused.Rule);

        var settings = scope.ServiceProvider.GetRequiredService<AcceptanceGateSettings>();
        var current = await settings.GetAsync(default);
        await settings.SetAsync(new(current.Connectors.Select(row => row.ConnectorCode == "CX02"
            ? row with { Required = false } : row).ToList(), current.SettingsVersion), default);
        Assert.NotNull((await cutover.EnableTargetAsync(firstRun.ErpChangeRunId, Operator, default)).CutoverAt);
        firstRun.Status = ErpChangeRunStatus.Completed;
        firstRun.ClosedAt = fixture.Clock.UtcNow;
        firstRun.ClosedBy = Operator;
        await db.SaveChangesAsync();

        var secondTarget = await fixture.AddConnectionAsync(tenant, entity, "CX02");
        var acceptance = scope.ServiceProvider.GetRequiredService<ConnectorAcceptanceService>();
        var acceptanceRun = await acceptance.StartAsync(secondTarget, new("Asterra second sandbox"), Operator, default);
        acceptanceRun = await MarkPendingPassedAsync(acceptance, acceptanceRun);
        await acceptance.CompleteAsync(secondTarget, acceptanceRun.RunId, Operator, default);
        current = await settings.GetAsync(default);
        await settings.SetAsync(new(current.Connectors.Select(row => row.ConnectorCode == "CX02"
            ? row with { Required = true } : row).ToList(), current.SettingsVersion), default);
        var secondRun = ChangeRun(tenant, source, secondTarget);
        db.ErpChangeRuns.Add(secondRun);
        await db.SaveChangesAsync();
        await PrepareCutoverAsync(db, cutover, secondRun.ErpChangeRunId, secondTarget);
        Assert.NotNull((await cutover.EnableTargetAsync(secondRun.ErpChangeRunId, Operator, default)).CutoverAt);
    }

    private static async Task<AcceptanceRunView> MarkPendingPassedAsync(ConnectorAcceptanceService service,
        AcceptanceRunView run)
    {
        foreach (var item in run.Items.Where(item => item.Result == ConnectorAcceptanceItemResult.Pending))
            run = await service.MarkAsync(run.ConnectionId, run.RunId, item.ItemId,
                new(ConnectorAcceptanceItemResult.Passed, null, "Checked in the customer sandbox."),
                Operator, default);
        return run;
    }

    private static JsonElement File(string connector, DateTime generatedAt,
        IReadOnlyList<EdgeScenarioResult> results, string hash) => JsonSerializer.SerializeToElement(
            new EdgeResultFile(connector, generatedAt, results, hash), IntegrationContracts.Json);

    private async Task AddConnectionWithIdAsync(TenantId tenant, Guid id)
    {
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        db.Connections.Add(new()
        {
            TenantId = tenant, ConnectionId = id, ConnectorCode = "CX02",
            DisplayLabel = "Fictional service connection", Status = ConnectionStatus.Active, CreatedAt = At,
        });
        db.ConnectionGatewaySettings.Add(new()
        {
            TenantId = tenant, ConnectionId = id, LegalEntityId = Guid.NewGuid(),
            OutboundMode = "reference_only", NativeWarrantyObjectMode = "frozen", OrderingWaitMinutes = 15,
        });
        await db.SaveChangesAsync();
    }

    private static ErpChangeRun ChangeRun(TenantId tenant, Guid source, Guid target) => new()
    {
        TenantId = tenant, ErpChangeRunId = Guid.CreateVersion7(), SourceConnectionId = source,
        TargetConnectionId = target, CurrentStep = ErpChangeStep.Cutover,
        Status = ErpChangeRunStatus.Active, StartedBy = Operator, StartedAt = At,
    };

    private static async Task PrepareCutoverAsync(IntegrationDbContext db, ErpChangeCutoverService cutover,
        Guid runId, Guid target)
    {
        await cutover.BeginAsync(runId, new("reference_only"), Operator, default);
        var required = await db.ConnectionCapabilities.AsNoTracking().Where(row => row.ConnectionId == target
                && row.SupportLevel != CapabilitySupport.Unsupported
                && new[] { "CreateCreditMemo", "PostWarrantyExpense", "CreateReplacementDelivery",
                    "ChangeStockStatusOrLocation", "CreateServiceOrder", "AttachDocument",
                    "PushNativeWarrantyObject" }.Contains(row.CanonicalMessage))
            .Select(row => row.CanonicalMessage).ToListAsync();
        foreach (var command in required)
            await cutover.RecordValidationAsync(runId,
                new(command, ErpChangeValidationMethod.TestCompany, "Simulator validation passed."),
                Operator, default);
    }
}
