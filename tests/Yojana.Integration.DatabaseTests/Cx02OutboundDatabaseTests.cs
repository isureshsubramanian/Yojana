using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Connectors.CX02.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.DatabaseTests.ConnectorEdges;
using Yojana.Integration.Gateway;
using Yojana.Integration.Outbox;
using Yojana.Serialisation.Contracts;
using Xunit;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class Cx02OutboundDatabaseTests(IntegrationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
    private const string Actor = "user:cx02-outbound-test";

    public static TheoryData<string> DocumentKinds => new()
    {
        "credit", "journal", "replacement", "stock", "adjustment", "service", "attachment",
    };

    [DocumentDatabaseTheory]
    [MemberData(nameof(DocumentKinds))]
    public async Task Every_declared_outbound_document_runs_through_the_outbox(string kind)
    {
        await using var harness = new Cx02ConnectorEdgeHarness();
        await harness.StartAsync(default);
        var connection = await harness.CreateConnectionAsync(new IntegrationConnectorEdgeHost(fixture), null,
            true, default);
        var command = Command(kind, "FIC-" + kind.ToUpperInvariant() + "-" + Guid.NewGuid().ToString("N"));

        var rowId = await StageAsync(connection, command);
        using var scope = fixture.ScopeFor(connection.TenantId);

        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection.ConnectionId, default)).Completed);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = await db.OutboundCommands.SingleAsync(value => value.OutboundCommandId == rowId);
        Assert.Equal(OutboundCommandStatus.Done, row.Status);
        Assert.Equal(OutboundConnectorOutcome.Created, row.Outcome);
        Assert.Single(await db.ExternalIdMaps.Where(value => value.ConnectionId == connection.ConnectionId
            && value.EntityType == "document" && value.ExternalId == row.ErpExternalId).ToListAsync());
    }

    [DatabaseFact]
    public async Task Throttled_storm_and_token_expiry_mid_post_recover_without_duplicate_documents()
    {
        await using var harness = new Cx02ConnectorEdgeHarness();
        await harness.StartAsync(default);
        var connection = await harness.CreateConnectionAsync(new IntegrationConnectorEdgeHost(fixture), null,
            true, default);
        harness.ScriptThrottleStorm(3);
        harness.ScriptTokenExpiryDuringPost();
        await StageAsync(connection, Command("replacement", "FIC-THROTTLE-TOKEN"));

        using var scope = fixture.ScopeFor(connection.TenantId);
        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection.ConnectionId, default)).Completed);
        Assert.Equal(1, (await harness.ReadRemoteAsync(connection, default)).CreatedDocuments);
        Assert.Equal(OutboundCommandStatus.Done,
            (await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().OutboundCommands.SingleAsync()).Status);
    }

    [DatabaseFact]
    public async Task Attachment_over_the_tenant_limit_is_parked_before_remote_upload()
    {
        await using var harness = new Cx02ConnectorEdgeHarness();
        await harness.StartAsync(default);
        var connection = await harness.CreateConnectionAsync(new IntegrationConnectorEdgeHost(fixture), null,
            true, default);
        using var scope = fixture.ScopeFor(connection.TenantId);
        var store = scope.ServiceProvider.GetRequiredService<Cx02Settings>();
        var configured = await store.GetAsync(connection.ConnectionId, default);
        await store.SetAsync(connection.ConnectionId, configured with
        {
            Outbound = configured.Outbound! with { AttachmentSizeLimit = 4 },
        }, default);
        var rowId = await StageAsync(connection, Command("attachment", "FIC-ATTACH-LARGE"));

        Assert.Equal(1, (await scope.ServiceProvider.GetRequiredService<OutboundRelay>()
            .RelayConnectionAsync(connection.ConnectionId, default)).Parked);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var row = await db.OutboundCommands.SingleAsync(value => value.OutboundCommandId == rowId);
        Assert.Equal(OutboundCommandStatus.Parked, row.Status);
        Assert.Equal("missing_source_field",
            (await db.DeadLetterItems.SingleAsync(value => value.SourceId == rowId)).ReasonCode);
        Assert.Contains("size cap", row.LastFailure, StringComparison.OrdinalIgnoreCase);
    }

    [DatabaseFact]
    public async Task Identical_logical_references_are_isolated_by_tenant_and_remote_company()
    {
        await using var firstHarness = new Cx02ConnectorEdgeHarness();
        await using var secondHarness = new Cx02ConnectorEdgeHarness();
        await firstHarness.StartAsync(default);
        await secondHarness.StartAsync(default);
        var host = new IntegrationConnectorEdgeHost(fixture);
        var first = await firstHarness.CreateConnectionAsync(host, null, true, default);
        var second = await secondHarness.CreateConnectionAsync(host, null, true, default);
        const string logicalReference = "FIC-TENANT-ISOLATION";
        await StageAsync(first, Command("replacement", logicalReference));
        await StageAsync(second, Command("replacement", logicalReference));

        using (var firstScope = fixture.ScopeFor(first.TenantId))
            Assert.Equal(1, (await firstScope.ServiceProvider.GetRequiredService<OutboundRelay>()
                .RelayConnectionAsync(first.ConnectionId, default)).Completed);
        using (var secondScope = fixture.ScopeFor(second.TenantId))
            Assert.Equal(1, (await secondScope.ServiceProvider.GetRequiredService<OutboundRelay>()
                .RelayConnectionAsync(second.ConnectionId, default)).Completed);

        Assert.Equal(1, (await firstHarness.ReadRemoteAsync(first, default)).CreatedDocuments);
        Assert.Equal(1, (await secondHarness.ReadRemoteAsync(second, default)).CreatedDocuments);
        using var verifyFirst = fixture.ScopeFor(first.TenantId);
        using var verifySecond = fixture.ScopeFor(second.TenantId);
        Assert.Single(await verifyFirst.ServiceProvider.GetRequiredService<IntegrationDbContext>()
            .ExternalIdMaps.Where(value => value.ConnectionId == first.ConnectionId
                && value.EntityType == "document").ToListAsync());
        Assert.Single(await verifySecond.ServiceProvider.GetRequiredService<IntegrationDbContext>()
            .ExternalIdMaps.Where(value => value.ConnectionId == second.ConnectionId
                && value.EntityType == "document").ToListAsync());
    }

    private async Task<Guid> StageAsync(ConnectorEdgeConnection connection, IOutboundCommand command)
    {
        using var scope = fixture.ScopeFor(connection.TenantId);
        var header = new PublishedEventHeader("CX02.Outbound.Test", "1.0", Guid.CreateVersion7(),
            Guid.CreateVersion7(), At, At, 1, Guid.NewGuid(), Actor, null);
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OutboundCommandStager>()
            .StageAsync(header, command, default));
        return await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().OutboundCommands
            .Where(value => value.ConnectionId == connection.ConnectionId
                && value.CommandType == command.GetType().Name
                && value.ModuleDocumentRef == command.ModuleDocumentRef)
            .Select(value => value.OutboundCommandId).SingleAsync();
    }

    private static IOutboundCommand Command(string kind, string reference) => kind switch
    {
        "credit" => new CreateCreditMemo(Guid.CreateVersion7(), "document/" + Guid.NewGuid().ToString("N"),
            reference, "FIC-CUSTOMER", [new(null, "Fictional rack server", 1, new(125, "GBP"),
                "HW-SERVER-100")], null, "FIC-INVOICE"),
        "journal" => new PostWarrantyExpense(Guid.CreateVersion7(), "document/" + Guid.NewGuid().ToString("N"),
            reference, [new("warranty", 25, 0, "GBP"), new("reserve", 0, 25, "GBP")], "2026-09", false),
        "replacement" => new CreateReplacementDelivery(Guid.CreateVersion7(),
            "document/" + Guid.NewGuid().ToString("N"), reference, "FIC-CUSTOMER", "FIC-SHIP",
            new(Cx02FixtureFacts.UnitId, "FIC-SERIAL-001"), OpenEnum.Known("zero"), "FIC-INVOICE", "warranty"),
        "stock" => new ChangeStockStatusOrLocation(Guid.CreateVersion7(),
            "document/" + Guid.NewGuid().ToString("N"), reference,
            new(Cx02FixtureFacts.UnitId, "FIC-SERIAL-001"), "store", "repair", "inspection"),
        "adjustment" => new ChangeStockStatusOrLocation(Guid.CreateVersion7(),
            "document/" + Guid.NewGuid().ToString("N"), reference,
            new(Cx02FixtureFacts.UnitId, "FIC-SERIAL-001"), null, "repair", "inspection"),
        "service" => new CreateServiceOrder(Guid.CreateVersion7(),
            "document/" + Guid.NewGuid().ToString("N"), reference, "FIC-CUSTOMER",
            new(Cx02FixtureFacts.UnitId, "FIC-SERIAL-001"), "covered", "Replace server power supply"),
        "attachment" => new AttachDocument(Guid.CreateVersion7(),
            "document/" + Guid.NewGuid().ToString("N"), reference,
            "salesInvoices:" + Guid.NewGuid(), "service-note.pdf", "fixture:pdf", null),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
