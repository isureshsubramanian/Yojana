using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Presence;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Contracts.Time;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Presence;

namespace Yojana.Serialisation.DatabaseTests;

[Collection("serialisation host")]
public sealed class PresenceTests(SerialisationHostFixture fixture)
{
    private static readonly DateTime At = new(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc);

    [DatabaseFact]
    public async Task Heartbeat_upserts_one_row_changes_mode_since_and_returns_only_other_users()
    {
        var tenant = await fixture.NewTenantAsync("presence-upsert");
        var viewer = Guid.NewGuid();
        var editor = Guid.NewGuid();
        var clock = new TestClock(At);

        Assert.Empty(await HeartbeatAsync(tenant, clock, viewer, "rma", "RMA-1", PresenceMode.Viewing));
        clock.UtcNow = At.AddSeconds(20);
        Assert.Empty(await HeartbeatAsync(tenant, clock, viewer, "rma", "RMA-1", PresenceMode.Editing));
        var others = await HeartbeatAsync(tenant, clock, editor, "rma", "RMA-1", PresenceMode.Viewing);

        var present = Assert.Single(others);
        Assert.Equal((viewer, PresenceMode.Editing, At.AddSeconds(20)), (present.UserId, present.Mode, present.Since));
        using var scope = fixture.ScopeFor(tenant);
        var rows = await scope.ServiceProvider.GetRequiredService<PlatformDbContext>().DocumentPresence
            .Where(p => p.DocumentType == "rma" && p.DocumentId == "RMA-1").ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(At.AddSeconds(20), rows.Single(p => p.UserId == viewer).LastHeartbeat);
    }

    [DatabaseFact]
    public async Task Presence_older_than_sixty_seconds_is_ignored_using_the_test_clock()
    {
        var tenant = await fixture.NewTenantAsync("presence-expiry");
        var clock = new TestClock(At);
        await HeartbeatAsync(tenant, clock, Guid.NewGuid(), "claim", "CLM-1", PresenceMode.Editing);

        clock.UtcNow = At.AddSeconds(61);
        var others = await HeartbeatAsync(tenant, clock, Guid.NewGuid(), "claim", "CLM-1", PresenceMode.Viewing);

        Assert.Empty(others);
    }

    [DatabaseFact]
    public async Task Presence_is_tenant_isolated_for_the_same_document_key()
    {
        var firstTenant = await fixture.NewTenantAsync("presence-a");
        var secondTenant = await fixture.NewTenantAsync("presence-b");
        var clock = new TestClock(At);
        await HeartbeatAsync(firstTenant, clock, Guid.NewGuid(), "campaign", "shared-id", PresenceMode.Editing);

        var others = await HeartbeatAsync(secondTenant, clock, Guid.NewGuid(), "campaign", "shared-id", PresenceMode.Viewing);

        Assert.Empty(others);
        using var firstScope = fixture.ScopeFor(firstTenant);
        using var secondScope = fixture.ScopeFor(secondTenant);
        Assert.Single(await firstScope.ServiceProvider.GetRequiredService<PlatformDbContext>().DocumentPresence.ToListAsync());
        Assert.Single(await secondScope.ServiceProvider.GetRequiredService<PlatformDbContext>().DocumentPresence.ToListAsync());
    }

    [DatabaseFact]
    public async Task Leave_removes_only_the_calling_users_document_presence()
    {
        var tenant = await fixture.NewTenantAsync("presence-leave");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var clock = new TestClock(At);
        await HeartbeatAsync(tenant, clock, first, "policy", "POL-1", PresenceMode.Editing);
        await HeartbeatAsync(tenant, clock, second, "policy", "POL-1", PresenceMode.Viewing);

        using (var scope = fixture.ScopeFor(tenant))
        {
            var service = new PresenceService(scope.ServiceProvider.GetRequiredService<PlatformDbContext>(),
                scope.ServiceProvider.GetRequiredService<ITenantContext>(), clock);
            await service.LeaveAsync(first, "policy", "POL-1", CancellationToken.None);
        }

        using var verify = fixture.ScopeFor(tenant);
        var remaining = Assert.Single(await verify.ServiceProvider.GetRequiredService<PlatformDbContext>()
            .DocumentPresence.Where(p => p.DocumentType == "policy" && p.DocumentId == "POL-1").ToListAsync());
        Assert.Equal(second, remaining.UserId);
    }

    [DatabaseFact]
    public async Task Platform_presence_table_is_tenant_keyed_indexed_and_under_rls()
    {
        using var scope = fixture.ScopeFor(null);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var tenantTables = db.Model.GetEntityTypes().Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType)).ToList();
        Assert.Equal(3, tenantTables.Count);
        Assert.Empty(db.ValidateTenantKeyConventions());
        var covered = await db.Database.SqlQueryRaw<string>("""
            SELECT OBJECT_NAME(p.target_object_id) AS [Value]
            FROM sys.security_predicates p JOIN sys.security_policies s ON s.object_id = p.object_id
            WHERE s.name = 'tenant_policy_platform' AND p.predicate_type_desc = 'FILTER'
            """).ToListAsync();
        Assert.Contains("document_presence", covered);
        var indexes = db.Model.FindEntityType(typeof(Yojana.Platform.Data.Platform.Entities.DocumentPresence))!
            .GetIndexes().Select(index => index.Properties.Select(p => p.Name).ToArray()).ToList();
        Assert.Contains(indexes, properties => properties.SequenceEqual(
            ["TenantId", "DocumentType", "DocumentId", "LastHeartbeat"]));
        Assert.Contains(indexes, properties => properties.SequenceEqual(["TenantId", "LastHeartbeat"]));
    }

    private async Task<IReadOnlyList<PresenceEntry>> HeartbeatAsync(TenantId tenant, TestClock clock, Guid userId,
        string documentType, string documentId, string mode)
    {
        using var scope = fixture.ScopeFor(tenant);
        var service = new PresenceService(scope.ServiceProvider.GetRequiredService<PlatformDbContext>(),
            scope.ServiceProvider.GetRequiredService<ITenantContext>(), clock);
        return await service.HeartbeatAsync(userId, documentType, documentId, mode, CancellationToken.None);
    }

    private sealed class TestClock(DateTime now) : IClock
    {
        public DateTime UtcNow { get; set; } = now;
    }
}
