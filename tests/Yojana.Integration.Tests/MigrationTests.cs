using Yojana.Integration.LegacyMigration;
using Yojana.Integration.LegacyMigration.Staging;
using Yojana.Platform.Contracts.Tenancy;

namespace Yojana.Integration.Tests;

public sealed class MigrationTests
{
    [Fact]
    public void Linked_server_route_uses_a_quoted_four_part_name()
    {
        var source = new MigrationSource
        {
            TenantId = new TenantId(Guid.NewGuid()),
            ConnectionId = Guid.NewGuid(),
            DisplayLabel = "Fictional archive",
            SourceSystem = "cx01",
            Runtime = MigrationSourceRuntime.SqlServer,
            Route = MigrationSourceRoute.LinkedServer,
            LinkedServerName = "source-node",
            DatabaseName = "source-database",
            SchemaName = "dbo",
            CommandTimeoutSeconds = 30,
            LicencePosition = MigrationLicencePosition.Confirmed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var qualified = SourceExtractor.QualifiedName(source, SourceProtocol.Tables[0].SourceTable);

        Assert.StartsWith("[source-node].[source-database].[dbo].", qualified, StringComparison.Ordinal);
        Assert.Equal(4, qualified.Count(character => character == '['));
    }

    [Fact]
    public void Every_source_protocol_has_keys_and_a_distinct_staging_table()
    {
        Assert.All(SourceProtocol.Tables, table => Assert.NotEmpty(table.Keys));
        Assert.Equal(SourceProtocol.Tables.Count,
            SourceProtocol.Tables.Select(table => table.StagingTable).Distinct(StringComparer.Ordinal).Count());
    }
}
