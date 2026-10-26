using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class ADR21B2SortIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_warranty_line_exception_tenant_id_raised_at_exception_id",
                schema: "serialisation",
                table: "warranty_line_exception",
                columns: new[] { "tenant_id", "raised_at", "exception_id" });

            migrationBuilder.CreateIndex(
                name: "ix_warranty_line_exception_tenant_id_status_exception_id",
                schema: "serialisation",
                table: "warranty_line_exception",
                columns: new[] { "tenant_id", "status", "exception_id" });

            migrationBuilder.CreateIndex(
                name: "ix_warranty_line_exception_tenant_id_trigger_at_exception_id",
                schema: "serialisation",
                table: "warranty_line_exception",
                columns: new[] { "tenant_id", "trigger_at", "exception_id" });

            migrationBuilder.CreateIndex(
                name: "ix_three_pl_feed_message_tenant_id_recorded_time_feed_message_id",
                schema: "serialisation",
                table: "three_pl_feed_message",
                columns: new[] { "tenant_id", "recorded_time", "feed_message_id" });

            migrationBuilder.CreateIndex(
                name: "ix_three_pl_feed_message_tenant_id_status_feed_message_id",
                schema: "serialisation",
                table: "three_pl_feed_message",
                columns: new[] { "tenant_id", "status", "feed_message_id" });

            migrationBuilder.CreateIndex(
                name: "ix_rma_tenant_id_requested_at_rma_id",
                schema: "serialisation",
                table: "rma",
                columns: new[] { "tenant_id", "requested_at", "rma_id" });

            migrationBuilder.CreateIndex(
                name: "ix_rma_tenant_id_stage_rma_id",
                schema: "serialisation",
                table: "rma",
                columns: new[] { "tenant_id", "stage", "rma_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_return_due_replacement_link_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "return_due", "replacement_link_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_state_replacement_link_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "state", "replacement_link_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refurbishment_work_order_tenant_id_opened_at_work_order_id",
                schema: "serialisation",
                table: "refurbishment_work_order",
                columns: new[] { "tenant_id", "opened_at", "work_order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_refurbishment_work_order_tenant_id_status_work_order_id",
                schema: "serialisation",
                table: "refurbishment_work_order",
                columns: new[] { "tenant_id", "status", "work_order_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recovery_claim_tenant_id_raised_at_recovery_claim_id",
                schema: "serialisation",
                table: "recovery_claim",
                columns: new[] { "tenant_id", "raised_at", "recovery_claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_recovery_claim_tenant_id_status_recovery_claim_id",
                schema: "serialisation",
                table: "recovery_claim",
                columns: new[] { "tenant_id", "status", "recovery_claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_print_job_tenant_id_requested_at_job_id",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "requested_at", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_print_job_tenant_id_status_job_id",
                schema: "serialisation",
                table: "print_job",
                columns: new[] { "tenant_id", "status", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_job_tenant_id_file_name_job_id",
                schema: "serialisation",
                table: "import_job",
                columns: new[] { "tenant_id", "file_name", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_import_job_tenant_id_status_job_id",
                schema: "serialisation",
                table: "import_job",
                columns: new[] { "tenant_id", "status", "job_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_mail_message_tenant_id_status_message_id",
                schema: "serialisation",
                table: "customer_mail_message",
                columns: new[] { "tenant_id", "status", "message_id" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_tenant_id_status_claim_id",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "status", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_claim_tenant_id_submitted_at_claim_id",
                schema: "serialisation",
                table: "claim",
                columns: new[] { "tenant_id", "submitted_at", "claim_id" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_unit_tenant_id_selected_at_unit_id",
                schema: "serialisation",
                table: "campaign_unit",
                columns: new[] { "tenant_id", "selected_at", "unit_id" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_tenant_id_status_campaign_id",
                schema: "serialisation",
                table: "campaign",
                columns: new[] { "tenant_id", "status", "campaign_id" });

            migrationBuilder.CreateIndex(
                name: "ix_campaign_tenant_id_updated_at_campaign_id",
                schema: "serialisation",
                table: "campaign",
                columns: new[] { "tenant_id", "updated_at", "campaign_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_warranty_line_exception_tenant_id_raised_at_exception_id",
                schema: "serialisation",
                table: "warranty_line_exception");

            migrationBuilder.DropIndex(
                name: "ix_warranty_line_exception_tenant_id_status_exception_id",
                schema: "serialisation",
                table: "warranty_line_exception");

            migrationBuilder.DropIndex(
                name: "ix_warranty_line_exception_tenant_id_trigger_at_exception_id",
                schema: "serialisation",
                table: "warranty_line_exception");

            migrationBuilder.DropIndex(
                name: "ix_three_pl_feed_message_tenant_id_recorded_time_feed_message_id",
                schema: "serialisation",
                table: "three_pl_feed_message");

            migrationBuilder.DropIndex(
                name: "ix_three_pl_feed_message_tenant_id_status_feed_message_id",
                schema: "serialisation",
                table: "three_pl_feed_message");

            migrationBuilder.DropIndex(
                name: "ix_rma_tenant_id_requested_at_rma_id",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.DropIndex(
                name: "ix_rma_tenant_id_stage_rma_id",
                schema: "serialisation",
                table: "rma");

            migrationBuilder.DropIndex(
                name: "ix_replacement_link_tenant_id_return_due_replacement_link_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropIndex(
                name: "ix_replacement_link_tenant_id_state_replacement_link_id",
                schema: "serialisation",
                table: "replacement_link");

            migrationBuilder.DropIndex(
                name: "ix_refurbishment_work_order_tenant_id_opened_at_work_order_id",
                schema: "serialisation",
                table: "refurbishment_work_order");

            migrationBuilder.DropIndex(
                name: "ix_refurbishment_work_order_tenant_id_status_work_order_id",
                schema: "serialisation",
                table: "refurbishment_work_order");

            migrationBuilder.DropIndex(
                name: "ix_recovery_claim_tenant_id_raised_at_recovery_claim_id",
                schema: "serialisation",
                table: "recovery_claim");

            migrationBuilder.DropIndex(
                name: "ix_recovery_claim_tenant_id_status_recovery_claim_id",
                schema: "serialisation",
                table: "recovery_claim");

            migrationBuilder.DropIndex(
                name: "ix_print_job_tenant_id_requested_at_job_id",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropIndex(
                name: "ix_print_job_tenant_id_status_job_id",
                schema: "serialisation",
                table: "print_job");

            migrationBuilder.DropIndex(
                name: "ix_import_job_tenant_id_file_name_job_id",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropIndex(
                name: "ix_import_job_tenant_id_status_job_id",
                schema: "serialisation",
                table: "import_job");

            migrationBuilder.DropIndex(
                name: "ix_customer_mail_message_tenant_id_status_message_id",
                schema: "serialisation",
                table: "customer_mail_message");

            migrationBuilder.DropIndex(
                name: "ix_claim_tenant_id_status_claim_id",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropIndex(
                name: "ix_claim_tenant_id_submitted_at_claim_id",
                schema: "serialisation",
                table: "claim");

            migrationBuilder.DropIndex(
                name: "ix_campaign_unit_tenant_id_selected_at_unit_id",
                schema: "serialisation",
                table: "campaign_unit");

            migrationBuilder.DropIndex(
                name: "ix_campaign_tenant_id_status_campaign_id",
                schema: "serialisation",
                table: "campaign");

            migrationBuilder.DropIndex(
                name: "ix_campaign_tenant_id_updated_at_campaign_id",
                schema: "serialisation",
                table: "campaign");
        }
    }
}
