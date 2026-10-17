using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Serialisation.Data.Migrations
{
    /// <inheritdoc />
    public partial class P5Replacement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "warranty_replacement",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    warranty_replacement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replacement_number = table.Column<string>(type: "varchar(24)", unicode: false, maxLength: 24, nullable: false),
                    source_kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    source_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_number = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    original_invoice_type = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    original_invoice_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    original_invoice_line = table.Column<int>(type: "int", nullable: true),
                    legal_entity_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    service_country = table.Column<string>(type: "char(2)", unicode: false, fixedLength: true, maxLength: 2, nullable: false),
                    consumer_contract = table.Column<bool>(type: "bit", nullable: false),
                    status = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    replacement_policy_code = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    return_reason_code = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    restocking_fee_rule = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    restocking_fee_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ship_command_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    shipped_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    shipped_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    stock_posting_reference = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warranty_replacement", x => new { x.tenant_id, x.warranty_replacement_id });
                    table.CheckConstraint("ck_warranty_replacement_fee", "restocking_fee_amount >= 0");
                    table.CheckConstraint("ck_warranty_replacement_source_kind", "source_kind IN ('claim', 'rma')");
                    table.CheckConstraint("ck_warranty_replacement_status", "status IN ('proposed', 'shipped', 'cancelled')");
                });

            migrationBuilder.CreateTable(
                name: "replacement_link",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replacement_link_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    warranty_replacement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    previous_replacement_link_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    direction = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    replacement_reason = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    state = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    source_kind = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    source_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_number = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    original_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replacement_unit_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    original_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replacement_item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    source_entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    target_entitlement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    policy_applied = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    computed_end = table.Column<DateOnly>(type: "date", nullable: true),
                    price_policy = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    difference_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    return_due = table.Column<DateOnly>(type: "date", nullable: true),
                    non_return_price_basis = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: true),
                    replacement_condition_allowed = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    replacement_condition = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    consent_refurbished = table.Column<bool>(type: "bit", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    shipped_at = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                    shipped_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    shipped_event_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_replacement_link", x => new { x.tenant_id, x.replacement_link_id });
                    table.CheckConstraint("ck_replacement_link_condition_allowed", "replacement_condition_allowed IN ('new', 'new_or_refurbished', 'any')");
                    table.CheckConstraint("ck_replacement_link_direction", "direction IN ('customer_facing', 'vendor_facing')");
                    table.CheckConstraint("ck_replacement_link_price_policy", "price_policy IN ('zero', 'difference', 'full')");
                    table.CheckConstraint("ck_replacement_link_reason", "replacement_reason IN ('warranty_defect', 'dead_on_arrival', 'seller_error', 'goodwill', 'recall')");
                    table.CheckConstraint("ck_replacement_link_source_kind", "source_kind IN ('claim', 'rma')");
                    table.CheckConstraint("ck_replacement_link_state", "state IN ('proposed', 'awaiting_return', 'closed_returned', 'closed_billed', 'cancelled', 'corrected')");
                    table.CheckConstraint("ck_replacement_link_units", "original_unit_id <> replacement_unit_id");
                    table.CheckConstraint("ck_replacement_link_zero_price", "price_policy <> 'zero' OR difference_amount = 0");
                    table.ForeignKey(
                        name: "fk_replacement_link_tenant_id_warranty_replacement_id",
                        columns: x => new { x.tenant_id, x.warranty_replacement_id },
                        principalSchema: "serialisation",
                        principalTable: "warranty_replacement",
                        principalColumns: new[] { "tenant_id", "warranty_replacement_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "warranty_replacement_line",
                schema: "serialisation",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    line_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    warranty_replacement_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    replacement_link_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    sequence = table.Column<int>(type: "int", nullable: false),
                    kind = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                    item_id = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    quantity = table.Column<decimal>(type: "decimal(18,6)", precision: 18, scale: 6, nullable: false),
                    unit_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    line_amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    currency = table.Column<string>(type: "char(3)", unicode: false, fixedLength: true, maxLength: 3, nullable: false),
                    revenue_rule = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_warranty_replacement_line", x => new { x.tenant_id, x.line_id });
                    table.CheckConstraint("ck_warranty_replacement_line_amounts", "quantity > 0 AND unit_amount >= 0 AND line_amount >= 0");
                    table.CheckConstraint("ck_warranty_replacement_line_kind", "kind IN ('replacement', 'service_revenue')");
                    table.ForeignKey(
                        name: "fk_warranty_replacement_line_tenant_id_replacement_link_id",
                        columns: x => new { x.tenant_id, x.replacement_link_id },
                        principalSchema: "serialisation",
                        principalTable: "replacement_link",
                        principalColumns: new[] { "tenant_id", "replacement_link_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_warranty_replacement_line_tenant_id_warranty_replacement_id",
                        columns: x => new { x.tenant_id, x.warranty_replacement_id },
                        principalSchema: "serialisation",
                        principalTable: "warranty_replacement",
                        principalColumns: new[] { "tenant_id", "warranty_replacement_id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_original_unit_id_created_at",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "original_unit_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_previous_replacement_link_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "previous_replacement_link_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_replacement_unit_id_created_at",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "replacement_unit_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_source_kind_source_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "source_kind", "source_id" });

            migrationBuilder.CreateIndex(
                name: "ix_replacement_link_tenant_id_state_return_due",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "state", "return_due" });

            migrationBuilder.CreateIndex(
                name: "ux_replacement_link_tenant_id_warranty_replacement_id",
                schema: "serialisation",
                table: "replacement_link",
                columns: new[] { "tenant_id", "warranty_replacement_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_warranty_replacement_tenant_id_source_kind_source_id",
                schema: "serialisation",
                table: "warranty_replacement",
                columns: new[] { "tenant_id", "source_kind", "source_id" });

            migrationBuilder.CreateIndex(
                name: "ix_warranty_replacement_tenant_id_status_created_at",
                schema: "serialisation",
                table: "warranty_replacement",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_warranty_replacement_tenant_id_command_id",
                schema: "serialisation",
                table: "warranty_replacement",
                columns: new[] { "tenant_id", "command_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_warranty_replacement_tenant_id_replacement_number",
                schema: "serialisation",
                table: "warranty_replacement",
                columns: new[] { "tenant_id", "replacement_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_warranty_replacement_tenant_id_ship_command_id",
                schema: "serialisation",
                table: "warranty_replacement",
                columns: new[] { "tenant_id", "ship_command_id" },
                unique: true,
                filter: "[ship_command_id] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_warranty_replacement_line_tenant_id_item_id",
                schema: "serialisation",
                table: "warranty_replacement_line",
                columns: new[] { "tenant_id", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_warranty_replacement_line_tenant_id_replacement_link_id",
                schema: "serialisation",
                table: "warranty_replacement_line",
                columns: new[] { "tenant_id", "replacement_link_id" });

            migrationBuilder.CreateIndex(
                name: "ux_warranty_replacement_line_tenant_id_warranty_replacement_id_sequence",
                schema: "serialisation",
                table: "warranty_replacement_line",
                columns: new[] { "tenant_id", "warranty_replacement_id", "sequence" },
                unique: true);

            migrationBuilder.AddTenantPolicy("serialisation", "warranty_replacement");
            migrationBuilder.AddTenantPolicy("serialisation", "replacement_link");
            migrationBuilder.AddTenantPolicy("serialisation", "warranty_replacement_line");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("serialisation", "warranty_replacement_line");
            migrationBuilder.RemoveTenantPolicy("serialisation", "replacement_link");
            migrationBuilder.RemoveTenantPolicy("serialisation", "warranty_replacement");

            migrationBuilder.DropTable(
                name: "warranty_replacement_line",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "replacement_link",
                schema: "serialisation");

            migrationBuilder.DropTable(
                name: "warranty_replacement",
                schema: "serialisation");
        }
    }
}
