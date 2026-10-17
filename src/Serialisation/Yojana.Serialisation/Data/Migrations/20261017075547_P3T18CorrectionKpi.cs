using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P3T18CorrectionKpi : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_correction_record_root_cause",
                schema: "serialisation",
                table: "correction_record");

            migrationBuilder.AlterColumn<int>(
                name: "root_cause_category",
                schema: "serialisation",
                table: "correction_record",
                type: "int",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "int");

            migrationBuilder.AddColumn<string>(
                name: "request_status",
                schema: "serialisation",
                table: "correction_record",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "correction_record_unit",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    correction_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_correction_record_unit", x => new { x.tenant_id, x.correction_id, x.unit_id });
                    table.ForeignKey(
                        name: "fk_correction_record_unit_tenant_id_correction_id",
                        columns: x => new { x.tenant_id, x.correction_id },
                        principalSchema: "serialisation",
                        principalTable: "correction_record",
                        principalColumns: new[] { "tenant_id", "correction_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_correction_record_tenant_id_event_time_root_cause_category_request_status",
                schema: "serialisation",
                table: "correction_record",
                columns: new[] { "tenant_id", "event_time", "root_cause_category", "request_status" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_correction_record_root_cause",
                schema: "serialisation",
                table: "correction_record",
                sql: "root_cause_category IS NULL OR root_cause_category BETWEEN 1 AND 7");

            migrationBuilder.CreateIndex(
                name: "ix_correction_record_unit_tenant_id_item_id_correction_id",
                schema: "serialisation",
                table: "correction_record_unit",
                columns: new[] { "tenant_id", "item_id", "correction_id" });

            migrationBuilder.Sql("""
                ;WITH correction_units AS
                (
                    SELECT tenant_id, unit_id, TRY_CONVERT(uniqueidentifier, JSON_VALUE(attributes, '$.correction_id')) AS correction_id
                    FROM serialisation.unit_event
                    WHERE attributes IS NOT NULL AND JSON_VALUE(attributes, '$.correction_id') IS NOT NULL
                    UNION
                    SELECT tenant_id, unit_id, TRY_CONVERT(uniqueidentifier, JSON_VALUE(attributes, '$.correction_id')) AS correction_id
                    FROM serialisation.unit_event_archive
                    WHERE attributes IS NOT NULL AND JSON_VALUE(attributes, '$.correction_id') IS NOT NULL
                )
                INSERT INTO serialisation.correction_record_unit (tenant_id, correction_id, unit_id, item_id)
                SELECT facts.tenant_id, facts.correction_id, facts.unit_id, current_unit.item_id
                FROM correction_units facts
                INNER JOIN serialisation.correction_record correction
                    ON correction.tenant_id = facts.tenant_id AND correction.correction_id = facts.correction_id
                INNER JOIN serialisation.unit_current current_unit
                    ON current_unit.tenant_id = facts.tenant_id AND current_unit.unit_id = facts.unit_id
                WHERE facts.correction_id IS NOT NULL;
                """);

            migrationBuilder.AddTenantPolicy("serialisation", "correction_record_unit");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "correction_record_unit");

            migrationBuilder.DropTable(
                name: "correction_record_unit",
                schema: "serialisation");

            migrationBuilder.DropIndex(
                name: "ix_correction_record_tenant_id_event_time_root_cause_category_request_status",
                schema: "serialisation",
                table: "correction_record");

            migrationBuilder.DropCheckConstraint(
                name: "ck_correction_record_root_cause",
                schema: "serialisation",
                table: "correction_record");

            migrationBuilder.Sql("DELETE FROM serialisation.correction_record WHERE root_cause_category IS NULL;");

            migrationBuilder.DropColumn(
                name: "request_status",
                schema: "serialisation",
                table: "correction_record");

            migrationBuilder.AlterColumn<int>(
                name: "root_cause_category",
                schema: "serialisation",
                table: "correction_record",
                type: "int",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "int",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_correction_record_root_cause",
                schema: "serialisation",
                table: "correction_record",
                sql: "root_cause_category BETWEEN 1 AND 7");
        }
    }
}
