using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class WarrantyCurrent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both source tables are covered by serialisation.tenant_policy. SQL Server expands a normal view before
            // applying their RLS filter predicates, so callers cannot expose another tenant's JSON ids or entitlement.
            migrationBuilder.Sql("""
                CREATE VIEW [serialisation].[warranty_current]
                AS
                SELECT c.[tenant_id], c.[unit_id], ids.[entitlement_id], e.[coverage_family], e.[type], e.[status],
                       e.[start_date], e.[end_date], e.[duration_basis], e.[coverage]
                FROM [serialisation].[unit_current] AS c
                CROSS APPLY OPENJSON(c.[live_entitlement_ids])
                    WITH ([entitlement_id] uniqueidentifier '$') AS ids
                INNER JOIN [serialisation].[entitlement] AS e
                    ON e.[tenant_id] = c.[tenant_id]
                   AND e.[unit_id] = c.[unit_id]
                   AND e.[entitlement_id] = ids.[entitlement_id]
                WHERE e.[status] <> N'closed';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP VIEW [serialisation].[warranty_current];");
        }
    }
}
