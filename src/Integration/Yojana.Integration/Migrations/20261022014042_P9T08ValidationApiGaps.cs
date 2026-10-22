using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P9T08ValidationApiGaps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "failure_detail",
                schema: "migration",
                table: "migration_validation_run",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "display_number",
                schema: "migration",
                table: "migration_batch",
                type: "int",
                nullable: true);

            migrationBuilder.Sql("""
                CREATE TABLE #migration_batch_numbers
                (
                    tenant_id uniqueidentifier NOT NULL,
                    batch_id uniqueidentifier NOT NULL,
                    display_number int NOT NULL,
                    PRIMARY KEY (tenant_id, batch_id)
                );

                INSERT INTO #migration_batch_numbers (tenant_id, batch_id, display_number)
                SELECT tenant_id, batch_id,
                       ROW_NUMBER() OVER (PARTITION BY tenant_id ORDER BY requested_at, batch_id)
                FROM migration.migration_batch;

                WHILE EXISTS (SELECT 1 FROM #migration_batch_numbers)
                BEGIN
                    ;WITH next_rows AS
                    (
                        SELECT TOP (5000) tenant_id, batch_id, display_number
                        FROM #migration_batch_numbers
                        ORDER BY tenant_id, display_number
                    )
                    UPDATE batch
                    SET display_number = numbers.display_number
                    FROM migration.migration_batch batch
                    INNER JOIN next_rows numbers
                        ON numbers.tenant_id = batch.tenant_id AND numbers.batch_id = batch.batch_id;

                    ;WITH completed AS
                    (
                        SELECT TOP (5000) tenant_id, batch_id
                        FROM #migration_batch_numbers
                        ORDER BY tenant_id, display_number
                    )
                    DELETE FROM completed;
                END;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "display_number",
                schema: "migration",
                table: "migration_batch",
                type: "int",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_migration_batch_tenant_id_display_number",
                schema: "migration",
                table: "migration_batch",
                columns: new[] { "tenant_id", "display_number" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_migration_batch_tenant_id_display_number",
                schema: "migration",
                table: "migration_batch");

            migrationBuilder.DropColumn(
                name: "failure_detail",
                schema: "migration",
                table: "migration_validation_run");

            migrationBuilder.DropColumn(
                name: "display_number",
                schema: "migration",
                table: "migration_batch");
        }
    }
}
