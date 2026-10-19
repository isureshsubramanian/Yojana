using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T14SecurityStores : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_credential",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    purpose = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    data_key_version = table.Column<int>(type: "int", nullable: false),
                    nonce = table.Column<byte[]>(type: "varbinary(12)", maxLength: 12, nullable: false),
                    ciphertext = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    tag = table.Column<byte[]>(type: "varbinary(16)", maxLength: 16, nullable: false),
                    rotated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    rotation_due_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_credential", x => new { x.tenant_id, x.connection_id, x.purpose, x.created_at });
                });

            migrationBuilder.CreateTable(
                name: "connection_event",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence_no = table.Column<long>(type: "bigint", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_type = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    actor_ref = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    details_json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    previous_record_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    record_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_event", x => new { x.tenant_id, x.sequence_no });
                    table.UniqueConstraint("ak_connection_event", x => new { x.tenant_id, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "connection_throttle_profile",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connector_code = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    requests_per_second = table.Column<int>(type: "int", nullable: true),
                    requests_per_minute = table.Column<int>(type: "int", nullable: true),
                    requests_per_day = table.Column<int>(type: "int", nullable: true),
                    burst = table.Column<int>(type: "int", nullable: false),
                    max_concurrent_calls = table.Column<int>(type: "int", nullable: false),
                    retry_after_ceiling_seconds = table.Column<int>(type: "int", nullable: false),
                    alert_threshold_percent = table.Column<int>(type: "int", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    budget_date_utc = table.Column<DateOnly>(type: "date", nullable: true),
                    used_calls = table.Column<int>(type: "int", nullable: false),
                    approach_alert_raised = table.Column<bool>(type: "bit", nullable: false),
                    cap_alert_raised = table.Column<bool>(type: "bit", nullable: false),
                    budget_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_throttle_profile", x => new { x.tenant_id, x.connection_id });
                    table.CheckConstraint("ck_connection_throttle_limits", "burst >= 1 AND max_concurrent_calls >= 1 AND retry_after_ceiling_seconds >= 1 AND alert_threshold_percent BETWEEN 1 AND 100 AND used_calls >= 0 AND budget_revision >= 0");
                    table.CheckConstraint("ck_connection_throttle_positive_caps", "(requests_per_second IS NULL OR requests_per_second > 0) AND (requests_per_minute IS NULL OR requests_per_minute > 0) AND (requests_per_day IS NULL OR requests_per_day > 0)");
                });

            migrationBuilder.CreateTable(
                name: "tenant_data_key",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    version = table.Column<int>(type: "int", nullable: false),
                    key_encryption_key_id = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    key_encryption_key_version = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    key_encryption_algorithm = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    wrapped_key_nonce = table.Column<byte[]>(type: "varbinary(12)", maxLength: 12, nullable: false),
                    wrapped_key_ciphertext = table.Column<byte[]>(type: "varbinary(max)", nullable: false),
                    wrapped_key_tag = table.Column<byte[]>(type: "varbinary(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    rotated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_data_key", x => new { x.tenant_id, x.version });
                });

            migrationBuilder.CreateTable(
                name: "tenant_data_key_rewrap_checkpoint",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    operation_id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    data_key_version = table.Column<int>(type: "int", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant_data_key_rewrap_checkpoint", x => new { x.tenant_id, x.operation_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_connection_credential_tenant_id_connection_id_purpose_rotated_at",
                schema: "integration",
                table: "connection_credential",
                columns: new[] { "tenant_id", "connection_id", "purpose", "rotated_at" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_event_tenant_id_connection_id_occurred_at_sequence_no",
                schema: "integration",
                table: "connection_event",
                columns: new[] { "tenant_id", "connection_id", "occurred_at", "sequence_no" });

            migrationBuilder.CreateIndex(
                name: "ix_connection_throttle_profile_tenant_id_connector_code_connection_id",
                schema: "integration",
                table: "connection_throttle_profile",
                columns: new[] { "tenant_id", "connector_code", "connection_id" });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_data_key_tenant_id_is_active",
                schema: "integration",
                table: "tenant_data_key",
                columns: new[] { "tenant_id", "is_active" },
                unique: true,
                filter: "[is_active] = 1");

            migrationBuilder.CreateIndex(
                name: "ix_tenant_data_key_rewrap_checkpoint_tenant_id_updated_at_operation_id",
                schema: "integration",
                table: "tenant_data_key_rewrap_checkpoint",
                columns: new[] { "tenant_id", "updated_at", "operation_id" });

            foreach (var table in new[] { "connection_credential", "connection_event", "connection_throttle_profile",
                         "tenant_data_key", "tenant_data_key_rewrap_checkpoint" })
                migrationBuilder.AddTenantPolicy("integration", table);

            migrationBuilder.Sql("""
                CREATE TRIGGER [integration].[tr_connection_event_immutable]
                ON [integration].[connection_event]
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, 'connection_event is append-only', 1;
                END
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [integration].[tr_connection_event_immutable]");
            foreach (var table in new[] { "connection_credential", "connection_event", "connection_throttle_profile",
                         "tenant_data_key", "tenant_data_key_rewrap_checkpoint" })
                migrationBuilder.RemoveTenantPolicy("integration", table);
            migrationBuilder.DropTable(
                name: "connection_credential",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "connection_event",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "connection_throttle_profile",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "tenant_data_key",
                schema: "integration");

            migrationBuilder.DropTable(
                name: "tenant_data_key_rewrap_checkpoint",
                schema: "integration");
        }
    }
}
