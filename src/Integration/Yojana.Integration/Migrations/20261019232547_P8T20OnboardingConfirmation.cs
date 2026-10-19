using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Yojana.Platform.Data.Rls;

#nullable disable

namespace Yojana.Integration.Migrations
{
    /// <inheritdoc />
    public partial class P8T20OnboardingConfirmation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_onboarding_confirmation",
                schema: "integration",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    connection_id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    item_code = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    text_hash = table.Column<string>(type: "char(64)", unicode: false, fixedLength: true, maxLength: 64, nullable: false),
                    evidence = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    confirmed_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_connection_onboarding_confirmation", x => new { x.tenant_id, x.connection_id, x.item_code });
                });

            migrationBuilder.AddTenantPolicy("integration", "connection_onboarding_confirmation");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RemoveTenantPolicy("integration", "connection_onboarding_confirmation");

            migrationBuilder.DropTable(
                name: "connection_onboarding_confirmation",
                schema: "integration");
        }
    }
}
