using System;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Developer.Migrations
{
    /// <inheritdoc />
    public partial class P14T00DeveloperScaffold : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "developer");

            migrationBuilder.CreateTable(
                name: "api_access_token",
                schema: "developer",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    token_hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    client_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    issued_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    revoked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_access_token", x => new { x.tenant_id, x.token_hash });
                });

            migrationBuilder.CreateTable(
                name: "api_client",
                schema: "developer",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    client_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    secret_hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    secret_hint = table.Column<string>(type: "varchar(12)", unicode: false, maxLength: 12, nullable: false),
                    previous_secret_hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: true),
                    previous_secret_expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    scopes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: false),
                    disclosure_tier = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    customer_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    sandbox = table.Column<bool>(type: "bit", nullable: false),
                    status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    rate_limit_per_minute = table.Column<int>(type: "int", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    secret_rotated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    revoked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    revoked_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    last_used_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_client", x => new { x.tenant_id, x.client_id });
                    table.CheckConstraint("ck_api_client_status", "status IN ('active','revoked')");
                    table.CheckConstraint("ck_api_client_tier", "disclosure_tier IN ('customer','back_office')");
                });

            migrationBuilder.CreateTable(
                name: "api_idempotency_record",
                schema: "developer",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    client_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    idempotency_key = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    request_hash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    status_code = table.Column<int>(type: "int", nullable: false),
                    response_body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_api_idempotency_record", x => new { x.tenant_id, x.client_id, x.idempotency_key });
                });

            migrationBuilder.CreateTable(
                name: "bulk_operation",
                schema: "developer",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    operation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    kind = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    unit_count = table.Column<int>(type: "int", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bulk_operation", x => new { x.tenant_id, x.operation_id });
                });

            migrationBuilder.CreateTable(
                name: "bulk_operation_unit",
                schema: "developer",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    operation_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bulk_operation_unit", x => new { x.tenant_id, x.operation_id, x.unit_id });
                });

            migrationBuilder.CreateTable(
                name: "public_event",
                schema: "developer",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    type = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    version = table.Column<string>(type: "varchar(8)", unicode: false, maxLength: 8, nullable: false),
                    record_kind = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    record_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    occurred_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    recorded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    body = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    test = table.Column<bool>(type: "bit", nullable: false),
                    item_family_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    customer_party_ref = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    legal_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_public_event", x => new { x.tenant_id, x.event_id });
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery",
                schema: "developer",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replay_of = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    attempt_count = table.Column<int>(type: "int", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    next_attempt_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    last_attempt_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    succeeded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    parked_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    discarded_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    discarded_by = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_delivery", x => new { x.tenant_id, x.delivery_id });
                    table.CheckConstraint("ck_webhook_delivery_status", "status IN ('pending','succeeded','parked','discarded')");
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery_attempt",
                schema: "developer",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    attempt_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    attempted_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    outcome = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    status_code = table.Column<int>(type: "int", nullable: true),
                    duration_ms = table.Column<int>(type: "int", nullable: false),
                    response_excerpt = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: true),
                    error_code = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_delivery_attempt", x => new { x.tenant_id, x.attempt_id });
                });

            migrationBuilder.CreateTable(
                name: "webhook_subscription",
                schema: "developer",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    subscription_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    url = table.Column<string>(type: "nvarchar(2048)", maxLength: 2048, nullable: false),
                    relay_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    event_types = table.Column<string>(type: "varchar(4000)", unicode: false, maxLength: 4000, nullable: false),
                    filters = table.Column<string>(type: "varchar(4000)", unicode: false, maxLength: 4000, nullable: false),
                    status = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false),
                    secret_sealed = table.Column<byte[]>(type: "varbinary(512)", maxLength: 512, nullable: false),
                    previous_secret_sealed = table.Column<byte[]>(type: "varbinary(512)", maxLength: 512, nullable: true),
                    previous_secret_expires_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    rate_per_second = table.Column<int>(type: "int", nullable: false),
                    verification_challenge_hash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    verified_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    failing_since = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    paused_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    paused_reason = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    updated_by = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    row_version = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_webhook_subscription", x => new { x.tenant_id, x.subscription_id });
                    table.CheckConstraint("ck_webhook_subscription_status", "status IN ('pending_verification','active','paused','disabled')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_api_access_token_tenant_id_client_id_expires_at",
                schema: "developer",
                table: "api_access_token",
                columns: new[] { "tenant_id", "client_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_api_access_token_tenant_id_expires_at",
                schema: "developer",
                table: "api_access_token",
                columns: new[] { "tenant_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_api_client_tenant_id_status_created_at_client_id",
                schema: "developer",
                table: "api_client",
                columns: new[] { "tenant_id", "status", "created_at", "client_id" });

            migrationBuilder.CreateIndex(
                name: "ux_api_client_tenant_id_name",
                schema: "developer",
                table: "api_client",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_api_idempotency_record_tenant_id_expires_at",
                schema: "developer",
                table: "api_idempotency_record",
                columns: new[] { "tenant_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_public_event_tenant_id_record_kind_record_id_sequence",
                schema: "developer",
                table: "public_event",
                columns: new[] { "tenant_id", "record_kind", "record_id", "sequence" });

            migrationBuilder.CreateIndex(
                name: "ix_public_event_tenant_id_recorded_at_event_id",
                schema: "developer",
                table: "public_event",
                columns: new[] { "tenant_id", "recorded_at", "event_id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_tenant_id_subscription_id_created_at_delivery_id",
                schema: "developer",
                table: "webhook_delivery",
                columns: new[] { "tenant_id", "subscription_id", "created_at", "delivery_id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_tenant_id_subscription_id_status_next_attempt_at_delivery_id",
                schema: "developer",
                table: "webhook_delivery",
                columns: new[] { "tenant_id", "subscription_id", "status", "next_attempt_at", "delivery_id" });

            migrationBuilder.CreateIndex(
                name: "ux_webhook_delivery_tenant_id_subscription_id_event_id",
                schema: "developer",
                table: "webhook_delivery",
                columns: new[] { "tenant_id", "subscription_id", "event_id" },
                unique: true,
                filter: "[replay_of] IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_attempt_tenant_id_attempted_at",
                schema: "developer",
                table: "webhook_delivery_attempt",
                columns: new[] { "tenant_id", "attempted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_delivery_attempt_tenant_id_delivery_id_attempted_at_attempt_id",
                schema: "developer",
                table: "webhook_delivery_attempt",
                columns: new[] { "tenant_id", "delivery_id", "attempted_at", "attempt_id" });

            migrationBuilder.CreateIndex(
                name: "ix_webhook_subscription_tenant_id_status_subscription_id",
                schema: "developer",
                table: "webhook_subscription",
                columns: new[] { "tenant_id", "status", "subscription_id" });

            migrationBuilder.CreateIndex(
                name: "ux_webhook_subscription_tenant_id_name",
                schema: "developer",
                table: "webhook_subscription",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            foreach (var table in Tables)
                migrationBuilder.AddTenantPolicy("developer", table);
        }

        private static readonly string[] Tables = { "api_client", "api_access_token", "api_idempotency_record", "webhook_subscription", "public_event", "webhook_delivery", "webhook_delivery_attempt", "bulk_operation", "bulk_operation_unit" };

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var table in Tables.Reverse())
                migrationBuilder.RemoveTenantPolicy("developer", table);
            migrationBuilder.Sql("IF EXISTS (SELECT 1 FROM sys.security_policies WHERE name = N'tenant_policy_developer') DROP SECURITY POLICY [sec].[tenant_policy_developer];");

            migrationBuilder.DropTable(
                name: "api_access_token",
                schema: "developer");

            migrationBuilder.DropTable(
                name: "api_client",
                schema: "developer");

            migrationBuilder.DropTable(
                name: "api_idempotency_record",
                schema: "developer");

            migrationBuilder.DropTable(
                name: "bulk_operation",
                schema: "developer");

            migrationBuilder.DropTable(
                name: "bulk_operation_unit",
                schema: "developer");

            migrationBuilder.DropTable(
                name: "public_event",
                schema: "developer");

            migrationBuilder.DropTable(
                name: "webhook_delivery",
                schema: "developer");

            migrationBuilder.DropTable(
                name: "webhook_delivery_attempt",
                schema: "developer");

            migrationBuilder.DropTable(
                name: "webhook_subscription",
                schema: "developer");
        }
    }
}
