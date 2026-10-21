using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Yojana.Platform.Contracts.Identifiers;
using Yojana.Platform.Contracts.Tenancy;
using Yojana.Platform.Data.Platform;
using Yojana.Platform.Data.Platform.Entities;
using Yojana.Platform.Tenancy;

namespace Yojana.Platform.DatabaseTests;

[Collection("platform host")]
public sealed class TenancyTests(PlatformHostFixture fixture)
{
    private IServiceScope ScopeFor(TenantId? tenant)
    {
        var scope = fixture.Services.CreateScope();
        if (tenant is { } id)
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextSetter>().Set(id);
        }

        return scope;
    }

    private async Task<TenantId> NewTenantAsync(string prefix)
    {
        using var scope = ScopeFor(null);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var id = new TenantId(Guid.NewGuid());
        db.Tenants.Add(new Tenant { Id = id, Code = (prefix + Guid.NewGuid().ToString("N"))[..20], Name = "test " + prefix, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return id;
    }

    [DatabaseFact]
    public async Task Every_tenant_scoped_table_is_under_the_platform_security_policy()
    {
        using var scope = ScopeFor(null);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var expected = db.Model.GetEntityTypes().Where(e => typeof(ITenantScoped).IsAssignableFrom(e.ClrType)).Select(e => e.GetTableName()!).ToHashSet();
        var covered = await db.Database.SqlQueryRaw<string>("""
            SELECT OBJECT_NAME(p.target_object_id) AS [Value]
            FROM sys.security_predicates p JOIN sys.security_policies s ON s.object_id = p.object_id
            WHERE s.name = 'tenant_policy_platform' AND p.predicate_type_desc = 'FILTER'
            """).ToListAsync();
        Assert.Equal(expected.OrderBy(x => x), covered.OrderBy(x => x));
        var blocks = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.security_predicates p JOIN sys.security_policies s ON s.object_id = p.object_id WHERE s.name = 'tenant_policy_platform' AND p.predicate_type_desc = 'BLOCK'").SingleAsync();
        Assert.Equal(expected.Count * 2, blocks);
    }

    [DatabaseFact]
    public async Task Rows_of_one_tenant_are_invisible_to_another_even_through_raw_sql()
    {
        var a = await NewTenantAsync("a");
        var b = await NewTenantAsync("b");
        using (var scope = ScopeFor(a))
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.SmokeLogs.Add(new SmokeLog { TenantId = a, Id = TimeOrderedId.New(), Source = "test", Note = "a-row", RecordedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        using (var scope = ScopeFor(b))
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Empty(await db.SmokeLogs.ToListAsync());
            var raw = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM platform.smoke_log WHERE note = 'a-row'").SingleAsync();
            Assert.Equal(0, raw);
            var ctx = await db.Database.SqlQueryRaw<Guid>("SELECT CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier) AS [Value]").SingleAsync();
            Assert.Equal(b.Value, ctx);
        }

        using (var scope = ScopeFor(null))
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            var raw = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM platform.smoke_log WHERE note = 'a-row'").SingleAsync();
            Assert.Equal(0, raw);
            Assert.Empty(await db.SmokeLogs.ToListAsync());
        }

        using (var scope = ScopeFor(a))
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            Assert.Single(await db.SmokeLogs.Where(s => s.Note == "a-row").ToListAsync());
        }
    }

    [DatabaseFact]
    public async Task Writing_a_row_for_another_tenant_is_blocked_by_the_database()
    {
        var a = await NewTenantAsync("a");
        var b = await NewTenantAsync("b");
        using var scope = ScopeFor(a);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.SmokeLogs.Add(new SmokeLog { TenantId = b, Id = TimeOrderedId.New(), Source = "test", Note = "wrong tenant", RecordedAt = DateTime.UtcNow });
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.IsType<SqlException>(ex.InnerException);
        Assert.Contains("block predicate", ex.InnerException!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [DatabaseFact]
    public async Task Session_context_is_reset_when_a_pooled_connection_changes_tenant()
    {
        var a = await NewTenantAsync("a");
        var b = await NewTenantAsync("b");
        for (var round = 0; round < 3; round++)
        {
            foreach (var tenant in new[] { a, b })
            {
                using var scope = ScopeFor(tenant);
                var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
                var ctx = await db.Database.SqlQueryRaw<Guid>("SELECT CAST(SESSION_CONTEXT(N'tenant_id') AS uniqueidentifier) AS [Value]").SingleAsync();
                Assert.Equal(tenant.Value, ctx);
                var readOnly = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.dm_exec_session_wait_stats WHERE 1 = 0").SingleAsync();
                Assert.Equal(0, readOnly);
            }
        }
    }

    [DatabaseFact]
    public async Task Database_conventions_are_in_place()
    {
        using var scope = ScopeFor(null);
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var rcsi = await db.Database.SqlQueryRaw<bool>("SELECT is_read_committed_snapshot_on AS [Value] FROM sys.databases WHERE database_id = DB_ID()").SingleAsync();
        Assert.True(rcsi, "READ_COMMITTED_SNAPSHOT must be on");
        var temporal = await db.Database.SqlQueryRaw<int>("SELECT CAST(temporal_type AS int) AS [Value] FROM sys.tables WHERE name = 'tenant_setting' AND schema_id = SCHEMA_ID('platform')").SingleAsync();
        Assert.Equal(2, temporal);
        var quartz = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE schema_id = SCHEMA_ID('quartz') AND name LIKE 'QRTZ%'").SingleAsync();
        Assert.True(quartz >= 11, $"expected the Quartz job-store tables in schema quartz, found {quartz}");
        var wolverine = await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM sys.tables WHERE schema_id = SCHEMA_ID('wolverine')").SingleAsync();
        Assert.True(wolverine >= 3, $"expected Wolverine envelope tables in schema wolverine, found {wolverine}");
        var columnType = await db.Database.SqlQueryRaw<string>("SELECT CONCAT(t.name, '(', c.scale, ')') AS [Value] FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id WHERE c.object_id = OBJECT_ID('platform.smoke_log') AND c.name = 'recorded_at'").SingleAsync();
        Assert.Equal("datetime2(3)", columnType);
        var securityTables = await db.Database.SqlQueryRaw<string>("""
            SELECT name AS [Value] FROM sys.tables
            WHERE schema_id = SCHEMA_ID('platform') AND name IN ('mail_message', 'password_reset_token', 'security_event', 'sign_in_throttle', 'user_authenticator', 'user_invitation', 'user_recovery_code')
            ORDER BY name
            """).ToListAsync();
        Assert.Equal(["mail_message", "password_reset_token", "security_event", "sign_in_throttle", "user_authenticator", "user_invitation", "user_recovery_code"], securityTables);
        var stampColumn = await db.Database.SqlQueryRaw<int>("""
            SELECT COUNT(*) AS [Value] FROM sys.columns
            WHERE object_id = OBJECT_ID('platform.app_user') AND name = 'security_stamp'
              AND is_nullable = 0 AND TYPE_NAME(user_type_id) = 'uniqueidentifier'
            """).SingleAsync();
        Assert.Equal(1, stampColumn);
        var passwordColumns = await db.Database.SqlQueryRaw<string>("""
            SELECT name AS [Value] FROM sys.columns
            WHERE object_id = OBJECT_ID('platform.app_user')
              AND name IN ('must_change_password', 'temporary_password_expires_at')
            ORDER BY name
            """).ToListAsync();
        Assert.Equal(["must_change_password", "temporary_password_expires_at"], passwordColumns);
        Assert.Empty(db.ValidateTenantKeyConventions());
    }
}
