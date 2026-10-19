using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Integration.Data;
using Yojana.Integration.Gateway;
using Yojana.Integration.Security.Audit;
using Yojana.Integration.Security.Cryptography;
using Yojana.Integration.Security.Workers;

namespace Yojana.Integration.DatabaseTests;

[Collection("integration host")]
public sealed class SecurityAndIdentityPersistenceTests(IntegrationHostFixture fixture)
{
    [DatabaseFact]
    public async Task Ef_security_stores_replace_active_versions_and_preserve_the_audit_chain()
    {
        var tenant = await fixture.NewTenantAsync("sec");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var keys = scope.ServiceProvider.GetRequiredService<TenantDataKeyService>();
        var first = await keys.GetOrCreateAsync(tenant, CancellationToken.None);
        var second = await keys.RotateAsync(tenant, null, CancellationToken.None);
        Assert.Equal(first.Version + 1, second.Version);

        var protector = scope.ServiceProvider.GetRequiredService<ConnectionCredentialProtector>();
        var store = scope.ServiceProvider.GetRequiredService<IConnectionCredentialStore>();
        var one = await protector.EncryptAsync(tenant, connection, "api", "first"u8.ToArray(), null,
            fixture.Clock.UtcNow.AddDays(30), CancellationToken.None);
        await store.SaveAsync(one, CancellationToken.None);
        fixture.Clock.UtcNow = fixture.Clock.UtcNow.AddSeconds(1);
        var two = await protector.EncryptAsync(tenant, connection, "api", "second"u8.ToArray(), null,
            fixture.Clock.UtcNow.AddDays(30), CancellationToken.None);
        await store.SaveAsync(two, CancellationToken.None);
        Assert.Equal(2, (await store.ListVersionsAsync(tenant, connection, "api", CancellationToken.None)).Count);
        Assert.Equal(two.CreatedAt, (await store.GetCurrentAsync(tenant, connection, "api", CancellationToken.None))!.CreatedAt);

        var audit = scope.ServiceProvider.GetRequiredService<IConnectionAuditSink>();
        await audit.AppendAsync(new(tenant, Guid.NewGuid(), connection, ConnectionAuditEventType.ConnectionRegistered,
            "test-actor", fixture.Clock.UtcNow, "{}"), CancellationToken.None);
        await audit.AppendAsync(new(tenant, Guid.NewGuid(), connection, ConnectionAuditEventType.CredentialSet,
            "test-actor", fixture.Clock.UtcNow.AddSeconds(1), "{}"), CancellationToken.None);
        var events = new List<ConnectionAuditEvent>();
        await foreach (var item in audit.ReadAsync(tenant, DateOnly.FromDateTime(fixture.Clock.UtcNow.AddDays(-1)),
                           DateOnly.FromDateTime(fixture.Clock.UtcNow.AddDays(1)), CancellationToken.None)) events.Add(item);
        Assert.True(ConnectionAuditChain.Verify(events).IsValid);
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(1, await db.TenantDataKeys.CountAsync(x => x.IsActive));
        await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => db.ConnectionEvents.ExecuteDeleteAsync());
    }

    [DatabaseFact]
    public async Task External_identity_and_catalogue_are_separate_tenant_data()
    {
        var tenant = await fixture.NewTenantAsync("extmap");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var catalogue = scope.ServiceProvider.GetRequiredService<ConnectorCatalogue>();
        Assert.Equal(18, await catalogue.SeedGenericEntriesAsync(CancellationToken.None));
        var reference = scope.ServiceProvider.GetRequiredService<GatewayReferenceStore>();
        var moduleRef = await reference.ResolveAsync(connection, "item", "ITEM-EXT", false, CancellationToken.None);
        await scope.ServiceProvider.GetRequiredService<IntegrationDbContext>().SaveChangesAsync();
        var db = scope.ServiceProvider.GetRequiredService<IntegrationDbContext>();
        Assert.Equal(moduleRef, await db.ExternalIdMaps.Where(x => x.ExternalId == "ITEM-EXT")
            .Select(x => x.ModuleRef).SingleAsync());
        Assert.Empty(await db.ReferenceProjections.Where(x => x.ModuleId == moduleRef).ToListAsync());
        var checklist = await scope.ServiceProvider.GetRequiredService<ConnectorOnboardingChecklistGenerator>()
            .GenerateAsync(connection, CancellationToken.None);
        Assert.Equal("Connector CX01", checklist.DisplayName);
        Assert.Contains(checklist.Items, x => x.Code == "costs");
    }

    [DatabaseFact]
    public async Task Daily_budget_compare_and_swap_allows_one_writer()
    {
        var tenant = await fixture.NewTenantAsync("budget");
        var connection = await fixture.AddConnectionAsync(tenant, Guid.NewGuid());
        using var scope = fixture.ScopeFor(tenant);
        var profiles = scope.ServiceProvider.GetRequiredService<ConnectionThrottleProfiles>();
        var key = new ConnectionWorkerKey(tenant, connection);
        await profiles.OnboardAsync(key, new("CX01", new(10, 100, 1_000, 10, 2,
            TimeSpan.FromMinutes(1))), CancellationToken.None);
        var store = scope.ServiceProvider.GetRequiredService<IConnectionDailyBudgetStore>();
        var date = DateOnly.FromDateTime(fixture.Clock.UtcNow);
        var current = await store.LoadAsync(key, date, CancellationToken.None);
        Assert.NotNull(current);
        var next = current! with { UsedCalls = 1, Revision = current.Revision + 1 };
        Assert.True(await store.TrySaveAsync(next, current.Revision, CancellationToken.None));
        Assert.False(await store.TrySaveAsync(next with { UsedCalls = 2 }, current.Revision, CancellationToken.None));
    }
}
