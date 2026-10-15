using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;
using Yojana.Platform.Scheduling;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // MOD-01 conventions first: the sec schema and the shared RLS predicate function
            // (read-committed snapshot is the preceding, transaction-free migration).
            migrationBuilder.EnsureTenantSecurityObjects();

            migrationBuilder.EnsureSchema(
                name: "platform");

            migrationBuilder.CreateTable(
                name: "app_user",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    password_hash = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_app_user", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "smoke_log",
                schema: "platform",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    note = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    recorded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_smoke_log", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "tenant",
                schema: "platform",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    is_active = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "tenant_setting",
                schema: "platform",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    value = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    sys_end = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodEndColumn", true),
                    sys_start = table.Column<DateTime>(type: "datetime2", nullable: false)
                        .Annotation("SqlServer:TemporalIsPeriodStartColumn", true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_setting", x => new { x.tenant_id, x.key });
                })
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "tenant_setting_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "platform")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.CreateTable(
                name: "user_tenant_membership",
                schema: "platform",
                columns: table => new
                {
                    user_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    roles = table.Column<string>(type: "nvarchar(max)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_tenant_membership", x => new { x.user_id, x.tenant_id });
                    table.ForeignKey(
                        name: "fk_user_tenant_membership_tenant_id",
                        column: x => x.tenant_id,
                        principalSchema: "platform",
                        principalTable: "tenant",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_user_tenant_membership_user_id",
                        column: x => x.user_id,
                        principalSchema: "platform",
                        principalTable: "app_user",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_app_user_email",
                schema: "platform",
                table: "app_user",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_smoke_log_tenant_id_recorded_at",
                schema: "platform",
                table: "smoke_log",
                columns: new[] { "tenant_id", "recorded_at" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_code",
                schema: "platform",
                table: "tenant",
                column: "code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_tenant_membership_tenant_id",
                schema: "platform",
                table: "user_tenant_membership",
                column: "tenant_id");

            // Row-level security on every tenant-scoped table of this schema (platform policy).
            migrationBuilder.AddTenantPolicy("platform", "tenant_setting");
            migrationBuilder.AddTenantPolicy("platform", "smoke_log");

            // Quartz.NET job store (schema quartz), from the vendored script; one Sql() per batch.
            migrationBuilder.Sql("IF SCHEMA_ID(N'quartz') IS NULL EXEC(N'CREATE SCHEMA [quartz]');");
            foreach (var batch in QuartzSchema.Batches())
            {
                migrationBuilder.Sql(batch);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("platform", "smoke_log");
            migrationBuilder.RemoveTenantPolicy("platform", "tenant_setting");
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'tenant_policy_platform') EXEC(N'DROP SECURITY POLICY [sec].[tenant_policy_platform]');");
            migrationBuilder.DropTable(
                name: "smoke_log",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "tenant_setting",
                schema: "platform")
                .Annotation("SqlServer:IsTemporal", true)
                .Annotation("SqlServer:TemporalHistoryTableName", "tenant_setting_history")
                .Annotation("SqlServer:TemporalHistoryTableSchema", "platform")
                .Annotation("SqlServer:TemporalPeriodEndColumnName", "sys_end")
                .Annotation("SqlServer:TemporalPeriodStartColumnName", "sys_start");

            migrationBuilder.DropTable(
                name: "user_tenant_membership",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "tenant",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "app_user",
                schema: "platform");
        }
    }
}
