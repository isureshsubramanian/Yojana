using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P5Condition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "life_episode",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    life_episode_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    started_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    ended_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    work_order_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    cycle_number = table.Column<int>(type: "int", nullable: true),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    opening_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    closing_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_life_episode", x => new { x.tenant_id, x.life_episode_id });
                    table.CheckConstraint("ck_life_episode_dates", "ended_at IS NULL OR ended_at >= started_at");
                    table.CheckConstraint("ck_life_episode_kind", "kind IN ('refurbish', 'remanufacture', 'rework')");
                });

            migrationBuilder.CreateTable(
                name: "sanitisation_record",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sanitisation_record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    manufacturer = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    model = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    media_serial = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    media_type = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    method = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    tool = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    tool_version = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: true),
                    verification_method = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    operator_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    sanitised_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    certificate_ref = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    signature_ref = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sanitisation_record", x => new { x.tenant_id, x.sanitisation_record_id });
                    table.CheckConstraint("ck_sanitisation_record_method", "method IN ('clear', 'purge', 'destroy')");
                });

            migrationBuilder.CreateTable(
                name: "test_record",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    test_record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    test_kind = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    result = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                    measurements_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    equipment = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    operator_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    tested_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_test_record", x => new { x.tenant_id, x.test_record_id });
                    table.CheckConstraint("ck_test_record_measurements_json", "ISJSON(measurements_json) = 1");
                    table.CheckConstraint("ck_test_record_result", "result IN ('pass', 'fail')");
                });

            migrationBuilder.CreateTable(
                name: "unit_attribute_history",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    attribute_history_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    effective_from = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_attribute_history", x => new { x.tenant_id, x.attribute_history_id });
                });

            migrationBuilder.CreateTable(
                name: "unit_condition_event",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    condition_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    grade = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    f_code = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: true),
                    c_code = table.Column<string>(type: "varchar(2)", unicode: false, maxLength: 2, nullable: true),
                    valuation_rule = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    inspector_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    graded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_unit_condition_event", x => new { x.tenant_id, x.condition_event_id });
                    table.CheckConstraint("ck_unit_condition_event_grade", "grade IN ('new', 'open_box', 'refurbished_a', 'refurbished_b', 'refurbished_c', 'used', 'remanufactured', 'defective', 'scrap')");
                });

            migrationBuilder.CreateTable(
                name: "usage_reading",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    usage_reading_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    meter_kind = table.Column<string>(type: "nvarchar(80)", maxLength: 80, nullable: false),
                    value = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    unit = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    read_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    meter_replacement = table.Column<bool>(type: "bit", nullable: false),
                    operator_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_usage_reading", x => new { x.tenant_id, x.usage_reading_id });
                    table.CheckConstraint("ck_usage_reading_value", "value >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "ix_life_episode_tenant_id_unit_id_started_at",
                schema: "serialisation",
                table: "life_episode",
                columns: new[] { "tenant_id", "unit_id", "started_at" });

            migrationBuilder.CreateIndex(
                name: "ix_life_episode_tenant_id_work_order_id",
                schema: "serialisation",
                table: "life_episode",
                columns: new[] { "tenant_id", "work_order_id" });

            migrationBuilder.CreateIndex(
                name: "ux_life_episode_tenant_id_unit_id_kind",
                schema: "serialisation",
                table: "life_episode",
                columns: new[] { "tenant_id", "unit_id", "kind" },
                unique: true,
                filter: "[ended_at] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_sanitisation_record_tenant_id_unit_id_sanitised_at",
                schema: "serialisation",
                table: "sanitisation_record",
                columns: new[] { "tenant_id", "unit_id", "sanitised_at" });

            migrationBuilder.CreateIndex(
                name: "ux_sanitisation_record_tenant_id_command_id",
                schema: "serialisation",
                table: "sanitisation_record",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_test_record_tenant_id_unit_id_tested_at",
                schema: "serialisation",
                table: "test_record",
                columns: new[] { "tenant_id", "unit_id", "tested_at" });

            migrationBuilder.CreateIndex(
                name: "ux_test_record_tenant_id_command_id",
                schema: "serialisation",
                table: "test_record",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_attribute_history_tenant_id_unit_id_key_effective_from",
                schema: "serialisation",
                table: "unit_attribute_history",
                columns: new[] { "tenant_id", "unit_id", "key", "effective_from" });

            migrationBuilder.CreateIndex(
                name: "ux_unit_attribute_history_tenant_id_command_id_key",
                schema: "serialisation",
                table: "unit_attribute_history",
                columns: new[] { "tenant_id", "command_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_unit_condition_event_tenant_id_unit_id_graded_at",
                schema: "serialisation",
                table: "unit_condition_event",
                columns: new[] { "tenant_id", "unit_id", "graded_at" });

            migrationBuilder.CreateIndex(
                name: "ux_unit_condition_event_tenant_id_command_id",
                schema: "serialisation",
                table: "unit_condition_event",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_reading_tenant_id_unit_id_meter_kind_read_at",
                schema: "serialisation",
                table: "usage_reading",
                columns: new[] { "tenant_id", "unit_id", "meter_kind", "read_at" });

            migrationBuilder.CreateIndex(
                name: "ux_usage_reading_tenant_id_command_id",
                schema: "serialisation",
                table: "usage_reading",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "life_episode");
            migrationBuilder.AddTenantPolicy("serialisation", "sanitisation_record");
            migrationBuilder.AddTenantPolicy("serialisation", "test_record");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_attribute_history");
            migrationBuilder.AddTenantPolicy("serialisation", "unit_condition_event");
            migrationBuilder.AddTenantPolicy("serialisation", "usage_reading");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "life_episode",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "sanitisation_record",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "test_record",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit_attribute_history",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "unit_condition_event",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "usage_reading",
                schema: "serialisation");
        }
    }
}
