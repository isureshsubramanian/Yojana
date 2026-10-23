using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Connections;
using Yojana.Integration.Connectors;
using Yojana.Integration.Connectors.CX02;
using Yojana.Integration.Connectors.CX02.Testing;
using Yojana.Integration.Contracts;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ExtensionStatusDatabaseTests(IntegrationHostFixture fixture) : IAsyncLifetime
{
    private const string Actor = "fictional-extension-operator";
    private DateTime clockBeforeTest;

    public Task InitializeAsync()
    {
        clockBeforeTest = fixture.Clock.UtcNow;
        fixture.Clock.UtcNow = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        fixture.Clock.UtcNow = clockBeforeTest;
        return Task.CompletedTask;
    }

    [DatabaseFact]
    public async Task Check_records_all_reachable_pages_and_reads_only_figures_with_durable_sources()
    {
        await using var simulator = new Cx02Simulator();
        var setup = await SetupAsync(simulator, outboundEnabled: true);
        using var scope = fixture.ScopeFor(setup.Tenant);
        var service = scope.ServiceProvider.GetRequiredService<ExtensionStatusService>();

        var view = await service.CheckAsync(setup.Connection, Actor, default);

        Assert.Equal(12, view.Pages.Count);
        Assert.Equal(12, view.UsedPages);
        Assert.Equal(12, view.ReachablePages);
        Assert.All(view.Pages, page => Assert.Equal(ConnectionExtensionPageStatus.Reachable, page.Status));
        Assert.Equal("1.2", view.InstalledVersion);
        Assert.Equal("1.0.0.0", view.ShippedVersion);
        Assert.True(view.IsNewerThanOnePointZero);
        Assert.Equal(1, view.Pages.Single(page => page.Code == "trackedItemLedgerEntries").RowsReadToday);
        Assert.Null(view.Pages.Single(page => page.Code == "itemEntryRelations").RowsReadToday);
        Assert.Equal(900, view.Pages.Single(page => page.Code == "trackedItemLedgerEntries")
            .FallbackPollIntervalSeconds);
        Assert.True(view.Access.ApplicationRegistrationPerTenant);
        Assert.True(view.Access.PermissionSetWithoutFullAccessRole);
        Assert.Null(view.Access.FullAccessRoleAssigned);
        Assert.Equal(new DateTime(2027, 8, 14, 0, 0, 0, DateTimeKind.Utc), view.Access.CredentialExpiresAt);
        Assert.Equal(new DateTime(2027, 7, 31, 0, 0, 0, DateTimeKind.Utc), view.Access.CredentialReminderAt);
        Assert.Equal(2, view.Notifications.LiveCount);
        Assert.Equal(new DateTime(2026, 9, 3, 21, 0, 0, DateTimeKind.Utc), view.Notifications.NextRenewalAt);
        Assert.Equal(fixture.Clock.UtcNow, view.LastCheckedAt);

        var audit = await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().ConnectionEvents
            .SingleAsync(row => row.ConnectionId == setup.Connection && row.EventType == "ExtensionChecked");
        Assert.Equal(Actor, audit.ActorRef);
        var details = JsonSerializer.Deserialize<ConnectionExtensionCheckAudit>(audit.DetailsJson,
            IntegrationContracts.Json)!;
        Assert.Equal(12, details.Pages.Count);
        Assert.All(details.Pages, page => Assert.Equal(ConnectionExtensionPageStatus.Reachable, page.Status));
    }

    [DatabaseFact]
    public async Task Check_records_one_failed_page_without_hiding_the_other_results()
    {
        await using var simulator = new Cx02Simulator(options: new()
        {
            UnreachableResources = new HashSet<string>(StringComparer.Ordinal) { "trackingSpecifications" },
        });
        var setup = await SetupAsync(simulator, outboundEnabled: true);
        using var scope = fixture.ScopeFor(setup.Tenant);

        var view = await scope.ServiceProvider.GetRequiredService<ExtensionStatusService>()
            .CheckAsync(setup.Connection, Actor, default);

        Assert.Equal(11, view.ReachablePages);
        var failed = view.Pages.Single(page => page.Code == "trackingSpecifications");
        Assert.Equal(ConnectionExtensionPageStatus.NotReachable, failed.Status);
        Assert.Contains("BadRequest_NotFound", failed.Reason, StringComparison.Ordinal);
        Assert.All(view.Pages.Where(page => page.Code != failed.Code), page =>
            Assert.Equal(ConnectionExtensionPageStatus.Reachable, page.Status));
    }

    [DatabaseFact]
    public async Task Outbound_page_is_not_used_until_cutover_and_other_tenants_cannot_read_the_result()
    {
        await using var simulator = new Cx02Simulator();
        var setup = await SetupAsync(simulator, outboundEnabled: false);
        using (var scope = fixture.ScopeFor(setup.Tenant))
        {
            var view = await scope.ServiceProvider.GetRequiredService<ExtensionStatusService>()
                .CheckAsync(setup.Connection, Actor, default);
            Assert.Equal(11, view.UsedPages);
            Assert.Equal(11, view.ReachablePages);
            var outbound = view.Pages.Single(page => page.Code == "trackedDocumentRequests");
            Assert.Equal(ConnectionExtensionPageStatus.NotUsedYet, outbound.Status);
            Assert.Equal("Outbound is off until cutover.", outbound.Reason);
        }

        var otherTenant = await fixture.NewTenantAsync("ext-other");
        using var otherScope = fixture.ScopeFor(otherTenant);
        var refused = await Assert.ThrowsAsync<ExtensionStatusRuleException>(() => otherScope.ServiceProvider
            .GetRequiredService<ExtensionStatusService>().ReadAsync(setup.Connection, default));
        Assert.Equal("connection_not_found", refused.Rule);
        Assert.Empty(await otherScope.ServiceProvider.GetRequiredService<IntegrationDbContext>()
            .ConnectionEvents.IgnoreQueryFilters().ToListAsync());
    }

    private async Task<(TenantId Tenant, Guid Connection)> SetupAsync(Cx02Simulator simulator,
        bool outboundEnabled)
    {
        var tenant = await fixture.NewTenantAsync("extension");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.CreateVersion7(),
            Cx02Capabilities.ConnectorCode);
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var gateway = await db.ConnectionGatewaySettings.SingleAsync(row => row.ConnectionId == connection);
        gateway.OutboundMode = outboundEnabled ? "documents" : "none";
        gateway.ReadOnly = !outboundEnabled;
        db.ConnectionPollWatermarks.AddRange(
            Poll(tenant, connection, "tracked_ledger_entries"),
            Poll(tenant, connection, "transfer_shipments"),
            Poll(tenant, connection, "transfer_receipts"),
            Poll(tenant, connection, "return_receipts"));
        db.InboundMessages.Add(new()
        {
            TenantId = tenant,
            InboundMessageId = Guid.CreateVersion7(),
            MessageId = Guid.CreateVersion7(),
            ConnectionId = connection,
            ConnectorCode = Cx02Capabilities.ConnectorCode,
            ErpEntity = "trackedItemLedgerEntries",
            ErpExternalId = "FICTIONAL-LEDGER-100",
            ErpChangeMarker = "2026-09-01T08:30:00Z",
            MarkerKind = "timestamp",
            ContractVersion = IntegrationContracts.CurrentVersion,
            MessageType = "InventoryAdjusted",
            RawPayload = "{}",
            CanonicalMessage = "{}",
            OccurredAt = fixture.Clock.UtcNow.AddMinutes(-30),
            ObservedAt = fixture.Clock.UtcNow.AddMinutes(-20),
            AcceptedAt = fixture.Clock.UtcNow.AddMinutes(-20),
            IsLatest = true,
            TranslationStatus = InboundTranslationStatus.Completed,
            TranslatedAt = fixture.Clock.UtcNow.AddMinutes(-19),
        });
        await db.SaveChangesAsync();

        var settings = scope.ServiceProvider.GetRequiredService<Cx02Settings>();
        await settings.SetAsync(connection, new(simulator.BaseUri, Cx02Simulator.DefaultTenant,
            Cx02Simulator.DefaultTenant, Cx02Simulator.DefaultEnvironment, Cx02Simulator.CompanyId,
            "1.2", TokenUri: simulator.TokenUri(), PollPageSize: 2), default);
        var notificationExpiry = fixture.Clock.UtcNow.AddDays(3);
        await settings.SetNotificationsAsync(connection, new([
            new(Guid.CreateVersion7(), $"/api/yojana/integration/v1.0/companies({Cx02Simulator.CompanyId:D})/trackedItemLedgerEntries",
                true, "W/\"1\"", notificationExpiry),
            new(Guid.CreateVersion7(), $"/api/yojana/integration/v1.0/companies({Cx02Simulator.CompanyId:D})/postedTransferShipments",
                true, "W/\"1\"", notificationExpiry.AddHours(1)),
        ]), default);
        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var credentials = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        await credentials.SaveAsync(await protector.EncryptAsync(tenant, connection, Cx02Client.CredentialPurpose,
            JsonSerializer.SerializeToUtf8Bytes(new Cx02ClientCredential("fictional-client",
                simulator.Options.ClientSecret, "fictional-client-state"), IntegrationContracts.Json),
            new DateTime(2027, 8, 14, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2027, 7, 15, 0, 0, 0, DateTimeKind.Utc), default), default);
        return (tenant, connection);
    }

    private ConnectionPollWatermark Poll(TenantId tenant, Guid connection, string entity) => new()
    {
        TenantId = tenant,
        ConnectionId = connection,
        EntityType = entity,
        CanonicalMessage = "InventoryAdjusted",
        IntervalSeconds = 900,
        OverlapSeconds = 300,
        NextPollAt = fixture.Clock.UtcNow,
    };
}
