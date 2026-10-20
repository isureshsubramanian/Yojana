using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Backfills;
using Yojana.Integration.Connectors.CX01;
using Yojana.Integration.Relays;
using Yojana.Integration.Connections;
using Yojana.Integration.Data;
using Yojana.Integration.Data.Entities;
using Yojana.Integration.Reconciliation;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class ScreenReadModelTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Reconciliation_result_pages_are_bounded_disjoint_filtered_and_tenant_scoped()
    {
        var tenant = await fixture.NewTenantAsync("t21p");
        var other = await fixture.NewTenantAsync("t21o");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        var run = Guid.NewGuid();
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.ReconciliationRuns.Add(new ReconciliationRun { TenantId = tenant, ReconciliationRunId = run, ConnectionId = connection,
                RangeStart = new(2026, 9, 1), RangeEnd = new(2026, 9, 1), Trigger = "scheduled", Status = "clean", StartedAt = fixture.Clock.UtcNow });
            for (var index = 0; index < 15; index++)
            {
                db.ReconciliationEntityDayResults.Add(new ReconciliationEntityDayResult
                {
                    TenantId = tenant, ReconciliationEntityDayResultId = Guid.NewGuid(), ReconciliationRunId = run,
                    ConnectionId = connection, Entity = "deliveries", BusinessDate = new(2026, 9, 1),
                    SourceCount = index, ModuleCount = index, SourceKeyFieldHash = "same", ModuleKeyFieldHash = "same", Status = "clean",
                });
                db.ReconciliationLocationResults.Add(new ReconciliationLocationResult
                {
                    TenantId = tenant, ReconciliationLocationResultId = Guid.NewGuid(), ReconciliationRunId = run,
                    ConnectionId = connection, ItemCode = "FIC-NX-01", ItemName = "Fictional network switch",
                    LocationCode = "FIC-WH-01", LocationName = "Fictional warehouse", SourceOnHand = 2,
                    ModuleUnitCount = index % 2 == 0 ? 1 : 2, Status = index % 2 == 0 ? "mismatch" : "clean",
                });
            }
            await db.SaveChangesAsync();
            var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
            var first = await service.EntityDaysAsync(run, null, 12, CancellationToken.None);
            var second = await service.EntityDaysAsync(run, first.NextCursor, 12, CancellationToken.None);
            Assert.Equal(12, first.Items.Count);
            Assert.Equal(3, second.Items.Count);
            Assert.Null(second.NextCursor);
            Assert.Empty(first.Items.Select(x => x.ResultId).Intersect(second.Items.Select(x => x.ResultId)));
            var differences = await service.LocationsAsync(run, null, 1000, true, CancellationToken.None);
            Assert.Equal(8, differences.Items.Count);
            Assert.All(differences.Items, x => Assert.NotEqual(x.SourceOnHand, x.ModuleUnitCount));
            Assert.Equal(15, (await service.LocationsAsync(run, null, 100, false, CancellationToken.None)).Items.Count);
            await Assert.ThrowsAsync<ReconciliationRuleException>(() => service.EntityDaysAsync(run, "invalid", 12, CancellationToken.None));
        }
        using (var scope = fixture.ScopeFor(other))
        {
            var service = scope.ServiceProvider.GetRequiredService<ReconciliationService>();
            Assert.Empty((await service.EntityDaysAsync(run, null, 12, CancellationToken.None)).Items);
            Assert.Empty((await service.LocationsAsync(run, null, 12, false, CancellationToken.None)).Items);
        }
    }

    [DatabaseFact]
    public async Task Registration_catalogue_returns_tenant_labels_and_adapter_form_without_credentials()
    {
        var tenant = await fixture.NewTenantAsync("t21c");
        var other = await fixture.NewTenantAsync("t21co");
        using (var scope = fixture.ScopeFor(tenant))
        {
            var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
            db.ConnectorCatalogue.Add(new ConnectorCatalogueEntry { TenantId = tenant, ConnectorCode = "CX01", DisplayName = "Fictional hardware ERP", CustomerCostDisclosure = "Customer licence applies", Enabled = true, UpdatedAt = fixture.Clock.UtcNow });
            await db.SaveChangesAsync();
            var result = await scope.ServiceProvider.GetRequiredService<ConnectionQueries>().RegistrationConnectorsAsync(null, 12, CancellationToken.None);
            var connector = Assert.Single(result.Items);
            Assert.Equal("Fictional hardware ERP", connector.DisplayName);
            Assert.Contains(connector.Form.SecretFields, x => x.Type == "password");
            Assert.All(connector.Form.SecretFields, x => Assert.Null(x.DefaultValue));
            Assert.DoesNotContain(connector.Form.Settings, x => x.Type == "password");
        }
        using (var scope = fixture.ScopeFor(other))
            Assert.Empty((await scope.ServiceProvider.GetRequiredService<ConnectionQueries>().RegistrationConnectorsAsync(null, 12, CancellationToken.None)).Items);
    }

    [DatabaseFact]
    public async Task Paused_connections_keep_relay_visibility_but_cannot_register_an_agent()
    {
        var tenant = await fixture.NewTenantAsync("t21r");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        (await db.Connections.SingleAsync(x => x.ConnectionId == connection)).Status = ConnectionStatus.Paused;
        db.SiteRelays.Add(new SiteRelay { TenantId = tenant, RelayId = Guid.NewGuid(), ConnectionId = connection,
            DisplayName = "Fictional warehouse relay", Status = "revoked", CreatedAt = fixture.Clock.UtcNow });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<Cx01Settings>().SetAsync(connection,
            new(new Uri("https://example.invalid/"), "FICTIONAL", "1000340", Transport: "site_relay"), CancellationToken.None);
        var registry = scope.ServiceProvider.GetRequiredService<RelayRegistry>();
        var result = await registry.ListAsync(connection, null, 12, CancellationToken.None);
        Assert.Equal("Fictional warehouse relay", Assert.Single(result.Items).DisplayName);
        await Assert.ThrowsAsync<ConnectionRuleException>(() => registry.RegisterAsync(connection,
            new("Another relay", new Uri("https://example.invalid/")), "system:test", CancellationToken.None));
    }

    [DatabaseFact]
    public async Task Backfill_detail_returns_its_saved_cursor_without_loading_unrelated_jobs()
    {
        var tenant = await fixture.NewTenantAsync("t21b");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        var id = Guid.NewGuid();
        db.BackfillCursors.Add(new BackfillCursor { TenantId = tenant, BackfillCursorId = id, ConnectionId = connection, Entity = "deliveries", RangeStart = new(2026, 9, 1), RangeEnd = new(2026, 9, 2), Cursor = "saved-document-42", PageSize = 25, Status = "paused", RequestedBy = "system:test", LastChangedBy = "system:test", CreatedAt = fixture.Clock.UtcNow });
        await db.SaveChangesAsync();
        var detail = await scope.ServiceProvider.GetRequiredService<BackfillService>().DetailAsync(id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Equal("saved-document-42", detail.SavedCursor);
        Assert.Equal(25, detail.PageSize);
        Assert.Equal("Fictional ERP", detail.ConnectionName);
    }
}
