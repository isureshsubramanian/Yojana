using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Platform.Data.Platform.Migrations
{
    /// <inheritdoc />
    public partial class ADR21B4SortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_security_event_tenant_id_kind_id",
                schema: "platform",
                table: "security_event",
                columns: new[] { "tenant_id", "kind", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_erasure_request_tenant_id_request_received_on_erasure_id",
                schema: "platform",
                table: "erasure_request",
                columns: new[] { "tenant_id", "request_received_on", "erasure_id" });

            migrationBuilder.CreateIndex(
                name: "ix_erasure_request_tenant_id_status_erasure_id",
                schema: "platform",
                table: "erasure_request",
                columns: new[] { "tenant_id", "status", "erasure_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_security_event_tenant_id_kind_id",
                schema: "platform",
                table: "security_event");

            migrationBuilder.DropIndex(
                name: "ix_erasure_request_tenant_id_request_received_on_erasure_id",
                schema: "platform",
                table: "erasure_request");

            migrationBuilder.DropIndex(
                name: "ix_erasure_request_tenant_id_status_erasure_id",
                schema: "platform",
                table: "erasure_request");
        }
    }
}
