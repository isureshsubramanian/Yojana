using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Yojana.Developer.Data;
using Yojana.Platform.Data;

namespace Yojana.Developer.DatabaseTests;

[Collection("developer host")]
public sealed class DeveloperSchemaDatabaseTests(DeveloperHostFixture fixture)
{
    [DatabaseFact]
    public async Task Schema_contains_all_tables_with_tenant_first_keys_and_rls()
    {
        var setup = await fixture.NewTenantAsync("schema");
        using var scope = fixture.ScopeFor(setup.Tenant);
        var db = scope.ServiceProvider.GetRequiredService<DeveloperDbContext>();
        Assert.Empty(db.ValidateTenantKeyConventions());
        var tables = await db.Database.SqlQueryRaw<string>("SELECT name AS [Value] FROM sys.tables WHERE schema_id = SCHEMA_ID('developer') AND name <> '__migrations'").ToListAsync();
        Assert.Equal(new[] { "api_access_token", "api_client", "api_idempotency_record", "bulk_operation",
            "bulk_operation_unit", "public_event", "webhook_delivery", "webhook_delivery_attempt",
            "webhook_subscription" }, tables.Order());
        var protectedTables = await db.Database.SqlQueryRaw<int>("SELECT COUNT(DISTINCT p.target_object_id) AS [Value] FROM sys.security_predicates p JOIN sys.tables t ON t.object_id=p.target_object_id WHERE t.schema_id=SCHEMA_ID('developer')").SingleAsync();
        Assert.Equal(tables.Count, protectedTables);
    }
}
